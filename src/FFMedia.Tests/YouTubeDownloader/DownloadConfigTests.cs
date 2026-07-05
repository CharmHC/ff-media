using FFMedia.Tools.YouTubeDownloader.Models;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloadConfigTests
{
    [Fact]
    public void Default_IsMp4Video_At1080p()
    {
        var c = DownloadConfig.Default;
        Assert.Equal(OutputKind.Video, c.Kind);
        Assert.Equal(VideoContainer.Mp4, c.Container);
        Assert.Equal(VideoResolution.P1080, c.Resolution);
    }

    [Fact]
    public void Record_SupportsWithExpression()
    {
        var c = DownloadConfig.Default with { Kind = OutputKind.Audio, AudioFormat = AudioFormat.Mp3 };
        Assert.Equal(OutputKind.Audio, c.Kind);
        Assert.Equal(AudioFormat.Mp3, c.AudioFormat);
    }

    [Fact]
    public void ProcessingDefault_EmbedsMetadataAndThumbnail_SubsOff_NoTrim()
    {
        var p = ProcessingOptions.Default;
        Assert.Null(p.Trim);
        Assert.False(p.EmbedSubtitles);
        Assert.False(p.PreciseCut);
        Assert.Equal("en", p.SubtitleLanguage);
        Assert.True(p.EmbedMetadata);
        Assert.True(p.EmbedThumbnail);
    }

    [Fact]
    public void Default_UsesProcessingDefault()
    {
        Assert.Same(ProcessingOptions.Default, DownloadConfig.Default.Processing);
    }
}
