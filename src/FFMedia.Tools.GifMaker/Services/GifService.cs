using System.Globalization;
using System.IO;
using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;

namespace FFMedia.Tools.GifMaker.Services;

public interface IGifService
{
    /// <summary>Makes the GIF. Returns the path it was written to.</summary>
    Task<Result<string>> CreateAsync(
        GifRequest request, IProgress<GifProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Two ffmpeg passes, a verified result, and no litter.
///
/// <para><b>The output is re-probed before success is reported</b> — including its DURATION, not just
/// "a readable video with a video stream". ffmpeg's exit code is exactly what cannot be trusted — its
/// concat demuxer exits 0 having silently dropped segments (SDD Changelog 0.15), and the same failure
/// mode reaches here: a filtergraph that dies partway through still probes as a valid, short GIF. A GIF
/// that "succeeded" but is not what was asked for is deleted rather than handed over, because a corrupt
/// or truncated file the user finds later is worse than an error they see now.</para>
///
/// <para><b>The render never writes the real destination directly.</b> This tool's whole workflow is
/// iterative — load once, tune, re-render to the SAME filename, look, tune again — so
/// <c>request.OutputPath</c> routinely already holds a good GIF from thirty seconds ago. ffmpeg is
/// given <c>-y</c> and would truncate that file the instant it opened it, so the render targets a
/// temporary sibling instead; only a sibling that passes every check below is moved onto the real
/// filename. Every unhappy path (render failure, verification failure, cancellation) deletes the
/// sibling and leaves <c>request.OutputPath</c> untouched — the same rule <c>MergeService</c> already
/// enforces for the merger (CLAUDE.md: "Nothing the user already had is touched until we have something
/// worth replacing it with").</para>
///
/// <para><b>The temp palette is deleted on EVERY exit path</b> — success, failure and cancel alike.</para></summary>
public sealed class GifService : IGifService
{
    /// <summary>The palette pass reads every frame but writes one small image; the render pass does the
    /// real work. Weighting the bar 30/70 keeps it from stalling at "nearly done" for most of the run.</summary>
    private const double PaletteShare = 0.30;

    private readonly IFfmpegRunner _ffmpeg;
    private readonly IMediaAnalyzer _analyzer;
    private readonly IGifSizeProfileStore _profiles;
    private readonly string _tempRoot;

    public GifService(IFfmpegRunner ffmpeg, IMediaAnalyzer analyzer, IGifSizeProfileStore profiles, string tempRoot)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);

        _ffmpeg = ffmpeg;
        _analyzer = analyzer;
        _profiles = profiles;
        _tempRoot = tempRoot;
    }

