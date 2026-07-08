using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FFMedia.App.Views;
using FFMedia.Core.Tools;
using Wpf.Ui.Controls;

namespace FFMedia.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(
        IToolRegistry registry,
        IEnumerable<IToolPage> pages,
        UpdateViewModel updates)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(updates);
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
                Icon = new SymbolIcon { Symbol = SymbolRegular.History24 },
                TargetPageType = typeof(HistoryPage),
            },
            new NavigationViewItem
            {
                Content = "Settings",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
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
}
