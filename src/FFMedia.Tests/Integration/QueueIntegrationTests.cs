using FFMedia.Core.Binaries;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.Integration;

[Trait("Category", "Integration")]
public class QueueIntegrationTests
{
    private static IBinaryProvider RealBinaries()
        => new BundledBinaryProvider(Path.Combine(AppContext.BaseDirectory, "assets", "binaries"));

    // "Me at the zoo" — the first YouTube video (~19s), short and stable.
    private const string TestUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    [Fact]
    public async Task Queue_TwoJobs_BothCompleteWithFiles()
    {
        var binaries = RealBinaries();
        Assert.True(binaries.Exists(ExternalBinary.Ffmpeg), "Run build/fetch-binaries.ps1 first.");
        var dir1 = Path.Combine(Path.GetTempPath(), "ffmedia-q-" + Guid.NewGuid().ToString("N"));
        var dir2 = Path.Combine(Path.GetTempPath(), "ffmedia-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        try
        {
            var svc = new YtDlpDownloadService(new YoutubeDlFactory(binaries));
            using var manager = new DownloadManager(svc, RetryPolicy.Default);

            var j1 = manager.Enqueue(new DownloadJob(TestUrl, "one", DownloadConfig.Default, dir1));
            var j2 = manager.Enqueue(new DownloadJob(TestUrl, "two", DownloadConfig.Default, dir2));
            await manager.IdleAsync();

            Assert.Equal(JobStatus.Completed, j1.Status);
            Assert.Equal(JobStatus.Completed, j2.Status);
            Assert.NotEmpty(Directory.GetFiles(dir1, "*.mp4"));
            Assert.NotEmpty(Directory.GetFiles(dir2, "*.mp4"));
        }
        finally
        {
            if (Directory.Exists(dir1)) Directory.Delete(dir1, recursive: true);
            if (Directory.Exists(dir2)) Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    public async Task Expand_SingleVideo_ResolvesOneEntryWithTitle()
    {
        var binaries = RealBinaries();
        Assert.True(binaries.Exists(ExternalBinary.YtDlp), "Run build/fetch-binaries.ps1 first.");
        var probe = new YtDlpPlaylistProbe(new YoutubeDlFactory(binaries));

        var result = await probe.ExpandAsync(TestUrl, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var entry = Assert.Single(result.Value!);
        Assert.False(string.IsNullOrWhiteSpace(entry.Title));
    }
}
