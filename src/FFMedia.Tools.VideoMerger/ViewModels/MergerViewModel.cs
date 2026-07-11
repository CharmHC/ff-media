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

    /// <summary>True while a merge is running. The commands that flip it arrive in Task 7; it lives
    /// here because <see cref="CanMerge"/> — which this task owns — is defined in terms of it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    private bool _isMerging;

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

    /// <summary>The fit mode lives on the target, but the page binds a plain dropdown, so surface it
    /// as a settable scalar. Writing the value already in force is NOT an edit — a ComboBox echoes
    /// its own selection back when the page loads, and treating that as an override would freeze the
    /// target at whatever the first clip happened to imply.</summary>
    public FitMode SelectedFitMode
    {
        get => Target.FitMode;
        set
        {
            if (Target.FitMode != value)
            {
                Target = Target with { FitMode = value };
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

        // MergeCommand arrives in Task 7, along with the notification of its CanExecute. It cannot be
        // named here at all — `?.` would not help, the symbol does not exist yet.
    }

    private MergeTarget Derive()
        => Clips.Count == 0
            ? MergeTarget.Default // Derive() throws on an empty list, and there is nothing to propose
            : MergeTargetDerivation.Derive([.. Clips.Select(c => c.Clip.Info)]);

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
