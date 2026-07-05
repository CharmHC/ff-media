using CommunityToolkit.Mvvm.ComponentModel;

namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>One queued download: identity + chosen config, plus observable live state for the UI.</summary>
public partial class DownloadJob : ObservableObject
{
    public DownloadJob(string url, string title, DownloadConfig config, string outputFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(outputFolder);
        Url = url;
        Title = title;
        Config = config;
        OutputFolder = outputFolder;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Url { get; }
    public string Title { get; }
    public DownloadConfig Config { get; }
    public string OutputFolder { get; }

    /// <summary>Per-job cancellation. The manager passes this token to the download service.</summary>
    internal CancellationTokenSource Cts { get; } = new();

    [ObservableProperty] private JobStatus _status = JobStatus.Queued;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _outputPath;

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Canceled or JobStatus.Failed;
}
