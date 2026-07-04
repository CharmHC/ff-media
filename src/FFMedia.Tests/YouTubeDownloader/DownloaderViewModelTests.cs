using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using FFMedia.Tools.YouTubeDownloader.ViewModels;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloaderViewModelTests
{
    private sealed class FakeProbe : IMediaProbe
    {
        public Result<MediaInfo> Next = Result<MediaInfo>.Success(new MediaInfo("Test Video", null, null, null));
        public Task<Result<MediaInfo>> ProbeAsync(string url, CancellationToken ct) => Task.FromResult(Next);
    }

    private sealed class FakeDownload : IDownloadService
    {
        public Result<string> Next = Result<string>.Success(@"C:\out\Test Video.mp4");
        public IReadOnlyList<DownloadUpdate> Updates = new[] { new DownloadUpdate(50, "1MiB/s", "00:05", "Downloading") };
        public Task<Result<string>> DownloadAsync(DownloadRequest request, IProgress<DownloadUpdate> progress, CancellationToken ct)
        {
            foreach (var u in Updates) progress.Report(u);
            return Task.FromResult(Next);
        }
    }

    /// <summary>
    /// Invokes the handler synchronously and inline, unlike <see cref="Progress{T}"/> which
    /// posts to the captured SynchronizationContext (or the ThreadPool when none is present,
    /// as in xUnit). Used only to make progress-callback assertions deterministic in tests;
    /// production code keeps using <see cref="Progress{T}"/> for correct UI-thread marshaling.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [Fact]
    public async Task Probe_Success_SetsMediaTitle()
    {
        var vm = new DownloaderViewModel(new FakeProbe(), new FakeDownload()) { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        Assert.Equal("Test Video", vm.MediaTitle);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Probe_Failure_SetsStatusAndNoTitle()
    {
        var probe = new FakeProbe { Next = Result<MediaInfo>.Failure("Video unavailable") };
        var vm = new DownloaderViewModel(probe, new FakeDownload()) { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.MediaTitle);
        Assert.Contains("unavailable", vm.StatusMessage);
    }

    [Fact]
    public async Task Download_Success_ReportsProgressAndSavedPath()
    {
        var vm = new DownloaderViewModel(
            new FakeProbe(),
            new FakeDownload(),
            progressFactory: h => new SynchronousProgress<DownloadUpdate>(h))
        { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.Progress);
        Assert.Contains("50%", vm.ProgressText);
        Assert.Contains("Test Video.mp4", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Download_Failure_SetsErrorStatus()
    {
        var dl = new FakeDownload { Next = Result<string>.Failure("network error"), Updates = Array.Empty<DownloadUpdate>() };
        var vm = new DownloaderViewModel(new FakeProbe(), dl) { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Contains("network error", vm.StatusMessage);
    }
}
