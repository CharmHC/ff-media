using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Media.Preview;
using FFMedia.Ui.Playback;
using FFMedia.Ui.ViewModels;
using Xunit;

namespace FFMedia.Tests.Ui;

public class VideoPreviewViewModelTests
{
    private static MediaInfo Info(double seconds = 30, int fps = 25)
        => new(TimeSpan.FromSeconds(seconds), "matroska,webm",
            new VideoStreamInfo(1920, 1080, new FrameRate(fps, 1), "vp9", "yuv420p", 0), null);

    /// <summary>A player that can be told to reject a given path — which is the whole point: the real
    /// MediaElement rejects VP9/WebM, and the fallback is the entire design.</summary>
    private sealed class FakePlayer : IMediaPlayer
    {
        public List<string> Opened { get; } = new();

        /// <summary>Paths this player refuses, simulating MediaElement's codec limits.</summary>
        public HashSet<string> Unplayable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public event EventHandler? MediaOpened;

        public event EventHandler<string>? MediaFailed;

        public void Open(string path)
        {
            Opened.Add(path);
            if (Unplayable.Contains(path))
            {
                MediaFailed?.Invoke(this, "Media file download failed.");
                return;
            }

            Duration = TimeSpan.FromSeconds(30);
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;
    }

    private sealed class FakeAnalyzer : IMediaAnalyzer
    {
        public Func<string, Result<MediaInfo>> Behavior { get; set; } = _ => Result<MediaInfo>.Success(Info());

        public Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(Behavior(filePath));
    }

    private sealed class FakeProxies : IPreviewProxyService
    {
        public int Calls { get; private set; }

        public Func<string, Result<string>> Behavior { get; set; } = src => Result<string>.Success(src + ".proxy.mp4");

        public Task<Result<string>> GetOrCreateAsync(
            string sourcePath, MediaInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Behavior(sourcePath));
        }

        public void SweepStale() { }
    }

    private static (VideoPreviewViewModel Vm, FakePlayer Player, FakeAnalyzer Analyzer, FakeProxies Proxies) Build()
    {
        var player = new FakePlayer();
        var analyzer = new FakeAnalyzer();
        var proxies = new FakeProxies();

        return (new VideoPreviewViewModel(analyzer, proxies, player), player, analyzer, proxies);
    }

    [Fact]
    public async Task LoadAsync_APlayableSource_PlaysItDirectly_WithNoTranscode()
    {
        // THE FAST PATH. Most videos (MP4/MKV H.264) just play. Paying a transcode for them would be
        // pure waste -- this is the merger's conformance discipline: conforming input is left alone.
        var (vm, player, _, proxies) = Build();

        await vm.LoadAsync(@"C:\clip.mp4");

        Assert.Equal(new[] { @"C:\clip.mp4" }, player.Opened);
        Assert.Equal(0, proxies.Calls);
        Assert.False(vm.IsPreparingProxy);
    }

    [Fact]
    public async Task LoadAsync_WhenThePlayerRejectsTheSource_BuildsAProxy_AndPlaysTHAT()
    {
        // THE WHOLE DESIGN. MediaElement FAILS on VP9/WebM -- a format our own downloader produces -- so
        // without this the preview is simply blank for videos FFMedia itself made.
        var (vm, player, _, proxies) = Build();
        player.Unplayable.Add(@"C:\clip.webm");

        await vm.LoadAsync(@"C:\clip.webm");

        Assert.Equal(1, proxies.Calls);
        Assert.Equal(2, player.Opened.Count);
        Assert.Equal(@"C:\clip.webm", player.Opened[0]);          // tried the source first
        Assert.Equal(@"C:\clip.webm.proxy.mp4", player.Opened[1]); // then the proxy
    }

    [Fact]
    public async Task LoadAsync_WhenTheProxyAlsoFails_SaysSo_AndDoesNotPretendItIsPlaying()
    {
        // The preview is an AID, never a GATE: the tool must still be usable by typing a timecode.
        var (vm, player, _, proxies) = Build();
        player.Unplayable.Add(@"C:\clip.webm");
        proxies.Behavior = _ => Result<string>.Failure("ffmpeg exploded");

        await vm.LoadAsync(@"C:\clip.webm");

        Assert.False(vm.IsReady);
        Assert.Contains("preview", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsPreparingProxy);
    }

