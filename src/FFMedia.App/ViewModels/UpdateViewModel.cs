using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.Updates;
using Microsoft.Extensions.Logging;

namespace FFMedia.App.ViewModels;

/// <summary>Drives the shell update banner and the Settings "check for updates" action.</summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ILogger<UpdateViewModel> _logger;

    public UpdateViewModel(IUpdateService updates, ILogger<UpdateViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(logger);
        _updates = updates;
        _logger = logger;
        CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
    }

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string? _targetVersion;
    [ObservableProperty] private string _currentVersion;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Background check invoked once at startup. Never throws; a dead feed is logged, not fatal.</summary>
    public async Task CheckOnStartupAsync()
    {
        try
        {
            var info = await _updates.CheckForUpdatesAsync().ConfigureAwait(true);
            if (info is not null)
            {
                TargetVersion = info.TargetVersion;
                IsUpdateAvailable = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup update check failed");
        }
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        IsBusy = true;
        StatusMessage = "Checking for updates…";
        try
        {
            var info = await _updates.CheckForUpdatesAsync().ConfigureAwait(true);
            if (info is null)
            {
                IsUpdateAvailable = false;
                StatusMessage = $"You're up to date (v{CurrentVersion}).";
            }
            else
            {
                TargetVersion = info.TargetVersion;
                IsUpdateAvailable = true;
                StatusMessage = $"Update available: v{info.TargetVersion}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual update check failed");
            StatusMessage = "Update check failed. See logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAndRestartAsync()
    {
        IsBusy = true;
        StatusMessage = "Downloading update…";
        try
        {
            await _updates.DownloadAndApplyAndRestartAsync().ConfigureAwait(true);
            // On success the process is replaced/restarted and does not return here.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying update failed");
            StatusMessage = "Update failed. See logs.";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => IsUpdateAvailable = false;
}
