using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FFMedia.Core;
using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Core.Results;
using FFMedia.Core.Settings;
using FFMedia.Media;
using FFMedia.Media.Preview;
using FFMedia.Tools.GifMaker;
using FFMedia.Tools.GifMaker.Models;
using FFMedia.Tools.GifMaker.Services;
using FFMedia.Tools.GifMaker.ViewModels;
using FFMedia.Tests.Views;
using FFMedia.Tools.GifMaker.Views;
using FFMedia.Ui.Playback;
using FFMedia.Ui.ViewModels;
using FFMedia.Ui.Views;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;
using Xunit;

namespace FFMedia.Tests.GifMaker;

/// <summary>Proves <see cref="GifMakerPage"/>'s XAML actually LOADS.
///
/// <para>Everything else about the page is checked by the compiler or by eye, and neither catches the
/// failure that matters: a <c>StaticResource</c> that does not resolve compiles clean, passes every
/// other test, and then throws <c>XamlParseException</c> the first time a human clicks the nav item.
/// Mirrors <c>MergerPageLoadTests</c> exactly, which caught precisely this failure once already.</para>
///
/// <para>So: build the page for real, on an STA thread, against the same two resource dictionaries
/// App.xaml merges. If any resource lookup in the XAML is wrong, this fails here instead of in front
/// of the user.</para></summary>
[Collection("wpf")]
public class GifMakerPageLoadTests
{
    private readonly WpfHost _wpf;

    public GifMakerPageLoadTests(WpfHost wpf) => _wpf = wpf;

    [Fact]
    public void GifMakerPage_LoadsItsXaml_WithTheAppsRealResourceDictionaries()
    {
        var error = RunOnStaThread(() =>
        {
            // InitializeComponent() — where the XAML is parsed and every StaticResource resolved.
            var vm = BuildViewModel();
            _ = new GifMakerPage(vm, BuildPreviewControl(vm));
        });

        Assert.True(error is null, $"GifMakerPage's XAML failed to load:\n{error}");
    }

    [Fact]
    public void GifMakerPage_DoesNotNestItsOwnScrollViewer_SoTheMouseWheelStillReachesTheShell()
    {
        // WPF-UI's NavigationViewContentPresenter ALREADY wraps every page in a ScrollViewer — which is
        // why no other page in this app has one. MergerPage shipped with a second, nested one once. The
        // outer scroller hands the inner one unbounded height, so the inner can never scroll
        // (ScrollableHeight = 0) — but WPF's ScrollViewer marks mouse-wheel events HANDLED even when it
        // cannot move. So it swallowed every tick and the shell's scroller, which DID have room, never
        // saw them.
        //
        // Everything below "Source" is collapsed until a video loads, so an EMPTY page is too short to
        // overflow any reasonably-sized window — that would prove nothing either way (ScrollableHeight
        // would read 0 whether or not a nested ScrollViewer exists). Loading a fake source first makes
        // every section visible, which is the tall, realistic state the bug actually manifested in.
        double shellScrollable = 0;
        object? pageRoot = null;

        var error = RunOnStaThread(() =>
        {
            var vm = BuildLoadedViewModel();
            var page = new GifMakerPage(vm, BuildPreviewControl(vm));
            pageRoot = page.Content;

            // The shell's real host, in a window small enough that the page must overflow it.
            var presenter = new NavigationViewContentPresenter { Content = page };
            var window = new Window { Content = presenter, Width = 1100, Height = 400 };
            window.Show();

            // A content presenter navigates on a dispatcher pass; without draining the queue the visual
            // tree does not exist yet and every measurement below reads 0.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();

            for (DependencyObject? cur = page; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (cur is ScrollViewer ancestor && !ReferenceEquals(cur, page))
                {
                    shellScrollable = ancestor.ScrollableHeight;
                    break;
                }
            }

            window.Close();
        });

        Assert.True(error is null, $"Hosting GifMakerPage threw:\n{error}");

        Assert.False(
            pageRoot is ScrollViewer,
            "GifMakerPage's root is a ScrollViewer. The shell already provides one; a nested scroller " +
            "cannot scroll and still swallows the mouse wheel.");

        Assert.True(
            shellScrollable > 0,
            $"The shell's ScrollViewer reports ScrollableHeight={shellScrollable}, so a page taller than " +
            "the window cannot be scrolled at all.");
    }

    /// <summary>FINDING (Task 5 review, IMPORTANT 3). Nothing proved the REAL container could construct
    /// this page any more. <c>GifMakerServiceCollectionTests</c> asserts only that a descriptor exists,
    /// and every test in this file hand-builds the page (<c>new GifMakerPage(vm, control)</c>), bypassing
    /// DI entirely — so the constructor parameter this task added could have had no registration behind
    /// it, the build would be clean, all 795 tests green, and the app would throw <c>Unable to resolve
    /// service for type 'FFMedia.Ui.Views.VideoPreview'</c> the first time the user clicked the nav item.
    ///
    /// <para>So: build the container <c>App.xaml.cs</c> builds and resolve the page out of it — on the STA
    /// thread, because <c>GifMakerPage</c> and <c>VideoPreview</c> are real WPF objects.</para></summary>
    [Fact]
    public void GifMakerPage_ResolvesFromTheRealContainer_SoClickingTheNavItemCannotThrow()
    {
        var temp = Path.GetTempPath();
        using var provider = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: temp, dataDirectory: temp)
            .AddSingleton<INotificationService, StubNotifications>()
            .AddGifMakerEngine(dataDirectory: temp, tempRoot: temp)
            .AddGifMaker()
            .BuildServiceProvider();

