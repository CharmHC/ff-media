using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.History;

namespace FFMedia.App.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryService _history;

    public HistoryViewModel(IHistoryService history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
        _history.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(Refresh);
        Refresh();
    }

    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => Refresh();

    private void Refresh()
    {
        var filter = FilterText?.Trim() ?? string.Empty;
        var matches = _history.Query().Where(e =>
            filter.Length == 0
            || (e.Title?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Url?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Format?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

        Entries.Clear();
        foreach (var entry in matches)
            Entries.Add(entry);
    }

    [RelayCommand]
    private void Clear() => _history.Clear();

    [RelayCommand]
    private void OpenFile(HistoryEntry? entry)
    {
        if (entry?.OutputPath is not { } path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder(HistoryEntry? entry)
    {
        if (entry?.OutputPath is not { } path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
