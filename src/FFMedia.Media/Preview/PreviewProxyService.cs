using FFMedia.Core.Results;

namespace FFMedia.Media.Preview;

/// <summary>Builds — or reuses — a small H.264 proxy of a video the player cannot open.
///
/// <para>Failure here is <b>never fatal</b>: the preview is an aid, not a gate. The caller falls back to
/// typing a timecode, exactly as before M9.</para></summary>
public sealed class PreviewProxyService : IPreviewProxyService
{
    /// <summary>Proxies older than this are assumed abandoned.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private readonly IFfmpegRunner _ffmpeg;
    private readonly string _proxyDirectory;

    public PreviewProxyService(IFfmpegRunner ffmpeg, string proxyDirectory)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyDirectory);

        _ffmpeg = ffmpeg;
        _proxyDirectory = proxyDirectory;
    }

    public async Task<Result<string>> GetOrCreateAsync(
        string sourcePath, MediaInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(info);

        if (!File.Exists(sourcePath))
        {
            return Result<string>.Failure("The video could not be found. It may have been moved or renamed.");
        }

        string proxyPath;
        try
        {
            // Real I/O, both lines: a locked-down or unavailable proxy directory throws here, and the
            // cache-check can race a concurrent SweepStale() deleting the file between File.Exists and
            // the length read. Either would throw OUT of GetOrCreateAsync and take the tool down with
            // it -- the preview must never be a gate, so any environmental failure here becomes a
            // Result.Failure instead, exactly like the environmental catches in DeleteQuietly/SweepStale.
            Directory.CreateDirectory(_proxyDirectory);

            // Preflight — reclaim proxies abandoned by previous runs, exactly where MergeService sweeps
            // its own orphaned temp directories (from inside its own preflight, not from app startup: a
            // host that forgets to call the sweeper silently reintroduces the leak). SweepStale swallows
            // its own environmental failures: the preview is an aid, never a gate, so a temp directory it
            // cannot enumerate or a file it cannot delete must not stop this proxy being built.
            SweepStale();

            proxyPath = PreviewProxyPath.For(sourcePath, _proxyDirectory);

            // Cached from a previous open of this exact file. Re-opening must not pay the transcode again.
            if (File.Exists(proxyPath) && new FileInfo(proxyPath).Length > 0)
            {
                return Result<string>.Success(proxyPath);
            }
        }
        catch (IOException ex)
        {
            return Result<string>.Failure($"The preview could not be prepared: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<string>.Failure($"The preview could not be prepared: {ex.Message}");
        }

        // ffmpeg writes to a SIBLING of the cache entry, never to the cache entry itself -- the rule
        // GifService and MergeService already live by: render beside it, verify THAT, and only then move
        // a proven-whole file into place. Nothing the user already has is touched until we have something
        // worth replacing it with.
        //
        // Here what "the user already has" is the CACHE, and the cache check is only File.Exists &&
        // Length > 0 -- so whatever sits at the proxy path IS the proxy, for seven days. Pointing ffmpeg
        // straight at it meant a transcode that died without exiting cleanly (quit or crash the app while
        // "Preparing a preview…" is on screen -- no race required) left a moov-less file there that every
        // future open was served: MediaFailed, "The preview could not be played", PERMANENTLY. For a
        // WebM -- the format the fallback exists for, and one our own downloader produces -- the feature
        // was simply dead for that file. (-movflags +faststart writes the moov atom LAST, so a killed
        // transcode's output is always unplayable, however many megabytes of it there are.)
        //
        // Same directory, so the File.Move below is a free rename rather than a copy -- and the SAME .mp4
        // EXTENSION, because ffmpeg picks its MUXER from the output file's extension and refuses an
        // unknown one outright ("Unable to choose an output format for '...mp4.part'"). Exactly the
        // constraint GifService and MergeService already state for their own siblings; proven here by the
        // real-ffmpeg integration test, which failed on a ".part" suffix the fakes were perfectly happy
        // with.
        var partPath = Path.Combine(
            _proxyDirectory, Path.GetFileNameWithoutExtension(proxyPath) + ".part.mp4");

        var total = info.Duration.TotalSeconds;
        var reporter = progress is null
            ? null
            : new SyncProgress<FfmpegProgress>(p => progress.Report(
                total <= 0 ? 0 : Math.Clamp(p.Position.TotalSeconds / total, 0, 1) * 100));

        try
        {
            var run = await _ffmpeg.RunAsync(
                PreviewProxyArgs.Build(sourcePath, info, partPath), reporter, ct).ConfigureAwait(false);

            // The runner may complete its Task without throwing even when the token was cancelled
            // mid-run (e.g. the process wrote output then the caller cancelled before exit was
            // observed). Re-checking here ensures a cancelled request never returns a cached-looking
            // success built from a proxy that should be discarded.
            ct.ThrowIfCancellationRequested();

            if (!run.IsSuccess)
            {
                DeleteQuietly(partPath);   // never promote a half-written proxy
                return Result<string>.Failure($"The preview could not be prepared: {run.Error}");
            }

            // ffmpeg's exit code is exactly what cannot be trusted. A zero-byte "success" promoted into
            // the cache would poison every future open of this video.
            if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
            {
                DeleteQuietly(partPath);
                return Result<string>.Failure("The preview could not be prepared: ffmpeg wrote nothing.");
            }

            // Only now -- a finished, non-empty transcode -- does anything land at the cache path.
            File.Move(partPath, proxyPath, overwrite: true);

            return Result<string>.Success(proxyPath);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(partPath);
            throw;
        }
        catch (IOException ex)
        {
            // The move itself is real I/O: the destination can be held open by a MediaElement still
            // playing the previous proxy of this very file. An aid, never a gate.
            DeleteQuietly(partPath);
            return Result<string>.Failure($"The preview could not be prepared: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            DeleteQuietly(partPath);
            return Result<string>.Failure($"The preview could not be prepared: {ex.Message}");
        }
    }

    public void SweepStale()
    {
        try
        {
            if (!Directory.Exists(_proxyDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - StaleAfter;

            // This glob covers the ".part.mp4" siblings as well as finished proxies, and it must: the
            // sibling indirection keeps a killed transcode's wreckage OUT of the cache, but the wreckage
            // still exists -- and an app killed mid-transcode runs no cleanup at all. Without the sweep
            // reaching them, the fix would trade a poisoned cache for an unbounded pile of half-transcodes.
            foreach (var file in Directory.EnumerateFiles(_proxyDirectory, "preview-*.mp4"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    DeleteQuietly(file);
                }
            }
        }
        catch (IOException)
        {
            // Sweeping is housekeeping. It must never break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reports on the calling thread — ffmpeg's stdout callback thread. The BCL
    /// <see cref="Progress{T}"/> marshals to the captured context and reorders reports.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
