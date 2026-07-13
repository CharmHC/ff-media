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

    /// <summary>A player that does NOT answer synchronously — the test decides exactly when (and
    /// whether) each <see cref="Open"/> call gets a response, which is the only way to reproduce
    /// FINDING 1's hang: it depends entirely on the ORDER a second load starts relative to the first's
    /// still-pending answer, and the real <c>MediaElement</c> answers asynchronously, off a message
    /// pump this test can't drive.</summary>
    private sealed class ManualPlayer : IMediaPlayer
    {
        public List<string> Opened { get; } = new();

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public event EventHandler? MediaOpened;

        public event EventHandler<string>? MediaFailed;

        public void Open(string path) => Opened.Add(path);

        /// <summary>Raises whatever answer is currently "in flight" from the player's own point of
        /// view. Just like the real <c>MediaElement</c>, this interface has no way to say WHICH Open
        /// call an answer belongs to — there is exactly one event stream.</summary>
        public void Answer(bool success)
        {
            if (success)
            {
                Duration = TimeSpan.FromSeconds(30);
                MediaOpened?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                MediaFailed?.Invoke(this, "Media file download failed.");
            }
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

    [Fact]
    public async Task LoadAsync_CalledAgainBeforeThePlayerAnswers_NeitherCallHangs()
    {
        // FINDING 1 (CRITICAL). The buggy code stored the pending open in ONE shared field, resolved
        // by handlers wired ONCE in the constructor — so whichever attempt the field CURRENTLY pointed
        // to is the one a late answer resolved, even when that answer belonged to an EARLIER,
        // superseded load. This VM is a SINGLETON and LoadAsync awaits real I/O (a probe, maybe a whole
        // transcode), so "drop clip A, then drop clip B before A's player has answered" is a directly
        // reachable path, not an edge case: under the bug, LoadAsync(A)'s task would never complete —
        // on the UI thread.
        //
        // ManualPlayer does NOT answer synchronously, so this test controls exactly when each Open()
        // gets a response — the only way to force the ordering that hangs the buggy code.
        var player = new ManualPlayer();
        var analyzer = new FakeAnalyzer();
        var proxies = new FakeProxies();
        var vm = new VideoPreviewViewModel(analyzer, proxies, player);

        var taskA = vm.LoadAsync(@"C:\a.mp4");
        Assert.Equal(new[] { @"C:\a.mp4" }, player.Opened); // A's Open landed; nobody has answered yet

        var taskB = vm.LoadAsync(@"C:\b.mp4");
        Assert.Equal(new[] { @"C:\a.mp4", @"C:\b.mp4" }, player.Opened);

        // Only ONE answer is even representable here — exactly like the real MediaElement, there is
        // one event stream, not one per Open() call.
        player.Answer(true);

        // Neither call may hang. WaitAsync means a regression FAILS (times out) instead of freezing
        // the whole suite forever.
        await taskA.WaitAsync(TimeSpan.FromSeconds(5));
        await taskB.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsReady); // B is what is actually on screen now
    }

    [Theory]
    [InlineData(10, 100)] // 10 fps -> 100 ms/frame
    [InlineData(50, 20)]  // 50 fps -> 20 ms/frame
    public async Task StepForward_AtADifferentFrameRate_StepsByExactlyOneOverFps(int fps, int stepMs)
    {
        // FINDING 2. Every OTHER fixture in this file runs at 25 fps, where 1/fps happens to be EXACTLY
        // the 40 ms a hardcoded "return 40ms" mutant also returns — so the existing 25 fps step test
        // cannot tell a correct implementation from that mutant. An fps that does NOT reduce to 40 ms
        // is the only way to prove the step is actually DERIVED from the source, not a constant.
        var (vm, player, analyzer, _) = Build();
        analyzer.Behavior = _ => Result<MediaInfo>.Success(Info(fps: fps));
        await vm.LoadAsync(@"C:\clip.mp4");
        player.Position = TimeSpan.FromSeconds(2);

        vm.StepForward();

        Assert.Equal(2 + stepMs / 1000.0, player.Position.TotalSeconds, 3);
    }

    [Fact]
    public async Task CaptureEnd_IsRefused_WhenTheHostHasFrozenIt()
    {
        // FINDING 3. Symmetric to Capture_IsRefused_WhenTheHostHasFrozenIt (CaptureSTART), which only
        // ever calls CaptureStart() directly — so CaptureEnd's OWN method-body guard could be deleted,
        // alone, and nothing in the suite would notice. That is exactly the asymmetric-guard bug M8
        // shipped TWICE. The guard must live in the METHOD, not only in CanExecute, because a gesture
        // that is not a command bypasses CanExecute entirely.
        var (vm, player, _, _) = Build();
        await vm.LoadAsync(@"C:\clip.mp4");
        player.Position = TimeSpan.FromSeconds(5);
        vm.CanCapture = false;

        var raised = false;
        vm.EndCaptured += (_, _) => raised = true;
        vm.CaptureEnd(); // called DIRECTLY, bypassing the command
        Assert.False(vm.CaptureEndCommand.CanExecute(null));

        Assert.False(raised);
    }
}
