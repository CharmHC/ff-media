using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergeServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "ffmedia-merge-" + Guid.NewGuid().ToString("N"));
    private static readonly MergeTarget Target = MergeTarget.Default;

    /// <summary>Heuristic output bitrate of <see cref="MergeTarget.Default"/> — the number the
    /// disk-guard expectations below are computed from. Pinned so a change to the bitrate
    /// heuristic fails loudly here rather than silently shifting the reservations.</summary>
    private const long Bps = 5_168_640;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static MergeClip Conforming(string path, double seconds = 5) => new(path, new MediaInfo(
        TimeSpan.FromSeconds(seconds), "mov,mp4,m4a",
        new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
        new AudioStreamInfo("aac", 48000, 2)));

    private static MergeClip NonConforming(string path, double seconds = 5) => new(path, new MediaInfo(
        TimeSpan.FromSeconds(seconds), "matroska,webm",
        new VideoStreamInfo(1280, 720, new FrameRate(60, 1), "vp9", "yuv420p", 0),
        null));

    /// <summary>Clips that a lazier "needs re-encoding" rule would wave straight through — each one
    /// differs from <see cref="MergeTarget.Default"/> in exactly one property, several of them not
    /// the codec. Indexed by <see cref="MergeAsync_DecidesConformanceWithConformanceCheck"/>.</summary>
    private static readonly MediaInfo[] NearMisses =
    [
        Info(new VideoStreamInfo(1280, 720, new FrameRate(30, 1), "h264", "yuv420p", 0), Aac),   // 0 resolution
        Info(new VideoStreamInfo(1920, 1080, new FrameRate(60, 1), "h264", "yuv420p", 0), Aac),  // 1 frame rate
        Info(new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv422p", 0), Aac),  // 2 pixel format
        Info(new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 90), Aac), // 3 rotation
        Info(Yuv1080p30, new AudioStreamInfo("aac", 44100, 2)),                                  // 4 sample rate
        Info(Yuv1080p30, new AudioStreamInfo("aac", 48000, 1)),                                  // 5 channels
        Info(Yuv1080p30, new AudioStreamInfo("opus", 48000, 2)),                                 // 6 audio codec
        Info(Yuv1080p30, null),                                                                  // 7 no audio at all
    ];

    private static VideoStreamInfo Yuv1080p30 => new(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0);

    private static AudioStreamInfo Aac => new("aac", 48000, 2);

    private static MediaInfo Info(VideoStreamInfo video, AudioStreamInfo? audio)
        => new(TimeSpan.FromSeconds(5), "mov,mp4,m4a", video, audio);

    private sealed class FakeFfmpeg : IFfmpegRunner
    {
        private readonly Func<IReadOnlyList<string>, Result> _behavior;
        private int _current;

        public ConcurrentQueue<IReadOnlyList<string>> Invocations { get; } = new();

        /// <summary>Verbatim content of the concat list file, snapshotted while it still exists.</summary>
        public string? ConcatListContent { get; private set; }

        public int MaxObservedConcurrency;

        public FakeFfmpeg(Func<IReadOnlyList<string>, Result>? behavior = null)
            => _behavior = behavior ?? (_ => Result.Success());

        public async Task<Result> RunAsync(IReadOnlyList<string> arguments,
            IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            Invocations.Enqueue(arguments);
            var now = Interlocked.Increment(ref _current);
            InterlockedMax(ref MaxObservedConcurrency, now);

            if (arguments.Contains("concat"))
            {
                ConcatListContent = File.ReadAllText(ArgAfter(arguments, "-i"));
            }

            // Touch the output file so the concat phase sees real segments.
            var output = arguments[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, "segment", ct);

            // A real ffmpeg emits a progress block every few hundred ms, so emit SEVERAL. With a
            // single report, "one speed sample per clip" and "one per progress line" are
            // observationally identical, and the regression that folds every line into the
            // rolling average — saturating a ten-RUN window from one file, and narrowing the
            // confidence band to its floor off a single measurement — would pass unnoticed.
            progress?.Report(new FfmpegProgress(TimeSpan.FromSeconds(1), 4.0, IsFinal: false));
            progress?.Report(new FfmpegProgress(TimeSpan.FromSeconds(3), 4.0, IsFinal: false));
            progress?.Report(new FfmpegProgress(TimeSpan.FromSeconds(5), 4.0, IsFinal: true));
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref _current);
            return _behavior(arguments);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }
    }

    private sealed class HangingFfmpeg : IFfmpegRunner
    {
        public async Task<Result> RunAsync(IReadOnlyList<string> arguments,
            IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Result.Success();
        }
    }

    private sealed class FakeSpeedStore : ISpeedProfileStore
    {
        public SpeedProfile Profile { get; set; } = new();
        public int SaveCount { get; private set; }
        public SpeedProfile Load() => Profile;
        public void Save(SpeedProfile profile) { Profile = profile; SaveCount++; }
    }

    private MergeService Build(IFfmpegRunner ffmpeg, ISpeedProfileStore? store = null, long freeBytes = long.MaxValue,
        int maxConcurrency = 2)
        => new(ffmpeg, store ?? new FakeSpeedStore(), _ => freeBytes, _tempRoot, maxConcurrency,
            NullLogger<MergeService>.Instance);

    private static MergeRequest Request(params MergeClip[] clips)
        => new(clips, Target, Path.Combine(Path.GetTempPath(), "merged-" + Guid.NewGuid().ToString("N") + ".mp4"));

    private static IReadOnlyList<string> NormalizeCalls(FakeFfmpeg ffmpeg)
        => [.. ffmpeg.Invocations.Where(a => a.Contains("-vf")).Select(a => a[^1])];

    /// <summary><see cref="IReadOnlyList{T}"/> has no IndexOf.</summary>
    private static string ArgAfter(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }

        throw new InvalidOperationException($"'{flag}' not found in [{string.Join(' ', args)}]");
    }

    // ---------------------------------------------------------------- fast path

    [Fact]
    public async Task MergeAsync_NormalizesAClipCarryingASubtitleTrack_RatherThanFastPathingIt()
    {
        // The clip matches the target in every property the model carries, and differs only in
        // holding an extra stream. Stream-copying it into the concat is silent corruption: ffmpeg
        // matches segments by stream INDEX, lands the plain clip's audio on this clip's subtitle
        // slot, exits 0, and the merge "succeeds" with a mute second half. So it must be encoded.
        var withSubtitles = new MergeClip(
            "subbed.mp4", Info(Yuv1080p30, Aac) with { ExtraStreamCount = 1 });
        var ffmpeg = new FakeFfmpeg();
        var request = Request(withSubtitles, Conforming("plain.mp4"));

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);

        // Two invocations: one encode for the subbed clip, then the concat. Not a fast path.
        Assert.Equal(2, ffmpeg.Invocations.Count);
        var encode = ffmpeg.Invocations.First();
        Assert.Contains("-vf", encode);
        Assert.Contains("subbed.mp4", encode);

        // And the concat references the NORMALIZED intermediate for it, never the original.
        Assert.DoesNotContain("subbed.mp4", ffmpeg.ConcatListContent);
        Assert.Contains("plain.mp4", ffmpeg.ConcatListContent);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_FastPath_SkipsNormalizationEntirely()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(Conforming("a.mp4"), Conforming("b.mp4"));

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        var only = Assert.Single(ffmpeg.Invocations);

        // The one invocation is the stream-copy concat, argument for argument — no encode ran, so
        // NormalizeArgsBuilder was never reached.
        var listPath = ArgAfter(only, "-i");
        Assert.Equal(ConcatArgsBuilder.BuildArgs(listPath, request.OutputPath, MergeContainer.Mp4), only);

        // The conforming clips are concatenated from where they already sit.
        Assert.Equal(ConcatArgsBuilder.BuildListFile(["a.mp4", "b.mp4"]), ffmpeg.ConcatListContent);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_FastPath_DoesNotRecordTheStreamCopySpeedAsAnEncodeMeasurement()
    {
        var store = new FakeSpeedStore();
        var request = Request(Conforming("a.mp4"));

        var result = await Build(new FakeFfmpeg(), store).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, store.SaveCount);

        // Still the seeded 1080p/H.264 factor — a stream copy's speed=4.0x is not an encode speed.
        Assert.Equal(3.5, store.Profile.GetFactor(Target));
        File.Delete(request.OutputPath);
    }

    // ---------------------------------------------------------------- normalize phase

    [Fact]
    public async Task MergeAsync_NormalizesOnlyNonConformingClips()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(Conforming("a.mp4"), NonConforming("b.webm"), NonConforming("c.webm"));

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(3, ffmpeg.Invocations.Count); // 2 normalize + 1 concat
        Assert.Equal(2, ffmpeg.Invocations.Count(a => a.Contains("-vf")));
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_PassesNormalizeArgsBuilderOutputThroughVerbatim()
    {
        var ffmpeg = new FakeFfmpeg();
        var clip = NonConforming("b.webm");
        var request = Request(Conforming("a.mp4"), clip);

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        var encode = Assert.Single(ffmpeg.Invocations, a => a.Contains("-vf"));

        // The temp intermediate is named for the clip's index in the final order, in the target
        // container, inside the merge's own working directory under the temp root.
        var temp = encode[^1];
        Assert.Equal("0001.mp4", Path.GetFileName(temp));
        Assert.Equal(_tempRoot, Path.GetDirectoryName(Path.GetDirectoryName(temp)));

        Assert.Equal(NormalizeArgsBuilder.Build(clip.SourcePath, clip.Info, Target, temp), encode);
    }

    [Fact]
    public async Task MergeAsync_ConcatListMapsNormalizedClipsToTempsAndConformingClipsToOriginals()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(NonConforming("a.webm"), Conforming("b.mp4"), NonConforming("c.webm"));

        var result = await Build(ffmpeg, maxConcurrency: 3).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        var temps = NormalizeCalls(ffmpeg).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(2, temps.Count);
        Assert.Equal(new[] { "0000.mp4", "0002.mp4" }, temps.Select(p => Path.GetFileName(p)!).ToArray());

        // Source order is preserved: temp(0), original b.mp4, temp(2).
        Assert.Equal(ConcatArgsBuilder.BuildListFile([temps[0], "b.mp4", temps[1]]), ffmpeg.ConcatListContent);
        File.Delete(request.OutputPath);
    }

    /// <summary>The engine must decide "needs re-encoding" by asking <see cref="ConformanceCheck"/>
    /// and nothing else. <see cref="MergeEstimator"/> already does, so a bespoke predicate here would
    /// have the ETA, the fast-path promise and the disk reservation describe a different plan than
    /// the one that actually runs. Each case below is a clip a narrower rule (e.g. "is it H.264?")
    /// would wrongly wave through into the stream copy, corrupting the merge.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task MergeAsync_DecidesConformanceWithConformanceCheck(int nearMiss)
    {
        var clip = new MergeClip("almost.mp4", NearMisses[nearMiss]);

        // Pin the premise: ConformanceCheck rejects this clip, so the engine must normalize it.
        Assert.False(ConformanceCheck.Evaluate(clip.Info, Target).IsConforming);

        var ffmpeg = new FakeFfmpeg();
        var request = Request(clip);

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, ffmpeg.Invocations.Count); // normalize + concat, not concat alone

        var encode = Assert.Single(ffmpeg.Invocations, a => a.Contains("-vf"));
        Assert.Equal(NormalizeArgsBuilder.Build(clip.SourcePath, clip.Info, Target, encode[^1]), encode);

        // ...and the concat consumes the temp intermediate, never the original.
        Assert.Equal(ConcatArgsBuilder.BuildListFile([encode[^1]]), ffmpeg.ConcatListContent);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_RespectsConcurrencyCap()
    {
        var ffmpeg = new FakeFfmpeg();
        var clips = Enumerable.Range(0, 6).Select(i => NonConforming($"c{i}.webm")).ToArray();
        var request = Request(clips);

        var result = await Build(ffmpeg, maxConcurrency: 2).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(ffmpeg.MaxObservedConcurrency <= 2, $"saw {ffmpeg.MaxObservedConcurrency}");
        Assert.True(ffmpeg.MaxObservedConcurrency >= 2, "the cap was never actually reached");
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_SingleNonConformingClip_StillNormalizesAndConcats()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(NonConforming("only.webm"));

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, ffmpeg.Invocations.Count);
        Assert.Equal(ConcatArgsBuilder.BuildListFile([NormalizeCalls(ffmpeg)[0]]), ffmpeg.ConcatListContent);
        File.Delete(request.OutputPath);
    }

    // ---------------------------------------------------------------- preflight

    [Fact]
    public async Task MergeAsync_FailsPreflight_WhenDiskIsFull()
    {
        var ffmpeg = new FakeFfmpeg();

        var result = await Build(ffmpeg, freeBytes: 1).MergeAsync(Request(NonConforming("a.webm", seconds: 600)));

        Assert.False(result.IsSuccess);
        Assert.Contains("disk space", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ffmpeg.Invocations); // nothing ran
    }

    /// <summary>The regression test for the disk guard: <see cref="MergeEstimate.TempBytesEstimate"/>
    /// counts only the re-encoded clips and is legitimately 0 on the fast path, so a guard fed that
    /// figure alone would reserve nothing for a two-hour all-conforming merge and die half-written.</summary>
    [Fact]
    public async Task MergeAsync_DiskGuard_ReservesTheOutputFile_EvenWhenNothingIsReencoded()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(Conforming("a.mp4", seconds: 7200)); // ~4.33 GiB of output, 0 bytes of temp

        // Exactly one byte short of the required output size + the guard's 20% margin.
        var required = (7200L * Bps / 8) * 6 / 5;
        var result = await Build(ffmpeg, freeBytes: required - 1).MergeAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Contains("disk space", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ffmpeg.Invocations);
    }

    [Fact]
    public async Task MergeAsync_DiskGuard_DoesNotOverReserve()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(Conforming("a.mp4", seconds: 7200));

        var required = (7200L * Bps / 8) * 6 / 5;
        var result = await Build(ffmpeg, freeBytes: required).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_RejectsEmptyClipList()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = new MergeRequest([], Target, "out.mp4");

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Add at least one clip to merge.", result.Error);
        Assert.Empty(ffmpeg.Invocations);
    }

    // ---------------------------------------------------------------- failure

    [Fact]
    public async Task MergeAsync_FailsWhenAClipFailsToNormalize()
    {
        var ffmpeg = new FakeFfmpeg(args => args.Contains("-vf") ? Result.Failure("codec explosion") : Result.Success());

        var result = await Build(ffmpeg).MergeAsync(Request(NonConforming("bad.webm")));

        Assert.False(result.IsSuccess);
        Assert.Equal("Could not standardize 'bad.webm': codec explosion", result.Error);
        Assert.DoesNotContain(ffmpeg.Invocations, a => a.Contains("concat")); // never concatenated
    }

    /// <summary>A failed clip must cancel its still-queued siblings rather than let the machine grind
    /// through hours of encoding for a merge that is already doomed.</summary>
    [Fact]
    public async Task MergeAsync_CancelsRemainingEncodes_WhenOneClipFails()
    {
        var ffmpeg = new FakeFfmpeg(args => args.Contains("-vf") ? Result.Failure("nope") : Result.Success());
        var request = Request(NonConforming("a.webm"), NonConforming("b.webm"), NonConforming("c.webm"));

        var result = await Build(ffmpeg, maxConcurrency: 1).MergeAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("Could not standardize 'a.webm': nope", result.Error);
        Assert.Single(ffmpeg.Invocations); // b and c never started
    }

    [Fact]
    public async Task MergeAsync_FailsWhenTheConcatFails()
    {
        var ffmpeg = new FakeFfmpeg(args => args.Contains("concat") ? Result.Failure("muxer said no") : Result.Success());

        var result = await Build(ffmpeg).MergeAsync(Request(Conforming("a.mp4")));

        Assert.False(result.IsSuccess);
        Assert.Equal("muxer said no", result.Error);
    }

    // ---------------------------------------------------------------- temp cleanup

    [Fact]
    public async Task MergeAsync_CleansTempDirectory_OnSuccess()
    {
        var request = Request(NonConforming("a.webm"));

        var result = await Build(new FakeFfmpeg()).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.GetFileSystemEntries(_tempRoot) : []);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_CleansTempDirectory_OnFailure()
    {
        var ffmpeg = new FakeFfmpeg(args => args.Contains("-vf") ? Result.Failure("nope") : Result.Success());

        await Build(ffmpeg).MergeAsync(Request(NonConforming("a.webm")));

        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.GetFileSystemEntries(_tempRoot) : []);
    }

    [Fact]
    public async Task MergeAsync_CleansTempDirectory_OnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var task = Build(new HangingFfmpeg()).MergeAsync(Request(NonConforming("a.webm")), null, cts.Token);
        await cts.CancelAsync();

        var result = await task;

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.GetFileSystemEntries(_tempRoot) : []);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task MergeAsync_ReportsCanceled_RatherThanThrowing()
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<MergeProgress>();
        var task = Build(new HangingFfmpeg()).MergeAsync(
            Request(NonConforming("a.webm")), new SyncProgress<MergeProgress>(seen.Add), cts.Token);
        await cts.CancelAsync();

        var result = await task;

        Assert.False(result.IsSuccess);
        Assert.Equal("Merge canceled.", result.Error);
        Assert.Equal(MergeJobStatus.Canceled, seen[^1].Status);
    }

    /// <summary>Cancelling a merge whose encodes have already failed must still read as canceled: the
    /// user's own cancel is why the run stopped, and a "Failed" verdict would blame ffmpeg.</summary>
    [Fact]
    public async Task MergeAsync_PrefersCanceled_OverAConcurrentEncodeFailure()
    {
        using var cts = new CancellationTokenSource();
        var ffmpeg = new FakeFfmpeg(args =>
        {
            cts.Cancel();
            return Result.Failure("nope");
        });

        var result = await Build(ffmpeg, maxConcurrency: 1)
            .MergeAsync(Request(NonConforming("a.webm"), NonConforming("b.webm")), null, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("Merge canceled.", result.Error);
    }

    // ---------------------------------------------------------------- progress

    [Fact]
    public async Task MergeAsync_ProgressIsMonotonicAndEndsAt100()
    {
        var seen = new List<MergeProgress>();

        // 5 s + 15 s of encoding (a 1:3 split of the 95-point encode segment) + a conforming clip.
        var request = Request(NonConforming("a.webm", 5), NonConforming("b.webm", 15), Conforming("c.mp4", 5));

        var result = await Build(new FakeFfmpeg(), maxConcurrency: 1)
            .MergeAsync(request, new SyncProgress<MergeProgress>(seen.Add));

        Assert.True(result.IsSuccess, result.Error);
        Assert.NotEmpty(seen);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].OverallPercent >= seen[i - 1].OverallPercent,
                $"progress went backwards: {seen[i - 1].OverallPercent} → {seen[i].OverallPercent}");
        }

        // Encode progress is weighted by clip duration, not by clip count: finishing the 5 s clip is
        // a quarter of the encode work (95 x 0.25 = 23.75), and the fake's mid-encode report of
        // 5 s into the 15 s clip is another third of the remaining three quarters (95 x 0.5).
        Assert.Contains(seen, p => p.Status == MergeJobStatus.Normalizing && Near(p.OverallPercent, 23.75));
        Assert.Contains(seen, p => p.Status == MergeJobStatus.Normalizing && Near(p.OverallPercent, 47.5));
        Assert.Equal(95, seen.Where(p => p.Status == MergeJobStatus.Normalizing).Max(p => p.OverallPercent), 6);
        Assert.All(seen.Where(p => p.Status == MergeJobStatus.Concatenating), p => Assert.True(p.OverallPercent >= 95));

        Assert.Equal(100, seen[^1].OverallPercent, 3);
        Assert.Equal(MergeJobStatus.Completed, seen[^1].Status);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_ProgressIsMonotonic_EvenWithClipsFinishingOutOfOrder()
    {
        var seen = new List<MergeProgress>();
        var clips = new[]
        {
            NonConforming("a.webm", 60), NonConforming("b.webm", 5), NonConforming("c.webm", 30),
            NonConforming("d.webm", 5), NonConforming("e.webm", 90),
        };
        var request = Request(clips);

        var result = await Build(new FakeFfmpeg(), maxConcurrency: 4)
            .MergeAsync(request, new SyncProgress<MergeProgress>(seen.Add));

        Assert.True(result.IsSuccess, result.Error);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].OverallPercent >= seen[i - 1].OverallPercent,
                $"progress went backwards: {seen[i - 1].OverallPercent} → {seen[i].OverallPercent}");
        }

        Assert.Equal(100, seen[^1].OverallPercent, 3);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_FastPath_GivesTheWholeBarToTheConcat()
    {
        var seen = new List<MergeProgress>();
        var request = Request(Conforming("a.mp4"));

        var result = await Build(new FakeFfmpeg()).MergeAsync(request, new SyncProgress<MergeProgress>(seen.Add));

        Assert.True(result.IsSuccess, result.Error);
        Assert.DoesNotContain(seen, p => p.Status == MergeJobStatus.Normalizing);
        Assert.Equal(0, seen[0].OverallPercent);
        Assert.Equal(MergeJobStatus.Concatenating, seen[0].Status);
        Assert.Equal(100, seen[^1].OverallPercent, 3);
        File.Delete(request.OutputPath);
    }

    // ---------------------------------------------------------------- speed profile

    [Fact]
    public async Task MergeAsync_RecordsMeasuredSpeed()
    {
        var store = new FakeSpeedStore();
        var request = Request(NonConforming("a.webm"));

        var result = await Build(new FakeFfmpeg(), store).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(4.0, store.Profile.GetFactor(Target)); // the fake reports speed=4.0x
        File.Delete(request.OutputPath);
    }

    /// <summary>One sample per encoded clip — not one per ffmpeg progress line, which would fill the
    /// ten-run rolling window from a single file and claim a confidence the profile has not earned.</summary>
    [Fact]
    public async Task MergeAsync_RecordsOneSpeedSamplePerClip_NotPerProgressLine()
    {
        var store = new FakeSpeedStore();
        var request = Request(NonConforming("a.webm"), NonConforming("b.webm"));

        var result = await Build(new FakeFfmpeg(), store).MergeAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, store.SaveCount); // persisted once, at the end
        Assert.Equal(2, store.Profile.Samples[SpeedProfile.KeyFor(Target)].Count);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_DoesNotRecordSpeed_WhenTheEncodeFailed()
    {
        var store = new FakeSpeedStore();
        var ffmpeg = new FakeFfmpeg(args => args.Contains("-vf") ? Result.Failure("nope") : Result.Success());

        var result = await Build(ffmpeg, store).MergeAsync(Request(NonConforming("a.webm")));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, store.SaveCount);
    }

    // ---------------------------------------------------------------- result

    [Fact]
    public async Task MergeAsync_ReturnsOutputPath()
    {
        var request = Request(Conforming("a.mp4"));

        var result = await Build(new FakeFfmpeg()).MergeAsync(request);

        Assert.Equal(request.OutputPath, result.Value);
        File.Delete(request.OutputPath);
    }

    /// <summary>Exact to within double rounding — the expected values below are computed, not
    /// eyeballed, so the tolerance is 1e-9 and not a fudge factor.</summary>
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
