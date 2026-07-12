using System.Diagnostics;
using System.Globalization;
using System.IO;
using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using Microsoft.Extensions.Logging;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Two-phase merge (spec §6.4): re-encode only the non-conforming clips to temp
/// intermediates under a concurrency cap, then stream-copy concat. Conforming clips are referenced
/// in place. Temp files are removed on every exit path — success, failure, cancellation and any
/// unexpected exception — so an abandoned merge never leaves gigabytes behind.</summary>
public sealed class MergeService : IMergeService
{
    /// <summary>Share of the progress bar owned by the normalize phase when there is anything to
    /// normalize; encoding dominates the wall clock and the copy-concat is nearly free. On the fast
    /// path this is 0 and the concat owns the whole bar.</summary>
    private const double EncodeWeight = 95.0;

    /// <summary>Below this, a wall-clock throughput reading is noise (or a division by ~zero).</summary>
    private const double MinTimedSeconds = 0.25;

    /// <summary>How stale an abandoned <c>merge-*</c> temp directory must be before the sweep will
    /// reclaim it. Generous on purpose: age is the only evidence that a directory is orphaned rather
    /// than belonging to a merge running right now, possibly in another instance of the app.</summary>
    private static readonly TimeSpan OrphanMaxAge = TimeSpan.FromHours(24);

    private readonly IFfmpegRunner _ffmpeg;
    private readonly ISpeedProfileStore _speedStore;
    private readonly Func<string, long> _getFreeBytes;
    private readonly string _tempRoot;
    private readonly int _maxConcurrency;
    private readonly ILogger<MergeService> _logger;
    private readonly IMediaAnalyzer? _verifier;

    /// <param name="verifier">Probes the finished file to prove the merge is whole. Optional (the
    /// same trailing-optional shape <c>DownloadManager</c> uses for its sinks): when absent, only
    /// the cheaper unreadable-segment preflight guards against a silently truncated output.</param>
    public MergeService(
        IFfmpegRunner ffmpeg,
        ISpeedProfileStore speedStore,
        Func<string, long> getFreeBytes,
        string tempRoot,
        int maxConcurrency,
        ILogger<MergeService> logger,
        IMediaAnalyzer? verifier = null)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(speedStore);
        ArgumentNullException.ThrowIfNull(getFreeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentNullException.ThrowIfNull(logger);

        _ffmpeg = ffmpeg;
        _speedStore = speedStore;
        _getFreeBytes = getFreeBytes;
        _tempRoot = tempRoot;
        _maxConcurrency = maxConcurrency;
        _logger = logger;
        _verifier = verifier;
    }

