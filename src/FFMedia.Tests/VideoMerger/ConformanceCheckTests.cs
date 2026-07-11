using System;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class ConformanceCheckTests
{
    private static readonly MergeTarget Target = MergeTarget.Default; // 1920x1080 @30, h264, yuv420p, aac 48k/2

    private static MediaInfo Conforming() => new(
        TimeSpan.FromSeconds(5),
        "mov,mp4,m4a",
        new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
        new AudioStreamInfo("aac", 48000, 2));

    [Fact]
    public void Evaluate_ConformingClip_HasNoMismatches()
    {
        var result = ConformanceCheck.Evaluate(Conforming(), Target);

        Assert.True(result.IsConforming);
        Assert.Empty(result.Mismatches);
    }

    [Theory]
    [InlineData(1)] // e.g. an embedded subtitle track — what our own downloader writes with --embed-subs
    [InlineData(3)] // subtitles + a second audio language + a data stream
    public void Evaluate_FlagsExtraStreams_EvenWhenVideoAndAudioBothMatch(int extras)
    {
        // The dangerous direction. This clip matches the target in every property we MODEL, so
        // before ExtraStreamCount existed it was judged conforming and stream-copied — and ffmpeg,
        // which matches segments by stream INDEX, put the next clip's audio on this clip's subtitle
        // slot, exited 0, and produced an output whose later clips were silently mute.
        var clip = Conforming() with { ExtraStreamCount = extras };

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.False(result.IsConforming);
        Assert.Equal([$"{extras} extra stream(s) (e.g. subtitles) must be dropped"], result.Mismatches);
    }

    [Fact]
    public void Evaluate_FlagsResolution()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1280, 720, new FrameRate(30, 1), "h264", "yuv420p", 0),
        };

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.False(result.IsConforming);
        Assert.Contains(result.Mismatches, m => m.Contains("resolution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsFrameRate()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(60, 1), "h264", "yuv420p", 0),
        };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains("frame rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FrameRateSameValueDifferentRepresentation_IsNotAMismatch()
    {
        // 60/2 reduces to the same 30 fps as the target's 30/1 — an unreduced ffprobe
        // fraction must not be treated as a different frame rate.
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(60, 2), "h264", "yuv420p", 0),
        };

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.True(result.IsConforming);
        Assert.DoesNotContain(result.Mismatches, m => m.Contains("frame rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsVideoCodec()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "vp9", "yuv420p", 0),
        };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains("video codec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_H265Target_MatchesFfprobesHevcCodecName()
    {
        // ffprobe reports HEVC streams with codec_name "hevc", never "h265" — the target
        // enum's H265 member must map to that ffprobe string, not to its own name.
        var target = Target with { VideoCodec = MergeVideoCodec.H265 };
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "hevc", "yuv420p", 0),
        };

        var result = ConformanceCheck.Evaluate(clip, target);

        Assert.DoesNotContain(result.Mismatches, m => m.Contains("video codec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_CodecComparisonIsCaseInsensitive()
    {
        var target = Target with { VideoCodec = MergeVideoCodec.H265 };
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "HEVC", "yuv420p", 0),
        };

        var result = ConformanceCheck.Evaluate(clip, target);

        Assert.DoesNotContain(result.Mismatches, m => m.Contains("video codec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsPixelFormat()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv444p", 0),
        };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains("pixel format", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsRotation()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", -90),
        };

        Assert.False(ConformanceCheck.Evaluate(clip, Target).IsConforming);
    }

    [Fact]
    public void Evaluate_FlagsMissingAudioTrack()
    {
        var clip = Conforming() with { Audio = null };

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.False(result.IsConforming);
        Assert.Contains(result.Mismatches, m => m.Contains("no audio track", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("opus", 48000, 2, "audio codec")]
    [InlineData("aac", 44100, 2, "sample rate")]
    [InlineData("aac", 48000, 6, "channel")]
    public void Evaluate_FlagsAudioMismatches(string codec, int rate, int channels, string expected)
    {
        var clip = Conforming() with { Audio = new AudioStreamInfo(codec, rate, channels) };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_ClipWithoutVideo_NeverConforms()
    {
        var audioOnly = new MediaInfo(TimeSpan.FromSeconds(5), "mp3", null, new AudioStreamInfo("aac", 48000, 2));

        var result = ConformanceCheck.Evaluate(audioOnly, Target);

        Assert.False(result.IsConforming);
        Assert.Contains(result.Mismatches, m => m.Contains("no video track", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_CollectsEveryMismatch()
    {
        var clip = new MediaInfo(
            TimeSpan.FromSeconds(5), "webm",
            new VideoStreamInfo(640, 480, new FrameRate(15, 1), "vp9", "yuv444p", 0),
            null);

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.Equal(5, result.Mismatches.Count); // resolution, frame rate, video codec, pixel format, no audio
    }

    [Fact]
    public void Evaluate_IsConforming_NeverDriftsFromEmptyMismatches()
    {
        var result = ConformanceCheck.Evaluate(Conforming(), Target);

        Assert.Equal(result.Mismatches.Count == 0, result.IsConforming);
    }
}
