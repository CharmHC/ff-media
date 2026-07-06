using System.Collections.ObjectModel;
using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>
/// Runs queued downloads off the UI thread under a concurrency cap, retrying transient failures
/// and isolating per-job failures. Progress is reported synchronously so a late callback can never
/// overwrite a terminal status (the manager has no UI SynchronizationContext).
/// </summary>
public sealed class DownloadManager : IDownloadManager, IDisposable
{
    private readonly IDownloadService _download;
    private readonly RetryPolicy _policy;
    private readonly SemaphoreSlim _slots;
    private readonly ObservableCollection<DownloadJob> _jobs = new();
    private readonly object _gate = new();
    private readonly IHistoryService? _history;
    private readonly INotificationService? _notifications;
    private int _activeCount;
    private TaskCompletionSource? _idleTcs;

    public DownloadManager(
        IDownloadService download,
        RetryPolicy policy,
        int maxConcurrency = 3,
        IHistoryService? history = null,
        INotificationService? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(policy);
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _download = download;
        _policy = policy;
        _history = history;
        _notifications = notifications;
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        Jobs = new ReadOnlyObservableCollection<DownloadJob>(_jobs);
    }

    public ReadOnlyObservableCollection<DownloadJob> Jobs { get; }

    public DownloadJob Enqueue(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs.Add(job);
        lock (_gate) _activeCount++;
        _ = Task.Run(() => RunAndTrackAsync(job));
        return job;
    }

    public void Cancel(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.IsTerminal) job.Cts.Cancel();
    }

    public void CancelAll()
    {
        foreach (var job in _jobs)
            if (!job.IsTerminal) job.Cts.Cancel();
    }

    public void ClearCompleted()
    {
        for (var i = _jobs.Count - 1; i >= 0; i--)
        {
            var job = _jobs[i];
            if (!job.IsTerminal) continue;
            _jobs.RemoveAt(i);
            job.Cts.Dispose(); // terminal job is done running — release its CTS
        }
    }

    public Task IdleAsync()
    {
        lock (_gate)
        {
            if (_activeCount == 0) return Task.CompletedTask;
            _idleTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idleTcs.Task;
        }
    }

    private async Task RunAndTrackAsync(DownloadJob job)
    {
        try
        {
            await RunAsync(job);
            RaiseTerminalSideEffects(job);
        }
        finally
        {
            TaskCompletionSource? toComplete = null;
            lock (_gate)
            {
                if (--_activeCount == 0) { toComplete = _idleTcs; _idleTcs = null; }
            }
            toComplete?.TrySetResult();
        }
    }

    /// <summary>Records history and raises a notification for a terminal job. Each side effect is
    /// isolated: side effects must never break the queue, and one failing must not skip the other.</summary>
    private void RaiseTerminalSideEffects(DownloadJob job)
    {
        switch (job.Status)
        {
            case JobStatus.Completed:
                Safe(() => _history?.Append(new HistoryEntry(
                    job.Title, job.Url, job.OutputPath, DescribeFormat(job.Config),
                    DateTimeOffset.Now, job.Status.ToString())));
                Safe(() => _notifications?.Notify(new Notification(
                    "Download complete", $"\"{job.Title}\" finished.", NotificationSeverity.Success)));
                break;
            case JobStatus.Failed:
                Safe(() => _notifications?.Notify(new Notification(
                    "Download failed", $"\"{job.Title}\": {job.ErrorMessage}", NotificationSeverity.Error)));
                break;
        }
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Terminal side-effect failed: {ex}");
        }
    }

    private static string DescribeFormat(DownloadConfig config) =>
        config.Kind == OutputKind.Video
            ? $"{config.Container} {config.Resolution}"
            : $"{config.AudioFormat} {config.Bitrate}";

    private async Task RunAsync(DownloadJob job)
    {
        var acquired = false;
        try
        {
            await _slots.WaitAsync(job.Cts.Token);
            acquired = true;
            // A job canceled while queued must not start a download: the semaphore's synchronous
            // fast-path can grant a free slot without observing an already-canceled token.
            job.Cts.Token.ThrowIfCancellationRequested();

            var progress = new SyncProgress<DownloadUpdate>(u =>
            {
                job.Progress = u.Percent;
                job.ProgressText = Describe(u);
                if (string.Equals(u.Stage, "PostProcessing", StringComparison.OrdinalIgnoreCase))
                    job.Status = JobStatus.Processing;
            });

            for (var attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
            {
                job.ErrorMessage = null;
                job.Status = JobStatus.Downloading;

                var request = new DownloadRequest(job.Url, job.OutputFolder, job.Config);
                var result = await _download.DownloadAsync(request, progress, job.Cts.Token);

                if (result.IsSuccess)
                {
                    job.OutputPath = result.Value;
                    job.Progress = 100;
                    job.Status = JobStatus.Completed;
                    return;
                }

                if (attempt < _policy.MaxAttempts && RetryPolicy.IsTransient(result.Error))
                {
                    await Task.Delay(_policy.DelayFor(attempt), job.Cts.Token);
                    continue;
                }

                job.ErrorMessage = result.Error;
                job.Status = JobStatus.Failed;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Canceled;
        }
        catch (Exception ex)
        {
            job.ErrorMessage = ex.Message;
            job.Status = JobStatus.Failed;
        }
        finally
        {
            if (acquired) _slots.Release();
        }
    }

    private static string Describe(DownloadUpdate u) =>
        $"{u.Stage} {u.Percent:0}%  {u.Speed}  ETA {u.Eta}";

    public void Dispose()
    {
        foreach (var job in _jobs)
            job.Cts.Dispose();
        _slots.Dispose();
    }

    /// <summary>An <see cref="IProgress{T}"/> that invokes the handler synchronously on the caller's thread
    /// (unlike <see cref="Progress{T}"/>, which posts to the ThreadPool when there is no SynchronizationContext).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
