using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class TargetBoundsTests
{
    [Fact]
    public void Resolution_RendersAsWidthByHeight_ForTheComboBox()
    {
        Assert.Equal("1920 × 1080", new Resolution(1920, 1080).ToString());
    }

    [Fact]
    public void StandardRates_IsExposed_SoTargetBoundsAndDerivationCannotDrift()
    {
        // TargetBounds must offer the SAME rates the derivation snaps to. A second, copied array
        // would let the offered rate and the derived rate disagree — the drift this design exists
        // to prevent.
        Assert.Contains(new FrameRate(30, 1), MergeTargetDerivation.StandardRates);
        Assert.Contains(new FrameRate(60, 1), MergeTargetDerivation.StandardRates);
        Assert.Equal(8, MergeTargetDerivation.StandardRates.Count);
    }

    private static MediaInfo Clip(
        int width = 1920, int height = 1080, int fpsNum = 30, int fpsDen = 1,
        int sampleRate = 48_000, int channels = 2)
        => new(TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(width, height, new FrameRate(fpsNum, fpsDen), "h264", "yuv420p", 0),
            new AudioStreamInfo("aac", sampleRate, channels));

    [Fact]
    public void From_PutsTheDerivedTargetFirstInEveryList()
    {
        // THE KEYSTONE. The derived target must be the head of each list, or the UI offers options
        // that disagree with what derivation picks — and the "you have overridden the derived
        // target" hint starts lying.
        var clips = new[] { Clip(1920, 1080, 30, 1, 48_000, 2), Clip(1280, 720, 24, 1, 44_100, 1) };
        var derived = MergeTargetDerivation.Derive(clips);
        var bounds = TargetBounds.From(clips);

        Assert.Equal(new Resolution(derived.Width, derived.Height), bounds.Resolutions[0]);
        Assert.Equal(derived.FrameRate, bounds.FrameRates[0]);
        Assert.Equal(derived.AudioSampleRate, bounds.SampleRates[0]);
        Assert.Equal(derived.AudioChannels, bounds.ChannelCounts[0]);
    }

    [Fact]
    public void From_NeverOffersAResolutionLargerThanTheSource()
    {
        var bounds = TargetBounds.From([Clip(1280, 720)]);

        Assert.All(bounds.Resolutions, r => Assert.True(r.PixelCount <= 1280L * 720));
    }

    [Fact]
    public void From_ResolutionLadderKeepsTheSourceAspectRatio_AndStaysEven()
    {
        var bounds = TargetBounds.From([Clip(1920, 1080)]); // 16:9

        Assert.All(bounds.Resolutions, r =>
        {
            Assert.Equal(0, r.Width % 2);
            Assert.Equal(0, r.Height % 2);
            // 16:9 within a pixel of rounding.
            Assert.InRange(r.Width / (double)r.Height, 16 / 9.0 - 0.02, 16 / 9.0 + 0.02);
        });
        Assert.Contains(new Resolution(1280, 720), bounds.Resolutions);
    }

    [Fact]
    public void From_ResolutionLadderHandlesAVerticalSource()
    {
        // A phone clip. The ladder steps DOWN the long edge; it must not silently rotate the video.
        var bounds = TargetBounds.From([Clip(1080, 1920)]);

        Assert.Equal(new Resolution(1080, 1920), bounds.Resolutions[0]);
        Assert.All(bounds.Resolutions, r => Assert.True(r.Height > r.Width, $"{r} is not portrait"));
    }

    [Fact]
    public void From_NeverOffersAFrameRateFasterThanTheFastestClip()
    {
        var bounds = TargetBounds.From([Clip(fpsNum: 30), Clip(fpsNum: 24)]);

        Assert.All(bounds.FrameRates, r => Assert.True(r.Value <= 30.0 + 0.001));
        Assert.DoesNotContain(new FrameRate(60, 1), bounds.FrameRates);
        Assert.Contains(new FrameRate(24, 1), bounds.FrameRates);
    }

    [Fact]
    public void From_OffersANonStandardSourceRate_RatherThanAnEmptyList()
    {
        // A 12 fps clip snaps to no standard rate, and every standard rate is FASTER than it. Filtering
        // "standard rates <= 12" alone would yield an EMPTY list and a ComboBox with nothing in it.
        var bounds = TargetBounds.From([Clip(fpsNum: 12)]);

        Assert.NotEmpty(bounds.FrameRates);
        Assert.Equal(new FrameRate(12, 1), bounds.FrameRates[0]);
    }

    [Fact]
    public void From_CapsSampleRateAndChannelsAtTheSource()
    {
        var bounds = TargetBounds.From([Clip(sampleRate: 44_100, channels: 1)]);

        Assert.Equal(44_100, bounds.SampleRates[0]);
        Assert.DoesNotContain(48_000, bounds.SampleRates);
        Assert.Equal(new[] { 1 }, bounds.ChannelCounts);
    }

    [Fact]
    public void From_OnClipsWithNoAudio_StillOffersTheDefaultAudioSpec()
    {
        // Derivation falls back to 48 kHz stereo for silent clips (anullsrc), so the bounds must
        // offer at least that — otherwise the ComboBox is empty and the page cannot bind.
        var silent = new MediaInfo(TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0), null);

        var bounds = TargetBounds.From([silent]);

        Assert.Equal(48_000, bounds.SampleRates[0]);
        Assert.Equal(2, bounds.ChannelCounts[0]);
    }

    [Fact]
    public void Empty_HasNoOptions_SoAPageWithNoClipsBindsToNothing()
    {
        Assert.Empty(TargetBounds.Empty.Resolutions);
        Assert.Empty(TargetBounds.Empty.FrameRates);
    }
}
