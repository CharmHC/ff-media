using System;
using System.IO;
using System.Linq;
using FFMedia.Core;
using FFMedia.Core.Notifications;
using FFMedia.Core.Tools;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger;
using FFMedia.Tools.VideoMerger.Services;
using FFMedia.Tools.VideoMerger.ViewModels;
using FFMedia.Tools.VideoMerger.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class VideoMergerServiceCollectionTests
{
    /// <summary>The snackbar lives in the App layer, so Core does not register an
    /// <see cref="INotificationService"/>. The merger's ViewModel requires one — this stands in for
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
            .AddVideoMergerEngine(dataDirectory: temp, tempRoot: temp, maxConcurrency: 2)
            .BuildServiceProvider();
    }

    /// <summary>The whole thing, engine + UI, wired the way <c>App.xaml.cs</c> wires it.</summary>
    private static ServiceProvider BuildWithUi()
    {
        var temp = Path.GetTempPath();
        return new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddSingleton<INotificationService, NullNotifications>()
            .AddVideoMergerEngine(dataDirectory: temp, tempRoot: temp, maxConcurrency: 2)
            .AddVideoMerger()
            .BuildServiceProvider();
    }

    [Fact]
    public void AddVideoMergerEngine_ResolvesTheWholeEngine()
    {
        // Resolution, not registration: a factory that compiles but cannot construct its
        // dependencies fails only at runtime, in front of the user.
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<IMergeService>());
        Assert.NotNull(provider.GetRequiredService<IMediaAnalyzer>());
        Assert.NotNull(provider.GetRequiredService<IFfmpegRunner>());
        Assert.NotNull(provider.GetRequiredService<ISpeedProfileStore>());
    }

    [Fact]
    public void AddVideoMergerEngine_RegistersTheEngineAsSingletons()
    {
        // MergeService holds the one-merge-at-a-time contract (spec D8) and SpeedProfileStore backs
        // a file — a transient registration would give each caller its own.
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<IMergeService>(), provider.GetRequiredService<IMergeService>());
        Assert.Same(
            provider.GetRequiredService<ISpeedProfileStore>(), provider.GetRequiredService<ISpeedProfileStore>());
    }

    [Fact]
    public void AddVideoMergerEngine_RegistersNoTool_SoTheShellHasNothingToNavigateTo()
    {
        // The engine is headless: a host can merge without showing a page. Registering an ITool here
        // would put a nav item in the shell pointing at a page that host never asked for.
        using var provider = Build();

        Assert.Empty(provider.GetRequiredService<IToolRegistry>().Tools);
        Assert.Empty(provider.GetServices<ITool>());
        Assert.Empty(provider.GetServices<IToolPage>());
    }

    [Fact]
    public void AddVideoMerger_RegistersTheToolAndItsPage()
    {
        using var provider = BuildWithUi();

        var tool = Assert.Single(provider.GetServices<ITool>());
        Assert.Equal("video-merger", tool.Id);
        Assert.Equal("Video Merger", tool.DisplayName);
        Assert.Equal("Standardize and join clips into one video.", tool.Description);
        Assert.Equal("VideoClipMultiple24", tool.IconGlyph);

        var page = Assert.Single(provider.GetServices<IToolPage>());
        Assert.Equal("video-merger", page.ToolId);
        Assert.Equal(typeof(MergerPage), page.PageType);

        // The registry is what the shell actually reads.
        var registered = Assert.Single(provider.GetRequiredService<IToolRegistry>().Tools);
        Assert.Equal("video-merger", registered.Id);
    }

    /// <summary>The page must be registered IN THE CONTAINER, not merely referenced by the
    /// <see cref="IToolPage"/> descriptor.</summary>
    /// <remarks><para>The sibling test asserts <c>page.PageType == typeof(MergerPage)</c>, which is a
    /// COMPILE-TIME type reference: it passes with `AddTransient&lt;MergerPage&gt;()` deleted, because
    /// nothing in it ever asks the container for the page. The shell, however, resolves the page by
    /// type when it navigates — so losing that one line yields a null page in front of the user, and a
    /// green test suite.</para>
    /// <para>Asserted against the descriptor rather than by resolving it: a WPF <c>Page</c> cannot be
    /// constructed on this runner's MTA thread, so an actual resolution would fail for a reason that
    /// has nothing to do with the registration.</para></remarks>
    [Fact]
    public void AddVideoMerger_RegistersThePageInTheContainer_NotJustItsTypeName()
    {
        var temp = Path.GetTempPath();
        var services = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddSingleton<INotificationService, NullNotifications>()
            .AddVideoMergerEngine(dataDirectory: temp, tempRoot: temp, maxConcurrency: 2)
            .AddVideoMerger();

        var page = Assert.Single(services, d => d.ServiceType == typeof(MergerPage));
        Assert.Equal(ServiceLifetime.Transient, page.Lifetime);
    }

    [Fact]
    public void AddVideoMerger_ResolvesTheViewModel_SoNavigationCannotFailInFrontOfTheUser()
    {
        // A page whose ViewModel cannot be constructed throws at NAVIGATION time — the user clicks
        // the nav item and the app blows up. Resolve it here instead.
        using var provider = BuildWithUi();

        Assert.NotNull(provider.GetRequiredService<MergerViewModel>());
    }

    [Fact]
    public void AddVideoMerger_SharesOneViewModelAcrossNavigations_SoTheClipListSurvives()
    {
        // Deliberately NOT transient (which is what this shipped as, and what the downloader uses).
        // The merger's clip list lives in the ViewModel, so a transient one silently threw away every
        // clip the user had added the moment they clicked Settings and came back. The downloader gets
        // away with transient because its queue lives in a singleton DownloadManager, not the VM.
        using var provider = BuildWithUi();

        Assert.Same(
            provider.GetRequiredService<MergerViewModel>(), provider.GetRequiredService<MergerViewModel>());
    }

    [Fact]
    public void VideoMergerTool_IconGlyph_IsARealSymbolRegular_NotASilentApps24Fallback()
    {
        // The shell does Enum.TryParse and falls back to Apps24 on a miss (MainWindowViewModel), so a
        // typo does not fail — it just quietly shows the wrong icon. MergeDuplicate24, the first pick,
        // does not exist in Wpf.Ui 4.3.0 at all.
        var glyph = new VideoMergerTool().IconGlyph;

        Assert.True(Enum.TryParse<SymbolRegular>(glyph, ignoreCase: true, out var symbol));
        Assert.Equal(SymbolRegular.VideoClipMultiple24, symbol);
    }

    [Fact]
    public void VideoMergerTool_SortsAfterTheYouTubeDownloader()
    {
        // Ordering is ASCENDING and YouTubeDownloaderTool.SortOrder is 10. The spec says "2", meaning
        // "the second tool" — but 2 would sort it ABOVE the downloader, the opposite of its intent.
        var merger = new VideoMergerTool();
        var downloader = new global::FFMedia.Tools.YouTubeDownloader.YouTubeDownloaderTool();

        Assert.Equal(20, merger.SortOrder);
        Assert.True(merger.SortOrder > downloader.SortOrder);

        // And prove it through the registry the shell reads, not just the raw numbers.
        using var provider = new ServiceCollection()
            .AddSingleton<ITool>(merger)
            .AddSingleton<ITool>(downloader)
            .AddFFMediaCore(binariesDirectory: Path.GetTempPath(), dataDirectory: Path.GetTempPath())
            .BuildServiceProvider();

        Assert.Equal(
            new[] { "youtube-downloader", "video-merger" },
            provider.GetRequiredService<IToolRegistry>().Tools.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void AddVideoMergerEngine_RejectsZeroConcurrency()
    {
        var temp = Path.GetTempPath();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddVideoMergerEngine(temp, temp, maxConcurrency: 0));
    }

    [Theory]
    [InlineData("", "C:\\temp")]
    [InlineData("   ", "C:\\temp")]
    [InlineData("C:\\data", "")]
    [InlineData("C:\\data", "   ")]
    public void AddVideoMergerEngine_RejectsBlankDirectories(string dataDirectory, string tempRoot)
    {
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddVideoMergerEngine(dataDirectory, tempRoot, maxConcurrency: 1));
    }
}
