using System;
using System.IO;
using System.Linq;
using FFMedia.Core;
using FFMedia.Core.Notifications;
using FFMedia.Core.Tools;
using FFMedia.Media;
using FFMedia.Tools.GifMaker;
using FFMedia.Tools.GifMaker.Services;
using FFMedia.Tools.GifMaker.ViewModels;
using FFMedia.Tools.GifMaker.Views;
using FFMedia.Tools.VideoMerger;
using FFMedia.Tools.VideoMerger.Services;
using FFMedia.Ui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;
using Xunit;

namespace FFMedia.Tests.GifMaker;

/// <summary>Mirrors <c>VideoMergerServiceCollectionTests</c>: proves the DI wiring actually resolves
/// (a factory that compiles but cannot construct its dependencies fails only at runtime, in front of
/// the user) and pins the icon glyph against a silent fallback.</summary>
public class GifMakerServiceCollectionTests
{
    /// <summary>The snackbar lives in the App layer, so Core does not register an
    /// <see cref="INotificationService"/>. The GIF Maker's ViewModel requires one — this stands in for
    /// the App's <c>SnackbarNotificationService</c> so the resolution test exercises the real
    /// dependency graph.</summary>
    private sealed class NullNotifications : INotificationService
    {
        public void Notify(Notification notification)
        {
        }
    }

    private static ServiceProvider Build()
    {
        var temp = Path.GetTempPath();
        return new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddGifMakerEngine(dataDirectory: temp, tempRoot: temp)
            .BuildServiceProvider();
    }

    /// <summary>The whole thing, engine + UI, wired the way <c>App.xaml.cs</c> wires it.</summary>
    private static ServiceProvider BuildWithUi()
    {
        var temp = Path.GetTempPath();
        return new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddSingleton<INotificationService, NullNotifications>()
            .AddGifMakerEngine(dataDirectory: temp, tempRoot: temp)
            .AddGifMaker()
            .BuildServiceProvider();
    }