    /// <summary>The first segment we cannot actually open for reading, or null if all are fine.
    /// Opening is the test, not <see cref="File.Exists"/>: a clip whose ACL denies us read still
    /// "exists", and ffmpeg would drop it just the same.</summary>
    private static string? FirstUnreadableSegment(IEnumerable<string> segments)
    {
        foreach (var segment in segments)
        {
            try
            {
                using var stream = File.OpenRead(segment);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>How much shorter than the sum of its clips the output may legitimately be.</summary>
    /// <remarks><para>A fixed 1 s was too blunt in the direction that costs the user a file. Every clip
    /// contributes a little rounding of its own (the container's duration versus the stream the
    /// normalize pass actually writes, a trailing partial frame, a VFR source), and those errors
    /// ACCUMULATE — so a healthy 40-clip merge could land more than a second short, be declared
    /// truncated, and have its perfectly good output deleted. A false positive here is not a warning,
    /// it is data loss.</para>
    /// <para>It cannot simply grow without bound, though, or a genuinely dropped clip stops being
    /// detectable — which is the whole reason this check exists. So it is also clamped to half the
    /// SHORTEST clip: whatever else we forgive, losing an entire clip still moves the duration by more
    /// than the tolerance.</para></remarks>
    private static TimeSpan ToleranceFor(IReadOnlyList<MergeClip> clips)
    {
        var accumulated = TimeSpan.FromSeconds(1 + (0.05 * clips.Count));

        var shortest = clips.Min(c => c.Info.Duration);
        var half = shortest > TimeSpan.Zero ? shortest / 2 : accumulated;

        return accumulated < half ? accumulated : half;
    }

    /// <summary>Proves the output really contains every clip. ffmpeg's concat demuxer exits 0 even
    /// when it silently drops a segment it cannot decode, so a short output is the only signal that
    /// this happened — and shipping a truncated video the user believes is complete is the worst
    /// thing this tool could do.</summary>
    private async Task<Result> VerifyOutputAsync(
        string outputPath, TimeSpan expected, IReadOnlyList<MergeClip> clips, CancellationToken ct)
    {
        if (_verifier is null || expected <= TimeSpan.Zero)
        {
            return Result.Success();
        }

        var probe = await _verifier.AnalyzeAsync(outputPath, ct).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            // We cannot read back what we just wrote. Say so rather than claim success.
            return Result.Failure($"The merged file could not be read back: {probe.Error}");
        }

        var actual = probe.Value!.Duration;
        if (actual >= expected - ToleranceFor(clips))
        {
            return Result.Success();
        }

        return Result.Failure(
            $"The merge finished but the result is {Format(actual)} instead of {Format(expected)} — "
            + "at least one clip was dropped, most likely because it is corrupt or unreadable. "
            + "The incomplete file has been discarded.");

        static string Format(TimeSpan span)
            => span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Where the concat writes while it is still unproven: a sibling of the destination,
    /// keeping the real extension because ffmpeg picks its muxer from it.</summary>
    private static string PartialPathFor(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);

        return Path.Combine(directory, $"{name}.merging-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>Removes the unproven merge. Always safe: this file is one we created moments ago and
    /// nothing else can be at that path — it carries a fresh GUID.</summary>
    private void TryDeletePendingOutput(string? pendingOutput)
    {
        if (pendingOutput is null)
        {
            return;
        }

        try
        {
            if (File.Exists(pendingOutput))
            {
                File.Delete(pendingOutput);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the incomplete merge output at {Path}.", pendingOutput);
        }
    }

    public async Task<Result<string>> MergeAsync(
        MergeRequest request,
        IProgress<MergeProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Clips.Count == 0)
        {
            return Result<string>.Failure("Add at least one clip to merge.");
        }

        var workingDirectory = Path.Combine(_tempRoot, "merge-" + Guid.NewGuid().ToString("N"));

        // The half-written merge, next to its destination but not yet in it. Non-null exactly while a
        // file exists that is ours to clean up; nulled the instant it is moved into place.
        string? pendingOutput = null;

        // The bar is built before the tracker so the fast path can hand the whole 100 points to the
        // concat rather than pretend a normalize phase that never happens got us to 95 %.
        var plan = Plan(request);
        var tracker = new ProgressTracker(
            progress,
            [.. plan.Work.Select(w => w.Clip.Info.Duration.TotalSeconds)],
            // Work[slot].Index IS the clip's position in request.Clips, which is exactly the mapping
            // the per-clip bars need: _fractions is indexed by normalize SLOT, the published array by
            // CLIP. Publishing by slot would attribute one clip's progress to another.
            [.. plan.Work.Select(w => w.Index)],
            request.Clips.Count,
            plan.Work.Count == 0 ? 0 : EncodeWeight);

        try
        {
            var preflight = Preflight(request);
            if (!preflight.IsSuccess)
            {
                tracker.ReportTerminal(MergeJobStatus.Failed);
                return Result<string>.Failure(preflight.Error!);
            }

            Directory.CreateDirectory(workingDirectory);

            // Phase 1 — normalize the non-conforming clips. Skipped entirely on the fast path:
            // NormalizeArgsBuilder is never reached and ffmpeg is never spawned to encode.
            if (plan.Work.Count > 0)
            {
                var normalize = await NormalizeAsync(request, plan, workingDirectory, tracker, ct)
                    .ConfigureAwait(false);
                if (!normalize.IsSuccess)
                {
                    tracker.ReportTerminal(MergeJobStatus.Failed);
                    return Result<string>.Failure(normalize.Error!);
                }

                // Persist the throughput we actually achieved now that the encodes are done and
                // measured — a later concat failure does not make these readings any less real.
                RecordSpeeds(request.Target, normalize.Value!);
            }

            // Phase 2 — stream-copy concat. The segment list mixes temp intermediates (for the clips
            // we re-encoded) with the ORIGINAL paths (for the clips that already conformed), in the
            // requested order.
            tracker.ReportConcat(0);

            var listPath = Path.Combine(workingDirectory, "list.txt");
            var segments = plan.Segments.Select(s => s!).ToList();

            // ffmpeg's concat demuxer does NOT fail on a segment it cannot open: it prints
            // "Impossible to open", drops that segment AND EVERY SEGMENT AFTER IT, and exits 0. We
            // would report a completed merge and hand the user a silently truncated video. This is
            // reachable without doing anything strange — on the fast path the list holds the user's
            // OWN file paths, so moving or deleting a clip between adding it and clicking Merge is
            // enough. Refuse the merge instead, and name the clip.
            var unreadable = FirstUnreadableSegment(segments);
            if (unreadable is not null)
            {
                tracker.ReportTerminal(MergeJobStatus.Failed);
                return Result<string>.Failure(
                    $"'{Path.GetFileName(unreadable)}' can no longer be read — it may have been moved, "
                    + "renamed, or deleted since it was added. Remove it from the list, or put it back.");
            }

            await File.WriteAllTextAsync(listPath, ConcatArgsBuilder.BuildListFile(segments), ct)
                .ConfigureAwait(false);

            EnsureOutputDirectory(request.OutputPath);

            // ffmpeg is given -y and would overwrite the destination the moment the concat starts. So
            // it never writes the destination: it writes a SIBLING, we verify that, and only a merge
            // that passed verification is moved into place. Until then the file already sitting there
            // — very likely the previous merge, since the default output name is a constant — is
            // untouched. A failed merge must not cost the user the good video they already had.
            //
            // The sibling lives in the OUTPUT directory, not the temp root: File.Move within a volume
            // is a rename (free), while moving across volumes would copy the whole merged file. And it
            // keeps the real extension, because ffmpeg picks its muxer from it.
            pendingOutput = PartialPathFor(request.OutputPath);

            var outputSeconds = plan.OutputDuration.TotalSeconds;
            var concatSink = new SyncProgress<FfmpegProgress>(p =>
                tracker.ReportConcat(outputSeconds > 0 ? p.Position.TotalSeconds / outputSeconds : 0));

            var concat = await _ffmpeg
                .RunAsync(
                    ConcatArgsBuilder.BuildArgs(listPath, pendingOutput, request.Target.Container),
                    concatSink,
                    ct)
                .ConfigureAwait(false);

            if (!concat.IsSuccess)
            {
                tracker.ReportTerminal(MergeJobStatus.Failed);
                return Result<string>.Failure(concat.Error!);
            }

            // Exit 0 is not proof of a whole merge. The demuxer drops a segment it cannot decode and
            // still succeeds, so the only trustworthy evidence is the output itself: if it is
            // materially shorter than the clips we put in, something was silently dropped.
            var verified = await VerifyOutputAsync(pendingOutput, plan.OutputDuration, request.Clips, ct)
                .ConfigureAwait(false);
            if (!verified.IsSuccess)
            {
                tracker.ReportTerminal(MergeJobStatus.Failed);
                return Result<string>.Failure(verified.Error!);
            }

            // Verified whole. NOW it may take the destination.
            File.Move(pendingOutput, request.OutputPath, overwrite: true);
            pendingOutput = null;

            tracker.ReportTerminal(MergeJobStatus.Completed);
            return Result<string>.Success(request.OutputPath);
        }
        catch (OperationCanceledException)
        {
            // Must precede the broad catch (CS0160 enforces it) — a killed ffmpeg is a cancellation,
            // not a failure, and the user should not be told their merge broke.
            tracker.ReportTerminal(MergeJobStatus.Canceled);
            return Result<string>.Failure("Merge canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge failed unexpectedly.");
            tracker.ReportTerminal(MergeJobStatus.Failed);
            return Result<string>.Failure($"Merge failed: {ex.Message}");
        }
        finally
        {
            // Every exit path: failed, canceled, or thrown. An unproven merge left lying next to the
            // real one would be indistinguishable from a finished video — which is the whole thing we
            // are trying not to hand the user. (On success it is already null: it was moved into
            // place.)
            TryDeletePendingOutput(pendingOutput);
            TryCleanup(workingDirectory);
        }
    }

    /// <summary>Splits the clips into "already conforming" (referenced in place) and "must be
    /// re-encoded", using <see cref="ConformanceCheck"/> — the same predicate
    /// <see cref="MergeEstimator"/> uses. Anything else here and the ETA, the fast-path promise and
    /// the disk reservation would all describe a different plan than the one that runs.</summary>
    private static MergePlan Plan(MergeRequest request)
    {
        var segments = new string?[request.Clips.Count];
        var work = new List<(int Index, MergeClip Clip)>();
        var ticks = 0L;

        for (var i = 0; i < request.Clips.Count; i++)
        {
            var clip = request.Clips[i];
            ticks += Math.Max(0L, clip.Info.Duration.Ticks);

            if (ConformanceCheck.Evaluate(clip.Info, request.Target).IsConforming)
            {
                segments[i] = clip.SourcePath; // referenced in place — no temp file, no encode
            }
            else
            {
                work.Add((i, clip));
            }
        }

        return new MergePlan(segments, work, TimeSpan.FromTicks(ticks));
    }

    /// <summary>Phase 0 — fail fast, before a single byte is written, if the disk cannot hold what
    /// this merge is about to produce.</summary>
    private Result Preflight(MergeRequest request)
    {
        // Reclaim debris from a merge that crashed or was killed before its finally could run. Done
        // here, before we measure free space, precisely so a previous run's orphans do not count
        // against this one's disk check. Best-effort: a directory it cannot remove is skipped.
        var swept = TempDirectorySweeper.SweepOrphans(_tempRoot, OrphanMaxAge, DateTime.UtcNow);
        if (swept > 0)
        {
            _logger.LogInformation("Swept {Count} orphaned merge temp director(ies).", swept);
        }

        var estimate = MergeEstimator.Estimate(request.Clips, request.Target, _speedStore.Load());

        // TempBytesEstimate counts ONLY the re-encoded intermediates and excludes the merged output —
        // it is legitimately 0 on the fast path. Reserving it alone would wave a two-hour
        // all-conforming merge onto a full disk and die half-written, so add the output's size.
        var outputBytes = ToBytes(estimate.OutputDuration.TotalSeconds * request.Target.EstimatedBitsPerSecond / 8.0);
        var required = SaturatingAdd(estimate.TempBytesEstimate, outputBytes);

        // Temp and output can sit on different volumes; take the tighter of the two rather than
        // check one and hope. On the usual single-volume setup this is just that volume.
        var free = _getFreeBytes(_tempRoot);
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            free = Math.Min(free, _getFreeBytes(outputDirectory));
        }

        return DiskSpaceGuard.Evaluate(free, required);
    }

    /// <summary>Phase 1 — re-encode the non-conforming clips concurrently under a
    /// <see cref="SemaphoreSlim"/> cap (SDD §12). Returns one measured throughput sample per clip.
    /// </summary>
    private async Task<Result<double[]>> NormalizeAsync(
        MergeRequest request,
        MergePlan plan,
        string workingDirectory,
        ProgressTracker tracker,
        CancellationToken ct)
    {
        var work = plan.Work;
        var extension = request.Target.Container == MergeContainer.Mkv ? ".mkv" : ".mp4";
        var measured = new double[work.Count];

        // Cancels the siblings the moment one clip fails: the merge is already doomed, and grinding
        // through hours of encoding for a file that will never be written helps nobody.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        string? failure = null;

        tracker.ReportEncode(MergeJobStatus.Normalizing, null);

        var tasks = new Task[work.Count];
        for (var slot = 0; slot < work.Count; slot++)
        {
            var captured = slot;
            tasks[slot] = RunOneAsync(captured);
        }

        // Every task is awaited, always: nothing is left unobserved even when a sibling blows up.
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // The user's own cancel outranks any failure it caused on the way down.
        ct.ThrowIfCancellationRequested();

        return failure is not null
            ? Result<double[]>.Failure(failure)
            : Result<double[]>.Success(measured);

        async Task RunOneAsync(int slot)
        {
            var (index, clip) = work[slot];
            var name = Path.GetFileName(clip.SourcePath);

            try
            {
                await gate.WaitAsync(abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // aborted before this clip ever started
            }

            try
            {
                // Acquiring the gate is NOT proof we are still wanted. Cancelling a token does not
                // synchronously dequeue a pending SemaphoreSlim.WaitAsync waiter — the cancellation
                // completes an internal task whose continuation is queued to the thread pool, so a
                // Release() on the cancelling thread (ours: Cancel() then finally-Release, microseconds
                // apart) still finds the waiter in the queue and hands it the permit. Without this
                // re-check a doomed merge reliably launches one more full ffmpeg encode.
                abort.Token.ThrowIfCancellationRequested();

                var output = Path.Combine(workingDirectory, index.ToString("D4", CultureInfo.InvariantCulture) + extension);
                var args = NormalizeArgsBuilder.Build(clip.SourcePath, clip.Info, request.Target, output);

                var clipSeconds = clip.Info.Duration.TotalSeconds;
                var lastSpeed = 0.0;
                var sink = new SyncProgress<FfmpegProgress>(p =>
                {
                    if (p.Speed > 0)
                    {
                        lastSpeed = p.Speed;
                    }

                    tracker.ReportEncode(
                        slot,
                        clipSeconds > 0 ? p.Position.TotalSeconds / clipSeconds : 0,
                        name);
                });

                var stopwatch = Stopwatch.StartNew();
                var result = await _ffmpeg.RunAsync(args, sink, abort.Token).ConfigureAwait(false);
                stopwatch.Stop();

                if (!result.IsSuccess)
                {
                    Interlocked.CompareExchange(
                        ref failure, $"Could not standardize '{name}': {result.Error}", null);
                    abort.Cancel();
                    return;
                }

                plan.Segments[index] = output;
                measured[slot] = MeasureSpeed(lastSpeed, clipSeconds, stopwatch.Elapsed);

                tracker.CompleteEncode(slot, name);
            }
            catch (OperationCanceledException)
            {
                // Either the caller cancelled or a sibling failed; both are decided after WhenAll.
                // Swallowing here (rather than letting WhenAll rethrow) is what keeps a *failed*
                // merge from being misreported as a *canceled* one.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Normalizing {Clip} threw.", clip.SourcePath);
                Interlocked.CompareExchange(ref failure, $"Could not standardize '{name}': {ex.Message}", null);
                abort.Cancel();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>One throughput sample (encoded video-seconds per wall-clock second) per finished
    /// clip. ffmpeg's own final <c>speed=</c> is the cumulative average for the whole run and is
    /// preferred; the wall-clock fallback is guarded because a near-instant elapsed time divides to
    /// +infinity, which would be silently dropped by <see cref="SpeedProfile.Record"/> — better to
    /// be deliberate about it than to rely on that.</summary>
    private static double MeasureSpeed(double reportedSpeed, double clipSeconds, TimeSpan elapsed)
    {
        if (double.IsFinite(reportedSpeed) && reportedSpeed > 0)
        {
            return reportedSpeed;
        }

        if (clipSeconds <= 0 || elapsed.TotalSeconds < MinTimedSeconds)
        {
            return 0; // not a measurement — dropped by RecordSpeeds
        }

        return clipSeconds / elapsed.TotalSeconds;
    }

    /// <summary>Folds the run's samples into the rolling profile and persists once. Note the concat
    /// pass is deliberately NOT sampled: a stream copy runs at hundreds of times realtime and would
    /// poison an average that exists to predict <em>encode</em> time.</summary>
    private void RecordSpeeds(MergeTarget target, IReadOnlyList<double> speeds)
    {
        var usable = speeds.Where(s => double.IsFinite(s) && s > 0).ToList();
        if (usable.Count == 0)
        {
            return;
        }

        var profile = _speedStore.Load();
        foreach (var speed in usable)
        {
            profile.Record(target, speed);
        }

        try
        {
            _speedStore.Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the encode-speed profile.");
        }
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void TryCleanup(string workingDirectory)
    {
        try
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove temp directory {Path}.", workingDirectory);
        }
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private static long ToBytes(double bytes)
    {
        if (double.IsNaN(bytes) || bytes <= 0)
        {
            return 0;
        }

        return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
    }

    /// <param name="Segments">Final concat order; a null entry is a clip still being normalized.</param>
    private sealed record MergePlan(
        string?[] Segments,
        IReadOnlyList<(int Index, MergeClip Clip)> Work,
        TimeSpan OutputDuration);

    /// <summary>Turns N clips each reporting their own 0→100 % into one bar that never goes
    /// backwards. Each clip owns a slice of the encode segment proportional to its duration (encode
    /// time tracks length, not clip count), the whole total is recomputed under a lock so a stale
    /// snapshot can never be emitted after a fresher one, and a high-water mark makes retreat
    /// impossible even so.</summary>
    private sealed class ProgressTracker
    {
        private readonly IProgress<MergeProgress>? _sink;
        private readonly double[] _fractions;
        private readonly double[] _weights;

        /// <summary>Normalize slot → the clip's index in <c>MergeRequest.Clips</c>. Only the clips
        /// that need re-encoding have a slot at all.</summary>
        private readonly int[] _slotToClipIndex;

        private readonly int _clipCount;
        private readonly double _encodeWeight;
        private readonly Lock _gate = new();
        private double _highWater;

        public ProgressTracker(
            IProgress<MergeProgress>? sink,
            double[] clipSeconds,
            int[] slotToClipIndex,
            int clipCount,
            double encodeWeight)
        {
            _sink = sink;
            _encodeWeight = encodeWeight;
            _slotToClipIndex = slotToClipIndex;
            _clipCount = clipCount;
            _fractions = new double[clipSeconds.Length];
            _weights = new double[clipSeconds.Length];

            var total = clipSeconds.Where(s => s > 0).Sum();
            for (var i = 0; i < clipSeconds.Length; i++)
            {
                // Zero/unknown durations fall back to an equal share — better a slightly wrong bar
                // than a division by zero or a clip that contributes nothing when it completes.
                _weights[i] = total > 0 && clipSeconds[i] > 0
                    ? clipSeconds[i] / total
                    : 1.0 / clipSeconds.Length;
            }
        }

        public void ReportEncode(MergeJobStatus status, string? clip)
        {
            lock (_gate)
            {
                Emit(status, EncodePercent(), clip);
            }
        }

        public void ReportEncode(int slot, double fraction, string? clip)
        {
            lock (_gate)
            {
                Advance(slot, fraction);
                Emit(MergeJobStatus.Normalizing, EncodePercent(), clip);
            }
        }

        public void CompleteEncode(int slot, string? clip)
        {
            lock (_gate)
            {
                Advance(slot, 1);
                Emit(MergeJobStatus.Normalizing, EncodePercent(), clip);
            }
        }

        public void ReportConcat(double fraction)
        {
            lock (_gate)
            {
                var span = 100 - _encodeWeight;
                Emit(MergeJobStatus.Concatenating, _encodeWeight + (span * Clamp01(fraction)), null);
            }
        }

        /// <summary>Terminal status. Completed is 100 %; a failed or canceled merge keeps the bar
        /// exactly where it stopped rather than snapping back to zero.</summary>
        public void ReportTerminal(MergeJobStatus status)
        {
            lock (_gate)
            {
                Emit(status, status == MergeJobStatus.Completed ? 100 : _highWater, null);
            }
        }

        private void Advance(int slot, double fraction)
            => _fractions[slot] = Math.Max(_fractions[slot], Clamp01(fraction));

        private double EncodePercent()
        {
            var done = 0.0;
            for (var i = 0; i < _fractions.Length; i++)
            {
                done += _fractions[i] * _weights[i];
            }

            return _encodeWeight * Clamp01(done);
        }

        /// <summary>Per-clip percentages in request order. A clip with no normalize slot already
        /// conforms, so it is 100 — it is not waiting on work, it simply has none. Called only from
        /// <see cref="Emit"/>, i.e. always under <see cref="_gate"/>: snapshotting outside the lock
        /// would tear across a concurrent <see cref="Advance"/> from another clip's encode.</summary>
        private double[] ClipPercents()
        {
            var percents = new double[_clipCount];
            Array.Fill(percents, 100.0);
            for (var slot = 0; slot < _slotToClipIndex.Length; slot++)
            {
                percents[_slotToClipIndex[slot]] = Math.Clamp(_fractions[slot] * 100.0, 0, 100);
            }

            return percents;
        }

        private void Emit(MergeJobStatus status, double percent, string? clip)
        {
            var value = Math.Clamp(percent, 0, 100);
            if (value < _highWater)
            {
                value = _highWater;
            }
            else
            {
                _highWater = value;
            }

            _sink?.Report(new MergeProgress(status, value, clip, ClipPercents()));
        }

        private static double Clamp01(double value)
            => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
    }

    /// <summary>Reports on the calling thread. The BCL's <see cref="Progress{T}"/> posts to the
    /// captured <see cref="SynchronizationContext"/>, so its callbacks arrive out of order (and,
    /// headless, on the thread pool) — which would let a stale percentage land after a fresher one
    /// and make the bar visibly retreat.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