    [Fact]
    public async Task LoadAsync_AFailedProbe_ReportsTheANALYZERsOwnReason()
    {
        // NEVER a generic "not a video". That exact mistake blamed a user's perfectly good .mp4 for a
        // MISSING FFPROBE and sent them off to inspect their file (CLAUDE.md, M7).
        var (vm, _, analyzer, _) = Build();
        analyzer.Behavior = _ => Result<MediaInfo>.Failure("Could not run ffprobe: file not found.");

        await vm.LoadAsync(@"C:\clip.mp4");

        Assert.False(vm.IsReady);
        Assert.Contains("ffprobe", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureStart_RaisesTheCurrentPosition()
    {
        var (vm, player, _, _) = Build();
        await vm.LoadAsync(@"C:\clip.mp4");
        player.Position = TimeSpan.FromSeconds(83.45);

        TimeSpan? captured = null;
        vm.StartCaptured += (_, t) => captured = t;
        vm.CaptureStartCommand.Execute(null);

        Assert.Equal(TimeSpan.FromSeconds(83.45), captured);
    }

    [Fact]
    public async Task CaptureEnd_RaisesTheCurrentPosition()
    {
        var (vm, player, _, _) = Build();
        await vm.LoadAsync(@"C:\clip.mp4");
        player.Position = TimeSpan.FromSeconds(12.5);

        TimeSpan? captured = null;
        vm.EndCaptured += (_, t) => captured = t;
        vm.CaptureEndCommand.Execute(null);

        Assert.Equal(TimeSpan.FromSeconds(12.5), captured);
    }

    [Fact]
    public async Task Capture_IsRefused_WhenTheHostHasFrozenIt()
    {
        // A GIF render holds a SNAPSHOT of the request. A page that can still mutate Start/End describes
        // a job that is NOT the one running -- the bug M8 shipped twice. And the guard must live in the
        // METHOD, not only in CanExecute, because A GESTURE THAT IS NOT A COMMAND BYPASSES CanExecute.
        var (vm, player, _, _) = Build();
        await vm.LoadAsync(@"C:\clip.mp4");
        player.Position = TimeSpan.FromSeconds(5);
        vm.CanCapture = false;

        var raised = false;
        vm.StartCaptured += (_, _) => raised = true;
        vm.CaptureStart();                       // called DIRECTLY, bypassing the command
        Assert.False(vm.CaptureStartCommand.CanExecute(null));

        Assert.False(raised);
    }

    [Fact]
    public async Task StepForward_PausesAndAdvancesExactlyOneFrame()
    {
        // "One frame" is 1/fps of the SOURCE -- a 25 fps video steps 40 ms. Stepping a fixed 100 ms would
        // skip frames on a fast video and stall on a slow one.
        var (vm, player, _, _) = Build();
        await vm.LoadAsync(@"C:\clip.mp4");    // 25 fps
        player.Position = TimeSpan.FromSeconds(2);
        vm.Play();

        vm.StepForward();

        Assert.False(player.IsPlaying);        // stepping implies pausing
        Assert.Equal(2.04, player.Position.TotalSeconds, 3);
    }

    [Fact]
    public async Task StepBack_NeverGoesBeforeTheStartOfTheVideo()
    {
        var (vm, player, _, _) = Build();
        await vm.LoadAsync(@"C:\clip.mp4");
        player.Position = TimeSpan.FromMilliseconds(10);

        vm.StepBack();

        Assert.True(player.Position >= TimeSpan.Zero);
    }

    [Fact]
    public async Task LoadingASecondVideo_ReplacesTheFirst_RatherThanStackingOnIt()
    {
        // The VM is long-lived (the GIF Maker's VM is a SINGLETON so state survives navigation), so a
        // second load must fully re-initialise. M8 shipped an NRE on exactly this "load a second video"
        // path because nothing ever tested it.
        var (vm, player, _, _) = Build();
        await vm.LoadAsync(@"C:\first.mp4");

        await vm.LoadAsync(@"C:\second.mp4");

        Assert.Equal(@"C:\second.mp4", player.Opened[^1]);
        Assert.True(vm.IsReady);
    }
}
