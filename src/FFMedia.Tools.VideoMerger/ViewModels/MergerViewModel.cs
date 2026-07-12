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

    /// <summary>Seeds the NEXT shuffle. Settable so tests are deterministic; <see cref="Shuffle"/>
    /// re-seeds it after each use, so consecutive clicks do not replay one permutation.</summary>
    public int ShuffleSeed { get; set; } = Environment.TickCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    private string _outputFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
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

    // ---- the target, projected one field at a time (spec §7.3: "every field overridable") --------

    /// <summary>Rounds an overridden dimension DOWN to even, exactly as
    /// <see cref="MergeTargetDerivation"/> does when it derives one.</summary>
    /// <remarks>yuv420p subsamples chroma 2×2, so libx264 rejects an odd width or height outright —
    /// the derivation rounds for that reason and says so at length. Overriding the field by hand must
    /// not be a way back around it: type 1921 and every clip's normalize pass would die on
    /// <c>scale=1921:1081</c>, after the preflight has already promised the merge would work. Silently
    /// snapping to 1920 is the same answer the derived path would have given.</remarks>
    private static int ToEven(int value) => value - (value % 2);

    /// <remarks><para>The page edits the target through these projections rather than binding into
    /// <see cref="Target"/> itself, for two reasons. <see cref="MergeTarget"/> is a record, so its
    /// properties are <c>init</c>-only — a two-way binding to <c>Target.Width</c> has no setter to
    /// call. And a record edit that lands on the value already there (<c>Target with { Width = 1920 }</c>
    /// when the width IS 1920) no-ops the generated setter, so the <c>[ObservableProperty]</c> hook
    /// never runs and an override that leaned on it would silently fail to latch.</para>
    /// <para>Each setter therefore commits through <see cref="OverrideTarget"/>, which latches
    /// <see cref="IsTargetOverridden"/> EXPLICITLY. Writing back the value that is already there is
    /// still a no-op — a ComboBox echoing its own selection on load must not be mistaken for a user
    /// edit (the same rule <see cref="SelectedFitMode"/> follows).</para></remarks>
    public int TargetWidth
    {
        get => Target.Width;
        set
        {
            // A non-positive dimension cannot be encoded either. Ignore it rather than committing a
            // target the merge would only fail on, minutes later.
            var even = ToEven(value);
            if (even > 0 && even != Target.Width)
            {
                OverrideTarget(Target with { Width = even });
            }
        }
    }

    public int TargetHeight
    {
        get => Target.Height;
        set
        {
            var even = ToEven(value);
            if (even > 0 && even != Target.Height)
            {
                OverrideTarget(Target with { Height = even });
            }
        }
    }

    /// <summary>x264/x265 constant rate factor: 0 (lossless) – 51 (worst). Out-of-range values are
    /// ignored; ffmpeg would reject them.</summary>
    public int TargetCrf
    {
        get => Target.Crf;
        set
        {
            if (value is >= 0 and <= 51 && value != Target.Crf)
            {
                OverrideTarget(Target with { Crf = value });
            }
        }
    }

    public MergeVideoCodec SelectedVideoCodec
    {
        get => Target.VideoCodec;
        set
        {
            if (value != Target.VideoCodec)
            {
                OverrideTarget(Target with { VideoCodec = value });
            }
        }
    }

    public MergeAudioCodec SelectedAudioCodec
    {
        get => Target.AudioCodec;
        set
        {
            if (value != Target.AudioCodec)
            {
                OverrideTarget(Target with { AudioCodec = value });
            }
        }
    }

    public int TargetAudioSampleRate
    {
        get => Target.AudioSampleRate;
        set
        {
            if (value > 0 && value != Target.AudioSampleRate)
            {
                OverrideTarget(Target with { AudioSampleRate = value });
            }
        }
    }

    public int TargetAudioChannels
    {
        get => Target.AudioChannels;
        set
        {
            if (value > 0 && value != Target.AudioChannels)
            {
                OverrideTarget(Target with { AudioChannels = value });
            }
        }
    }

    /// <summary>The container the merged file is written in — and, because ffmpeg picks its muxer
    /// from the output file's EXTENSION (<see cref="ConcatArgsBuilder"/> emits no <c>-f</c>), the
    /// thing that decides what <see cref="OutputFileName"/> must end in. The two are kept in lockstep
    /// by <see cref="SyncFileNameToContainer"/> and <see cref="OnOutputFileNameChanged"/>: choose MKV
    /// and you get an MKV, not an MP4 wearing an .mkv name — or, as it was before, the reverse.</summary>
    public MergeContainer SelectedContainer
    {
        get => Target.Container;
        set
        {
            if (value != Target.Container)
            {
                OverrideTarget(Target with { Container = value });
            }
        }
    }

    public IReadOnlyList<MergeContainer> Containers { get; } = Enum.GetValues<MergeContainer>();

    public IReadOnlyList<MergeVideoCodec> VideoCodecs { get; } = Enum.GetValues<MergeVideoCodec>();

    public IReadOnlyList<MergeAudioCodec> AudioCodecs { get; } = Enum.GetValues<MergeAudioCodec>();

    /// <summary>The rates the Output panel offers. Not a fixed list: a derived rate that is not one
    /// of the standards (a 12 fps timelapse, say) is added to it, or the ComboBox would show a blank
    /// selection and the user could not see the rate their own clips implied.</summary>
    public ObservableCollection<FrameRateOption> FrameRates { get; } =
    [
        new(new FrameRate(24000, 1001)), new(new FrameRate(24, 1)), new(new FrameRate(25, 1)),
        new(new FrameRate(30000, 1001)), new(new FrameRate(30, 1)), new(new FrameRate(50, 1)),
        new(new FrameRate(60000, 1001)), new(new FrameRate(60, 1)),
    ];

    public FrameRateOption? SelectedFrameRate
    {
        get => FrameRates.FirstOrDefault(option => option.Rate == Target.FrameRate);
        set
        {
            // A ComboBox pushes null while its ItemsSource is being rebuilt. Ignore it: null is not
            // a frame rate the user asked for.
            if (value is not null && value.Rate != Target.FrameRate)
            {
                OverrideTarget(Target with { FrameRate = value.Rate });
            }
        }
    }

    /// <summary>One entry in the frame-rate dropdown. A rational (30000/1001) shown as a decimal
    /// (29.97 fps), invariant — a frame rate is a stream parameter, not localized prose.</summary>
    public sealed record FrameRateOption(FrameRate Rate)
    {
        public string Label => string.Create(CultureInfo.InvariantCulture, $"{Rate.Value:0.###} fps");
    }

    public string OutputPath => Path.Combine(OutputFolder, OutputFileName);

    /// <summary>The output name must actually name a FILE. Blank is the case that matters: the box is
    /// editable, so clearing it is one keystroke, and <see cref="Path.Combine"/> then quietly yields
    /// the FOLDER — which we would hand to ffmpeg as the thing to write.</summary>
    /// <remarks>The damage is out of all proportion to the typo, because the concat is phase TWO: the
    /// merge would re-encode every clip first, for however many minutes that takes, and only then die
    /// on a raw ffmpeg error about a path that is a directory. Refuse it up front instead — this is
    /// the same reason the disk guard runs before any encoding rather than after.</remarks>
    private bool HasValidOutputFileName =>
        !string.IsNullOrWhiteSpace(OutputFileName)
        && OutputFileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Merging a single clip is a copy, not a merge — and a second merge while one is
    /// already running would fight it for the temp directory.</summary>
    public bool CanMerge => Clips.Count >= 2 && !IsMerging && HasValidOutputFileName;

    /// <summary>The clip list is FROZEN while a merge runs. Every mutation path is gated on this —
    /// the buttons through <c>CanExecute</c>, and the gestures (file drop, drag-to-reorder) through an
    /// explicit guard, because they do not go through a command at all.</summary>
    /// <remarks><para>Three separate things break if the list moves under a running merge, and the
    /// worst is not the obvious one.</para>
    /// <para><b>It corrupts the display.</b> <c>MergeProgress.ClipPercents</c> is indexed by position
    /// in the <see cref="MergeRequest"/> snapshot taken when Merge was clicked. Reorder the rows and
    /// row N starts showing clip M's progress.</para>
    /// <para><b>It makes the page lie.</b> The list would no longer describe the merge that is
    /// actually running: a removed clip is still in the output, and an added one re-derives
    /// <see cref="Target"/> — which can rewrite <see cref="OutputFileName"/> out from under a merge
    /// already writing to the old path.</para>
    /// <para><b>And it can kill the process.</b> <see cref="OnMergeProgress"/> runs on ffmpeg's stdout
    /// callback thread and indexes <c>Clips</c>. A concurrent mutation from the UI thread makes that
    /// throw <see cref="ArgumentOutOfRangeException"/> inside a <c>Process.OutputDataReceived</c>
    /// handler — which has no catch anywhere up the stack, so it takes the whole app down and strands
    /// the temp directory. Freezing the list is what makes reporting straight through on the worker
    /// thread safe (see the note in <see cref="MergeAsync"/>).</para></remarks>
    public bool CanEditClips => !IsMerging;

    /// <summary>Probes each path and appends it. A file the analyzer cannot read — or one with no
    /// video track (an audio file) — is rejected here, at add time (spec §8): letting it into the
    /// list would fail the whole merge much later, after the user has ordered everything.</summary>
    [RelayCommand(CanExecute = nameof(CanEditClips))]
    public async Task AddClipsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (IsMerging)
        {
            return; // the page's file-DROP handler does not go through the command's CanExecute
        }

        foreach (var path in paths)
        {
            if (Clips.Any(c => string.Equals(c.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // already in the list — do not probe it again
            }

            var probe = await _analyzer.AnalyzeAsync(path).ConfigureAwait(true);

            // A probe that FAILED and a probe that succeeded on a file with no video track are two
            // different problems, and lumping them together actively misleads. ffprobe.exe is
            // git-ignored and fetched by build/fetch-binaries.ps1 — when it is missing, EVERY file
            // fails to probe, and reporting that as "not a video" blames the user's perfectly good
            // .mp4 for a missing binary. The analyzer already says what actually went wrong; say it.
            if (!probe.IsSuccess || probe.Value is null)
            {
                _notifications.Notify(new Notification(
                    $"Could not read {Path.GetFileName(path)}",
                    probe.Error ?? "The file could not be analyzed.",
                    NotificationSeverity.Warning));
                continue;
            }

            if (probe.Value.Video is null)
            {
                _notifications.Notify(new Notification(
                    "Not a video",
                    $"{Path.GetFileName(path)} has no video track and was not added.",
                    NotificationSeverity.Warning));
                continue;
            }

            Clips.Add(new MergeClipViewModel(new MergeClip(path, probe.Value)));
        }

        Recompute();
    }

    [RelayCommand(CanExecute = nameof(CanEditClips))]
    public void RemoveClip(MergeClipViewModel clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (IsMerging)
        {
            return;
        }

        if (Clips.Remove(clip))
        {
            ResyncLocks();
            Recompute();
        }
    }

    /// <summary>Empties the clip list.</summary>
    [RelayCommand(CanExecute = nameof(CanEditClips))]
    public void ClearClips()
    {
        if (IsMerging)
        {
            return; // a mutator like any other — the merge holds a snapshot indexed by position
        }

        if (Clips.Count == 0)
        {
            return;
        }

        Clips.Clear();
        Recompute(); // no ResyncLocks: there is nothing left to pin
    }

    [RelayCommand(CanExecute = nameof(CanEditClips))]
    public void MoveUp(MergeClipViewModel clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (IsMerging)
        {
            return;
        }

        var index = Clips.IndexOf(clip);
        if (index > 0)
        {
            Clips.Move(index, index - 1);
            ResyncLocks();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditClips))]
    public void MoveDown(MergeClipViewModel clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (IsMerging)
        {
            return;
        }

        var index = Clips.IndexOf(clip);
        if (index >= 0 && index < Clips.Count - 1)
        {
            Clips.Move(index, index + 1);
            ResyncLocks();
        }
    }

    /// <summary>Drops <paramref name="clip"/> at <paramref name="index"/> — what the page's
    /// drag-to-reorder ends in. An index outside the list, or a clip that is not in it, is ignored
    /// rather than throwing: a drag that lands on empty space below the last row is a normal thing
    /// for a user to do, not an error.</summary>
    public void MoveTo(MergeClipViewModel clip, int index)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (IsMerging)
        {
            return; // a drag gesture bypasses CanExecute entirely — see CanEditClips
        }

        var current = Clips.IndexOf(clip);
        if (current < 0 || index < 0 || index >= Clips.Count || index == current)
        {
            return;
        }

        Clips.Move(current, index);
        ResyncLocks();
    }

    /// <summary>Randomizes the order, leaving every locked row in the slot it occupies.</summary>
    [RelayCommand(CanExecute = nameof(CanEditClips))]
    public void Shuffle()
    {
        if (IsMerging)
        {
            return;
        }

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

        // Re-seed, or every click replays the SAME permutation for the life of the page — and any
        // index that permutation maps to itself is a row the user can never move, however many times
        // they click. (It is a fixed seed, not a fixed shuffle: Ordering.Shuffle is unbiased.)
        ShuffleSeed = Random.Shared.Next();
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

    /// <summary>Every command's enablement hangs off this flag, and ICommand does not listen to
    /// PropertyChanged — it has to be told. The list commands are here too: while a merge runs the
    /// clip list is frozen (see <see cref="CanEditClips"/>), and a button that stays lit is a button
    /// the user will press.</summary>
    partial void OnIsMergingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanEditClips));
        MergeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        AddClipsCommand.NotifyCanExecuteChanged();
        RemoveClipCommand.NotifyCanExecuteChanged();
        ClearClipsCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        ShuffleCommand.NotifyCanExecuteChanged();
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
        SyncFileNameToContainer(value.Container);
        NotifyTargetProjections();
        RefreshBadgesAndSummary();
    }

    /// <summary>Commits a target edit made on the page. Everything the Output panel writes goes
    /// through here so the override latches EXPLICITLY rather than relying on the record's setter
    /// having actually changed a value.</summary>
    private void OverrideTarget(MergeTarget target) => SetTarget(target, overridden: true);

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
        SyncFileNameToContainer(Target.Container);
        NotifyTargetProjections();
        RefreshBadgesAndSummary();
    }

    /// <summary>Every field of the target is projected as its own bindable property, and none of them
    /// is an <c>[ObservableProperty]</c> — the target moved, so they all have to be re-read.</summary>
    private void NotifyTargetProjections()
    {
        EnsureFrameRateOffered(Target.FrameRate);

        OnPropertyChanged(nameof(SelectedFitMode));
        OnPropertyChanged(nameof(TargetWidth));
        OnPropertyChanged(nameof(TargetHeight));
        OnPropertyChanged(nameof(TargetCrf));
        OnPropertyChanged(nameof(SelectedVideoCodec));
        OnPropertyChanged(nameof(SelectedAudioCodec));
        OnPropertyChanged(nameof(TargetAudioSampleRate));
        OnPropertyChanged(nameof(TargetAudioChannels));
        OnPropertyChanged(nameof(SelectedContainer));
        OnPropertyChanged(nameof(SelectedFrameRate));
    }

    /// <summary>A derived rate that is not one of the standards still has to appear in the dropdown,
    /// or the ComboBox shows an empty selection for a target that is perfectly valid.</summary>
    private void EnsureFrameRateOffered(FrameRate rate)
    {
        if (!FrameRates.Any(option => option.Rate == rate))
        {
            FrameRates.Add(new FrameRateOption(rate));
        }
    }

    // ---- container ⇄ file extension ----------------------------------------
    //
    // ConcatArgsBuilder emits no `-f`, so ffmpeg picks the MUXER FROM THE OUTPUT FILE'S EXTENSION;
    // Target.Container only gates `-movflags +faststart`. Left unreconciled, a derived MKV target
    // wrote a real MP4 named "merged.mp4": the user picks MKV and gets MP4. These two keep the
    // extension and the container in lockstep in both directions.

    /// <summary>True only while <see cref="SyncFileNameToContainer"/> is rewriting the extension, so
    /// <see cref="OnOutputFileNameChanged"/> does not read its own write back as a user edit.</summary>
    private bool _isSyncingFileName;

    private static string ExtensionFor(MergeContainer container)
        => container == MergeContainer.Mkv ? ".mkv" : ".mp4";

    /// <summary>Extensions we are willing to treat as "the file's extension" and therefore REPLACE.
    /// Anything else that follows a dot is part of the name.</summary>
    private static readonly string[] MediaExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".mpg", ".mpeg", ".wmv", ".flv", ".ts", ".m2ts"];

    /// <summary>Puts <paramref name="extension"/> on <paramref name="fileName"/> — replacing an
    /// existing media extension, but otherwise APPENDING.</summary>
    /// <remarks><see cref="Path.ChangeExtension"/> cannot be used here: it replaces everything after
    /// the LAST dot, whatever that is. Dots are ordinary characters in a file name, so it silently
    /// eats part of perfectly normal ones — <c>Trip 2026.07.11</c> becomes <c>Trip 2026.07.mp4</c>,
    /// and <c>S01.E01</c> becomes <c>S01.mp4</c>. The user did not ask us to rename their file; we are
    /// only here to make the extension agree with the container (which is what ffmpeg picks the muxer
    /// from). Truncating the name to do it is a cure worse than the disease.</remarks>
    private static string WithExtension(string fileName, string extension)
    {
        var current = Path.GetExtension(fileName);

        return MediaExtensions.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? string.Concat(fileName.AsSpan(0, fileName.Length - current.Length), extension)
            : fileName + extension;
    }

    /// <summary>The container an extension asks for, or null if it names one we do not merge to.</summary>
    private static MergeContainer? ContainerFor(string? extension)
    {
        if (string.Equals(extension, ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return MergeContainer.Mkv;
        }

        return string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
            ? MergeContainer.Mp4
            : null;
    }

    /// <summary>The user typed a file name. If they named a container we support, that IS a choice of
    /// container — honour it (and it is theirs, so it latches the override; otherwise adding the next
    /// clip would re-derive the container and rename their file back underneath them). Anything else
    /// would hand ffmpeg a muxer nobody picked, so it is put back to the target's own extension.</summary>
    partial void OnOutputFileNameChanged(string value)
    {
        if (_isSyncingFileName)
        {
            return;
        }

        if (ContainerFor(Path.GetExtension(value)) is { } chosen)
        {
            if (chosen != Target.Container)
            {
                OverrideTarget(Target with { Container = chosen });
            }

            return;
        }

        SyncFileNameToContainer(Target.Container);
    }

    /// <summary>Rewrites the output file's extension to match the container. A blank name is left
    /// alone: there is nothing to put an extension on, and the merge would fail on it anyway.</summary>
    private void SyncFileNameToContainer(MergeContainer container)
    {
        if (string.IsNullOrWhiteSpace(OutputFileName))
        {
            return;
        }

        var wanted = WithExtension(OutputFileName, ExtensionFor(container));
        if (string.Equals(wanted, OutputFileName, StringComparison.Ordinal))
        {
            return;
        }

        _isSyncingFileName = true;
        try
        {
            OutputFileName = wanted;
        }
        finally
        {
            _isSyncingFileName = false;
        }
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
