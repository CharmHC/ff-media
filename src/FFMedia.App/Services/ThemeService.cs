using FFMedia.Core.Settings;
using Wpf.Ui.Appearance;

namespace FFMedia.App.Services;

/// <summary>Applies the user's <see cref="AppTheme"/> preference via WPF-UI's theme manager.</summary>
public sealed class ThemeService
{
    public void Apply(AppTheme theme)
    {
        switch (theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }
}
