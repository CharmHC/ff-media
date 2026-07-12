using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;
using FFMedia.Tools.GifMaker.Services;
using Xunit;

namespace FFMedia.Tests.GifMaker;

public class GifSizeEstimatorTests
{
    private static GifRequest Request(int width = 480, int height = 270, int fps = 15, double seconds = 6)
        => new(@"C:\in.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(seconds),
            new Resolution(width, height), new FrameRate(fps, 1), @"C:\out.gif");

    [Fact]
    public void Estimate_IsARange_NotAFalselyPreciseNumber()
    {
        // GIF size depends on CONTENT -- a talking head compresses far better than confetti. A single
        // number would be a lie told confidently. The UI shows a range.
        var estimate = GifSizeEstimator.Estimate(Request(), new GifSizeProfile());

        Assert.True(estimate.LowBytes < estimate.HighBytes, "the estimate must be a range");
        Assert.True(estimate.LowBytes > 0);
    }

    [Fact]
    public void Estimate_ScalesWithFramesAndPixels()
    {
        var profile = new GifSizeProfile();
        var small = GifSizeEstimator.Estimate(Request(320, 180, 10, 3), profile);
        var big = GifSizeEstimator.Estimate(Request(640, 360, 20, 6), profile);

        // 4x the pixels, 2x the rate, 2x the duration => far bigger. The exact factor is content-
        // dependent; the ORDERING is not.
        Assert.True(big.LowBytes > small.HighBytes, "a bigger, longer, faster GIF must estimate larger");
    }

    [Fact]
    public void Record_MovesTheProfileTowardsWhatTheGifActuallyWeighed()
    {
        // The seed constant is a guess. The user's own GIFs are evidence. This is the SpeedProfile
        // pattern: the same class of unknowable, solved the same way.
        var profile = new GifSizeProfile();
        var seeded = profile.BytesPerPixelPerFrame;

        // A GIF that came out much heavier than the seed predicted.
        profile.Record(actualBytes: 10_000_000, pixelsPerFrame: 480 * 270, frames: 90);

        Assert.NotEqual(seeded, profile.BytesPerPixelPerFrame);
        Assert.True(profile.BytesPerPixelPerFrame > seeded, "a heavier-than-predicted GIF must raise the estimate");
        Assert.Equal(1, profile.SampleCount);
    }

    [Fact]
    public void Record_IgnoresNonsense_RatherThanPoisoningTheProfile()
    {
        // A zero-byte or zero-frame result means something went wrong. Folding it into the rolling
        // average would corrupt every future estimate -- and the profile PERSISTS, so the damage would
        // outlive the session.
        var profile = new GifSizeProfile();
        var seeded = profile.BytesPerPixelPerFrame;

        profile.Record(actualBytes: 0, pixelsPerFrame: 480 * 270, frames: 90);
        profile.Record(actualBytes: 1_000_000, pixelsPerFrame: 0, frames: 90);
        profile.Record(actualBytes: 1_000_000, pixelsPerFrame: 480 * 270, frames: 0);

        Assert.Equal(seeded, profile.BytesPerPixelPerFrame);
        Assert.Equal(0, profile.SampleCount);
    }

    [Fact]
    public void Estimate_CentreMovesWithTheProfilesCalibration()
    {
        // This is the whole point of calibration: the estimate must actually use the profile's own
        // learned value, not just narrow its range around a fixed seed. A mutant that hardcoded the
        // seed constant in the estimator would still pass every other test here -- the range and
        // scaling tests never vary BytesPerPixelPerFrame away from the seed, and the narrowing test
        // asserts only on RELATIVE width, which is algebraically independent of the centre.
        var fresh = new GifSizeProfile();
        var heavy = new GifSizeProfile();
        for (var i = 0; i < 8; i++)
        {
            // Far heavier than the seed (0.75) predicts for this request.
            heavy.Record(actualBytes: 40_000_000, pixelsPerFrame: 480 * 270, frames: 90);
        }

        var freshEstimate = GifSizeEstimator.Estimate(Request(), fresh);
        var heavyEstimate = GifSizeEstimator.Estimate(Request(), heavy);

        Assert.True(heavyEstimate.LowBytes > freshEstimate.HighBytes,
            "a profile calibrated on much heavier GIFs must produce a materially larger estimate");
    }

    [Fact]
    public void Estimate_StaysARange_EvenAfterHeavyLearning()
    {
        // Past ~12 samples the band's floor (not its raw formula) is what keeps Low < High. Without
        // the floor, a well-learned profile would invert the range.
        var profile = new GifSizeProfile();
        for (var i = 0; i < 30; i++)
        {
            profile.Record(actualBytes: 4_000_000, pixelsPerFrame: 480 * 270, frames: 90);
        }

        var estimate = GifSizeEstimator.Estimate(Request(), profile);

        Assert.True(estimate.LowBytes < estimate.HighBytes,
            "even heavily-learned, the estimate must stay a real range, never inverted");
    }

    [Fact]
    public void Estimate_NarrowsAsTheProfileLearns()
    {
        // With no evidence the range must be wide and honest. After the user has made several GIFs of
        // their own, it can tighten. A range that never narrows is just a fixed disclaimer.
        var fresh = new GifSizeProfile();
        var learned = new GifSizeProfile();
        for (var i = 0; i < 8; i++)
        {
            learned.Record(actualBytes: 4_000_000, pixelsPerFrame: 480 * 270, frames: 90);
        }

        var freshEstimate = GifSizeEstimator.Estimate(Request(), fresh);
        var learnedEstimate = GifSizeEstimator.Estimate(Request(), learned);

        var freshWidth = (double)(freshEstimate.HighBytes - freshEstimate.LowBytes) / freshEstimate.LowBytes;
        var learnedWidth = (double)(learnedEstimate.HighBytes - learnedEstimate.LowBytes) / learnedEstimate.LowBytes;

        Assert.True(learnedWidth < freshWidth, "the range must narrow as the profile gains evidence");
    }
}
