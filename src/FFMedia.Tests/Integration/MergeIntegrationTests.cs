using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Integration;

/// <summary>The first end-to-end proof the merger actually merges. PR 1 shipped the engine with
/// none: its argv was validated against a real ffmpeg by hand, but no merge was ever run. These
/// tests synthesize real clips with ffmpeg's own <c>testsrc</c>, merge them through the real
/// <see cref="MergeService"/>, and then <b>probe the output</b> — because ffmpeg's concat demuxer
/// exits 0 even when it silently drops a segment, so the finished file's own duration is the only
/// evidence worth trusting.</summary>
[Trait("Category", "Integration")]
public class MergeIntegrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ffmedia-merge-it-" + Guid.NewGuid().ToString("N"));

    private readonly ProcessRunner _runner = new();
    private readonly IBinaryProvider _binaries =
        new BundledBinaryProvider(Path.Combine(AppContext.BaseDirectory, "assets", "binaries"));

    public MergeIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Collects progress reports synchronously. The BCL's <see cref="Progress{T}"/> posts to
    /// the captured SynchronizationContext, so under a headless test runner its reports arrive out of
    /// order (or after the await completes) — which would make any assertion about them a race.</summary>
    private sealed class CollectingProgress : IProgress<MergeProgress>
    {
        private readonly List<MergeProgress> _reports = [];

        public IReadOnlyList<MergeProgress> Reports
        {
            get { lock (_reports) { return [.. _reports]; } }
        }

        public void Report(MergeProgress value)
        {
            lock (_reports) { _reports.Add(value); }
        }
    }

    private MergeService NewService() => new(
        new FfmpegRunner(_runner, _binaries),
        new SpeedProfileStore(_dir, NullLogger<SpeedProfileStore>.Instance),
        _ => long.MaxValue,      // disk space is not what these tests are about
        _dir,
        maxConcurrency: 2,
        NullLogger<MergeService>.Instance,
        // The real composition root passes the analyzer as the output verifier, so these tests do
        // too: without it, a concat that silently truncated the output would still report success.
        new FfprobeMediaAnalyzer(_runner, _binaries));

    /// <summary>Synthesizes a clip with ffmpeg's own <c>testsrc</c>, at a chosen size and rate, so the
    /// merge has genuinely mismatched inputs to standardize.</summary>
    private async Task<string> MakeClipAsync(string name, string size, int fps, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var result = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffmpeg), [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc=size={size}:rate={fps}:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path,
        ]);

        Assert.Equal(0, result.ExitCode);
        return path;
    }

    private async Task<MergeClip> ProbeAsync(string path)
    {
        var analyzer = new FfprobeMediaAnalyzer(_runner, _binaries);
        var probe = await analyzer.AnalyzeAsync(path);
        Assert.True(probe.IsSuccess, probe.Error);
        return new MergeClip(path, probe.Value!);
    }

    private async Task<MediaInfo> AnalyzeAsync(string path)
    {
        var analyzer = new FfprobeMediaAnalyzer(_runner, _binaries);
        var probe = await analyzer.AnalyzeAsync(path);
        Assert.True(probe.IsSuccess, probe.Error);
        return probe.Value!;
    }

    [Fact]
    public async Task MergeAsync_JoinsThreeMismatchedClips_IntoOneStandardizedFile()
    {
        // Deliberately mismatched: different resolutions AND different frame rates, so every clip
        // has to be normalized. This is the slow path, end to end.
        var clips = new List<MergeClip>
        {
            await ProbeAsync(await MakeClipAsync("a.mp4", "1280x720", 30, 2)),
            await ProbeAsync(await MakeClipAsync("b.mp4", "1920x1080", 25, 2)),
            await ProbeAsync(await MakeClipAsync("c.mp4", "640x480", 60, 2)),
        };

        var target = MergeTargetDerivation.Derive([.. clips.Select(c => c.Info)]);
        Assert.All(clips, c => Assert.False(ConformanceCheck.Evaluate(c.Info, target).IsConforming));

        var output = Path.Combine(_dir, "merged.mp4");
        var progress = new CollectingProgress();

        var result = await NewService().MergeAsync(new MergeRequest(clips, target, output), progress);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(output, result.Value);
        Assert.True(File.Exists(output));

        // The real proof: probe the OUTPUT. Concat exits 0 even when it drops segments, so a merge
        // that reported success but produced a 4-second file would pass every other assertion here.
        var info = await AnalyzeAsync(output);
        Assert.Equal(6, info.Duration.TotalSeconds, tolerance: 0.5); // 3 x 2s, no transitions
        Assert.Equal(target.Width, info.Video!.Width);
        Assert.Equal(target.Height, info.Video.Height);
        Assert.Equal("h264", info.Video.CodecName);
        Assert.Equal("yuv420p", info.Video.PixelFormat);
        Assert.True(info.HasAudio);
        Assert.Equal(0, info.ExtraStreamCount);

        // Every clip needed encoding, so the normalize phase must actually have run.
        Assert.Contains(progress.Reports, r => r.Status == MergeJobStatus.Normalizing);
        Assert.Contains(progress.Reports, r => r.Status == MergeJobStatus.Concatenating);

        // The bar must reach the end, and must never retreat.
        Assert.Equal(100, progress.Reports[^1].OverallPercent, tolerance: 0.01);
        var percents = progress.Reports.Select(r => r.OverallPercent).ToList();
        Assert.Equal(percents.OrderBy(p => p), percents);

        // And no temp debris survived — MergeService sweeps its working directory on every exit path.
        Assert.Empty(Directory.GetDirectories(_dir, "merge-*"));
    }

    [Fact]
    public async Task MergeAsync_FastPath_StreamCopiesIdenticalClips_WithoutNormalizing()
    {
        // Same size, same rate, same codec -> every clip already conforms -> no re-encode at all.
        var clips = new List<MergeClip>
        {
            await ProbeAsync(await MakeClipAsync("x.mp4", "1280x720", 30, 2)),
            await ProbeAsync(await MakeClipAsync("y.mp4", "1280x720", 30, 2)),
        };

        var target = MergeTargetDerivation.Derive([.. clips.Select(c => c.Info)]);
        Assert.All(clips, c => Assert.True(ConformanceCheck.Evaluate(c.Info, target).IsConforming));

        var output = Path.Combine(_dir, "fast.mp4");
        var progress = new CollectingProgress();

        var result = await NewService().MergeAsync(new MergeRequest(clips, target, output), progress);

        Assert.True(result.IsSuccess, result.Error);

        var info = await AnalyzeAsync(output);
        Assert.Equal(4, info.Duration.TotalSeconds, tolerance: 0.5); // 2 x 2s
        Assert.True(info.HasAudio);

        // The fast path's whole promise: NOTHING was re-encoded. If the normalize phase ran at all,
        // the "already conforming clips merge in about a second" guarantee is broken — and the ETA,
        // the disk reservation and the progress weighting all described a plan that did not run.
        Assert.DoesNotContain(progress.Reports, r => r.Status == MergeJobStatus.Normalizing);
        Assert.Contains(progress.Reports, r => r.Status == MergeJobStatus.Concatenating);

        // A conforming clip has no encoding work, so it reads 100 from the very first report.
        Assert.All(progress.Reports, r => Assert.All(r.ClipPercents, p => Assert.Equal(100, p, tolerance: 0.01)));

        Assert.Empty(Directory.GetDirectories(_dir, "merge-*"));
    }

    [Fact]
    public async Task MergeAsync_WhenCanceled_ReportsCancellation_AndLeavesNoTempDebris()
    {
        // Long enough that the merge is still normalizing when we pull the plug.
        var clips = new List<MergeClip>
        {
            await ProbeAsync(await MakeClipAsync("long-a.mp4", "1920x1080", 30, 5)),
            await ProbeAsync(await MakeClipAsync("long-b.mp4", "1280x720", 25, 5)),
        };

        var target = MergeTargetDerivation.Derive([.. clips.Select(c => c.Info)]);
        var output = Path.Combine(_dir, "canceled.mp4");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var result = await NewService().MergeAsync(new MergeRequest(clips, target, output), progress: null, cts.Token);

        // Cancellation is an expected outcome, not an exception: the engine never throws for it.
        Assert.False(result.IsSuccess);

        // The point of the test: a canceled merge must not strand gigabytes of intermediates, and
        // must not leave a half-written output file that looks like a finished video.
        Assert.Empty(Directory.GetDirectories(_dir, "merge-*"));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task MergeAsync_ClampedTo720pTarget_ProducesAReal720pFile()
    {
        // A resolution ladder that yields something ffmpeg REJECTS — an odd height, a broken aspect —
        // is precisely the failure this feature would be embarrassed by. And the exit code is exactly
        // what cannot be trusted (concat exits 0 on a truncated merge), so PROBE the output.
        var clips = new List<MergeClip>
        {
            await ProbeAsync(await MakeClipAsync("a.mp4", "1920x1080", 30, 2)),
            await ProbeAsync(await MakeClipAsync("b.mp4", "1920x1080", 30, 2)),
        };

        var infos = clips.Select(c => c.Info).ToList();
        var bounds = TargetBounds.From(infos);
        Assert.Contains(new Resolution(1280, 720), bounds.Resolutions);

        var target = MergeTargetDerivation.Derive(infos).ClampTo(bounds) with { Width = 1280, Height = 720 };

        var output = Path.Combine(_dir, "merged720.mp4");
        var result = await NewService().MergeAsync(new MergeRequest(clips, target, output));

        Assert.True(result.IsSuccess, result.Error);

        // The real proof: probe the OUTPUT, not the exit code.
        var merged = await AnalyzeAsync(output);
        Assert.Equal(1280, merged.Video!.Width);
        Assert.Equal(720, merged.Video.Height);
        Assert.Equal(4, merged.Duration.TotalSeconds, tolerance: 0.5); // both clips are really there
    }
}
