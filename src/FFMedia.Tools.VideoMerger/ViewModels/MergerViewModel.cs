using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Core.Settings;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;

namespace FFMedia.Tools.VideoMerger.ViewModels;

/// <summary>The Video Merger page's brain. Headless and fully unit-testable: every dependency is an
/// interface, and no clip is ever probed twice — the probe result rides along in the row.</summary>
public partial class MergerViewModel : ObservableObject
{
    private readonly IMediaAnalyzer _analyzer;
    private readonly IMergeService _merger;
    private readonly ISpeedProfileStore _speeds;
    private readonly IHistoryService _history;
    private readonly INotificationService _notifications;

    /// <summary>True only while <see cref="SetTarget"/> is re-deriving, so <see cref="OnTargetChanged"/>
    /// can tell a proposal apart from a user edit.</summary>
    private bool _isRederiving;

    /// <summary>Non-null exactly while a merge is in flight. Only one at a time (spec D8).</summary>
    private CancellationTokenSource? _cancellation;

    public MergerViewModel(
        IMediaAnalyzer analyzer,
        IMergeService merger,
        ISpeedProfileStore speeds,
        ISettingsService settings,
        IHistoryService history,
        INotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(merger);
        ArgumentNullException.ThrowIfNull(speeds);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(notifications);

        _analyzer = analyzer;
        _merger = merger;
        _speeds = speeds;
        _history = history;
        _notifications = notifications;

        OutputFolder = settings.Current.DefaultOutputFolder;
    }

    /// <summary>The clip list, in the order it will be concatenated. Bound directly to the page.</summary>
    public ObservableCollection<MergeClipViewModel> Clips { get; } = [];

    /// <summary>Seeds the shuffle. Settable so tests are deterministic; the UI re-seeds it from the
    /// clock on every Shuffle click.</summary>
    public int ShuffleSeed { get; set; } = Environment.TickCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    private string _outputFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    private string _outputFileName = "merged.mp4";

    /// <summary>True while a merge is running. Gates both commands: one merge at a time (spec D8).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    private bool _isMerging;

    /// <summary>0–100 for the whole merge. ffmpeg's real progress once merging starts — the §6.5
    /// estimate is only a guess to look at beforehand.</summary>
    [ObservableProperty] private double _overallPercent;

    /// <summary>What the merge is doing right now, in words, under the overall bar.</summary>
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>The standardization target every clip is conformed to. Derived from the clips, then
    /// freely overridable — see <see cref="IsTargetOverridden"/>.</summary>
    [ObservableProperty] private MergeTarget _target = MergeTarget.Default;

    /// <summary>Set the moment the user edits <see cref="Target"/> through its public setter. From
    /// then on the target is theirs: the clip list no longer re-derives it.</summary>
    [ObservableProperty] private bool _isTargetOverridden;

    /// <summary>The spec §6.5 line under the clip list.</summary>
    [ObservableProperty] private string _summary = NoClipsSummary;

    private const string NoClipsSummary = "Add at least two clips to merge.";

    public IReadOnlyList<FitMode> FitModes { get; } = Enum.GetValues<FitMode>();

    /// <summary>How a clip whose aspect does not match the target is fitted into it.</summary>
    /// <remarks><para>The fit mode rides on the target because that is what the engine consumes, but
    /// it is NOT a target override: it says nothing about the resolution, codec or rate we are aiming
    /// at, only about how a mismatched clip gets there. So choosing it must never latch
    /// <see cref="IsTargetOverridden"/>, and it must SURVIVE a re-derivation.</para>
    /// <para>Latching it would be a quiet disaster. Fit mode is a merge-wide preference, so setting it
    /// BEFORE adding any clips is a perfectly natural order — and if that froze the target, derivation
    /// would never run: two 4K clips would land against the 1080p default, be silently downscaled, and
    /// take the slow path, with no error shown anywhere.</para></remarks>
    public FitMode SelectedFitMode
    {
        get => Target.FitMode;
        set
        {
            if (Target.FitMode != value)
            {
                // Carry the existing override state through — never manufacture one.
                SetTarget(Target with { FitMode = value }, IsTargetOverridden);
            }
        }
    }

    public string OutputPath => Path.Combine(OutputFolder, OutputFileName);

    /// <summary>Merging a single clip is a copy, not a merge — and a second merge while one is
    /// already running would fight it for the temp directory.</summary>
    public bool CanMerge => Clips.Count >= 2 && !IsMerging;

