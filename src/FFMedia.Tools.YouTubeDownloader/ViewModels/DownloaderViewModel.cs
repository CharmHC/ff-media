using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;

namespace FFMedia.Tools.YouTubeDownloader.ViewModels;

public partial class DownloaderViewModel : ObservableObject
{
    private readonly IMediaProbe _probe;
    private readonly IDownloadService _download;
    private CancellationTokenSource? _cts;

    public DownloaderViewModel(IMediaProbe probe, IDownloadService download)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(download);
        _probe = probe;
        _download = download;
        OutputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFMedia");
    }

    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _mediaTitle = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _outputFolder;

    [RelayCommand]
    private async Task ProbeAsync()
    {
        IsBusy = true;
        MediaTitle = string.Empty;
        StatusMessage = "Probing…";
        try
        {
            var result = await _probe.ProbeAsync(Url, CancellationToken.None);
            if (result.IsSuccess)
            {
                MediaTitle = result.Value!.Title;
                StatusMessage = "Ready to download";
            }
            else
            {
                StatusMessage = result.Error ?? "Probe failed";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        IsBusy = true;
        Progress = 0;
        StatusMessage = "Downloading…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<DownloadUpdate>(u =>
        {
            Progress = u.Percent;
            StatusMessage = $"{u.Stage} {u.Percent:0}%  {u.Speed}  ETA {u.Eta}";
        });
        try
        {
            var result = await _download.DownloadAsync(
                new DownloadRequest(Url, OutputFolder), progress, _cts.Token);
            await Task.Yield();
            StatusMessage = result.IsSuccess
                ? $"Saved to {result.Value}"
                : result.Error ?? "Download failed";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Canceled";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
