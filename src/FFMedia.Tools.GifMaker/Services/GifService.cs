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
/// <para><b>The output is re-probed before success is reported.</b> ffmpeg's exit code is exactly what
/// cannot be trusted — its concat demuxer exits 0 having silently dropped segments (SDD Changelog 0.15).
/// A GIF that "succeeded" but is not a readable GIF is deleted rather than handed over, because a
/// corrupt file the user finds later is worse than an error they see now.</para>
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
                GifArgsBuilder.RenderPass(request, palettePath),
                Weighted(progress, GifPhase.Rendering, request, PaletteShare, 1 - PaletteShare),
                ct).ConfigureAwait(false);

            if (!render.IsSuccess)
            {
                DeleteQuietly(request.OutputPath); // never leave a half-written GIF behind
                return Result<string>.Failure(GifErrors.Explain(render.Error));
            }

            return await VerifyAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(request.OutputPath);
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

    /// <summary>ffmpeg said it worked. Check.</summary>
    private async Task<Result<string>> VerifyAsync(GifRequest request, CancellationToken ct)
    {
        if (!File.Exists(request.OutputPath) || new FileInfo(request.OutputPath).Length == 0)
        {
            DeleteQuietly(request.OutputPath);
            return Result<string>.Failure("ffmpeg reported success but wrote no GIF.");
        }

        var probe = await _analyzer.AnalyzeAsync(request.OutputPath, ct).ConfigureAwait(false);
        if (!probe.IsSuccess || probe.Value?.Video is null)
        {
            DeleteQuietly(request.OutputPath);
            return Result<string>.Failure(
                "The GIF was written but cannot be read back, so it is not usable. It has been removed.");
        }

        RecordActualSize(request);
        return Result<string>.Success(request.OutputPath);
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
