using System.Windows.Controls;
using FFMedia.Tools.YouTubeDownloader.ViewModels;

namespace FFMedia.Tools.YouTubeDownloader.Views;

public partial class DownloaderPage : Page
{
    public DownloaderPage(DownloaderViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
