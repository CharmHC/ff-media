using System;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using YoutubeDLSharp.Options;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class OptionSetBuilderTests
{
    private static DownloadConfig Video(VideoContainer c, VideoResolution r) =>
        new(OutputKind.Video, c, r, AudioFormat.Mp3, AudioBitrate.Best, ProcessingOptions.Default);

    private static DownloadConfig Audio(AudioFormat f, AudioBitrate b) =>
        new(OutputKind.Audio, VideoContainer.Mp4, VideoResolution.Best, f, b, ProcessingOptions.Default);

    private static DownloadConfig WithProcessing(ProcessingOptions p, OutputKind kind = OutputKind.Video) =>
        DownloadConfig.Default with { Kind = kind, Processing = p };

    [Fact]
    public void Video_Mp4_1080p_SetsMergeMp4_HeightCap_Mp4ExtPreference_AndOutputTemplate()
    {
        var o = OptionSetBuilder.Build(Video(VideoContainer.Mp4, VideoResolution.P1080), @"C:\out");
        Assert.Equal(DownloadMergeFormat.Mp4, o.MergeOutputFormat);
        Assert.Contains("[height<=1080]", o.Format);
        Assert.Contains("ext=mp4", o.Format);
        Assert.Contains(@"C:\out", o.Output);
        Assert.Contains("%(title)s.%(ext)s", o.Output);
        Assert.True(o.NoPlaylist);
    }

    [Fact]
    public void Video_Best_HasNoHeightCap()
    {
        var o = OptionSetBuilder.Build(Video(VideoContainer.Mp4, VideoResolution.Best), @"C:\out");
        Assert.DoesNotContain("height<=", o.Format);
    }

    [Theory]
    [InlineData(VideoResolution.P2160, "[height<=2160]")]
    [InlineData(VideoResolution.P1440, "[height<=1440]")]
    [InlineData(VideoResolution.P1080, "[height<=1080]")]
    [InlineData(VideoResolution.P720, "[height<=720]")]
    [InlineData(VideoResolution.P480, "[height<=480]")]
    public void Video_HeightCap_MatchesResolution(VideoResolution resolution, string expectedCap)
    {
        var o = OptionSetBuilder.Build(Video(VideoContainer.Mp4, resolution), @"C:\out");
        Assert.Contains(expectedCap, o.Format);
    }

    [Fact]
    public void Video_Mkv_SetsMergeMkv_AndGenericSelector()
    {
        var o = OptionSetBuilder.Build(Video(VideoContainer.Mkv, VideoResolution.P720), @"C:\out");
        Assert.Equal(DownloadMergeFormat.Mkv, o.MergeOutputFormat);
        Assert.Contains("[height<=720]", o.Format);
        Assert.DoesNotContain("ext=", o.Format); // mkv holds any codec
    }

    [Fact]
    public void Video_Webm_SetsMergeWebm_AndWebmExtPreference()
    {
        var o = OptionSetBuilder.Build(Video(VideoContainer.Webm, VideoResolution.P480), @"C:\out");
        Assert.Equal(DownloadMergeFormat.Webm, o.MergeOutputFormat);
        Assert.Contains("ext=webm", o.Format);
        Assert.Contains("[height<=480]", o.Format);
    }

    [Theory]
    [InlineData(AudioFormat.Mp3, AudioConversionFormat.Mp3)]
    [InlineData(AudioFormat.Wav, AudioConversionFormat.Wav)]
    [InlineData(AudioFormat.M4a, AudioConversionFormat.M4a)]
    [InlineData(AudioFormat.Opus, AudioConversionFormat.Opus)]
    [InlineData(AudioFormat.Flac, AudioConversionFormat.Flac)]
    public void Audio_SetsExtractAudio_AndMappedFormat(AudioFormat input, AudioConversionFormat expected)
    {
        var o = OptionSetBuilder.Build(Audio(input, AudioBitrate.Best), @"C:\out");
        Assert.True(o.ExtractAudio);
        Assert.Equal(expected, o.AudioFormat);
        Assert.Equal("ba/b", o.Format);
        Assert.True(o.NoPlaylist);
    }

    [Theory]
    [InlineData(AudioBitrate.K320, "320K")]
    [InlineData(AudioBitrate.K256, "256K")]
    [InlineData(AudioBitrate.K192, "192K")]
    [InlineData(AudioBitrate.K128, "128K")]
    public void Audio_Mp3_EmitsExpectedAudioQualityValue(AudioBitrate bitrate, string expected)
    {
        var o = OptionSetBuilder.Build(Audio(AudioFormat.Mp3, bitrate), @"C:\out");
        var rendered = o.ToString();
        Assert.Contains("--audio-quality", rendered);
        Assert.Contains(expected, rendered);
    }

    [Theory]
    [InlineData(AudioFormat.Mp3)]
    [InlineData(AudioFormat.M4a)]
    [InlineData(AudioFormat.Opus)]
    public void Audio_LossyFormats_NonBestBitrate_EmitAudioQuality(AudioFormat format)
    {
        var o = OptionSetBuilder.Build(Audio(format, AudioBitrate.K192), @"C:\out");
        Assert.Contains("--audio-quality", o.ToString());
    }

    [Fact]
    public void Audio_Mp3_Best_OmitsAudioQuality()
    {
        var o = OptionSetBuilder.Build(Audio(AudioFormat.Mp3, AudioBitrate.Best), @"C:\out");
        Assert.DoesNotContain("--audio-quality", o.ToString());
    }

    [Fact]
    public void Audio_Wav_IgnoresBitrate_NoAudioQuality()
    {
        var o = OptionSetBuilder.Build(Audio(AudioFormat.Wav, AudioBitrate.K320), @"C:\out");
        Assert.DoesNotContain("--audio-quality", o.ToString());
    }

    [Fact]
    public void Audio_Flac_IgnoresBitrate_NoAudioQuality()
    {
        var o = OptionSetBuilder.Build(Audio(AudioFormat.Flac, AudioBitrate.K256), @"C:\out");
        Assert.DoesNotContain("--audio-quality", o.ToString());
    }

    [Fact]
    public void Default_EmbedsMetadataAndThumbnail_NoTrim_NoSubs()
    {
        var o = OptionSetBuilder.Build(DownloadConfig.Default, @"C:\out");
        Assert.True(o.EmbedMetadata);
        Assert.True(o.EmbedThumbnail);
        Assert.False(o.WriteSubs);
        Assert.DoesNotContain("download-sections", o.ToString());
    }

    [Fact]
    public void Trim_Fast_SetsDownloadSections_NoForceKeyframes()
    {
        var p = ProcessingOptions.Default with { Trim = new TrimRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5)) };
        var o = OptionSetBuilder.Build(WithProcessing(p), @"C:\out");
        Assert.Contains("*0-5", o.ToString());
        Assert.False(o.ForceKeyframesAtCuts);
    }

    [Fact]
    public void Trim_Precise_SetsForceKeyframes()
    {
        var p = ProcessingOptions.Default with
        {
            Trim = new TrimRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)),
            PreciseCut = true,
        };
        var o = OptionSetBuilder.Build(WithProcessing(p), @"C:\out");
        Assert.Contains("*10-20", o.ToString());
        Assert.True(o.ForceKeyframesAtCuts);
    }

    [Fact]
    public void Subtitles_Video_SetsWriteAndEmbedAndLangs()
    {
        var p = ProcessingOptions.Default with { EmbedSubtitles = true, SubtitleLanguage = "es" };
        var o = OptionSetBuilder.Build(WithProcessing(p, OutputKind.Video), @"C:\out");
        Assert.True(o.WriteSubs);
        Assert.True(o.WriteAutoSubs);
        Assert.True(o.EmbedSubs);
        Assert.Equal("es", o.SubLangs);
    }

    [Fact]
    public void Subtitles_Audio_AreIgnored()
    {
        var p = ProcessingOptions.Default with { EmbedSubtitles = true, SubtitleLanguage = "en" };
        var o = OptionSetBuilder.Build(WithProcessing(p, OutputKind.Audio), @"C:\out");
        Assert.False(o.WriteSubs);
        Assert.False(o.EmbedSubs);
    }

    [Fact]
    public void Embed_FlagsOff_DisableMetadataAndThumbnail()
    {
        var p = ProcessingOptions.Default with { EmbedMetadata = false, EmbedThumbnail = false };
        var o = OptionSetBuilder.Build(WithProcessing(p), @"C:\out");
        Assert.False(o.EmbedMetadata);
        Assert.False(o.EmbedThumbnail);
    }
}
