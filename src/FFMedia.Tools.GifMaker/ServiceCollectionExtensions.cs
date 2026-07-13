using System.IO;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Core.Tools;
using FFMedia.Media;
using FFMedia.Media.Preview;
using FFMedia.Tools.GifMaker.Navigation;
using FFMedia.Tools.GifMaker.Services;
using FFMedia.Tools.GifMaker.ViewModels;
using FFMedia.Tools.GifMaker.Views;
using FFMedia.Ui.Playback;
using FFMedia.Ui.ViewModels;
using FFMedia.Ui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFMedia.Tools.GifMaker;

/// <summary>Registers the GIF Maker. <see cref="AddGifMakerEngine"/> is the headless engine and
/// registers no <c>ITool</c> — a host that wants the tool in its navigation pane also calls
/// <see cref="AddGifMaker"/>. Mirrors the Video Merger's split exactly.</summary>
public static class ServiceCollectionExtensions
{
    /// <param name="services">The app's service collection. Must already have <c>AddFFMediaCore</c>
    /// applied: the analyzer and the ffmpeg runner resolve Core's <see cref="IProcessRunner"/> and
    /// <see cref="IBinaryProvider"/>.</param>
    /// <param name="dataDirectory">Where <c>gif-size.json</c> lives, e.g. <c>%AppData%\FFMedia</c>.</param>
    /// <param name="tempRoot">Where the temporary palette image is written, e.g. <c>%Temp%\FFMedia</c>.</param>
    public static IServiceCollection AddGifMakerEngine(
        this IServiceCollection services, string dataDirectory, string tempRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);

        // IMediaAnalyzer and IFfmpegRunner are cross-cutting FFMedia.Media services, not owned by this
        // module -- the Video Merger registers the identical factories. TryAdd keeps registration
        // idempotent regardless of which module's Add*Engine call runs first in the composition root,
        // rather than relying on both factories happening to stay interchangeable ("last one wins").
        services.TryAddSingleton<IMediaAnalyzer>(sp => new FfprobeMediaAnalyzer(
            sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IBinaryProvider>()));
        services.TryAddSingleton<IFfmpegRunner>(sp => new FfmpegRunner(
            sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IBinaryProvider>()));

        // IPreviewProxyService (M9) is cross-cutting too -- the same reason as the two lines above:
        // a future module that also wants a video preview (the Merger/Downloader, M10) would otherwise
        // register a second, independent proxy cache pointed at the same temp directory.
        services.TryAddSingleton<IPreviewProxyService>(sp => new PreviewProxyService(
            sp.GetRequiredService<IFfmpegRunner>(), Path.Combine(tempRoot, "previews")));

        services.AddSingleton<IGifSizeProfileStore>(sp => new GifSizeProfileStore(
            dataDirectory,
            sp.GetService<ILogger<GifSizeProfileStore>>() ?? NullLogger<GifSizeProfileStore>.Instance));
        services.AddSingleton<IGifService>(sp => new GifService(
            sp.GetRequiredService<IFfmpegRunner>(),
            sp.GetRequiredService<IMediaAnalyzer>(),
            sp.GetRequiredService<IGifSizeProfileStore>(),
            tempRoot));

        return services;
    }

    /// <summary>Registers the GIF Maker's UI: the tool metadata, the page the shell navigates to, and
    /// the page's ViewModel. The shell discovers both through <c>ITool</c>/<c>IToolPage</c> and is not
    /// modified.</summary>
    /// <remarks>Call <see cref="AddGifMakerEngine"/> first — the ViewModel resolves
    /// <see cref="IMediaAnalyzer"/>, <see cref="IGifService"/> and <see cref="IGifSizeProfileStore"/>
    /// from it (and the preview resolves <see cref="IMediaAnalyzer"/> and
    /// <see cref="IPreviewProxyService"/>) — and note that it also needs an <c>ISettingsService</c>, an
    /// <c>IHistoryService</c> and an <c>INotificationService</c>, all of which are registered elsewhere
    /// in the composition root (the notification service is realized in the App layer, the snackbar,
    /// not in Core).</remarks>
    public static IServiceCollection AddGifMaker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Cross-cutting UI plumbing for the video preview (M9): a future module (the Merger/Downloader,
        // M10) may register the same seam, so these use TryAddSingleton exactly like IMediaAnalyzer/
        // IFfmpegRunner above -- the composition root already registers two other modules, and a
        // duplicate registration was a real M8 finding. MediaElementPlayer is registered as its OWN
        // concrete type (not just IMediaPlayer) because VideoPreview's constructor takes it directly --
        // the real MediaElement doesn't exist until the control's XAML parses, so the VM can't be handed
        // a real player at ITS construction time (see MediaElementPlayer's own doc comment).
        services.TryAddSingleton<MediaElementPlayer>();
        services.TryAddSingleton<IMediaPlayer>(sp => sp.GetRequiredService<MediaElementPlayer>());

        // Singleton, exactly like GifMakerViewModel: the loaded video, its position, and IsReady all
        // live in the ViewModel, so a transient one would throw the loaded preview away the moment the
        // user visits Settings and comes back. TryAdd for the same reason as the two lines above -- this
        // is the MOST cross-cutting thing here, and M10 rolls the same preview out to the Merger and the
        // Downloader; whichever module registered it second would otherwise add a duplicate descriptor.
        services.TryAddSingleton<VideoPreviewViewModel>();

        services.AddSingleton<ITool, GifMakerTool>();
        services.AddSingleton<IToolPage>(new ToolPage("gif-maker", typeof(GifMakerPage)));

        // Singleton, exactly like MergerViewModel: the loaded video and every chosen parameter live in
        // the ViewModel, so a transient one would throw all of it away the moment the user visits
        // Settings and comes back.
        services.AddSingleton<GifMakerViewModel>();
        services.AddTransient<VideoPreview>();
        services.AddTransient<GifMakerPage>();

        return services;
    }
}
