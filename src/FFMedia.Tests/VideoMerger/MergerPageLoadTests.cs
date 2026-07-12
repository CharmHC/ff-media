using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Core.Results;
using FFMedia.Core.Settings;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using FFMedia.Tools.VideoMerger.ViewModels;
using FFMedia.Tools.VideoMerger.Views;
using Wpf.Ui.Controls;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

/// <summary>Proves <see cref="MergerPage"/>'s XAML actually LOADS.
///
/// <para>Everything else about the page is checked by the compiler or by eye, and neither catches the
/// failure that matters: a <c>StaticResource</c> that does not resolve compiles clean, passes every
/// other test, and then throws <c>XamlParseException</c> the first time a human clicks the nav item —
/// "Provide value on System.Windows.StaticResourceExtension threw an exception". That shipped once
/// (a <c>BasedOn</c> pointed at <c>{x:Type ListViewItem}</c>, but WPF-UI keys its implicit styles to
/// its OWN subclasses and ships nothing for the plain WPF ListView).</para>
///
/// <para>So: build the page for real, on an STA thread, against the same two resource dictionaries
/// App.xaml merges. If any resource lookup in the XAML is wrong, this fails here instead of in front
/// of the user.</para></summary>
public class MergerPageLoadTests
{
    [Fact]
    public void MergerPage_LoadsItsXaml_WithTheAppsRealResourceDictionaries()
    {
        var error = RunOnStaThread(() =>
        {
            // Mirrors App.xaml. Without ControlsDictionary every WPF-UI style lookup on the page fails,
            // which is exactly the class of bug under test.
            var app = Application.Current ?? new Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

            // InitializeComponent() — where the XAML is parsed and every StaticResource resolved.
            _ = new MergerPage(BuildViewModel());
        });

        Assert.True(error is null, $"MergerPage's XAML failed to load:\n{error}");
    }

    [Fact]
    public void MergerPage_DoesNotNestItsOwnScrollViewer_SoTheMouseWheelStillReachesTheShell()
    {
        // WPF-UI's NavigationViewContentPresenter ALREADY wraps every page in a ScrollViewer — which is
        // why no other page in this app has one. MergerPage shipped with a second, nested one. The outer
        // scroller hands the inner one unbounded height, so the inner can never scroll
        // (ScrollableHeight = 0) — but WPF's ScrollViewer marks mouse-wheel events HANDLED even when it
        // cannot move. So it swallowed every tick and the shell's scroller, which DID have room, never
        // saw them: the page was unscrollable everywhere except inside the clip list.
        double shellScrollable = 0;
        object? pageRoot = null;

        var error = RunOnStaThread(() =>
        {
            var app = Application.Current ?? new Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

            var page = new MergerPage(BuildViewModel());
            pageRoot = page.Content;

            // The shell's real host, in a window small enough that the page must overflow it.
            var presenter = new NavigationViewContentPresenter { Content = page };
            var window = new Window { Content = presenter, Width = 1100, Height = 700 };
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

        Assert.True(error is null, $"Hosting MergerPage threw:\n{error}");

        Assert.False(
            pageRoot is ScrollViewer,
            "MergerPage's root is a ScrollViewer. The shell already provides one; a nested scroller " +
            "cannot scroll and still swallows the mouse wheel.");

        Assert.True(
            shellScrollable > 0,
            $"The shell's ScrollViewer reports ScrollableHeight={shellScrollable}, so a page taller than " +
            "the window cannot be scrolled at all.");
    }

    [Fact]
    public void PinToggle_SwapsItsGlyph_WhenTheRowIsPinned()
    {
        // The pin replaced a checkbox, which the user read as "include this clip in the merge" — the
        // meaning every list UI gives a checkbox. It only ever exempted the row from Shuffle.
        //
        // The glyph swap is a DataTemplate.Trigger on a named element. A trigger that never fires is
        // invisible to the compiler AND to MergerPage_LoadsItsXaml (which proves only that the XAML
        // parses): the toggle would look permanently unpinned no matter how often it was clicked. So
        // realize the row for real and read the glyph out of the visual tree.
        SymbolRegular? unpinned = null;
        SymbolRegular? pinned = null;

        var error = RunOnStaThread(() =>
        {
            var app = Application.Current ?? new Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

            var vm = BuildViewModel();
            var page = new MergerPage(vm);
            var window = new Window { Content = page, Width = 1100, Height = 700 };
            window.Show();

            var clip = new MergeClipViewModel(new MergeClip(
                @"C:\a.mp4",
                new MediaInfo(TimeSpan.FromSeconds(5), "mov,mp4,m4a",
                    new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
                    new AudioStreamInfo("aac", 48_000, 2))));
            vm.Clips.Add(clip);

            window.UpdateLayout();
            unpinned = FindPinIcon(page)?.Symbol;

            clip.IsLocked = true;
            window.UpdateLayout();
            pinned = FindPinIcon(page)?.Symbol;

            window.Close();
        });

        Assert.True(error is null, $"Hosting MergerPage threw:\n{error}");
        Assert.Equal(SymbolRegular.PinOff24, unpinned);
        Assert.Equal(SymbolRegular.Pin24, pinned);
    }

    /// <summary>The first <see cref="SymbolIcon"/> inside the clip list's realized row.</summary>
    private static SymbolIcon? FindPinIcon(DependencyObject root)
    {
        if (root is ToggleButton toggle)
        {
            return Descendants<SymbolIcon>(toggle).FirstOrDefault();
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var hit = FindPinIcon(VisualTreeHelper.GetChild(root, i));
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static MergerViewModel BuildViewModel() => new(
        new StubAnalyzer(), new StubMergeService(), new StubSpeedStore(),
        new StubSettings(), new StubHistory(), new StubNotifications());

    /// <summary>WPF types demand an STA thread; xUnit runs tests on an MTA pool thread.</summary>
    private static Exception? RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }

    // ---- the thinnest possible stubs: this test is about XAML, not behaviour ----

    private sealed class StubAnalyzer : IMediaAnalyzer
    {
        public Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(Result<MediaInfo>.Failure("stub"));
    }

    private sealed class StubMergeService : IMergeService
    {
        public Task<Result<string>> MergeAsync(
            MergeRequest request, IProgress<MergeProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Failure("stub"));
    }

    private sealed class StubSpeedStore : ISpeedProfileStore
    {
        public SpeedProfile Load() => new();

        public void Save(SpeedProfile profile)
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
