using System;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class PresetMappingTests
{
    [Fact]
    public void RoundTrip_PreservesConfig()
    {
        var config = new DownloadConfig(
            OutputKind.Audio, VideoContainer.Mkv, VideoResolution.P720,
            AudioFormat.Opus, AudioBitrate.K256,
            new ProcessingOptions(
                new TrimRange(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)),
                PreciseCut: true, EmbedSubtitles: true, SubtitleLanguage: "es",
                EmbedMetadata: false, EmbedThumbnail: false));

        var back = PresetMapping.Deserialize(PresetMapping.Serialize(config));

        Assert.Equal(config, back);
    }

    [Fact]
    public void Deserialize_Blank_ReturnsDefault()
    {
        Assert.Equal(DownloadConfig.Default, PresetMapping.Deserialize("   "));
    }

    [Fact]
    public void Deserialize_Malformed_ReturnsDefault()
    {
        Assert.Equal(DownloadConfig.Default, PresetMapping.Deserialize("{ not valid json "));
    }

    [Fact]
    public void Deserialize_PartialPayload_DoesNotThrow()
    {
        // Missing fields fall back to defaults rather than throwing (tolerant to older shapes).
        var result = PresetMapping.Deserialize("{\"Kind\":\"Audio\"}");

        Assert.Equal(OutputKind.Audio, result.Kind);
    }

    [Fact]
    public void Deserialize_PartialPayload_FillsProcessingDefault()
    {
        var result = PresetMapping.Deserialize("{\"Kind\":\"Audio\"}");

        Assert.Equal(OutputKind.Audio, result.Kind);
        Assert.Equal(ProcessingOptions.Default, result.Processing);
    }
}
