using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloadManagerTests
{
    // Zero-delay policy so retry backoff never sleeps in tests.
    private static RetryPolicy FastPolicy(int attempts = 3) => new(attempts, TimeSpan.Zero);

    private static DownloadJob Job() => new("https://x", "T", DownloadConfig.Default, @"C:\out");

    // Fake that completes immediately with a fixed result and optional progress reports.
    private sealed class ImmediateDownload : IDownloadService
    {
        public Result<string> Result = Result<string>.Success(@"C:\out\T.mp4");
        public IReadOnlyList<DownloadUpdate> Updates = Array.Empty<DownloadUpdate>();
        public Task<Result<string>> DownloadAsync(DownloadRequest r, IProgress<DownloadUpdate> p, CancellationToken ct)
        {
            foreach (var u in Updates) p.Report(u);
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Enqueue_Success_SetsCompletedAndOutputPath()
    {
        var mgr = new DownloadManager(new ImmediateDownload(), FastPolicy());
        var job = mgr.Enqueue(Job());
        await mgr.IdleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(@"C:\out\T.mp4", job.OutputPath);
        Assert.Contains(job, mgr.Jobs);
    }

    [Fact]
    public async Task Enqueue_ReportsProgressIntoJob()
    {
        var dl = new ImmediateDownload { Updates = new[] { new DownloadUpdate(50, "1MiB/s", "00:05", "Downloading") } };
        var mgr = new DownloadManager(dl, FastPolicy());
        var job = mgr.Enqueue(Job());
        await mgr.IdleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(100, job.Progress); // forced to 100 on success
    }

    [Fact]
    public async Task PostProcessingStage_MovesJobToProcessing()
    {
        // A download that reports a PostProcessing update but blocks before returning,
        // so we can observe the interim Processing status.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dl = new GatedDownload(gate.Task, onReport: () => reported.TrySetResult(),
            update: new DownloadUpdate(90, null, null, "PostProcessing"));
        var mgr = new DownloadManager(dl, FastPolicy());
        var job = mgr.Enqueue(Job());
        await reported.Task; // progress delivered synchronously before DownloadAsync returns
        Assert.Equal(JobStatus.Processing, job.Status);
        gate.SetResult();
        await mgr.IdleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task Failure_PermanentError_SetsFailedWithoutRetry()
    {
        var dl = new CountingDownload(_ => Result<string>.Failure("Video unavailable"));
        var mgr = new DownloadManager(dl, FastPolicy(attempts: 3));
        var job = mgr.Enqueue(Job());
        await mgr.IdleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("Video unavailable", job.ErrorMessage);
        Assert.Equal(1, dl.Calls); // no retry for permanent errors
    }

    [Fact]
    public async Task Failure_Transient_RetriesThenSucceeds()
    {
        var dl = new CountingDownload(n =>
            n < 3 ? Result<string>.Failure("connection reset") : Result<string>.Success(@"C:\out\T.mp4"));
        var mgr = new DownloadManager(dl, FastPolicy(attempts: 3));
        var job = mgr.Enqueue(Job());
        await mgr.IdleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(3, dl.Calls);
    }

    [Fact]
    public async Task Failure_Transient_ExhaustsAttemptsThenFails()
    {
        var dl = new CountingDownload(_ => Result<string>.Failure("connection reset"));
        var mgr = new DownloadManager(dl, FastPolicy(attempts: 3));
        var job = mgr.Enqueue(Job());
        await mgr.IdleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, dl.Calls);
    }

    [Fact]
    public async Task Enqueue_BoundsConcurrencyToCap()
    {
        var fake = new PeakTrackingDownload();
        var mgr = new DownloadManager(fake, FastPolicy(), maxConcurrency: 3);
        var jobs = Enumerable.Range(0, 5).Select(_ => mgr.Enqueue(Job())).ToList();

        // Wait until exactly `cap` downloads have entered; the semaphore prevents a 4th.
        for (int i = 0; i < 3; i++) await fake.Entered.WaitAsync();
        Assert.Equal(3, Volatile.Read(ref fake.Current));
        Assert.Equal(3, Volatile.Read(ref fake.Peak));

        fake.Release.SetResult();          // let all downloads finish
        await mgr.IdleAsync();
        Assert.Equal(3, Volatile.Read(ref fake.Peak)); // never exceeded the cap
        Assert.All(jobs, j => Assert.Equal(JobStatus.Completed, j.Status));
    }

    [Fact]
    public async Task FailureIsolation_OneFailedJobDoesNotStopOthers()
    {
        var dl = new PerUrlDownload();
        var mgr = new DownloadManager(dl, FastPolicy());
        var good1 = mgr.Enqueue(new DownloadJob("good1", "T", DownloadConfig.Default, @"C:\out"));
        var bad = mgr.Enqueue(new DownloadJob("bad", "T", DownloadConfig.Default, @"C:\out"));
        var good2 = mgr.Enqueue(new DownloadJob("good2", "T", DownloadConfig.Default, @"C:\out"));
        await mgr.IdleAsync();
        Assert.Equal(JobStatus.Completed, good1.Status);
        Assert.Equal(JobStatus.Failed, bad.Status);
        Assert.Equal(JobStatus.Completed, good2.Status);
    }

    [Fact]
    public async Task CancelAll_CancelsRunningAndQueuedJobs()
    {
        var fake = new PeakTrackingDownload();
        var mgr = new DownloadManager(fake, FastPolicy(), maxConcurrency: 2);
        var jobs = Enumerable.Range(0, 4).Select(_ => mgr.Enqueue(Job())).ToList();
        for (int i = 0; i < 2; i++) await fake.Entered.WaitAsync(); // 2 running, 2 queued
        mgr.CancelAll();
        // Do NOT complete the gate. Cancellation alone must unblock every job: running jobs observe
        // the canceled token inside Release.WaitAsync(ct); queued jobs cancel at (or just after) the
        // slot. Completing the gate here would let a late-scheduled job finish before its cancel.
        await mgr.IdleAsync();
        Assert.All(jobs, j => Assert.Equal(JobStatus.Canceled, j.Status));
    }

    [Fact]
    public async Task ClearCompleted_RemovesTerminalJobs()
    {
        var mgr = new DownloadManager(new ImmediateDownload(), FastPolicy());
        var a = mgr.Enqueue(Job());
        var b = mgr.Enqueue(Job());
        await mgr.IdleAsync();
        Assert.Equal(2, mgr.Jobs.Count);
        Assert.All(mgr.Jobs, j => Assert.True(j.IsTerminal));
        mgr.ClearCompleted();
        Assert.Empty(mgr.Jobs);
    }

    // --- fakes ---

    private sealed class CountingDownload : IDownloadService
    {
        private readonly Func<int, Result<string>> _resultForCall;
        public int Calls;
        public CountingDownload(Func<int, Result<string>> resultForCall) => _resultForCall = resultForCall;
        public Task<Result<string>> DownloadAsync(DownloadRequest r, IProgress<DownloadUpdate> p, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            return Task.FromResult(_resultForCall(n));
        }
    }

    private sealed class PerUrlDownload : IDownloadService
    {
        public Task<Result<string>> DownloadAsync(DownloadRequest r, IProgress<DownloadUpdate> p, CancellationToken ct)
            => Task.FromResult(r.Url == "bad"
                ? Result<string>.Failure("Video unavailable")
                : Result<string>.Success(@"C:\out\T.mp4"));
    }

    private sealed class GatedDownload : IDownloadService
    {
        private readonly Task _gate;
        private readonly Action _onReport;
        private readonly DownloadUpdate _update;
        public GatedDownload(Task gate, Action onReport, DownloadUpdate update)
        { _gate = gate; _onReport = onReport; _update = update; }
        public async Task<Result<string>> DownloadAsync(DownloadRequest r, IProgress<DownloadUpdate> p, CancellationToken ct)
        {
            p.Report(_update);
            _onReport();
            await _gate.WaitAsync(ct);
            return Result<string>.Success(@"C:\out\T.mp4");
        }
    }

    // Tracks concurrent entries; blocks each call until Release completes.
    private sealed class PeakTrackingDownload : IDownloadService
    {
        public int Current;
        public int Peak;
        public readonly SemaphoreSlim Entered = new(0);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Result<string>> DownloadAsync(DownloadRequest r, IProgress<DownloadUpdate> p, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Current);
            int peak;
            do { peak = Volatile.Read(ref Peak); if (now <= peak) break; }
            while (Interlocked.CompareExchange(ref Peak, now, peak) != peak);
            Entered.Release();
            try { await Release.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref Current); }
            return Result<string>.Success(@"C:\out\T.mp4");
        }
    }
}