    public async Task<Result<string>> CreateAsync(
        GifRequest request, IProgress<GifProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var preflight = await PreflightAsync(request, ct).ConfigureAwait(false);
        if (!preflight.IsSuccess)
        {
            return Result<string>.Failure(preflight.Error!);
        }

        Directory.CreateDirectory(_tempRoot);
        var palettePath = Path.Combine(_tempRoot, $"palette-{Guid.NewGuid():N}.png");

        // Where the render actually lands until it is proven good. Same directory as the destination
        // (so the final File.Move is a free rename, not a copy) and the same .gif extension (ffmpeg
        // picks its muxer from it) -- see the type's remarks for why this exists at all.
        var pendingOutput = PendingPathFor(request.OutputPath);

        try
        {
            var palette = await _ffmpeg.RunAsync(
                GifArgsBuilder.PalettePass(request, palettePath),
                Weighted(progress, GifPhase.Analyzing, request, 0, PaletteShare),
                ct).ConfigureAwait(false);

            if (!palette.IsSuccess)
            {
                return Result<string>.Failure(GifErrors.Explain(palette.Error));
            }

            var render = await _ffmpeg.RunAsync(
                GifArgsBuilder.RenderPass(request, palettePath, pendingOutput),
                Weighted(progress, GifPhase.Rendering, request, PaletteShare, 1 - PaletteShare),
                ct).ConfigureAwait(false);

            if (!render.IsSuccess)
            {
                DeleteQuietly(pendingOutput); // never leave a half-written GIF behind -- request.OutputPath is never touched
                return Result<string>.Failure(GifErrors.Explain(render.Error));
            }

            return await VerifyAsync(request, pendingOutput, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(pendingOutput);
            throw; // cancel is Canceled, not Failed -- the caller distinguishes them
        }
        finally
        {
            DeleteQuietly(palettePath);
        }
    }

    /// <summary>Rejects what ffmpeg would only discover minutes later — and, for a range outside the
    /// video, would not discover at all: it would silently produce a shorter GIF than asked for.</summary>
    private async Task<Result> PreflightAsync(GifRequest request, CancellationToken ct)
    {
        if (!File.Exists(request.SourcePath))
        {
            return Result.Failure("The video could not be found. It may have been moved or renamed.");
        }

        if (request.End <= request.Start)
        {
            return Result.Failure("The end time must be after the start time.");
        }

        // Nothing creates the output folder otherwise -- the Folder box is free text, and a typo'd or
        // since-deleted folder would surface as ffmpeg's own "No such file or directory", which
        // GifErrors.Explain's FIRST rule reports as "The video could not be found": a perfectly good
        // source blamed for a missing destination folder (the exact mistake CLAUDE.md's M7 entry
        // records -- "blamed the user's perfectly good mp4 for a missing binary"). Fail here instead,
        // naming the actual problem, or create the folder so the render can proceed.
        var ensureFolder = EnsureOutputDirectory(request.OutputPath);
        if (!ensureFolder.IsSuccess)
        {
            return ensureFolder;
        }

        var probe = await _analyzer.AnalyzeAsync(request.SourcePath, ct).ConfigureAwait(false);
        if (!probe.IsSuccess || probe.Value is null)
        {
            return Result.Failure(GifErrors.Explain(probe.Error));
        }

        if (probe.Value.Video is null)
        {
            return Result.Failure("That file has no video track, so there is nothing to turn into a GIF.");
        }

        // A tolerance, because a container's reported duration and its last frame's timestamp routinely
        // differ by a frame or two, and refusing a range that ends "at the end" would be maddening.
        if (request.End > probe.Value.Duration + TimeSpan.FromSeconds(0.5))
        {
            return Result.Failure(
                $"The end time is past the end of the video (which is {probe.Value.Duration:mm\\:ss} long).");
        }

        return Result.Success();
    }

    /// <summary>Creates the destination's folder if it does not already exist. A missing folder is the
    /// common case (a typo, or the folder was deleted since it was chosen) and is not an error by
    /// itself; only a folder that genuinely cannot be created (permissions, an invalid path) fails,
    /// naming the folder rather than leaving ffmpeg to fail on a path the user never sees.</summary>
    private static Result EnsureOutputDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(directory))
        {
            return Result.Success();
        }

