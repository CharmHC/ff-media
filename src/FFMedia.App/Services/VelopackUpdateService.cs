using FFMedia.Core.Updates;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace FFMedia.App.Services;

/// <summary>
/// <see cref="IUpdateService"/> backed by Velopack against GitHub Releases. Velopack's
/// UpdateInfo/UpdateManager types never leak into Core — only <see cref="AppUpdateInfo"/> crosses out.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    /// <summary>The CANONICAL owner. The repo was renamed from <c>ChamHC-dev/ff-media</c>, and GitHub's
    /// rename redirect currently answers the old path — but that redirect is not a guarantee: it is
    /// dropped the moment anyone creates a repository at the old name. This service downloads and
    /// installs executables, so a stale URL is not a broken link, it is a supply-chain hole — an
    /// attacker squatting the abandoned name would be serving the update. Point at the real one.</summary>
    private const string RepoUrl = "https://github.com/CharmHC/ff-media";

    private readonly UpdateManager _manager;
    private readonly ILogger<VelopackUpdateService> _logger;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        // prerelease: false → stable channel only.
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    public async Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        // Not installed via Velopack (e.g. `dotnet run` in dev): nothing to update.
        if (!_manager.IsInstalled)
        {
            return null;
        }

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        _pending = info;
        return info is null ? null : new AppUpdateInfo(info.TargetFullRelease.Version.ToString());
    }

    public async Task DownloadAndApplyAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!_manager.IsInstalled)
        {
            return;
        }

        var info = _pending ?? await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
        {
            return;
        }

        await _manager.DownloadUpdatesAsync(info, p => progress?.Report(p), ct).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(info.TargetFullRelease); // exits the process
    }
}
