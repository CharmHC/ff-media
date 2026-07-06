using System.Windows.Controls;
using FFMedia.App.ViewModels;

namespace FFMedia.App.Views;

public partial class HistoryPage : Page
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
