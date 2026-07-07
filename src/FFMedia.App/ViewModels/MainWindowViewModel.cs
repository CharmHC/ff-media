using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.App.Services;
using FFMedia.App.Views;
using FFMedia.Core.Settings;
using FFMedia.Core.Tools;
using Wpf.Ui.Controls;

namespace FFMedia.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;

    public MainWindowViewModel(
        IToolRegistry registry,
        IEnumerable<IToolPage> pages,
        ISettingsService settings,
        ThemeService theme,
        UpdateViewModel updates)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(updates);
        _settings = settings;
        _theme = theme;
        Updates = updates;

        var pageById = pages.ToDictionary(p => p.ToolId, p => p.PageType);
        var items = new ObservableCollection<object>();
        foreach (var tool in registry.Tools)
        {
            if (!pageById.TryGetValue(tool.Id, out var pageType))
            {
                continue;
            }

            items.Add(new NavigationViewItem
            {
                Content = tool.DisplayName,
                Icon = new FontIcon { Glyph = tool.IconGlyph },
                TargetPageType = pageType,
            });
        }

        MenuItems = items;

        FooterMenuItems = new ObservableCollection<object>
        {
            new NavigationViewItem
            {
                Content = "History",
                Icon = new FontIcon { Glyph = "" }, // Segoe Fluent "History"
                TargetPageType = typeof(HistoryPage),
            },
            new NavigationViewItem
            {
                Content = "Settings",
                Icon = new FontIcon { Glyph = "" }, // Segoe Fluent settings gear
                TargetPageType = typeof(SettingsPage),
            },
        };
    }

    /// <summary>Navigation-pane entries, one per registered tool with a mapped page.</summary>
    [ObservableProperty]
    private ObservableCollection<object> _menuItems = new();

    /// <summary>Footer navigation entries (app-level pages, not tools).</summary>
    [ObservableProperty]
    private ObservableCollection<object> _footerMenuItems = new();

    /// <summary>Shared update state; the shell banner and Settings "check now" bind to this instance.</summary>
    public UpdateViewModel Updates { get; }

    /// <summary>Quick title-bar toggle between Light and Dark; persists the choice.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _settings.Current.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _theme.Apply(next);
        _settings.Save(_settings.Current with { Theme = next });
    }
}
