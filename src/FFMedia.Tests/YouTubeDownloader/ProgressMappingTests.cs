using FFMedia.Tools.YouTubeDownloader.Services;
using YoutubeDLSharp;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class ProgressMappingTests
{
    [Fact]
    public void ToUpdate_MapsProgressFractionToPercent_AndCarriesFields()
    {
        var p = new DownloadProgress(DownloadState.Downloading, progress: 0.5f, totalDownloadSize: "10MiB", downloadSpeed: "1MiB/s", eta: "00:10");

        var update = ProgressMapping.ToUpdate(p);

        Assert.Equal(50.0, update.Percent, precision: 3);
        Assert.Equal("1MiB/s", update.Speed);
        Assert.Equal("00:10", update.Eta);
        Assert.Equal(DownloadState.Downloading.ToString(), update.Stage);
    }
}
