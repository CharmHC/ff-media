using System.Collections.ObjectModel;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>A bounded-concurrency download queue: enqueue jobs, observe their live state, cancel and clear.</summary>
public interface IDownloadManager
{
    /// <summary>The live queue, safe to bind to the UI.</summary>
    ReadOnlyObservableCollection<DownloadJob> Jobs { get; }

    /// <summary>Add a job to the queue and start it (subject to the concurrency cap). Returns the same job.</summary>
    DownloadJob Enqueue(DownloadJob job);

    /// <summary>Cancel a single job.</summary>
    void Cancel(DownloadJob job);

    /// <summary>Cancel every job that has not yet reached a terminal state.</summary>
    void CancelAll();

    /// <summary>Remove all completed/canceled/failed jobs from the queue.</summary>
    void ClearCompleted();

    /// <summary>Completes when no job is running or queued. Deterministic wait for "all done".</summary>
    Task IdleAsync();
}
