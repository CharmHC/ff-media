using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.History;
using FFMedia.Core.Media;
using FFMedia.Core.Notifications;
using FFMedia.Core.Settings;
using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;
using FFMedia.Tools.GifMaker.Services;

namespace FFMedia.Tools.GifMaker.ViewModels;

/// <summary>The GIF Maker page's brain. Headless and fully unit-testable: every dependency is an
/// interface. One video in, one GIF out — a single-item editor with a live estimate, not a queue.</summary>
public partial class GifMakerViewModel : ObservableObject
{
    /// <summary>Same tolerance <c>GifService.PreflightAsync</c> uses: a container's reported duration
    /// and its last frame's timestamp routinely differ by a frame or two, and refusing a range that
    /// ends "at the end" would be maddening.</summary>
    private static readonly TimeSpan RangeTolerance = TimeSpan.FromSeconds(0.5);

    /// <summary>The threshold past which the estimate is called out rather than left for the user to
    /// notice on their own.</summary>
    private const long SizeWarningThresholdBytes = 5L * 1024 * 1024;

    private readonly IMediaAnalyzer _analyzer;
    private readonly IGifService _gifService;
    private readonly IGifSizeProfileStore _profiles;
    private readonly IHistoryService _history;
    private readonly INotificationService _notifications;

    /// <summary>What was actually probed. Null until a video has loaded successfully — the thing
    /// <see cref="SourceLoaded"/> and the range checks key off.</summary>
    private MediaInfo? _sourceInfo;

    /// <summary>Non-null exactly while a render is in flight. Only one at a time — there is only one
    /// item to render.</summary>
    private CancellationTokenSource? _cancellation;

    public GifMakerViewModel(
        IMediaAnalyzer analyzer,
        IGifService gifService,
        IGifSizeProfileStore profiles,
        ISettingsService settings,
        IHistoryService history,
        INotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(gifService);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(notifications);

        _analyzer = analyzer;
        _gifService = gifService;
        _profiles = profiles;
        _history = history;
        _notifications = notifications;

        OutputFolder = settings.Current.DefaultOutputFolder;
    }

    // ---- the loaded source ---------------------------------------------------

    [ObservableProperty] private string _sourcePath = "";

    /// <summary>What the page shows above the parameters: dimensions, rate, and length. Blank until a
    /// video has loaded.</summary>
    [ObservableProperty] private string _sourceSummary = "";

    /// <summary>What the user is ALLOWED to choose, given the loaded video (spec keystone: the source
    /// is always the first entry of every list, and also the default).</summary>
    [ObservableProperty] private GifBounds _bounds = GifBounds.Empty;

    /// <summary>True once a video has been probed and found to have a video track. Everything the page
    /// shows besides the loader itself is gated on this.</summary>
    public bool SourceLoaded => _sourceInfo?.Video is not null;

    /// <summary>Probes <paramref name="path"/> and, on success, loads it as the source: resets
    /// <see cref="Bounds"/> to what that video allows, defaults size/rate to the source's own (the
    /// keystone), and defaults the range to the whole video.</summary>
    /// <remarks><para>A failed probe and a probe that succeeds on a file with no video track are two
    /// different problems, and lumping them together actively misleads — the exact mistake that once
    /// blamed a user's perfectly good <c>.mp4</c> for a missing ffprobe binary (CLAUDE.md,
    /// 2026-07-12). The analyzer already says what went wrong; say it.</para>
    /// <para>Gated on <see cref="CanEditParameters"/> — loading a video overwrites every parameter of
    /// whatever is currently loaded, so doing it mid-render mutates the job that is running. Same bug
    /// class as the merger's shipped one, and the same two-layer fix: <c>CanExecute</c> so the button
    /// greys out, AND an explicit guard here, because the page's file-drop gesture never goes through
    /// the command at all (see <c>MergerViewModel.CanEditClips</c>).</para></remarks>
    [RelayCommand(CanExecute = nameof(CanEditParameters))]
    public async Task LoadVideoAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (IsRendering)
        {
            return; // the page's file-drop handler does not go through the command's CanExecute
        }

        var probe = await _analyzer.AnalyzeAsync(path).ConfigureAwait(true);

