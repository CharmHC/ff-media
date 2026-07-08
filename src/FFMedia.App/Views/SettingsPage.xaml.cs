using System.Windows.Controls;
using FFMedia.App.ViewModels;

namespace FFMedia.App.Views;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is FFMedia.App.ViewModels.SettingsViewModel vm)
            {
                vm.Binaries.RefreshVersionsCommand.Execute(null);
            }
        };
    }
}
