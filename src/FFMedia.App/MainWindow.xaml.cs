using FFMedia.App.ViewModels;
using FFMedia.App.Views;      // WelcomePage
using Wpf.Ui;                 // INavigationService, ISnackbarService
using Wpf.Ui.Controls;        // FluentWindow

namespace FFMedia.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        ISnackbarService snackbarService)
    {
        DataContext = viewModel;
        InitializeComponent();

        // NavigationService.SetNavigationControl also propagates the DI-backed
        // INavigationViewPageProvider (registered via AddNavigationViewPageProvider())
        // onto RootNavigation, so selecting a MenuItemsSource entry resolves its
        // TargetPageType through the app's service provider.
        navigationService.SetNavigationControl(RootNavigation);

        // Point the snackbar service at the shell-owned presenter so notifications
        // raised anywhere (including off the UI thread) render here.
        snackbarService.SetSnackbarPresenter(RootSnackbar);

        // NavigationView selects nothing by default, so the content frame would be
        // blank until the user clicks a pane item. Land on the WelcomePage once the
        // navigation control is loaded (navigating earlier would no-op).
        RootNavigation.Loaded += (_, _) => navigationService.Navigate(typeof(WelcomePage));
    }
}
