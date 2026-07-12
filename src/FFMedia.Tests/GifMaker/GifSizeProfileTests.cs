using FFMedia.Tools.GifMaker.Models;
using Xunit;

namespace FFMedia.Tests.GifMaker;

public class GifSizeProfileTests
{
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EffectiveBytesPerPixelPerFrame_FallsBackToSeed_WhenPersistedValueIsNonsense(double corrupt)
    {
        // gif-size.json is user-visible and hand-editable. A corrupt value must never reach the
        // user-facing estimate -- it must fall back to the seed instead.
        var profile = new GifSizeProfile { BytesPerPixelPerFrame = corrupt, SampleCount = 5 };

        Assert.Equal(0.75, profile.EffectiveBytesPerPixelPerFrame);
    }

    [Fact]
    public void Record_RepairsANonsensePersistedAverage_RatherThanBlendingWithIt()
    {
        // The bad state is discarded, not averaged with: the new sample becomes the average outright,
        // exactly as if this were a fresh profile's first sample.
        var profile = new GifSizeProfile { BytesPerPixelPerFrame = double.NaN, SampleCount = 8 };

        profile.Record(actualBytes: 200, pixelsPerFrame: 100, frames: 2); // measured = 1.0

        Assert.Equal(1.0, profile.BytesPerPixelPerFrame, 10);
        Assert.Equal(1, profile.SampleCount);
    }

    [Fact]
    public void Record_ClampsANegativePersistedSampleCount_EvenWhenTheAverageIsUsable()
    {
        // The average is fine here, so the average-repair path above never fires, and the count
        // clamp is the only thing standing between us and weight = -1. Unclamped, Math.Min(-1, 9)
        // divides by zero (weight + 1 == 0) and would persist a NaN/Infinity that no later sample
        // could undo -- exactly the corruption a hand-edited "SampleCount": -1 would otherwise cause.
        var profile = new GifSizeProfile { BytesPerPixelPerFrame = 2.0, SampleCount = -1 };

        profile.Record(actualBytes: 400, pixelsPerFrame: 100, frames: 2); // measured = 2.0

        Assert.True(double.IsFinite(profile.BytesPerPixelPerFrame));
        Assert.Equal(2.0, profile.BytesPerPixelPerFrame, 10);
        Assert.Equal(1, profile.SampleCount);
    }

    [Fact]
    public void Record_TheWindowKeepsTheAverageResponsive_NotAnchoredByOldSamples()
    {
        // Establish a baseline: many recordings of a LIGHT gif (measured = 1.0).
        var profile = new GifSizeProfile();
        for (var i = 0; i < 20; i++)
        {
            profile.Record(actualBytes: 100, pixelsPerFrame: 100, frames: 1); // measured = 1.0
        }

        Assert.Equal(1.0, profile.BytesPerPixelPerFrame, 10);
        Assert.Equal(20, profile.SampleCount);

        // Then many recordings of a much HEAVIER gif (measured = 5.0). If the window did not cap the
        // weight at Window - 1, the 20-sample-old baseline would anchor the average and it would
        // barely move (a plain cumulative mean lands around 2.7 after this). With the window cap, the
        // newest sample always carries at least 1/Window of the weight, so the average tracks the new
        // content within a handful of samples (~4.18 after these 15).
        for (var i = 0; i < 15; i++)
        {
            profile.Record(actualBytes: 500, pixelsPerFrame: 100, frames: 1); // measured = 5.0
        }

        Assert.True(profile.BytesPerPixelPerFrame > 4.0,
            "the rolling window must let recent samples dominate rather than being anchored by old history");
    }

    [Fact]
    public void Band_FloorsAtMinBand_EvenPastTwelveSamples()
    {
        // Algebraically, MaxBand - SampleCount * BandNarrowingPerSample goes negative past ~11.25
        // samples (0.45 / 0.04). Without a floor the range would invert (Low > High) -- not a range
        // at all. 30 samples is comfortably past that point.
        var profile = new GifSizeProfile();
        for (var i = 0; i < 30; i++)
        {
            profile.Record(actualBytes: 100, pixelsPerFrame: 100, frames: 1);
        }

        Assert.Equal(0.20, profile.Band, 10);
    }
}