    /// <summary>Probes each path and appends it. A file the analyzer cannot read — or one with no
    /// video track (an audio file) — is rejected here, at add time (spec §8): letting it into the
    /// list would fail the whole merge much later, after the user has ordered everything.</summary>
    [RelayCommand]
    public async Task AddClipsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var path in paths)
        {
            if (Clips.Any(c => string.Equals(c.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // already in the list — do not probe it again
            }

            var probe = await _analyzer.AnalyzeAsync(path).ConfigureAwait(true);
            if (!probe.IsSuccess || probe.Value is null || probe.Value.Video is null)
            {
                _notifications.Notify(new Notification(
                    "Not a video",
                    $"{Path.GetFileName(path)} could not be read as a video and was not added.",
                    NotificationSeverity.Warning));
                continue;
            }

            Clips.Add(new MergeClipViewModel(new MergeClip(path, probe.Value)));
        }

        Recompute();
    }

    [RelayCommand]
    public void RemoveClip(MergeClipViewModel clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (Clips.Remove(clip))
        {
            ResyncLocks();
            Recompute();
        }
    }

    [RelayCommand]
    public void MoveUp(MergeClipViewModel clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var index = Clips.IndexOf(clip);
        if (index > 0)
        {
            Clips.Move(index, index - 1);
            ResyncLocks();
        }
    }

    [RelayCommand]
    public void MoveDown(MergeClipViewModel clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var index = Clips.IndexOf(clip);
        if (index >= 0 && index < Clips.Count - 1)
        {
            Clips.Move(index, index + 1);
            ResyncLocks();
        }
    }

    /// <summary>Randomizes the order, leaving every locked row in the slot it occupies.</summary>
    [RelayCommand]
    public void Shuffle()
    {
        if (Clips.Count < 2)
        {
            return; // nothing to permute — and Ordering would be asked to shuffle a single slot
        }

        // Capture the locks BEFORE consulting them, not just after we rearrange. The page's lock
        // toggle two-way binds a checkbox straight to IsLocked, which does not go through SetLock,
        // so a freshly-ticked row has IsLocked = true but LockedIndex = null. Ordering.Shuffle reads
        // only LockedIndex — so without this the "locked" row would be shuffled like any other and
        // the lock then re-pinned to wherever it randomly landed. That is worse than the lock doing
        // nothing: the user asked for one thing and got the opposite.
        ResyncLocks();

        var shuffled = Ordering.Shuffle([.. Clips], c => c.LockedIndex, ShuffleSeed);

        // Selection-sort the live collection into the shuffled order: everything below i is already
        // final, so Move() only ever disturbs rows the loop has yet to place. Moving (rather than
        // clearing and re-adding) keeps the bound ListView's selection and virtualization intact.
        for (var i = 0; i < shuffled.Count; i++)
        {
            var current = Clips.IndexOf(shuffled[i]);
            if (current != i)
            {
                Clips.Move(current, i);
            }
        }

        ResyncLocks();
    }

    /// <summary>A locked row is pinned to the index it currently OCCUPIES. Removing or moving a
    /// *neighbour* shifts it, so every lock's index is re-captured after any structural change —
    /// otherwise <see cref="Ordering.Shuffle"/> would pin a row to a stale slot, or pin two rows to
    /// the same slot, which throws.</summary>
    private void ResyncLocks()
    {
        for (var i = 0; i < Clips.Count; i++)
        {
            if (Clips[i].IsLocked)
            {
                Clips[i].SetLock(locked: true, index: i);
            }
        }
    }

    /// <summary>Throws the user's edits away and goes back to the proposal the clips imply. The only
    /// way out of <see cref="IsTargetOverridden"/>.</summary>
    [RelayCommand]
    public void ResetTargetToDerived() => SetTarget(Derive(), overridden: false);

    // ---- merging -----------------------------------------------------------

    /// <summary>Runs the merge, streaming progress into the overall bar and each clip's row, and
    /// finishing with exactly one notification.</summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    public async Task MergeAsync()
    {
        _cancellation = new CancellationTokenSource();
        IsMerging = true;
        OverallPercent = 0;
        StatusMessage = "Preparing…";

        // A second merge must not open with the first one's bars already full.
        foreach (var clip in Clips)
        {
            clip.Percent = 0;
        }

        try
        {
            var request = new MergeRequest([.. Clips.Select(c => c.Clip)], Target, OutputPath);

            // A synchronous IProgress, NOT the BCL Progress<T> — that one posts to the captured
            // SynchronizationContext, so reports arrive out of order and headless tests race.
            // Reporting straight through, on ffmpeg's own worker thread, is safe here because every
            // property it touches is a SCALAR: ObservableObject raises PropertyChanged on the calling
            // thread and WPF marshals a scalar property change to the UI thread for us. The Clips
            // COLLECTION is never mutated mid-merge (both commands are disabled while IsMerging), and
            // that — not the scalars — is the thing WPF would throw on. Do not "fix" this into a
            // Dispatcher.Invoke: on the UI thread that is a re-entrant wait, and it is how this
            // deadlocks.
            var sink = new SyncProgress<MergeProgress>(OnMergeProgress);

            var result = await _merger.MergeAsync(request, sink, _cancellation.Token).ConfigureAwait(true);

            if (result.IsSuccess)
            {
                // Result<T>.Value is nullable even on success. Fall back to the path we asked for
                // rather than suppressing with `!` — a null here would otherwise land in history as a
                // row whose "open file" button does nothing.
                var saved = result.Value ?? request.OutputPath;

                OverallPercent = 100;
                StatusMessage = "Merge complete.";
                RecordInHistory(saved);
                _notifications.Notify(new Notification(
                    "Merge complete", $"Saved to {saved}", NotificationSeverity.Success));
                return;
            }

            if (_cancellation.IsCancellationRequested)
            {
                ReportCanceled();
                return;
            }

            var friendly = MergeErrors.Describe(result.Error);
            StatusMessage = friendly;
            _notifications.Notify(new Notification("Merge failed", friendly, NotificationSeverity.Error));
        }
        catch (OperationCanceledException)
        {
            // The engine returns a failure Result for a cancel today, but an OperationCanceledException
            // escaping it is the ordinary shape of a cancelled Task. Same destination either way.
            ReportCanceled();
        }
        catch (Exception ex)
        {
            // The engine promises never to throw for an expected failure. A bug in it must still not
            // wedge the page at IsMerging = true with a dead Merge button — and AsyncRelayCommand
            // would swallow the exception into its ExecutionTask, where nobody looks.
            var friendly = MergeErrors.Describe(ex.Message);
            StatusMessage = friendly;
            _notifications.Notify(new Notification("Merge failed", friendly, NotificationSeverity.Error));
        }
        finally
        {
            IsMerging = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Records the finished merge, tolerating a broken history sink.</summary>
    /// <remarks>The merge is DONE — the output file is sitting on disk. If <c>history.json</c> happens
    /// to be locked or unwritable, letting that exception escape would roll straight into the failure
    /// path and show a red "Merge failed" for a merge that in fact succeeded, sending the user hunting
    /// for a problem with their video. Losing a log row is a footnote; lying about the outcome is not.
    /// <c>DownloadManager</c> guards its sinks for the same reason.</remarks>
    private void RecordInHistory(string outputPath)
    {
        try
        {
            _history.Append(new HistoryEntry(
                Title: OutputFileName,
                Url: "",                       // a merge has no URL — its inputs are local files
                OutputPath: outputPath,
                Format: DescribeTarget(),
                Timestamp: DateTimeOffset.Now,
                Status: "Completed",
                Source: HistorySource.Merge));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Notify(new Notification(
                "History not updated",
                $"The merge finished, but it could not be recorded in History: {ex.Message}",
                NotificationSeverity.Warning));
        }
    }

    /// <summary>Cancellation is NOT failure. The engine reports it as a failure <c>Result</c>, but
    /// telling a user who just clicked Cancel that their merge "broke" is a lie — and a red toast for
    /// an action they took on purpose is worse than no toast at all. No history row either: a merge
    /// that did not finish is not a merge.</summary>
    private void ReportCanceled()
    {
        StatusMessage = "Merge canceled.";
        _notifications.Notify(new Notification(
            "Merge canceled", "Merge canceled.", NotificationSeverity.Info));
    }

    /// <summary>Only meaningful while a merge is running — there is nothing else to cancel.</summary>
    public bool CanCancel => IsMerging;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>Arrives on ffmpeg's worker thread. See <see cref="MergeAsync"/> for why that is fine.</summary>
    private void OnMergeProgress(MergeProgress progress)
    {
        OverallPercent = progress.OverallPercent;
        StatusMessage = progress.Status switch
        {
            MergeJobStatus.Normalizing => progress.CurrentClip is null
                ? "Standardizing clips…"
                : $"Standardizing {progress.CurrentClip}…",
            MergeJobStatus.Concatenating => "Joining clips…",
            _ => StatusMessage,
        };

        // ClipPercents is indexed by MergeRequest.Clips, which IS this list, in this order. Guarding
        // the length anyway: an out-of-range throw on a progress callback would take a merge that was
        // otherwise succeeding down with it.
        for (var i = 0; i < Clips.Count && i < progress.ClipPercents.Count; i++)
        {
            Clips[i].Percent = progress.ClipPercents[i];
        }
    }

    /// <summary>The history row's "Format" column: what this merge standardized everything to.</summary>
    private string DescribeTarget()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Target.Container.ToString().ToUpperInvariant()} · {Target.Width}x{Target.Height} · {Target.FrameRate.Value:0.###} fps");

    /// <summary>Both commands' enablement hangs off this flag, and ICommand does not listen to
    /// PropertyChanged — it has to be told.</summary>
    partial void OnIsMergingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        MergeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reports on the calling thread. See the comment in <see cref="MergeAsync"/> for why
    /// this is right and the BCL <see cref="Progress{T}"/> is not.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>The user edited the target through its public setter, so stop re-deriving it. Adding
    /// a 720p clip must not silently undo a 4K target they deliberately chose.</summary>
    /// <remarks>A re-derivation writes the same property, so it would trip this hook too — hence the
    /// explicit <see cref="_isRederiving"/> guard. Writing the backing field through
    /// <c>SetProperty(ref _target, …)</c> would also dodge the hook (CommunityToolkit generates the
    /// callback inside the property setter, not inside <c>SetProperty</c>), but the toolkit's own
    /// analyzer rejects touching an <c>[ObservableProperty]</c> field directly (MVVMTK0034) — and a
    /// warning is a build failure here.</remarks>
    partial void OnTargetChanged(MergeTarget value)
    {
        if (_isRederiving)
        {
            return; // SetTarget settles the flag and then notifies + refreshes exactly once
        }

        IsTargetOverridden = true;
        OnPropertyChanged(nameof(SelectedFitMode));
        RefreshBadgesAndSummary();
    }

    /// <summary>The clip list changed. Re-derive the target (unless the user has claimed it), then
    /// re-run every badge and the summary against whichever target is now in force.</summary>
    private void Recompute()
    {
        if (IsTargetOverridden)
        {
            RefreshBadgesAndSummary();
        }
        else
        {
            SetTarget(Derive(), overridden: false);
        }

        OnPropertyChanged(nameof(CanMerge));

        // A raised PropertyChanged does not reach ICommand: the button caches its verdict until the
        // command itself says otherwise. Without this the Merge button stays greyed out after the
        // user adds the second clip — the exact moment it is supposed to light up.
        MergeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the clips imply — keeping the user's fit mode, which derivation knows nothing
    /// about and must not silently reset (it is a preference, not something we can infer).</summary>
    private MergeTarget Derive()
    {
        var derived = Clips.Count == 0
            ? MergeTarget.Default // Derive() throws on an empty list, and there is nothing to propose
            : MergeTargetDerivation.Derive([.. Clips.Select(c => c.Clip.Info)]);

        return derived with { FitMode = Target.FitMode };
    }

    /// <summary>Writes the target WITHOUT it counting as a user override.</summary>
    /// <remarks>The notify-and-refresh runs here rather than in the hook because the generated setter
    /// no-ops on an equal value (<see cref="MergeTarget"/> is a record): re-deriving the SAME target
    /// after a clip was added still has to badge the new row and re-count the summary.</remarks>
    private void SetTarget(MergeTarget target, bool overridden)
    {
        _isRederiving = true;
        try
        {
            Target = target;
        }
        finally
        {
            _isRederiving = false;
        }

        IsTargetOverridden = overridden;
        OnPropertyChanged(nameof(SelectedFitMode));
        RefreshBadgesAndSummary();
    }

    /// <summary>Re-runs conformance for every row and rebuilds the summary. No probe: the
    /// <see cref="MediaInfo"/> is already in hand, so this is pure arithmetic over what we have.</summary>
    private void RefreshBadgesAndSummary()
    {
        foreach (var clip in Clips)
        {
            clip.ApplyTarget(Target);
        }

        Summary = BuildSummary();
    }

    private string BuildSummary()
    {
        if (Clips.Count < 2)
        {
            return NoClipsSummary;
        }

        var estimate = MergeEstimator.Estimate([.. Clips.Select(c => c.Clip)], Target, _speeds.Load());

        var reencodes = estimate.ReencodeCount switch
        {
            0 => "all clips conform",
            1 => "1 needs re-encoding",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} need re-encoding"),
        };

        var eta = estimate.IsFastPath
            ? "under 5s"
            : $"{Clock(estimate.LowEta)}–{Clock(estimate.HighEta)}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Clips.Count} clips · {Clock(estimate.OutputDuration)} output · {reencodes} · est. {eta}");
    }

    /// <summary>m:ss, or h:mm:ss past an hour. Invariant — this is a duration, not a local time.</summary>
    private static string Clock(TimeSpan span)
        => span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
