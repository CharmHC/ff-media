using FFMedia.Media;
using Xunit;

namespace FFMedia.Tests.Media;

public class FfprobeParsingTests
{
    private const string VideoWithAudioJson = """
    {
      "streams": [
        { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
          "avg_frame_rate": "30000/1001", "pix_fmt": "yuv420p" },
        { "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 2 }
      ],
      "format": { "format_name": "mov,mp4,m4a", "duration": "12.500000" }
    }
    """;

    private const string SilentVideoJson = """
    {
      "streams": [
        { "codec_type": "video", "codec_name": "vp9", "width": 1080, "height": 1920,
          "avg_frame_rate": "60/1", "pix_fmt": "yuv420p" }
      ],
      "format": { "format_name": "matroska,webm", "duration": "3.000000" }
    }
    """;

    [Fact]
    public void Parse_ReadsVideoAndAudio()
    {
        var info = FfprobeParsing.Parse(VideoWithAudioJson);

        Assert.NotNull(info);
        Assert.Equal(TimeSpan.FromSeconds(12.5), info!.Duration);
        Assert.Equal("mov,mp4,m4a", info.ContainerFormat);
        Assert.NotNull(info.Video);
        Assert.Equal(1920, info.Video!.Width);
        Assert.Equal(1080, info.Video.Height);
        Assert.Equal("h264", info.Video.CodecName);
        Assert.Equal("yuv420p", info.Video.PixelFormat);
        Assert.Equal(new FrameRate(30000, 1001), info.Video.FrameRate);
        Assert.True(info.HasAudio);
        Assert.Equal("aac", info.Audio!.CodecName);
        Assert.Equal(48000, info.Audio.SampleRate);
        Assert.Equal(2, info.Audio.Channels);
    }

    [Fact]
    public void Parse_HandlesMissingAudioStream()
    {
        var info = FfprobeParsing.Parse(SilentVideoJson);

        Assert.NotNull(info);
        Assert.False(info!.HasAudio);
        Assert.Null(info.Audio);
        Assert.Equal(new FrameRate(60, 1), info.Video!.FrameRate);
    }

    [Fact]
    public void Parse_ReadsRotationFromSideData()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "avg_frame_rate": "30/1", "pix_fmt": "yuv420p",
              "side_data_list": [ { "rotation": -90 } ] }
          ],
          "format": { "format_name": "mov", "duration": "1.0" }
        }
        """;

        Assert.Equal(-90, FfprobeParsing.Parse(json)!.Video!.Rotation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{ "streams": [], "format": { "format_name": "x", "duration": "1.0" } }""")]
    public void Parse_ReturnsNull_WhenUnusable(string json)
    {
        Assert.Null(FfprobeParsing.Parse(json));
    }

    [Fact]
    public void Parse_ReturnsNull_WhenDurationMissing()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "avg_frame_rate": "30/1", "pix_fmt": "yuv420p" }
          ],
          "format": { "format_name": "mov" }
        }
        """;

        Assert.Null(FfprobeParsing.Parse(json));
    }

    [Theory]
    [InlineData("\"abc\"", "1080")]
    [InlineData("1920", "12.5")]
    public void Parse_ReturnsNull_ForAdversarialWidthOrHeight_WithoutThrowing(string width, string height)
    {
        var json = $$"""
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": {{width}}, "height": {{height}},
              "avg_frame_rate": "30/1", "pix_fmt": "yuv420p" }
          ],
          "format": { "format_name": "mov", "duration": "1.0" }
        }
        """;

        var result = FfprobeParsing.Parse(json);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_DropsAudioStream_WhenChannelsAdversarial()
    {
        // channels is malformed on the (only) audio stream; the video stream itself is fine,
        // so Parse should still succeed overall with HasAudio == false.
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "avg_frame_rate": "30/1", "pix_fmt": "yuv420p" },
            { "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": null }
          ],
          "format": { "format_name": "mov", "duration": "1.0" }
        }
        """;

        var info = FfprobeParsing.Parse(json);

        Assert.NotNull(info);
        Assert.False(info!.HasAudio);
        Assert.Null(info.Audio);
    }

    [Fact]
    public void Parse_Succeeds_WhenWidthHeightChannelsAreStringTyped()
    {
        // Real ffprobe output: numeric stream fields are frequently emitted as JSON strings.
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": "1920", "height": "1080",
              "avg_frame_rate": "30/1", "pix_fmt": "yuv420p" },
            { "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": "2" }
          ],
          "format": { "format_name": "mov", "duration": "1.0" }
        }
        """;

        var info = FfprobeParsing.Parse(json);

        Assert.NotNull(info);
        Assert.Equal(1920, info!.Video!.Width);
        Assert.Equal(1080, info.Video.Height);
        Assert.True(info.HasAudio);
        Assert.Equal(2, info.Audio!.Channels);
    }

    [Theory]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("25/1", 25, 1)]
    public void FrameRate_TryParse_ReadsRational(string text, int num, int den)
    {
        Assert.True(FrameRate.TryParse(text, out var rate));
        Assert.Equal(new FrameRate(num, den), rate);
    }

    [Theory]
    [InlineData("0/0")]
    [InlineData("garbage")]
    [InlineData("30/0")]
    public void FrameRate_TryParse_RejectsUnusable(string text)
    {
        Assert.False(FrameRate.TryParse(text, out _));
    }

    [Fact]
    public void FrameRate_FormatsForFfmpeg()
    {
        Assert.Equal("30000/1001", new FrameRate(30000, 1001).ToFfmpegString());
        Assert.Equal(29.97, Math.Round(new FrameRate(30000, 1001).Value, 2));
    }
}
