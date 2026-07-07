using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.Binaries;
using FFMedia.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace FFMedia.App.ViewModels;

/// <summary>Drives the Settings "Binaries" section and the startup yt-dlp check. Singleton so
/// the startup check and Settings share one instance (mirrors <see cref="UpdateViewModel"/>).</summary>
public partial class BinaryUpdateViewModel : ObservableObject
{
    private readonly IBinaryUpdateService _binaries;
    private readonly INotificationService _notifications;
    private readonly ILogger<BinaryUpdateViewModel> _logger;

    public BinaryUpdateViewModel(
        IBinaryUpdateService binaries, INotificationService notifications, ILogger<BinaryUpdateViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(logger);
        _binaries = binaries;
        _notifications = notifications;
        _logger = logger;
    }

    [ObservableProperty] private string _ytDlpVersion = "…";
    [ObservableProperty] private string _ffmpegVersion = "…";
    [ObservableProperty] private bool _isYtDlpUpdateAvailable;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        YtDlpVersion = await _binaries.GetInstalledVersionAsync(ExternalBinary.YtDlp) ?? "unknown";
        FfmpegVersion = await _binaries.GetInstalledVersionAsync(ExternalBinary.Ffmpeg) ?? "unknown";
    }

    [RelayCommand]
    private async Task UpdateYtDlpAsync()
    {
        IsBusy = true;
        StatusMessage = "Updating yt-dlp…";
        try
        {
            var result = await _binaries.UpdateYtDlpAsync();
            StatusMessage = result.Message;
            YtDlpVersion = result.ToVersion ?? YtDlpVersion;
            IsYtDlpUpdateAvailable = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp update failed");
            StatusMessage = "Update failed. See logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Background check invoked once at startup. Never throws; a dead feed is logged.</summary>
    public async Task CheckOnStartupAsync()
    {
        try
        {
            var latest = await _binaries.GetLatestYtDlpVersionAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(latest))
            {
                IsYtDlpUpdateAvailable = true;
                _notifications.Notify(new Notification(
                    "yt-dlp update available",
                    "A newer yt-dlp is available — update it in Settings.",
                    NotificationSeverity.Info));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup yt-dlp check failed");
        }
    }
}
