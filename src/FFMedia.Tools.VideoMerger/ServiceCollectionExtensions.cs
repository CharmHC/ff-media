using System.IO;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Core.Tools;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Navigation;
using FFMedia.Tools.VideoMerger.Services;
using FFMedia.Tools.VideoMerger.ViewModels;
using FFMedia.Tools.VideoMerger.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFMedia.Tools.VideoMerger;

/// <summary>Registers the Video Merger. <see cref="AddVideoMergerEngine"/> is the headless engine and
/// registers no <c>ITool</c> — a host that wants the tool in its navigation pane also calls
/// <see cref="AddVideoMerger"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <param name="services">The app's service collection. Must already have <c>AddFFMediaCore</c>
    /// applied: the analyzer and the ffmpeg runner resolve Core's <see cref="IProcessRunner"/> and
    /// <see cref="IBinaryProvider"/>.</param>
    /// <param name="dataDirectory">Where <c>encode-speed.json</c> lives, e.g. <c>%AppData%\FFMedia</c>.</param>
    /// <param name="tempRoot">Root for <c>merge-&lt;guid&gt;</c> working directories, e.g. <c>%Temp%\FFMedia</c>.</param>
    /// <param name="maxConcurrency">Simultaneous clip normalizations (SDD §12).</param>
    public static IServiceCollection AddVideoMergerEngine(
        this IServiceCollection services, string dataDirectory, string tempRoot, int maxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        services.AddSingleton<IMediaAnalyzer>(sp => new FfprobeMediaAnalyzer(
            sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IBinaryProvider>()));
        services.AddSingleton<IFfmpegRunner>(sp => new FfmpegRunner(
            sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IBinaryProvider>()));
        services.AddSingleton<ISpeedProfileStore>(sp => new SpeedProfileStore(
            dataDirectory,
            sp.GetService<ILogger<SpeedProfileStore>>() ?? NullLogger<SpeedProfileStore>.Instance));
        services.AddSingleton<IMergeService>(sp => new MergeService(
            sp.GetRequiredService<IFfmpegRunner>(),
            sp.GetRequiredService<ISpeedProfileStore>(),
            GetFreeBytes,
            tempRoot,
            maxConcurrency,
            sp.GetService<ILogger<MergeService>>() ?? NullLogger<MergeService>.Instance,
            // Proves the finished file is whole: ffmpeg's concat exits 0 even when it silently
            // drops a segment, so the output's own duration is the only trustworthy evidence.
            sp.GetRequiredService<IMediaAnalyzer>()));

        return services;
    }

    /// <summary>Registers the Video Merger's UI: the tool metadata, the page the shell navigates to,
    /// and the page's ViewModel. The shell discovers both through <c>ITool</c>/<c>IToolPage</c> and is
    /// not modified.</summary>
    /// <remarks>Call <see cref="AddVideoMergerEngine"/> first — the ViewModel resolves
    /// <see cref="IMergeService"/>, <see cref="IMediaAnalyzer"/> and <see cref="ISpeedProfileStore"/>
    /// from it — and note that it also needs an <c>INotificationService</c>, which is realized in the
    /// App layer (the snackbar), not in Core.</remarks>
    public static IServiceCollection AddVideoMerger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ITool, VideoMergerTool>();
        services.AddSingleton<IToolPage>(new ToolPage("video-merger", typeof(MergerPage)));

        // Singleton, unlike the downloader's: the merger's clip list lives in the ViewModel (the
        // downloader's queue lives in a singleton DownloadManager instead), so a transient one loses
        // every clip the moment the user visits Settings and comes back. The list now survives
        // navigation and resets only when the app closes — which also keeps a merge running while the
        // user is on another page. MergerPage stays transient and only sets DataContext, so a fresh
        // page binds to the surviving ViewModel rather than reinitializing it.
        services.AddSingleton<MergerViewModel>();
        services.AddTransient<MergerPage>();

        return services;
    }

    /// <summary>The real free-space query. It lives in the composition root so
    /// <see cref="MergeService"/> stays testable without a real volume — the disk guard is the last
    /// thing between the user and a half-written merge, and it must be exercised in unit tests.</summary>
    /// <remarks>Returns 0 rather than throwing when the volume cannot be queried (a disconnected
    /// network share, a path on a vanished drive): 0 free bytes fails the guard, which is the safe
    /// direction — refuse the merge rather than start one we cannot finish.</remarks>
    private static long GetFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
