using System.IO;
using System.Windows;
using FFMedia.App.ViewModels;
using FFMedia.Core;
using FFMedia.Tools.YouTubeDownloader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace FFMedia.App;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FFMedia");
        var binariesDir = Path.Combine(AppContext.BaseDirectory, "assets", "binaries");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(Path.Combine(appData, "logs", "ffmedia-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddFFMediaCore(binariesDir, appData);
                services.AddNavigationViewPageProvider();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddYouTubeDownloader();
                services.AddSingleton<FFMedia.App.Services.ThemeService>();
                services.AddSingleton<Wpf.Ui.ISnackbarService, Wpf.Ui.SnackbarService>();
                services.AddSingleton<FFMedia.Core.Notifications.INotificationService,
                    FFMedia.App.Services.SnackbarNotificationService>();
                services.AddSingleton<FFMedia.Core.Updates.IUpdateService,
                    FFMedia.App.Services.VelopackUpdateService>();
                services.AddSingleton<FFMedia.App.ViewModels.UpdateViewModel>();
                services.AddTransient<FFMedia.App.ViewModels.SettingsViewModel>();
                services.AddTransient<FFMedia.App.Views.SettingsPage>();
                services.AddTransient<FFMedia.App.ViewModels.HistoryViewModel>();
                services.AddTransient<FFMedia.App.Views.HistoryPage>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SDD §11 — no silent crashes: log every unhandled exception and show a friendly dialog.
        DispatcherUnhandledException += (_, args) =>
        {
            ReportFatal(args.Exception);
            args.Handled = true; // keep the app alive after a UI-thread exception
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportFatal(args.Exception);
            args.SetObserved();
        };

        await _host.StartAsync();

        var settings = _host.Services.GetRequiredService<FFMedia.Core.Settings.ISettingsService>();
        _host.Services.GetRequiredService<FFMedia.App.Services.ThemeService>().Apply(settings.Current.Theme);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        if (settings.Current.CheckForUpdatesOnStartup)
        {
            var updates = _host.Services.GetRequiredService<FFMedia.App.ViewModels.UpdateViewModel>();
            _ = updates.CheckOnStartupAsync(); // fire-and-forget; swallows+logs its own errors
        }
    }

    private static void ReportFatal(Exception? ex)
    {
        Log.Fatal(ex, "Unhandled exception");
        MessageBox.Show(
            ex?.Message ?? "An unexpected error occurred.",
            "FFMedia — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        Log.CloseAndFlush();
        _host.Dispose();
        base.OnExit(e);
    }
}
