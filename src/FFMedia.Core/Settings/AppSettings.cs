using System.IO;

namespace FFMedia.Core.Settings;

/// <summary>Persisted application settings (JSON at %AppData%\FFMedia\settings.json). The
/// <see cref="Version"/> field supports forward migration.</summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 3;
    public string DefaultOutputFolder { get; init; } = DefaultFolder();
    public int MaxConcurrency { get; init; } = 3;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public bool CheckForUpdatesOnStartup { get; init; } = true;
    public bool CheckYtDlpForUpdatesOnStartup { get; init; } = true;

    public static AppSettings Default => new();

    private static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFMedia");
}