        GifMakerPage? page = null;

        var error = RunOnStaThread(() => page = provider.GetRequiredService<GifMakerPage>());

        Assert.True(error is null, $"The container could not construct GifMakerPage:\n{error}");
        Assert.NotNull(page);
    }

    private static GifMakerViewModel BuildViewModel() => new(
        new StubAnalyzer(), new StubGifService(), new StubGifSizeProfileStore(),
        new StubSettings(), new StubHistory(), new StubNotifications(),
        new VideoPreviewViewModel(new StubAnalyzer(), new StubProxies(), new StubPreviewPlayer()));

    /// <summary>A ViewModel with a source already loaded, so every collapsed section (Range, Output,
    /// estimate, file name/folder, actions, and the preview) is actually in the rendered layout. The
    /// analyzer stub returns an already-completed Task, so awaiting it inline does not deadlock the
    /// dispatcher -- and <see cref="StubPreviewPlayer"/> answers <c>Open</c> SYNCHRONOUSLY for the same
    /// reason: <c>LoadVideoAsync</c> now also awaits <c>Preview.LoadAsync</c>, and an unattached real
    /// <c>MediaElementPlayer</c> would never raise <c>MediaOpened</c>/<c>MediaFailed</c> on its own --
    /// hanging this inline <c>GetAwaiter().GetResult()</c> forever.</summary>
    private static GifMakerViewModel BuildLoadedViewModel()
    {
        var vm = new GifMakerViewModel(
            new SuccessAnalyzer(), new StubGifService(), new StubGifSizeProfileStore(),
            new StubSettings(), new StubHistory(), new StubNotifications(),
            new VideoPreviewViewModel(new SuccessAnalyzer(), new StubProxies(), new StubPreviewPlayer()));

        vm.LoadVideoAsync(@"C:\fake\video.mp4").GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>A real <see cref="VideoPreview"/> control to host in the page. Deliberately backed by a
    /// FRESH, unattached <see cref="MediaElementPlayer"/> rather than whatever <c>IMediaPlayer</c> backs
    /// <paramref name="vm"/>'s own <c>Preview</c> -- this test is about the PAGE's XAML loading, not
    /// about the preview and player agreeing, and <c>VideoPreview</c>'s constructor requires the
    /// concrete <see cref="MediaElementPlayer"/> type, not the (here, stubbed) interface.</summary>
    private static VideoPreview BuildPreviewControl(GifMakerViewModel vm) => new(vm.Preview, new MediaElementPlayer());

    /// <summary>Runs on the ONE shared STA thread that owns the ONE WPF Application (see
    /// <see cref="WpfHost"/>).</summary>
    private Exception? RunOnStaThread(Action action) => _wpf.Run(action);

    // ---- the thinnest possible stubs: this test is about XAML, not behaviour ----

    private sealed class StubAnalyzer : IMediaAnalyzer
    {
        public Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(Result<MediaInfo>.Failure("stub"));
    }

    /// <summary>Always reports a healthy 1080p/30fps/2-channel source, regardless of the path asked
    /// for — used only to put the page into its fully-populated visual state.</summary>
    private sealed class SuccessAnalyzer : IMediaAnalyzer
    {
        public Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(30), "mov,mp4,m4a",
                new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
                new AudioStreamInfo("aac", 48_000, 2))));
    }

    /// <summary>Answers <c>Open</c> synchronously — see <see cref="BuildLoadedViewModel"/>'s doc comment
    /// for why an unattached real <c>MediaElementPlayer</c> would hang this file's inline
    /// <c>GetAwaiter().GetResult()</c> instead.</summary>
    private sealed class StubPreviewPlayer : IMediaPlayer
    {
        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public event EventHandler? MediaOpened;

#pragma warning disable CS0067 // This stub never fails an open or reaches the end; IMediaPlayer still requires the events.
        public event EventHandler<string>? MediaFailed;

        public event EventHandler? MediaEnded;
#pragma warning restore CS0067

        public void Open(string path)
        {
            Duration = TimeSpan.FromSeconds(30);
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;
    }

    private sealed class StubProxies : IPreviewProxyService
    {
        public Task<Result<string>> GetOrCreateAsync(
            string sourcePath, MediaInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success(sourcePath));

        public void SweepStale()
        {
        }
    }

    private sealed class StubGifService : IGifService
    {
        public Task<Result<string>> CreateAsync(
            GifRequest request, IProgress<GifProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Failure("stub"));
    }

    private sealed class StubGifSizeProfileStore : IGifSizeProfileStore
    {
        public GifSizeProfile Load() => new();

        public void Save(GifSizeProfile profile)
        {
        }
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = AppSettings.Default;

        public event EventHandler<AppSettings>? Changed;

        public void Save(AppSettings settings) => Changed?.Invoke(this, settings);
    }

    private sealed class StubHistory : IHistoryService
    {
        public event EventHandler? Changed;

        public IReadOnlyList<HistoryEntry> Query() => [];

        public void Append(HistoryEntry entry) => Changed?.Invoke(this, EventArgs.Empty);

        public void Clear() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class StubNotifications : INotificationService
    {
        public void Notify(Notification notification)
        {
        }
    }
}
