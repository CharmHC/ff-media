using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Tests.Views;
using FFMedia.Ui.Playback;
using Xunit;

namespace FFMedia.Tests.Integration;

/// <summary>Drives a REAL <see cref="MediaElement"/> against a REAL video file, on a REAL message loop.
///
/// <para><b>Why this file exists.</b> The entire preview shipped BROKEN — a black rectangle with every
/// control dead — and not one of the 822 tests noticed, because every one of them drives the VM through a
/// FAKE <c>IMediaPlayer</c> that raises <c>MediaOpened</c> synchronously, and the real-player tests only
/// ever asserted that <c>Source</c> was assigned. Nobody ever asked the actual question: <b>does a real
/// MediaElement, configured the way we configure it, actually OPEN?</b> It did not.</para>
///
/// <para>It was assumed this could not be tested headlessly. It can: <see cref="WpfHost"/> already runs a
/// real <c>Dispatcher.Run()</c> loop on an STA thread, which is all Media Foundation needs. The cost of
/// that assumption was the whole feature.</para></summary>
[Trait("Category", "Integration")]
[Collection("wpf")]
public class MediaElementPlayerIntegrationTests : IDisposable
{
    private readonly WpfHost _wpf;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ffmedia-player-it-" + Guid.NewGuid().ToString("N"));

    private readonly ProcessRunner _runner = new();
    private readonly IBinaryProvider _binaries =
        new BundledBinaryProvider(Path.Combine(AppContext.BaseDirectory, "assets", "binaries"));

    public MediaElementPlayerIntegrationTests(WpfHost wpf)
    {
        _wpf = wpf;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private async Task<string> MakeH264ClipAsync(int seconds)
    {
        var path = Path.Combine(_dir, "src.mp4");
        var result = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffmpeg), [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=25:duration={seconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", path,
        ]);

        Assert.Equal(0, result.ExitCode);
        return path;
    }

