using System;
using System.Windows;
using System.Windows.Controls;
using FFMedia.Tests.Views;
using FFMedia.Ui.Playback;
using Xunit;

namespace FFMedia.Tests.Ui;

/// <summary>Pins the two flags <see cref="MediaElementPlayer.Attach"/> sets on the real
/// <see cref="MediaElement"/> — neither was asserted anywhere before this.
///
/// <para><b>FINDING 3.</b> Without <c>ScrubbingEnabled</c>, setting <c>Position</c> while paused does
/// NOT render the new frame — so the user would capture a timestamp for a frame they never saw, and the
/// preview's entire reason for existing ("pause on the frame you want, then capture") is quietly wrong
/// while every other test in the suite stays green. <c>LoadedBehavior == Manual</c> is equally
/// load-bearing: without it the element auto-plays and ignores every <c>Play()</c>/<c>Pause()</c>/seek
/// this control issues.</para>
///
/// <para>Needs a real <see cref="MediaElement"/>, hence an STA thread — the shared <see cref="WpfHost"/>,
/// same as every other WPF-hosted test in this project.</para></summary>
[Collection("wpf")]
public class MediaElementPlayerTests
{
    private readonly WpfHost _wpf;

    public MediaElementPlayerTests(WpfHost wpf) => _wpf = wpf;

    [Fact]
    public void Attach_EnablesScrubbing_AndSetsManualLoadedBehavior()
    {
        // The MediaElement is owned by the STA thread that created it -- every read of it (not just the
        // construction) must happen on THAT thread, so the two flags under test are read inside the Run
        // lambda rather than smuggled out through a captured reference.
        bool scrubbingEnabled = false;
        MediaState loadedBehavior = MediaState.Play;

        var error = _wpf.Run(() =>
        {
            var element = new MediaElement();
            var player = new MediaElementPlayer();
            player.Attach(element);

            scrubbingEnabled = element.ScrubbingEnabled;
            loadedBehavior = element.LoadedBehavior;
        });

        Assert.True(error is null, $"Attach threw:\n{error}");
        Assert.True(
            scrubbingEnabled,
            "ScrubbingEnabled must be true -- without it, a paused seek does not render the new frame, " +
            "and the user captures a timestamp for a frame they never saw.");
        Assert.Equal(MediaState.Manual, loadedBehavior);
    }

    /// <summary>FINDING (Task 5 review, CRITICAL 1). The player is a DI <b>singleton</b> and
    /// <c>VideoPreview</c> is <b>transient</b> — so <see cref="MediaElementPlayer.Attach"/> runs again on
    /// the SAME player every time the user navigates back to the GIF Maker, handing it a brand-new,
    /// source-less <see cref="MediaElement"/>. The source only ever arrives through <c>Open()</c> inside
    /// <c>LoadAsync</c>, and nothing calls <c>LoadAsync</c> again on a revisit — so the preview went black
    /// while the (also singleton) ViewModel still reported <c>IsReady</c>, leaving the capture buttons
    /// armed over a dead player. <c>CaptureStart()</c> then read <c>Position</c> from the empty element
    /// and wrote <b>0:00</b> into Start, with no complaint. The source must survive a re-attach.</summary>
    [Fact]
    public void Reattach_ReplaysTheSourceOntoTheNewElement_SoAPageRevisitDoesNotBlankThePreview()
    {
        Uri? secondSource = null;

        var error = _wpf.Run(() =>
        {
            var player = new MediaElementPlayer();
            player.Attach(new MediaElement());
            player.Open(@"C:\video.mp4");

            // The user navigates away and back: a fresh VideoPreview builds a fresh MediaElement and
            // attaches it to the very same singleton player.
            var second = new MediaElement();
            player.Attach(second);

            secondSource = second.Source;
        });

        Assert.True(error is null, $"Re-attaching threw:\n{error}");
        Assert.Equal(new Uri(@"C:\video.mp4"), secondSource);
    }

    /// <summary>The OLD element's handlers must be unhooked before it is discarded. They fire into the
    /// singleton's own <c>MediaOpened</c>/<c>MediaFailed</c> events, which <c>VideoPreviewViewModel</c>
    /// subscribes to for the lifetime of ONE load attempt — so a phantom event raised by an abandoned
    /// element could settle a load attempt that belongs to a completely different element.
    ///
    /// <para>Only <c>MediaOpened</c> is raised here: <c>MediaFailed</c> carries
    /// <c>ExceptionRoutedEventArgs</c>, which has no public constructor, so it cannot be synthesized from
    /// a test at all. It is unhooked on the same line as this one.</para></summary>
    [Fact]
    public void Reattach_UnhooksTheOldElement_SoItCannotRaisePhantomEvents()
    {
        var opened = 0;

        var error = _wpf.Run(() =>
        {
            var first = new MediaElement();
            var player = new MediaElementPlayer();
            player.Attach(first);
            player.MediaOpened += (_, _) => opened++;

            player.Attach(new MediaElement());

            // The discarded element is still a live WPF object -- Media Foundation can still answer on
            // it. That answer must not reach the player any more.
            first.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent, first));
        });

        Assert.True(error is null, $"Re-attaching threw:\n{error}");
        Assert.Equal(0, opened);
    }

    /// <summary>A freshly-attached element has not been told to play. Leaving <c>IsPlaying</c> true across
    /// a re-attach would show the transport as playing over an element that is doing nothing.
    ///
    /// <para>Both cases, deliberately: WITH a source, the replayed <c>Open()</c> resets the flag on its
    /// own, so that case alone cannot tell whether <c>Attach</c> resets it too. WITHOUT one — a player
    /// told to play before anything was opened — <c>Attach</c>'s own reset is the only thing that can
    /// clear it, which is what makes this test able to fail at all.</para></summary>
    [Fact]
    public void Reattach_ResetsIsPlaying_BecauseANewElementIsNotPlaying()
    {
        var playingWithASource = true;
        var playingWithoutASource = true;

        var error = _wpf.Run(() =>
        {
            var player = new MediaElementPlayer();
            player.Attach(new MediaElement());
            player.Open(@"C:\video.mp4");
            player.Play();

            player.Attach(new MediaElement());
            playingWithASource = player.IsPlaying;

            var sourceless = new MediaElementPlayer();
            sourceless.Attach(new MediaElement());
            sourceless.Play();

            sourceless.Attach(new MediaElement());
            playingWithoutASource = sourceless.IsPlaying;
        });

        Assert.True(error is null, $"Re-attaching threw:\n{error}");
        Assert.False(playingWithASource);
        Assert.False(playingWithoutASource);
    }
}
