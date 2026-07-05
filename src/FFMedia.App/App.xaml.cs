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
                services.AddFFMediaCore(binariesDir);
                services.AddNavigationViewPageProvider();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddYouTubeDownloader();
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
        _host.Services.GetRequiredService<MainWindow>().Show();
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
