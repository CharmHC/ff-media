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
    private readonly Func<Action<DownloadUpdate>, IProgress<DownloadUpdate>> _progressFactory;
    private CancellationTokenSource? _cts;

    public DownloaderViewModel(
        IMediaProbe probe,
        IDownloadService download,
        Func<Action<DownloadUpdate>, IProgress<DownloadUpdate>>? progressFactory = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(download);
        _probe = probe;
        _download = download;
        // Production default: Progress<T> captures the current SynchronizationContext at
        // construction time and marshals callbacks back onto it (the WPF UI thread). Tests
        // run with no SynchronizationContext, so Progress<T> posts callbacks to the ThreadPool
        // instead, which can run after the awaited download completes. Injecting the factory
        // lets tests substitute a synchronous IProgress<T> without changing production wiring.
        _progressFactory = progressFactory ?? (handler => new Progress<DownloadUpdate>(handler));
        OutputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFMedia");
    }

    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _mediaTitle = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _progressText = string.Empty;
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
        ProgressText = string.Empty;
        StatusMessage = "Downloading…";
        _cts = new CancellationTokenSource();
        // Live progress updates ONLY Progress/ProgressText, never StatusMessage. StatusMessage
        // is a terminal-outcome field set exclusively on the awaiting thread below, so it can
        // never be clobbered by a progress callback that runs after the download completes.
        var progress = _progressFactory(u =>
        {
            Progress = u.Percent;
            ProgressText = $"{u.Stage} {u.Percent:0}%  {u.Speed}  ETA {u.Eta}";
        });
        try
        {
            var result = await _download.DownloadAsync(
                new DownloadRequest(Url, OutputFolder, DownloadConfig.Default), progress, _cts.Token);
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
