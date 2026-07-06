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

    public SettingsViewModel(ISettingsService settings, ThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        _settings = settings;
        _theme = theme;

        var current = settings.Current;
        _defaultOutputFolder = current.DefaultOutputFolder;
        _maxConcurrency = current.MaxConcurrency;
        _selectedTheme = current.Theme;
    }

    [ObservableProperty] private string _defaultOutputFolder;
    [ObservableProperty] private int _maxConcurrency;
    [ObservableProperty] private AppTheme _selectedTheme;
    [ObservableProperty] private string _statusMessage = string.Empty;

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
        };
        _settings.Save(updated);
        _theme.Apply(SelectedTheme);
        StatusMessage = "Settings saved. Concurrency changes take effect on next launch.";
    }
}