    [Fact]
    public void AddGifMakerEngine_ResolvesTheWholeEngine()
    {
        // Resolution, not registration: a factory that compiles but cannot construct its
        // dependencies fails only at runtime, in front of the user.
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<IGifService>());
        Assert.NotNull(provider.GetRequiredService<IMediaAnalyzer>());
        Assert.NotNull(provider.GetRequiredService<IFfmpegRunner>());
        Assert.NotNull(provider.GetRequiredService<IGifSizeProfileStore>());
    }

    [Fact]
    public void AddGifMakerEngine_RegistersTheEngineAsSingletons()
    {
        // IGifSizeProfileStore backs a file — a transient registration would give each caller its own,
        // and the estimate would never learn from the user's own past GIFs.
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<IGifService>(), provider.GetRequiredService<IGifService>());
        Assert.Same(
            provider.GetRequiredService<IGifSizeProfileStore>(),
            provider.GetRequiredService<IGifSizeProfileStore>());
    }

    [Fact]
    public void AddGifMakerEngine_RegistersNoTool_SoTheShellHasNothingToNavigateTo()
    {
        // The engine is headless: a host can create GIFs without showing a page. Registering an ITool
        // here would put a nav item in the shell pointing at a page that host never asked for.
        using var provider = Build();

        Assert.Empty(provider.GetRequiredService<IToolRegistry>().Tools);
        Assert.Empty(provider.GetServices<ITool>());
        Assert.Empty(provider.GetServices<IToolPage>());
    }

    [Fact]
    public void AddGifMaker_RegistersTheToolAndItsPage()
    {
        using var provider = BuildWithUi();

        var tool = Assert.Single(provider.GetServices<ITool>());
        Assert.Equal("gif-maker", tool.Id);
        Assert.Equal("GIF Maker", tool.DisplayName);
        Assert.Equal("Turn part of a video into a GIF.", tool.Description);
        Assert.Equal("Gif24", tool.IconGlyph);

        var page = Assert.Single(provider.GetServices<IToolPage>());
        Assert.Equal("gif-maker", page.ToolId);
        Assert.Equal(typeof(GifMakerPage), page.PageType);

        // The registry is what the shell actually reads.
        var registered = Assert.Single(provider.GetRequiredService<IToolRegistry>().Tools);
        Assert.Equal("gif-maker", registered.Id);
    }

    /// <summary>The page must be registered IN THE CONTAINER, not merely referenced by the
    /// <see cref="IToolPage"/> descriptor. See <c>VideoMergerServiceCollectionTests</c>'s sibling test
    /// for why: <c>page.PageType == typeof(GifMakerPage)</c> is a compile-time type reference that
    /// passes even with the registration deleted.</summary>
    [Fact]
    public void AddGifMaker_RegistersThePageInTheContainer_NotJustItsTypeName()
    {
        var temp = Path.GetTempPath();
        var services = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddSingleton<INotificationService, NullNotifications>()
            .AddGifMakerEngine(dataDirectory: temp, tempRoot: temp)
            .AddGifMaker();

        var page = Assert.Single(services, d => d.ServiceType == typeof(GifMakerPage));
        Assert.Equal(ServiceLifetime.Transient, page.Lifetime);
    }

    [Fact]
    public void AddGifMaker_ResolvesTheViewModel_SoNavigationCannotFailInFrontOfTheUser()
    {
        // A page whose ViewModel cannot be constructed throws at NAVIGATION time — the user clicks
        // the nav item and the app blows up. Resolve it here instead.
        using var provider = BuildWithUi();

        Assert.NotNull(provider.GetRequiredService<GifMakerViewModel>());
    }

    [Fact]
    public void AddGifMaker_SharesOneViewModelAcrossNavigations_SoTheLoadedVideoSurvives()
    {
        // Deliberately NOT transient. The loaded video and every chosen parameter live in the
        // ViewModel, so a transient one would throw all of it away the moment the user visits Settings
        // and comes back — the exact bug MergerViewModel had before it was made a singleton.
        using var provider = BuildWithUi();

        Assert.Same(
            provider.GetRequiredService<GifMakerViewModel>(),
            provider.GetRequiredService<GifMakerViewModel>());
    }

    /// <summary>FINDING (Task 5 review, MINOR 7). The video preview's ViewModel is the most cross-cutting
    /// thing this module registers — M10 rolls the same preview out to the Merger and the Downloader, and
    /// whichever of them registers it second would add a SECOND descriptor for the same singleton type.
    /// The plumbing three lines above it (<c>MediaElementPlayer</c>/<c>IMediaPlayer</c>) already uses
    /// <c>TryAdd</c> for exactly that reason.</summary>
    [Fact]
    public void AddGifMaker_RegistersThePreviewViewModelOnlyOnce_EvenIfAnotherModuleAlsoRegistersIt()
    {
        var temp = Path.GetTempPath();
        var services = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddSingleton<INotificationService, NullNotifications>()
            .AddGifMakerEngine(dataDirectory: temp, tempRoot: temp)
            .AddGifMaker()
            .AddGifMaker();   // stands in for a second module registering the same shared preview seam

        Assert.Single(services, d => d.ServiceType == typeof(VideoPreviewViewModel));
    }

    [Fact]
    public void GifMakerTool_IconGlyph_IsARealSymbolRegular_NotASilentApps24Fallback()
    {
        // The shell does Enum.TryParse and falls back to Apps24 on a miss (MainWindowViewModel), so a
        // typo does not fail — it just quietly shows the wrong icon.
        var glyph = new GifMakerTool().IconGlyph;

        Assert.True(Enum.TryParse<SymbolRegular>(glyph, ignoreCase: true, out var symbol));
        Assert.Equal(SymbolRegular.Gif24, symbol);
    }

    [Fact]
    public void GifMakerTool_SortsAfterTheVideoMerger()
    {
        // Ordering is ASCENDING: downloader 10, merger 20, GIF Maker 30 — the third tool.
        var gifMaker = new GifMakerTool();
        var merger = new global::FFMedia.Tools.VideoMerger.VideoMergerTool();
        var downloader = new global::FFMedia.Tools.YouTubeDownloader.YouTubeDownloaderTool();

        Assert.Equal(30, gifMaker.SortOrder);
        Assert.True(gifMaker.SortOrder > merger.SortOrder);

        // And prove it through the registry the shell reads, not just the raw numbers.
        using var provider = new ServiceCollection()
            .AddSingleton<ITool>(gifMaker)
            .AddSingleton<ITool>(merger)
            .AddSingleton<ITool>(downloader)
            .AddFFMediaCore(binariesDirectory: Path.GetTempPath(), dataDirectory: Path.GetTempPath())
            .BuildServiceProvider();

        Assert.Equal(
            new[] { "youtube-downloader", "video-merger", "gif-maker" },
            provider.GetRequiredService<IToolRegistry>().Tools.Select(t => t.Id).ToArray());
    }

    [Theory]
    [InlineData("", "C:\\temp")]
    [InlineData("   ", "C:\\temp")]
    [InlineData("C:\\data", "")]
    [InlineData("C:\\data", "   ")]
    public void AddGifMakerEngine_RejectsBlankDirectories(string dataDirectory, string tempRoot)
    {
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddGifMakerEngine(dataDirectory, tempRoot));
    }

    // --- Cross-module composition: what App.xaml.cs actually builds --------------------------------
    //
    // IMediaAnalyzer and IFfmpegRunner are cross-cutting FFMedia.Media services, but BOTH
    // AddVideoMergerEngine and AddGifMakerEngine self-register them. App.xaml.cs calls both engine
    // registrations in the same container, so without TryAddSingleton each interface would be
    // registered twice -- harmless only because both factories happen to wrap the same underlying
    // IProcessRunner/IBinaryProvider singletons, making "last one wins" a coincidence rather than a
    // guarantee. These tests build the container the real app builds, in BOTH orders, so the outcome
    // does not depend on which module's Add*Engine call happens to run first.

    [Fact]
    public void BothEnginesRegistered_MergerThenGifMaker_ShareOneAnalyzerAndOneRunner()
    {
        var temp = Path.GetTempPath();
        using var provider = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddVideoMergerEngine(dataDirectory: temp, tempRoot: temp, maxConcurrency: 2)
            .AddGifMakerEngine(dataDirectory: temp, tempRoot: temp)
            .BuildServiceProvider();

        Assert.Single(provider.GetServices<IMediaAnalyzer>());
        Assert.Single(provider.GetServices<IFfmpegRunner>());

        // Each module's own services must still resolve correctly.
        Assert.NotNull(provider.GetRequiredService<IMergeService>());
        Assert.NotNull(provider.GetRequiredService<ISpeedProfileStore>());
        Assert.NotNull(provider.GetRequiredService<IGifService>());
        Assert.NotNull(provider.GetRequiredService<IGifSizeProfileStore>());
    }

    [Fact]
    public void BothEnginesRegistered_GifMakerThenMerger_ShareOneAnalyzerAndOneRunner()
    {
        // Reverse registration order from the sibling test above: the whole point is that the
        // outcome must NOT depend on which module's Add*Engine call happens first in App.xaml.cs.
        var temp = Path.GetTempPath();
        using var provider = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddGifMakerEngine(dataDirectory: temp, tempRoot: temp)
            .AddVideoMergerEngine(dataDirectory: temp, tempRoot: temp, maxConcurrency: 2)
            .BuildServiceProvider();

        Assert.Single(provider.GetServices<IMediaAnalyzer>());
        Assert.Single(provider.GetServices<IFfmpegRunner>());

        Assert.NotNull(provider.GetRequiredService<IGifService>());
        Assert.NotNull(provider.GetRequiredService<IGifSizeProfileStore>());
        Assert.NotNull(provider.GetRequiredService<IMergeService>());
        Assert.NotNull(provider.GetRequiredService<ISpeedProfileStore>());
    }
}
