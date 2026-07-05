using FFMedia.App.ViewModels;
using Wpf.Ui;                 // INavigationService
using Wpf.Ui.Controls;        // FluentWindow

namespace FFMedia.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel, INavigationService navigationService)
    {
        DataContext = viewModel;
        InitializeComponent();

        // NavigationService.SetNavigationControl also propagates the DI-backed
        // INavigationViewPageProvider (registered via AddNavigationViewPageProvider())
        // onto RootNavigation, so selecting a MenuItemsSource entry resolves its
        // TargetPageType through the app's service provider.
        navigationService.SetNavigationControl(RootNavigation);
    }
}
