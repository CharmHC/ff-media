using System.Windows;
using System.Windows.Controls;
using FFMedia.Tools.GifMaker.ViewModels;
using FFMedia.Ui.Views;

namespace FFMedia.Tools.GifMaker.Views;

/// <summary>The GIF Maker page. The only code here is what genuinely needs the visual tree — the
/// file/folder pickers, drag-drop, and hosting the <see cref="VideoPreview"/> control. Everything else
/// lives in <see cref="GifMakerViewModel"/>, which is headless and unit-tested; a file dialog is not
/// something a ViewModel should own, and a drag gesture is not something one can express in a
/// binding.</summary>
public partial class GifMakerPage : Page
{
    private readonly GifMakerViewModel _viewModel;

    /// <summary><paramref name="preview"/> is taken as its own constructor parameter — like
    /// <c>VideoPreview</c> itself, which separately takes the <c>MediaElementPlayer</c> singleton — so
    /// DI can hand this page a fully-built control (constructed with the SAME
    /// <see cref="GifMakerViewModel.Preview"/> instance the ViewModel already exposes) rather than this
    /// page trying to construct one itself with no access to the real player.</summary>
    public GifMakerPage(GifMakerViewModel viewModel, VideoPreview preview)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(preview);

        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        PreviewHost.Content = preview;
    }

    // ---- choosing a video ---------------------------------------------------

    private async void OnPickVideo(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = false,
            Title = "Choose a video",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.flv|All files|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.LoadVideoAsync(dialog.FileName);
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

    // ---- dropping a file onto the page --------------------------------------

    private void OnDragOverPage(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Only the FIRST dropped file is used — this is a one-video-at-a-time editor, not a
    /// queue. <see cref="GifMakerViewModel.LoadVideoAsync"/> itself guards against overwriting a
    /// render already in flight (the page's drag gesture never goes through the command's
    /// <c>CanExecute</c> at all), so no extra guard belongs here.</summary>
    private async void OnFileDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Handled = true;
            await _viewModel.LoadVideoAsync(paths[0]);
        }
    }
}
