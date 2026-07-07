namespace FFMedia.Core.Updates;

/// <summary>Checks for and applies application updates. Realized in the App layer over Velopack.</summary>
public interface IUpdateService
{
    /// <summary>Returns the available update, or <c>null</c> if the app is up to date or updates
    /// are not applicable (e.g. running uninstalled in dev). Never throws for "no update".</summary>
    Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>Downloads the pending update and restarts into it. No-op if nothing to apply.</summary>
    Task DownloadAndApplyAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}
