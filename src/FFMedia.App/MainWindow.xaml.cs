using FFMedia.App.ViewModels;
using FFMedia.App.Views;
using Wpf.Ui.Controls;

namespace FFMedia.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Navigate once the NavigationView's control template (and its internal
        // content presenter) has been applied. WPF-UI 4.3.0's NavigationView has no
        // externally settable Content/Frame, so navigation happens via Navigate().
        Loaded += (_, _) => RootNavigation.Navigate(typeof(WelcomePage));
    }
}