    private async Task<string> MakeVp9ClipAsync(int seconds)
    {
        var path = Path.Combine(_dir, "src.webm");
        var result = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffmpeg), [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=25:duration={seconds}",
            "-c:v", "libvpx-vp9", "-b:v", "200k", path,
        ]);

        Assert.Equal(0, result.ExitCode);
        return path;
    }

    /// <summary>Waits for the player to answer, or for the timeout to prove it never will. Returns
    /// <c>true</c> = MediaOpened, <c>false</c> = MediaFailed, <c>null</c> = it never answered at all,
    /// which is the failure mode this whole file exists to catch (LoadAsync awaits exactly one of those
    /// two, so silence there is a permanent hang, not a slow load).</summary>
    private async Task<bool?> OpenAndAwaitAnswerAsync(string path)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? window = null;

        var error = _wpf.Run(() =>
        {
            var element = new MediaElement();
            window = new Window { Width = 320, Height = 240, ShowInTaskbar = false, Content = element };

            var player = new MediaElementPlayer();
            player.MediaOpened += (_, _) => answer.TrySetResult(true);
            player.MediaFailed += (_, _) => answer.TrySetResult(false);

            player.Attach(element);
            window.Show();
            player.Open(path);
        });

        Assert.True(error is null, $"Opening the player threw:\n{error}");

        var settled = await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        _wpf.Run(() => window!.Close());

        return ReferenceEquals(settled, answer.Task) ? await answer.Task : null;
    }

    /// <summary>The PREMISE OF THE ENTIRE MILESTONE, pinned against a real file for the first time: a
    /// VP9/WebM — <b>a format our own downloader produces</b> — must be REFUSED by the player, promptly and
    /// explicitly, so that <c>LoadAsync</c> can fall back to building an H.264 proxy.
    ///
    /// <para>A refusal is not a failure here: it is the signal the fallback is built on. Before the Open()
    /// fix this could not happen at all — the element never began loading, so it never got as far as
    /// deciding it could not decode VP9, and <c>MediaFailed</c> was as unreachable as <c>MediaOpened</c>.
    /// The proxy path — the reason M9 exists — was dead code in production, with every proxy unit test
    /// green because they all drive a FAKE player that refuses on cue.</para></summary>
    [Fact]
    public async Task Open_RefusesAVp9Webm_SoTheProxyFallbackCanActuallyTrigger()
    {
        var answer = await OpenAndAwaitAnswerAsync(await MakeVp9ClipAsync(seconds: 2));

        Assert.False(
            answer is null,
            "The player never answered AT ALL for a VP9/WebM. LoadAsync awaits MediaOpened or MediaFailed, " +
            "so it hangs forever and the proxy fallback -- the entire reason this milestone exists -- can " +
            "never trigger. A WebM must be REFUSED, not met with silence.");

        Assert.False(
            answer!.Value,
            "MediaElement reported it OPENED a VP9/WebM. The whole fallback design rests on it refusing " +
            "VP9 (verified at design time against real files). If Windows can now decode VP9, the proxy " +
            "is dead weight and this design should be revisited -- but do not assume it; re-verify.");
    }

    /// <summary>THE BUG THIS FILE WAS BORN FOR. <c>Attach</c> sets <c>LoadedBehavior = Manual</c> — which is
    /// load-bearing, since without it the element autoplays and ignores every <c>Play</c>/<c>Pause</c>/seek
    /// the transport issues. But <b>a MediaElement in Manual mode does not begin loading its media when you
    /// merely assign <c>Source</c>.</b> It waits to be told to do something. Measured, against this exact
    /// file: Source alone → <c>MediaOpened</c> NEVER fires; Source + <c>Pause()</c> → opens in ~440 ms.
    ///
    /// <para>So <c>Open()</c> assigned <c>Source</c> and returned, and <c>VideoPreviewViewModel.LoadAsync</c>
    /// sat awaiting a <c>MediaOpened</c>/<c>MediaFailed</c> that <b>could never arrive</b>: <c>IsReady</c>
    /// stayed false, so the slider, play/pause and both capture buttons stayed disabled over a black
    /// rectangle. And because <c>MediaFailed</c> could not arrive either, <b>the VP9/WebM proxy fallback —
    /// the entire reason M9 exists — was unreachable too.</b> A WebM would hang exactly like an MP4.</para></summary>
    [Fact]
    public async Task Open_ActuallyOpensTheMedia_SoTheVideoIsNotJustABlackRectangle()
    {
        var path = await MakeH264ClipAsync(seconds: 3);

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? window = null;
        MediaElementPlayer? player = null;

        var error = _wpf.Run(() =>
        {
            var element = new MediaElement();
            window = new Window { Width = 320, Height = 240, ShowInTaskbar = false, Content = element };

            player = new MediaElementPlayer();
            player.MediaOpened += (_, _) => answer.TrySetResult(true);
            player.MediaFailed += (_, _) => answer.TrySetResult(false);

            player.Attach(element);
            window.Show();
            player.Open(path);
        });

        Assert.True(error is null, $"Opening the player threw:\n{error}");

        // A real decode, so give it real time -- but time-boxed, because the bug being pinned here is
        // precisely an await that NEVER completes. Without the timeout a regression HANGS the suite
        // instead of failing it.
        var settled = await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromSeconds(15)));

        TimeSpan? duration = null;
        _wpf.Run(() =>
        {
            duration = player!.Duration;
            window!.Close();
        });

        Assert.True(
            ReferenceEquals(settled, answer.Task),
            "The player never raised MediaOpened OR MediaFailed for a perfectly good H.264 MP4. " +
            "LoadAsync awaits exactly one of those, so it hangs forever: the preview stays black and " +
            "every control stays disabled -- and the proxy fallback can never trigger either.");

        Assert.True(await answer.Task, "A real H.264 MP4 raised MediaFailed.");

        // It really DECODED it -- not merely answered. A player that answers but has no duration would
        // still leave the slider and the readout with nothing to show.
        Assert.NotNull(duration);
        Assert.Equal(3.0, duration!.Value.TotalSeconds, 1);
    }
}
