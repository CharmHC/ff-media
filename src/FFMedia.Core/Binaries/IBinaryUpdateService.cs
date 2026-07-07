namespace FFMedia.Core.Binaries;

/// <summary>Reports bundled-binary versions and performs the yt-dlp self-update (SDD §9).</summary>
public interface IBinaryUpdateService
{
    /// <summary>Installed version of the bundled binary, or null if it can't be read.</summary>
    Task<string?> GetInstalledVersionAsync(ExternalBinary binary, CancellationToken ct = default);

    /// <summary>Latest published yt-dlp version if newer than installed; null if up to date or
    /// the check fails. yt-dlp has no offline check-only mode, so this queries GitHub.</summary>
    Task<string?> GetLatestYtDlpVersionAsync(CancellationToken ct = default);

    /// <summary>Runs <c>yt-dlp -U</c> and reports the outcome.</summary>
    Task<BinaryUpdateResult> UpdateYtDlpAsync(CancellationToken ct = default);
}
