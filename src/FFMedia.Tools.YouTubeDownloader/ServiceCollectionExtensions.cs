using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Core.Settings;
using FFMedia.Core.Tools;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Navigation;
using FFMedia.Tools.YouTubeDownloader.Services;
using FFMedia.Tools.YouTubeDownloader.ViewModels;
using FFMedia.Tools.YouTubeDownloader.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FFMedia.Tools.YouTubeDownloader;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddYouTubeDownloader(this IServiceCollection services)
    {
        services.AddSingleton<ITool, YouTubeDownloaderTool>();
        services.AddSingleton<IToolPage>(new ToolPage("youtube-downloader", typeof(DownloaderPage)));
        services.AddSingleton<IYoutubeDlFactory, YoutubeDlFactory>();
        services.AddSingleton<IMediaProbe, YtDlpMediaProbe>();
        services.AddSingleton<IDownloadService, YtDlpDownloadService>();
        services.AddSingleton<RetryPolicy>(RetryPolicy.Default);
        services.AddSingleton<IDownloadManager>(sp => new DownloadManager(
            sp.GetRequiredService<IDownloadService>(),
            sp.GetRequiredService<RetryPolicy>(),
            Math.Max(1, sp.GetRequiredService<ISettingsService>().Current.MaxConcurrency),
            sp.GetService<IHistoryService>(),
            sp.GetService<INotificationService>()));
        services.AddSingleton<IPlaylistProbe, YtDlpPlaylistProbe>();
        services.AddTransient<DownloaderViewModel>();
        services.AddTransient<DownloaderPage>();
        return services;
    }
}
