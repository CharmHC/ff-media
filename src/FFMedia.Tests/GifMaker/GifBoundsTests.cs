using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;
using Xunit;

namespace FFMedia.Tests.GifMaker;

public class GifBoundsTests
{
    private static MediaInfo Source(int width = 1920, int height = 1080, int fps = 30)
        => new(TimeSpan.FromSeconds(30), "mov,mp4,m4a",
            new VideoStreamInfo(width, height, new FrameRate(fps, 1), "h264", "yuv420p", 0),
            null);

    [Fact]
    public void From_PutsTheSourceAtTheHeadOfEveryList()
    {
        // THE KEYSTONE. The defaults ARE the head of each list, because they are derived from the
        // source rather than recomputed by the UI. Bounds and defaults therefore cannot drift.
        var bounds = GifBounds.From(Source(1280, 720, 24));

        Assert.Equal(new Resolution(1280, 720), bounds.Sizes[0]);
        Assert.Equal(new FrameRate(24, 1), bounds.FrameRates[0]);
    }

    [Fact]
    public void From_NeverOffersASizeLargerThanTheSource()
    {
        // A GIF wider than its source contains no more information -- the extra pixels are invented.
        var bounds = GifBounds.From(Source(640, 360));

        Assert.All(bounds.Sizes, s => Assert.True(s.Width <= 640, $"{s} is wider than the source"));
    }

    [Fact]
    public void From_NeverOffersAFrameRateFasterThanTheSource()
    {
        // Extra frames would be duplicates: bigger file, no new information.
        var bounds = GifBounds.From(Source(fps: 15));

        Assert.All(bounds.FrameRates, r => Assert.True(r.Value <= 15.0 + 0.001));
        Assert.DoesNotContain(new FrameRate(30, 1), bounds.FrameRates);
    }

    [Fact]
    public void From_SizeLadderKeepsTheSourceAspect_AndStaysEven()
    {
        var bounds = GifBounds.From(Source(1920, 1080)); // 16:9

        Assert.All(bounds.Sizes, s =>
        {
            Assert.Equal(0, s.Width % 2);
            Assert.Equal(0, s.Height % 2);
            Assert.InRange(s.Width / (double)s.Height, 16 / 9.0 - 0.02, 16 / 9.0 + 0.02);
        });
        Assert.Contains(new Resolution(480, 270), bounds.Sizes);
    }

    [Fact]
    public void From_APortraitSource_StaysPortrait()
    {
        // A phone clip. Stepping a fixed 16:9 table would silently rotate it.
        var bounds = GifBounds.From(Source(1080, 1920));

        Assert.All(bounds.Sizes, s => Assert.True(s.Height > s.Width, $"{s} is not portrait"));
    }

    [Fact]
    public void From_ATinySource_StillOffersItsOwnSize_RatherThanAnEmptyList()
    {
        // A 200px-wide source is smaller than every standard step. Filtering the standard list alone
        // would leave an EMPTY list and a ComboBox with nothing in it.
        var bounds = GifBounds.From(Source(200, 150));

        Assert.NotEmpty(bounds.Sizes);
        Assert.Equal(new Resolution(200, 150), bounds.Sizes[0]);
    }

    [Fact]
    public void From_ASourceWithNoVideo_IsEmpty()
    {
        var audioOnly = new MediaInfo(TimeSpan.FromSeconds(30), "mp3", null, null);

        Assert.Empty(GifBounds.From(audioOnly).Sizes);
    }
}
