using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FFMedia.Tools.VideoMerger.ViewModels;

namespace FFMedia.Tools.VideoMerger.Views;

/// <summary>The Video Merger page. The only code here is what genuinely needs the visual tree — the
/// file/folder pickers and drag-drop. Everything else lives in <see cref="MergerViewModel"/>, which
/// is headless and unit-tested; a file dialog is not something a ViewModel should own, and a drag
/// gesture is not something one can express in a binding.</summary>
public partial class MergerPage : Page
{
    private readonly MergerViewModel _viewModel;

    /// <summary>Where the left button went down inside the clip list, and on which row — a drag only
    /// starts once the pointer has moved past the system drag threshold, or every click on a row
    /// would begin one.</summary>
    private Point _dragOrigin;
    private MergeClipViewModel? _dragCandidate;

    public MergerPage(MergerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    // ---- adding clips ------------------------------------------------------

    private async void OnPickClips(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Add clips",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.flv|All files|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.AddClipsAsync(dialog.FileNames);
        }
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the output folder",
            InitialDirectory = _viewModel.OutputFolder,
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.OutputFolder = dialog.FolderName;
        }
    }

    // ---- dropping files onto the page --------------------------------------

    private void OnDragOverPage(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files dropped anywhere on the page. A drop over the clip list that is NOT a reorder
    /// bubbles up to here, so dropping files onto the list itself works too.</summary>
    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            await _viewModel.AddClipsAsync(paths);
        }
    }

    // ---- dragging a row to reorder it --------------------------------------

    private void OnClipMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);

        // A press that lands on a row's own controls is not the start of a drag: nudging the mouse
        // while clicking Move up / Remove / the lock toggle would otherwise rip the row out from
        // under the click and swallow it into a DoDragDrop loop.
        _dragCandidate = IsInteractive(e.OriginalSource) ? null : RowUnder(e.OriginalSource);
    }

    /// <summary>True if the pointer is over a control that handles its own clicks (the row's buttons
    /// and its lock checkbox both derive from <see cref="ButtonBase"/>).</summary>
    private static bool IsInteractive(object? source)
    {
        for (var element = source as DependencyObject; element is not null;)
        {
            if (element is ButtonBase)
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element); // a Run inside a TextBlock has no visual parent
        }

        return false;
    }

    private void OnClipMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return; // a click, not a drag
        }

        var dragged = _dragCandidate;
        _dragCandidate = null; // DoDragDrop blocks until the drop; do not re-enter it on the next move

        DragDrop.DoDragDrop(ClipList, new DataObject(typeof(MergeClipViewModel), dragged), DragDropEffects.Move);
    }

    private void OnClipDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(MergeClipViewModel)))
        {
            return; // not a reorder — leave it to bubble to the page's file-drop handlers
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnClipDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(MergeClipViewModel)) is not MergeClipViewModel dragged)
        {
            return; // a file drop: let it bubble to OnFilesDropped
        }

        e.Handled = true;

        // Dropped on a row → take that row's slot. Dropped on empty space below the last row → go to
        // the end, which is what the gesture looks like it means.
        var target = RowUnder(e.OriginalSource);
        var index = target is null ? _viewModel.Clips.Count - 1 : _viewModel.Clips.IndexOf(target);

        _viewModel.MoveTo(dragged, index);
    }

    /// <summary>The clip whose row contains <paramref name="source"/>, or null if the pointer was not
    /// over a row. <c>ContainerFromElement</c> does the visual-tree walk, including the content-element
    /// hops (a TextBlock's inner Run) that a raw <c>VisualTreeHelper</c> loop would choke on.</summary>
    private MergeClipViewModel? RowUnder(object? source)
        => source is DependencyObject element
            && ClipList.ContainerFromElement(element) is ListViewItem row
                ? row.DataContext as MergeClipViewModel
                : null;
}
