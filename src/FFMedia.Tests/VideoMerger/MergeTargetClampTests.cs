using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergeTargetClampTests
{
    private static MediaInfo Clip(int width, int height, int fps = 30, int sampleRate = 48_000, int channels = 2)
        => new(TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(width, height, new FrameRate(fps, 1), "h264", "yuv420p", 0),
            new AudioStreamInfo("aac", sampleRate, channels));

    [Fact]
    public void ClampTo_LeavesAnOverrideThatIsStillAllowed_Untouched()
    {
        // Sources are 1080p; the user deliberately chose 720p. That intent must survive.
        var bounds = TargetBounds.From([Clip(1920, 1080)]);
        var target = MergeTarget.Default with { Width = 1280, Height = 720 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(1280, clamped.Width);
        Assert.Equal(720, clamped.Height);
    }

    [Fact]
    public void ClampTo_SnapsAnOversizedOverrideDownToTheCeiling()
    {
        // The user picked 1080p, then deleted the only 1080p clip. 1080p is now unreachable.
        var bounds = TargetBounds.From([Clip(1280, 720)]);
        var target = MergeTarget.Default with { Width = 1920, Height = 1080 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(1280, clamped.Width);
        Assert.Equal(720, clamped.Height);
    }

    [Fact]
    public void ClampTo_SnapsToTheLargestAllowedValueNotExceedingTheCurrentOne()
    {
        // The ladder shifted (the source aspect changed), so the user's exact choice is not on it.
        // Snap DOWN to the nearest allowed — never up, which would be the upscaling we forbid.
        var bounds = TargetBounds.From([Clip(1920, 1080)]);
        var target = MergeTarget.Default with { Width = 1600, Height = 900 };

        var clamped = target.ClampTo(bounds);

        Assert.True(
            (long)clamped.Width * clamped.Height <= 1600L * 900,
            $"snapped UP to {clamped.Width}x{clamped.Height}");
        Assert.Contains(new Resolution(clamped.Width, clamped.Height), bounds.Resolutions);
    }

    [Fact]
    public void ClampTo_SnapsFrameRateDown_NeverUp()
    {
        var bounds = TargetBounds.From([Clip(1920, 1080, fps: 30)]);
        var target = MergeTarget.Default with { FrameRate = new FrameRate(60, 1) };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(new FrameRate(30, 1), clamped.FrameRate);
    }

    [Fact]
    public void ClampTo_SnapsSampleRateAndChannelsDown()
    {
        var bounds = TargetBounds.From([Clip(1920, 1080, sampleRate: 44_100, channels: 1)]);
        var target = MergeTarget.Default with { AudioSampleRate = 96_000, AudioChannels = 6 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(44_100, clamped.AudioSampleRate);
        Assert.Equal(1, clamped.AudioChannels);
    }

    [Fact]
    public void ClampTo_LeavesTheFreeFieldsAlone()
    {
        // Nothing about the SOURCES makes H.265, CRF 28, MKV or Fill an invalid choice.
        var bounds = TargetBounds.From([Clip(1920, 1080)]);
        var target = MergeTarget.Default with
        {
            VideoCodec = MergeVideoCodec.H265,
            AudioCodec = MergeAudioCodec.Opus,
            Container = MergeContainer.Mkv,
            Crf = 28,
            FitMode = FitMode.Fill,
        };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(MergeVideoCodec.H265, clamped.VideoCodec);
        Assert.Equal(MergeAudioCodec.Opus, clamped.AudioCodec);
        Assert.Equal(MergeContainer.Mkv, clamped.Container);
        Assert.Equal(28, clamped.Crf);
        Assert.Equal(FitMode.Fill, clamped.FitMode);
    }

    [Fact]
    public void ClampTo_EmptyBounds_ReturnsTheTargetUnchanged()
    {
        // No clips → nothing to bound against. Do not mangle the target into a zero-size one.
        var target = MergeTarget.Default with { Width = 1280, Height = 720 };

        Assert.Equal(target, target.ClampTo(TargetBounds.Empty));
    }

    // The four tests below force LargestNotExceeding's "eligible.Count == 0" branch: every allowed
    // value is built strictly ABOVE the target's current value, so nothing qualifies for "largest ≤
    // current" and the fallback ("take the smallest") is the only thing that can produce the asserted
    // result. Before these existed, deleting `allowed.MinBy(weight)` and returning `eligible.MaxBy
    // (weight)!` unconditionally left all other tests green — half the documented rule was unpinned.
    //
    // TargetBounds is a plain record with a public ctor, so we build a ladder directly instead of
    // going through TargetBounds.From (which always seeds the ladder with the current/derived value,
    // making "nothing qualifies" unreachable from real derivation).

    [Fact]
    public void ClampTo_AllAllowedResolutionsExceedTheCurrentOne_FallsBackToTheSmallest()
    {
        // Reference-type weight (Resolution/PixelCount). Every rung is bigger than the 640x360
        // override, so eligible is empty and the rule's second half must pick the smallest rung.
        var bounds = new TargetBounds(
            [new Resolution(1920, 1080), new Resolution(1280, 720)],
            [MergeTarget.Default.FrameRate],
            [MergeTarget.Default.AudioSampleRate],
            [MergeTarget.Default.AudioChannels]);
        var target = MergeTarget.Default with { Width = 640, Height = 360 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(1280, clamped.Width);
        Assert.Equal(720, clamped.Height);
    }

    [Fact]
    public void ClampTo_AllAllowedFrameRatesExceedTheCurrentOne_FallsBackToTheSmallest()
    {
        // Value-type weight (FrameRate wraps a struct). This is the branch the brief warns about:
        // FirstOrDefault() on a value-type sequence yields default(FrameRate), not null, so a
        // `FirstOrDefault() ?? MinBy(...)` rewrite would never fall through to MinBy here — it would
        // silently return default(FrameRate) (0/1) instead. Only asserting the true smallest rung
        // (24 fps, not 0 fps) would catch that regression.
        var bounds = new TargetBounds(
            [new Resolution(MergeTarget.Default.Width, MergeTarget.Default.Height)],
            [new FrameRate(60, 1), new FrameRate(24, 1)],
            [MergeTarget.Default.AudioSampleRate],
            [MergeTarget.Default.AudioChannels]);
        var target = MergeTarget.Default with { FrameRate = new FrameRate(15, 1) };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(new FrameRate(24, 1), clamped.FrameRate);
    }

    [Fact]
    public void ClampTo_AllAllowedSampleRatesExceedTheCurrentOne_FallsBackToTheSmallest()
    {
        // Value-type weight (plain int). Same FirstOrDefault trap as frame rate: default(int) is 0,
        // a value nothing here would otherwise produce, so a broken fallback is unmistakable.
        var bounds = new TargetBounds(
            [new Resolution(MergeTarget.Default.Width, MergeTarget.Default.Height)],
            [MergeTarget.Default.FrameRate],
            [96_000, 48_000],
            [MergeTarget.Default.AudioChannels]);
        var target = MergeTarget.Default with { AudioSampleRate = 22_050 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(48_000, clamped.AudioSampleRate);
    }

    [Fact]
    public void ClampTo_AllAllowedChannelCountsExceedTheCurrentOne_FallsBackToTheSmallest()
    {
        // Value-type weight (plain int), the channels twin of the sample-rate case above.
        var bounds = new TargetBounds(
            [new Resolution(MergeTarget.Default.Width, MergeTarget.Default.Height)],
            [MergeTarget.Default.FrameRate],
            [MergeTarget.Default.AudioSampleRate],
            [8, 6]);
        var target = MergeTarget.Default with { AudioChannels = 1 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(6, clamped.AudioChannels);
    }
}
