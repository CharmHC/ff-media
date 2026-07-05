using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FFMedia.Core.Tools;
using Wpf.Ui.Controls;

namespace FFMedia.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(IToolRegistry registry, IEnumerable<IToolPage> pages)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pages);

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
    }

    /// <summary>Navigation-pane entries, one per registered tool with a mapped page.</summary>
    [ObservableProperty]
    private ObservableCollection<object> _menuItems = new();
}
