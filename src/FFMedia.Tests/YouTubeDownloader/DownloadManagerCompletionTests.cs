using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloadManagerCompletionTests
{
    private static RetryPolicy FastPolicy(int attempts = 3) => new(attempts, TimeSpan.Zero);
    private static DownloadJob Job() => new("https://x", "Clip", DownloadConfig.Default, @"C:\out");

    private sealed class FakeHistory : IHistoryService
    {
        public List<HistoryEntry> Appended { get; } = new();
        public IReadOnlyList<HistoryEntry> Query() => Appended;
        public void Append(HistoryEntry entry) => Appended.Add(entry);
        public void Clear() => Appended.Clear();
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<Notification> Sent { get; } = new();
        public void Notify(Notification notification) => Sent.Add(notification);
    }

    private sealed class ImmediateDownload : IDownloadService
    {
        public Result<string> Result = Result<string>.Success(@"C:\out\Clip.mp4");
        public Task<Result<string>> DownloadAsync(DownloadRequest r, IProgress<DownloadUpdate> p, CancellationToken ct)
            => Task.FromResult(Result);
    }

    private sealed class GatedDownload : IDownloadService
    {
        private readonly Task _gate;
        private readonly Action _onEnter;
        public GatedDownload(Task gate, Action onEnter) { _gate = gate; _onEnter = onEnter; }
        public async Task<Result<string>> DownloadAsync(DownloadRequest r, IProgress<DownloadUpdate> p, CancellationToken ct)
        {
            _onEnter();
            await _gate.WaitAsync(ct);
            return Result<string>.Success(@"C:\out\Clip.mp4");
        }
    }

    [Fact]
    public async Task Completed_AppendsHistoryAndNotifiesSuccess()
    {
        var history = new FakeHistory();
        var notifications = new FakeNotifications();
        var mgr = new DownloadManager(new ImmediateDownload(), FastPolicy(), 3, history, notifications);

        var job = mgr.Enqueue(Job());
        await mgr.IdleAsync();

        var entry = Assert.Single(history.Appended);
        Assert.Equal("Clip", entry.Title);
        Assert.Equal("https://x", entry.Url);
        Assert.Equal(@"C:\out\Clip.mp4", entry.OutputPath);
        Assert.Equal("Completed", entry.Status);
        Assert.Contains(notifications.Sent, n => n.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public async Task Failed_NotifiesErrorAndDoesNotAppendHistory()
    {
        var history = new FakeHistory();
        var notifications = new FakeNotifications();
        var dl = new ImmediateDownload { Result = Result<string>.Failure("Video unavailable") };
        var mgr = new DownloadManager(dl, FastPolicy(attempts: 1), 3, history, notifications);

        var job = mgr.Enqueue(Job());
        await mgr.IdleAsync();

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Empty(history.Appended);
        Assert.Contains(notifications.Sent, n => n.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public async Task Canceled_AppendsNothingAndNotifiesNothing()
    {
        var history = new FakeHistory();
        var notifications = new FakeNotifications();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dl = new GatedDownload(gate.Task, () => entered.TrySetResult());
        var mgr = new DownloadManager(dl, FastPolicy(), 3, history, notifications);

        var job = mgr.Enqueue(Job());
        await entered.Task;   // job is running inside DownloadAsync
        mgr.Cancel(job);      // cancel unblocks the gate.WaitAsync(ct)
        await mgr.IdleAsync();

        Assert.Equal(JobStatus.Canceled, job.Status);
        Assert.Empty(history.Appended);
        Assert.Empty(notifications.Sent);
    }
}
