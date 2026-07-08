using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.App.Services;
using FFMedia.Core.Settings;
using Microsoft.Win32;

namespace FFMedia.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;

    public SettingsViewModel(
        ISettingsService settings, ThemeService theme, UpdateViewModel updates, BinaryUpdateViewModel binaries)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(binaries);
        _settings = settings;
        _theme = theme;
        Updates = updates;
        Binaries = binaries;

        var current = settings.Current;
        _defaultOutputFolder = current.DefaultOutputFolder;
        _maxConcurrency = current.MaxConcurrency;
        _selectedTheme = current.Theme;
        _checkForUpdatesOnStartup = current.CheckForUpdatesOnStartup;
        _checkYtDlpForUpdatesOnStartup = current.CheckYtDlpForUpdatesOnStartup;
    }

    [ObservableProperty] private string _defaultOutputFolder;
    [ObservableProperty] private int _maxConcurrency;
    [ObservableProperty] private AppTheme _selectedTheme;
    [ObservableProperty] private bool _checkForUpdatesOnStartup;
    [ObservableProperty] private bool _checkYtDlpForUpdatesOnStartup;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Shared update state (also drives the shell banner). Bound by the Settings "check now" UI.</summary>
    public UpdateViewModel Updates { get; }

    /// <summary>Shared binary-update state (also drives the startup yt-dlp check).</summary>
    public BinaryUpdateViewModel Binaries { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DefaultOutputFolder };
        if (dialog.ShowDialog() == true)
        {
            DefaultOutputFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var updated = _settings.Current with
        {
            DefaultOutputFolder = DefaultOutputFolder,
            MaxConcurrency = Math.Max(1, MaxConcurrency),
            Theme = SelectedTheme,
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
            CheckYtDlpForUpdatesOnStartup = CheckYtDlpForUpdatesOnStartup,
        };
        _settings.Save(updated);
        _theme.Apply(SelectedTheme);
        StatusMessage = "Settings saved. Concurrency changes take effect on next launch.";
    }
}
