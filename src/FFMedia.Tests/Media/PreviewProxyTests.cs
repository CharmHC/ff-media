using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Media.Preview;
using Xunit;

namespace FFMedia.Tests.Media;

public class PreviewProxyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ffmedia-proxy-tests-" + Guid.NewGuid().ToString("N"));

    public PreviewProxyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static MediaInfo Info(int w = 1920, int h = 1080, bool audio = true)
        => new(TimeSpan.FromSeconds(30), "matroska,webm",
            new VideoStreamInfo(w, h, new FrameRate(30, 1), "vp9", "yuv420p", 0),
            audio ? new AudioStreamInfo("opus", 48000, 2) : null);

    private sealed class FakeFfmpeg : IFfmpegRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Func<Result> Behavior { get; set; } = Result.Success;

        public int OutputBytes { get; set; } = 2048;

        public Task<Result> RunAsync(
            IReadOnlyList<string> arguments, IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(arguments);
            var result = Behavior();
            if (result.IsSuccess && OutputBytes > 0)
            {
                File.WriteAllBytes(arguments[^1], new byte[OutputBytes]);
            }

            return Task.FromResult(result);
        }
    }

    // ---------- PreviewProxyArgs (pure) ----------

    [Fact]
    public void Args_NeverRetimeTheSource()
    {
        // THE ONE HARD RULE. The captured timestamp is read from the PLAYER's position, so if the proxy's
        // timeline differed from the source's by even a little, EVERY captured time would be a lie and the
        // GIF would be cut somewhere other than where the user saw. Rescale only. Never re-time.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");

        Assert.DoesNotContain("-r", args);        // no frame-rate change
        Assert.DoesNotContain("-ss", args);       // no seek
        Assert.DoesNotContain("-t", args);        // no duration cap
        Assert.DoesNotContain("-to", args);
        Assert.DoesNotContain("-vsync", args);
    }

    [Fact]
    public void Args_ProduceAFormatMediaElementCanActuallyPlay()
    {
        // MediaElement renders through Windows Media Foundation. VERIFIED: it plays H.264 and FAILS on
        // VP9. The proxy exists precisely to hand it something it can open.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");
        var joined = string.Join(" ", args);

        Assert.Contains("libx264", joined, StringComparison.Ordinal);
        Assert.Contains("yuv420p", joined, StringComparison.Ordinal);
        Assert.Equal(@"C:\tmp\p.mp4", args[^1]);
    }

    [Fact]
    public void Args_EscapeTheCommaInsideTheScaleExpression()
    {
        // A BARE comma inside min(640,iw) would SPLIT THE FILTERGRAPH -- ffmpeg separates filters with
        // commas -- and the whole -vf argument becomes garbage. It must be escaped.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Contains(@"\,", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("min(640,iw)", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void Args_CapTheWidthButNeverUpscaleATinySource()
    {
        // Upscaling a 320px source to 640 invents pixels, costs encode time, and buys nothing.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Contains("min(640", vf, StringComparison.Ordinal);   // a cap, not a target
        Assert.Contains(":h=-2", vf, StringComparison.Ordinal);     // height derived, and forced EVEN
    }

    [Fact]
    public void Args_DropAudioWhenTheSourceHasNone()
    {
        // Asking ffmpeg to encode an audio stream that does not exist fails the whole run.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(audio: false), @"C:\tmp\p.mp4");

        Assert.Contains("-an", args);
    }

    [Fact]
    public void Args_KeepAudioWhenTheSourceHasIt()
    {
        // The user is scrubbing to FIND a moment, and sound is often how a human finds it.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(audio: true), @"C:\tmp\p.mp4");

        Assert.DoesNotContain("-an", args);
        Assert.Contains("aac", string.Join(" ", args), StringComparison.Ordinal);
    }

    [Fact]
    public void Args_DoNotRepeatTheFlagsTheRunnerAlreadyAdds()
    {
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");

        Assert.DoesNotContain("-y", args);
        Assert.DoesNotContain("-progress", args);
        Assert.DoesNotContain("-hide_banner", args);
    }

    // ---------- PreviewProxyPath (pure) ----------

    [Fact]
    public void Path_ChangesWhenTheSourceChangesOnDisk()
    {
        // A cache keyed on the PATH ALONE would serve a stale proxy of a file the user has since
        // re-encoded or replaced -- they would scrub the OLD video and capture times into the NEW one.
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(file, new byte[100]);
        var before = PreviewProxyPath.For(file, _dir);

        File.WriteAllBytes(file, new byte[200]);            // same path, different content
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        var after = PreviewProxyPath.For(file, _dir);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Path_IsStableForAnUnchangedSource()
    {
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(file, new byte[100]);

        Assert.Equal(PreviewProxyPath.For(file, _dir), PreviewProxyPath.For(file, _dir));
    }

    // ---------- PreviewProxyService ----------

    private (PreviewProxyService Service, FakeFfmpeg Ffmpeg, string Source) Build()
    {
        var ffmpeg = new FakeFfmpeg();
        var source = Path.Combine(_dir, "src.webm");
        File.WriteAllBytes(source, new byte[128]);

        return (new PreviewProxyService(ffmpeg, _dir), ffmpeg, source);
    }

    [Fact]
    public async Task GetOrCreateAsync_BuildsAProxy_AndReturnsItsPath()
    {
        var (service, ffmpeg, source) = Build();

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(File.Exists(result.Value!));
        Assert.Single(ffmpeg.Calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReusesACachedProxy_RatherThanTranscodingTwice()
    {
        // Re-opening the same video must not pay the transcode again.
        var (service, ffmpeg, source) = Build();

        var first = await service.GetOrCreateAsync(source, Info());
        var second = await service.GetOrCreateAsync(source, Info());

        Assert.Equal(first.Value, second.Value);
        Assert.Single(ffmpeg.Calls);   // still ONE -- the second call was served from cache
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenFfmpegFails_ReportsFailure_AndLeavesNoHalfWrittenProxy()
    {
        var (service, ffmpeg, source) = Build();
        ffmpeg.Behavior = () => Result.Failure("Error while opening encoder");
        ffmpeg.OutputBytes = 512;   // ffmpeg wrote something before dying

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenTheProxyIsEmpty_ItFails_RatherThanCachingRubbish()
    {
        // A zero-byte "success" cached forever would poison every future open of this video.
        var (service, ffmpeg, source) = Build();
        ffmpeg.OutputBytes = 0;

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCancelled_LeavesNoHalfWrittenProxy()
    {
        var (service, ffmpeg, source) = Build();
        using var cts = new CancellationTokenSource();
        ffmpeg.Behavior = () => { cts.Cancel(); return Result.Success(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetOrCreateAsync(source, Info(), progress: null, cts.Token));

        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }
}
