using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using FFMedia.Core.Tools;

namespace FFMedia.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Tools = registry.Tools;
    }

    /// <summary>Tools shown in the navigation pane (empty in M0).</summary>
    public IReadOnlyList<ITool> Tools { get; }
}
