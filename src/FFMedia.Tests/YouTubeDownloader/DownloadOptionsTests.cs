using FFMedia.Tools.YouTubeDownloader.Services;
using YoutubeDLSharp.Options;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloadOptionsTests
{
    [Fact]
    public void Mp4_SetsRecodeMp4_NoPlaylist_AndOutputTemplateUnderFolder()
    {
        var options = DownloadOptions.Mp4(@"C:\out");

        Assert.Equal(VideoRecodeFormat.Mp4, options.RecodeVideo);
        Assert.True(options.NoPlaylist);
        Assert.Contains(@"C:\out", options.Output);
        Assert.Contains("%(title)s.%(ext)s", options.Output);
    }
}