        try
        {
            Directory.CreateDirectory(directory);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure($"The output folder '{directory}' could not be created: {ex.Message}");
        }
    }

    /// <summary>ffmpeg said it worked. Check the PENDING sibling -- never <c>request.OutputPath</c>
    /// itself -- and only move it onto the real destination once every check below has passed.</summary>
    private async Task<Result<string>> VerifyAsync(GifRequest request, string pendingOutput, CancellationToken ct)
    {
        if (!File.Exists(pendingOutput) || new FileInfo(pendingOutput).Length == 0)
        {
            DeleteQuietly(pendingOutput);
            return Result<string>.Failure("ffmpeg reported success but wrote no GIF.");
        }

        var probe = await _analyzer.AnalyzeAsync(pendingOutput, ct).ConfigureAwait(false);
        if (!probe.IsSuccess || probe.Value?.Video is null)
        {
            DeleteQuietly(pendingOutput);
            return Result<string>.Failure(
                "The GIF was written but cannot be read back, so it is not usable. It has been removed.");
        }

        // Spec §6.3: "a real GIF, non-empty, of roughly the expected duration." Exit 0 and a readable
        // video stream prove the first two, not the third -- and "ffmpeg exits 0 having silently
        // produced less than it was asked for" is the exact failure this project has already shipped
        // once (the concat demuxer, SDD Changelog 0.15). A filtergraph that dies after the first frame,
        // or a truncated write, still probes as "a readable GIF with a video stream".
        var actual = probe.Value.Duration;
        if ((actual - request.Duration).Duration() > DurationTolerance(request.Duration))
        {
            DeleteQuietly(pendingOutput);
            return Result<string>.Failure(
                $"The GIF finished at {Format(actual)} instead of the requested {Format(request.Duration)} -- "
                + "something went wrong while rendering. It has been removed.");
        }

        // Verified whole. NOW it may take the destination -- whatever was there before (very likely an
        // earlier, GOOD render of this exact request; the workflow is "tune and re-render to the same
        // filename") survives right up until this instant.
        //
        // This is the tool's core loop, so the destination being open elsewhere is not exotic -- the
        // user is very likely LOOKING at the GIF they just made (a viewer, a browser tab, Explorer's
        // preview pane) while tuning the next render. On Windows that throws IOException here. The
        // verified sibling must not become litter in the user's OWN output folder just because the move
        // failed, and the message must name the real problem rather than surface the raw exception.
        try
        {
            File.Move(pendingOutput, request.OutputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(pendingOutput);
            return Result<string>.Failure(
                "The GIF was rendered, but the file could not be replaced because something else has it open "
                + "-- close anything showing it (a viewer, a browser tab, Explorer's preview pane) and try "
                + "again, or choose a different file name.");
        }

        RecordActualSize(request);
        return Result<string>.Success(request.OutputPath);
    }

    /// <summary>How far the finished GIF's duration may legitimately differ from what was asked for.
    ///
    /// <para>Two sources of slack are both benign and both scale with the request rather than being a
    /// flat number of seconds: frame-count rounding (<see cref="GifRequest.FrameCount"/> rounds
    /// Duration*Fps to the nearest whole frame) and the GIF format's own timing granularity (each
    /// frame's delay is stored in HUNDREDTHS of a second). So the tolerance is PROPORTIONAL — 15% of
    /// the requested duration — with a small absolute floor (0.5 s, the same slack
    /// <see cref="PreflightAsync"/> already grants a range that ends "at the end" of the source) so a
    /// very short GIF is not held to an unreasonably tight bound.</para>
    ///
    /// <para>It is also clamped to HALF the requested duration: whatever else is forgiven, ffmpeg
    /// producing only a small fraction of what was asked for (a filtergraph that dies early, a
    /// truncated write) must still be caught -- that is the entire reason this check exists. A false
    /// positive here deletes a GOOD GIF, which is the same trap the merger's duration check hit before
    /// it learned to scale rather than use a flat tolerance -- so this one is generous everywhere except
    /// that one failure mode.</para></summary>
    private static TimeSpan DurationTolerance(TimeSpan requested)
    {
        var proportional = TimeSpan.FromSeconds(Math.Max(0.5, requested.TotalSeconds * 0.15));
        var half = requested / 2;

        return proportional < half ? proportional : half;
    }

    private static string Format(TimeSpan span)
        => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>Where the render lands while it is still unproven: a sibling of the destination, in the
    /// SAME directory (so the eventual <see cref="File.Move(string, string, bool)"/> is a rename, not a
    /// copy) and with the real <c>.gif</c> extension (ffmpeg picks its muxer from it).</summary>
    private static string PendingPathFor(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);

        return Path.Combine(directory, $"{name}.rendering-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>The estimate's whole credibility rests on this: the seed constant is a guess, and the
    /// user's own GIFs are the evidence that corrects it.</summary>
    private void RecordActualSize(GifRequest request)
    {
        try
        {
            var profile = _profiles.Load();
            profile.Record(
                new FileInfo(request.OutputPath).Length,
                (long)request.Size.Width * request.Size.Height,
                request.FrameCount);
            _profiles.Save(profile);
        }
        catch (IOException)
        {
            // A broken profile store must never fail a GIF the user already has. The next estimate is
            // merely less well calibrated.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above -- a read-only/locked profile file throws this, not IOException.
        }
    }

    /// <summary>Maps one pass's 0-100 onto its slice of the overall bar.</summary>
    private static IProgress<FfmpegProgress>? Weighted(
        IProgress<GifProgress>? progress, GifPhase phase, GifRequest request, double offset, double share)
    {
        if (progress is null)
        {
            return null;
        }

        var total = request.Duration.TotalSeconds;
        return new SyncProgress<FfmpegProgress>(p =>
        {
            var within = total <= 0 ? 0 : Math.Clamp(p.Position.TotalSeconds / total, 0, 1);
            progress.Report(new GifProgress(phase, (offset + (within * share)) * 100));
        });
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup must never mask the real result.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reports on the calling thread. See <c>MergeService</c>'s equivalent for why the BCL
    /// <see cref="Progress{T}"/> is wrong here (it marshals to the captured context, reordering reports).
    /// <c>MergeService</c>'s copy is <c>private</c>, so it is not reusable across modules — this is a
    /// deliberate duplicate, not an oversight.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
