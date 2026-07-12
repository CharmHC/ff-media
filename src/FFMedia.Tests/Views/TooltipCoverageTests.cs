using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Core.Presets;
using FFMedia.Core.Results;
using FFMedia.Core.Settings;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using FFMedia.Tools.VideoMerger.ViewModels;
using FFMedia.Tools.VideoMerger.Views;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using FFMedia.Tools.YouTubeDownloader.ViewModels;
using FFMedia.Tools.YouTubeDownloader.Views;
using Xunit;

// Both layers define a MediaInfo (the probe's, and the downloader's own). Only the media one is used here.
using MediaInfo = FFMedia.Media.MediaInfo;

namespace FFMedia.Tests.Views;

/// <summary>Every control a user can *set* must explain itself.
///
/// <para>FFMedia's parameters are the vocabulary of video encoding — container, CRF, bitrate, fit mode,
/// sample rate. To the person who just wants to save a video they mean nothing, and a setting you cannot
/// weigh is a setting you cannot choose. So every input carries a plain-English tooltip that names the
/// trade-off, not just the definition.</para>
///
/// <para>This test walks the REAL page and fails if any input has no tooltip — because the failure mode
/// is silent: a control added later simply has nothing to say, and nobody notices until a user is stuck.
/// A tooltip on an ancestor counts (they inherit down the tree, and several are attached to the
/// label+control row so that hovering the LABEL — where a confused user actually points — works too).</para>
///
/// <para><b>Settings is not covered here.</b> `SettingsPage` lives in `FFMedia.App`, the WinExe, which
/// this test project deliberately does not reference (SDD §14). Its tooltips are verified by build and
/// by eye only — stated plainly rather than implied.</para></summary>
[Collection("wpf")]
public class TooltipCoverageTests
{
    private readonly WpfHost _wpf;

    public TooltipCoverageTests(WpfHost wpf) => _wpf = wpf;

    [Fact]
    public void EveryInputOnTheDownloaderPage_ExplainsItself()
    {
        AssertEveryInputHasATooltip(() => new DownloaderPage(new DownloaderViewModel(
            new StubPlaylistProbe(), new StubDownloadManager(), new StubSettings(), new StubPresets())));
    }

    [Fact]
    public void EveryInputOnTheMergerPage_ExplainsItself()
    {
        AssertEveryInputHasATooltip(() => new MergerPage(new MergerViewModel(
            new StubAnalyzer(), new StubMergeService(), new StubSpeedStore(),
            new StubSettings(), new StubHistory(), new StubNotifications())));
    }

    private void AssertEveryInputHasATooltip(Func<Page> buildPage)
    {
        var bare = new List<string>();

        var error = _wpf.Run(() =>
        {
            var page = buildPage();
            var window = new Window { Content = page, Width = 1100, Height = 900 };
            window.Show();
            window.UpdateLayout();

            foreach (var input in Descendants<Control>(page))
            {
                if (!IsUserSettableInput(input) || HasTooltipOnItselfOrAnAncestor(input))
                {
                    continue;
                }

                bare.Add($"{input.GetType().Name} ({Describe(input)})");
            }

            window.Close();
        });

        Assert.True(error is null, $"Hosting the page threw:\n{error}");
        Assert.True(
            bare.Count == 0,
            "These controls have no tooltip, so a user who does not already know what they mean has " +
            "nowhere to find out:\n  " + string.Join("\n  ", bare));
    }

    /// <summary>Controls the user SETS. Buttons that merely act (Move up, Remove) are excluded — their
    /// label already says what they do, and demanding a tooltip on each would be noise, not help. The
    /// point is the parameters, not the verbs.
    ///
    /// <para>Deliberately does NOT filter on <c>IsVisible</c>. Half the Downloader's parameters live in
    /// rows that are collapsed until you pick the matching output kind — the audio format and bitrate
    /// are hidden while "Video" is selected, and vice versa. Skipping hidden controls would have let
    /// exactly those ship with no tooltip while the test sat green, which is the hole this test exists
    /// to close. A collapsed element is still in the visual tree.</para></summary>
    private static bool IsUserSettableInput(Control control)
        => control is ComboBox or CheckBox or ToggleButton or TextBox;

    private static bool HasTooltipOnItselfOrAnAncestor(DependencyObject element)
    {
        for (DependencyObject? cur = element; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is FrameworkElement fe && fe.ToolTip is string text && !string.IsNullOrWhiteSpace(text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Enough to find the offending control in the XAML.</summary>
    private static string Describe(Control control) => control switch
    {
        CheckBox { Content: string label } => label,
        TextBox { Name.Length: > 0 } named => named.Name,
        _ => NearestLabel(control) ?? "unlabelled",
    };

    /// <summary>The first TextBlock among the control's siblings — i.e. its label.</summary>
    private static string? NearestLabel(DependencyObject control)
    {
        var parent = VisualTreeHelper.GetParent(control);
        return parent is null
            ? null
            : Descendants<TextBlock>(parent).Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
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

    // ---- the thinnest possible stubs: this test is about tooltips, not behaviour ----

    private sealed class StubPlaylistProbe : IPlaylistProbe
    {
        public Task<Result<IReadOnlyList<MediaEntry>>> ExpandAsync(string url, CancellationToken ct)
            => Task.FromResult(Result<IReadOnlyList<MediaEntry>>.Failure("stub"));
    }

    private sealed class StubDownloadManager : IDownloadManager
    {
        private readonly ObservableCollection<DownloadJob> _jobs = [];

        public StubDownloadManager() => Jobs = new ReadOnlyObservableCollection<DownloadJob>(_jobs);

        public ReadOnlyObservableCollection<DownloadJob> Jobs { get; }

        public DownloadJob Enqueue(DownloadJob job) => job;

        public void Cancel(DownloadJob job)
        {
        }

        public void CancelAll()
        {
        }

        public void ClearCompleted()
        {
        }

        public Task IdleAsync() => Task.CompletedTask;
    }

    private sealed class StubPresets : IPresetService
    {
        public event EventHandler? Changed;

        public IReadOnlyList<Preset> List() => [];

        public void Save(Preset preset) => Changed?.Invoke(this, EventArgs.Empty);

        public void Delete(string name) => Changed?.Invoke(this, EventArgs.Empty);
    }

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
