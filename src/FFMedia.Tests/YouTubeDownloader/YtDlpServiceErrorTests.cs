using FFMedia.Core.Binaries;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

/// <summary>
/// Regression tests for the crash-on-Probe defect: when yt-dlp/ffmpeg is missing at
/// the resolved path, the services must return a graceful <see cref="FFMedia.Core.Results.Result{T}"/>
/// failure with an actionable message — never let the process-start exception escape (which
/// crashed the whole app when no global handler existed).
/// </summary>
public class YtDlpServiceErrorTests
{
    private static IYoutubeDlFactory FactoryWithMissingBinaries()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "ffmedia-nobin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        return new YoutubeDlFactory(new BundledBinaryProvider(emptyDir));
    }

    [Fact]
    public async Task Probe_WithMissingBinary_ReturnsFailure_DoesNotThrow()
    {
        var probe = new YtDlpMediaProbe(FactoryWithMissingBinaries());

        var result = await probe.ProbeAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("fetch-binaries", result.Error);
    }

    [Fact]
    public async Task Download_WithMissingBinary_ReturnsFailure_DoesNotThrow()
    {
        var svc = new YtDlpDownloadService(FactoryWithMissingBinaries());
        var progress = new Progress<DownloadUpdate>(_ => { });

        var result = await svc.DownloadAsync(
            new DownloadRequest("https://www.youtube.com/watch?v=jNQXAC9IVRw", Path.GetTempPath(), DownloadConfig.Default),
            progress, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("fetch-binaries", result.Error);
    }
}