        if (!probe.IsSuccess || probe.Value is null)
        {
            _notifications.Notify(new Notification(
                $"Could not read {Path.GetFileName(path)}",
                probe.Error ?? "The file could not be analyzed.",
                NotificationSeverity.Warning));
            return;
        }

        if (probe.Value.Video is null)
        {
            _notifications.Notify(new Notification(
                "Not a video",
                $"{Path.GetFileName(path)} has no video track, so there is nothing to turn into a GIF.",
                NotificationSeverity.Warning));
            return;
        }

        SourcePath = path;
        _sourceInfo = probe.Value;
        Bounds = GifBounds.From(probe.Value);
        SelectedSize = Bounds.Sizes[0];
        SelectedFrameRate = Bounds.FrameRates[0];
        StartText = FormatTime(TimeSpan.Zero);
        EndText = FormatTime(probe.Value.Duration);
        OutputFileName = Path.GetFileNameWithoutExtension(path) + ".gif";
        SourceSummary = DescribeSource(probe.Value);

        OnPropertyChanged(nameof(SourceLoaded));
        Recompute();
    }

    private static string DescribeSource(MediaInfo info) => string.Create(
        CultureInfo.InvariantCulture,
        $"{info.Video!.Width}x{info.Video.Height} · {info.Video.FrameRate.Value:0.###} fps · {FormatTime(info.Duration)}");

    // ---- parameters -----------------------------------------------------------

    [ObservableProperty] private Resolution _selectedSize = new(0, 0);

    [ObservableProperty] private FrameRate _selectedFrameRate;

    [ObservableProperty] private string _startText = "";

    [ObservableProperty] private string _endText = "";

    /// <summary>Explains what is wrong with the current range (unparseable, end before start, end past
    /// the video) — a disabled Create button with no explanation is a dead end.</summary>
    [ObservableProperty] private string _rangeHint = "";

    /// <summary>The estimated finished size, as a range ("1.2–2.4 MB").</summary>
    [ObservableProperty] private string _estimateText = "";

    /// <summary>True when the estimate's high end exceeds <see cref="SizeWarningThresholdBytes"/>. The
    /// page pairs this with <see cref="SizeWarningMessage"/>, which names the three levers.</summary>
    [ObservableProperty] private bool _showSizeWarning;

    /// <summary>The copy shown alongside <see cref="ShowSizeWarning"/>. A warning with no way out is
    /// just noise — name what the user can actually do about it.</summary>
    public string SizeWarningMessage => "Shorten the range, reduce the size, or lower the frame rate.";

    partial void OnSelectedSizeChanged(Resolution value) => Recompute();

    partial void OnSelectedFrameRateChanged(FrameRate value) => Recompute();

    partial void OnStartTextChanged(string value) => Recompute();

    partial void OnEndTextChanged(string value) => Recompute();

    private TimeSpan? ParsedStart => TrimParsing.TryParse(StartText);

    private TimeSpan? ParsedEnd => TrimParsing.TryParse(EndText);

    /// <summary>The validity and the hint, from ONE evaluation of the three boundary conditions
    /// (unparseable / end &lt;= start / end past the source). <see cref="RangeIsValid"/> and
    /// <see cref="UpdateRangeHint"/> used to be two hand-written implementations of the same three
    /// checks — they agreed today, but nothing kept them from drifting, which is exactly the class of
    /// risk <c>GifBounds</c>, <c>TargetBounds</c> and <c>ConformanceCheck</c> each exist to prevent.
    /// Now there is exactly one place that decides "is this range OK", and it hands back why.</summary>
    private readonly record struct RangeEvaluation(bool IsValid, string Hint);

    private RangeEvaluation EvaluateRange()
    {
        if (!SourceLoaded)
        {
            return new RangeEvaluation(false, "");
        }

        if (ParsedStart is not { } start || ParsedEnd is not { } end)
        {
            return new RangeEvaluation(false, "Enter start and end times as HH:MM:SS, MM:SS, or seconds.");
        }

        if (end <= start)
        {
            return new RangeEvaluation(false, "The end time must be after the start time.");
        }

        if (end > _sourceInfo!.Duration + RangeTolerance)
        {
            return new RangeEvaluation(false, string.Create(
                CultureInfo.InvariantCulture,
                $"The end time is past the end of the video (which is {FormatTime(_sourceInfo.Duration)} long)."));
        }

        return new RangeEvaluation(true, "");
    }

    /// <summary>True only when the range is something the service could actually render: both ends
    /// parse, end is after start, and end does not run past the source (spec: the same rule
    /// <c>GifService.PreflightAsync</c> enforces, checked here too so the button need not be clicked to
    /// find out).</summary>
    private bool RangeIsValid => EvaluateRange().IsValid;

    /// <summary>The range's own hint takes priority; once the range itself is fine, an empty output
    /// file name is the other way <see cref="CanCreate"/> can be false, and it deserves the same
    /// explanation — a disabled Create button with nothing said is a dead end whichever input caused
    /// it (see Finding 3, the "range hint" pre-existed this and the filename case was falling through
    /// to silence).</summary>
    private void UpdateRangeHint()
    {
        var range = EvaluateRange();
        if (!range.IsValid)
        {
            RangeHint = range.Hint;
            return;
        }

        RangeHint = string.IsNullOrWhiteSpace(OutputFileName)
            ? "Enter a file name for the GIF."
            : "";
    }

    private void UpdateEstimate()
    {
        if (!RangeIsValid)
        {
            EstimateText = "";
            ShowSizeWarning = false;
            return;
        }

        var request = new GifRequest(
            SourcePath, ParsedStart!.Value, ParsedEnd!.Value, SelectedSize, SelectedFrameRate, OutputPath);
        var estimate = GifSizeEstimator.Estimate(request, _profiles.Load());

        EstimateText = estimate.Describe();
        ShowSizeWarning = estimate.HighBytes > SizeWarningThresholdBytes;
    }

    /// <summary>Re-runs the range hint and the estimate, then tells <see cref="CreateCommand"/> to
    /// re-check <see cref="CanCreate"/> — a raised <c>PropertyChanged</c> does not reach
    /// <c>ICommand</c> on its own.</summary>
    private void Recompute()
    {
        UpdateRangeHint();
        UpdateEstimate();
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
    }

    // ---- output ---------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    private string _outputFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    private string _outputFileName = "output.gif";

    public string OutputPath => Path.Combine(OutputFolder, OutputFileName);

    partial void OnOutputFileNameChanged(string value) => Recompute();

    // ---- rendering --------------------------------------------------------------

    /// <summary>True while a render is running. The render holds a SNAPSHOT of the request taken when
    /// Create was clicked, so a page that can still be edited would describe a job that is not the one
    /// running — exactly the bug the merger shipped when the container was flipped mid-merge.</summary>
    [ObservableProperty] private bool _isRendering;

    [ObservableProperty] private double _percent;

    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Gates every parameter control on the page.</summary>
    public bool CanEditParameters => !IsRendering;

    public bool CanCreate => !IsRendering && SourceLoaded && RangeIsValid && !string.IsNullOrWhiteSpace(OutputFileName);

    public bool CanCancel => IsRendering;

    partial void OnIsRenderingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanEditParameters));
        CreateCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadVideoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    public async Task CreateAsync()
    {
        var request = new GifRequest(
            SourcePath, ParsedStart!.Value, ParsedEnd!.Value, SelectedSize, SelectedFrameRate, OutputPath);

        _cancellation = new CancellationTokenSource();
        IsRendering = true;
        Percent = 0;
        StatusMessage = "Preparing…";

        try
        {
            var sink = new SyncProgress<GifProgress>(OnGifProgress);
            var result = await _gifService.CreateAsync(request, sink, _cancellation.Token).ConfigureAwait(true);

            if (result.IsSuccess)
            {
                // Result<T>.Value is nullable even on success. Fall back to the path we asked for
                // rather than suppressing with `!` — a null here would otherwise land in history as a
                // row whose "open file" button does nothing.
                var saved = result.Value ?? request.OutputPath;

                Percent = 100;
                StatusMessage = "GIF created.";
                RecordInHistory(request, saved);
                _notifications.Notify(new Notification(
                    "GIF created", $"Saved to {saved}", NotificationSeverity.Success));
                return;
            }

            if (_cancellation.IsCancellationRequested)
            {
                ReportCanceled();
                return;
            }

            var reason = result.Error ?? "The GIF could not be created.";
            StatusMessage = reason;
            _notifications.Notify(new Notification("GIF creation failed", reason, NotificationSeverity.Error));
        }
        catch (OperationCanceledException)
        {
            // GifService throws rather than returning a failure Result for a cancel (its own tests
            // pin this — CreateAsync_WhenCancelled_LeavesNoPaletteAndNoHalfWrittenGif). Same
            // destination either way: cancellation is Canceled, not Failed.
            ReportCanceled();
        }
        catch (Exception ex)
        {
            // The service promises never to throw for an expected failure. A bug in it must still not
            // wedge the page at IsRendering = true with a dead Create button.
            StatusMessage = ex.Message;
            _notifications.Notify(new Notification("GIF creation failed", ex.Message, NotificationSeverity.Error));
        }
        finally
        {
            IsRendering = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Records the finished GIF, tolerating a broken history sink. The GIF is DONE — the file
    /// is sitting on disk. If <c>history.json</c> happens to be locked or unwritable, letting that
    /// exception escape would roll into the failure path and show a red "GIF creation failed" for a
    /// GIF that in fact succeeded, sending the user hunting for a problem with their video. Losing a
    /// log row is a footnote; lying about the outcome is not.</summary>
    private void RecordInHistory(GifRequest request, string outputPath)
    {
        try
        {
            _history.Append(new HistoryEntry(
                Title: Path.GetFileName(outputPath),
                Url: "", // a GIF made from a local video has no URL
                OutputPath: outputPath,
                Format: string.Create(
                    CultureInfo.InvariantCulture,
                    $"GIF · {request.Size.Width}x{request.Size.Height} · {request.Fps.Value:0.###}fps"),
                Timestamp: DateTimeOffset.Now,
                Status: "Completed",
                Source: HistorySource.Gif));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _notifications.Notify(new Notification(
                "History not updated",
                $"The GIF was created, but it could not be recorded in History: {ex.Message}",
                NotificationSeverity.Warning));
        }
    }

    /// <summary>Cancellation is NOT failure. The user asked for it — a red toast for an action they
    /// took on purpose is worse than no toast at all. No history row either: a render that did not
    /// finish is not a GIF.</summary>
    private void ReportCanceled()
    {
        StatusMessage = "GIF creation canceled.";
        _notifications.Notify(new Notification(
            "Canceled", "GIF creation canceled.", NotificationSeverity.Info));
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _cancellation?.Cancel();

    private void OnGifProgress(GifProgress progress)
    {
        Percent = progress.Percent;
        StatusMessage = progress.Phase switch
        {
            GifPhase.Analyzing => "Building the palette…",
            GifPhase.Rendering => "Rendering the GIF…",
            _ => StatusMessage,
        };
    }

    /// <summary>m:ss, or h:mm:ss past an hour. Invariant — this is a duration, not a local time. Also
    /// round-trips through <see cref="TrimParsing.TryParse"/>, since it is used to seed
    /// <see cref="StartText"/>/<see cref="EndText"/>.</summary>
    /// <remarks>Below one second, <c>m\:ss</c> truncates to <c>"0:00"</c> — for a source under a
    /// second long, that gives the SAME text for both the default start and the default end, which
    /// then fails the <c>end &lt;= start</c> check: the range defaults to invalid the moment the
    /// source itself is very short. Below one second, format as plain fractional seconds instead
    /// (<see cref="TrimParsing.TryParse"/> already accepts that form) so the end text always
    /// round-trips to the source's real duration rather than being floored away.</remarks>
    private static string FormatTime(TimeSpan span) => span > TimeSpan.Zero && span.TotalSeconds < 1
        ? span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
        : span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Reports on the calling thread. See <c>MergerViewModel</c>'s equivalent for why the BCL
    /// <see cref="Progress{T}"/> is wrong here (it marshals to the captured context, reordering
    /// reports). A deliberate duplicate, not an oversight — the merger's copy is <c>private</c>.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
