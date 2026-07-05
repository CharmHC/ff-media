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
}
