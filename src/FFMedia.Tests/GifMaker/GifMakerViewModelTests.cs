using System.Text.Json;
using FFMedia.Core.History;
using FFMedia.Core.Media;
using FFMedia.Core.Notifications;
using FFMedia.Core.Results;
using FFMedia.Core.Settings;
using FFMedia.Media;
using FFMedia.Media.Preview;
using FFMedia.Tools.GifMaker.Models;
using FFMedia.Tools.GifMaker.Services;
using FFMedia.Tools.GifMaker.ViewModels;
using FFMedia.Ui.Playback;
using FFMedia.Ui.ViewModels;
using Xunit;

namespace FFMedia.Tests.GifMaker;

public class GifMakerViewModelTests
{
    // ---- fakes -------------------------------------------------------------

    private sealed class FakeAnalyzer : IMediaAnalyzer
    {
        private readonly Dictionary<string, Result<MediaInfo>> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public void Returns(string path, MediaInfo info) => _byPath[path] = Result<MediaInfo>.Success(info);

        public void Rejects(string path, string error) => _byPath[path] = Result<MediaInfo>.Failure(error);

        public Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(_byPath.TryGetValue(filePath, out var r)
                ? r
                : Result<MediaInfo>.Failure("not configured"));
    }

    private sealed class FakeGifService : IGifService
    {
        public GifRequest? Request { get; private set; }

        public int Calls { get; private set; }

        public Func<GifRequest, IProgress<GifProgress>?, CancellationToken, Task<Result<string>>> Behavior
        { get; set; } = (request, _, _) => Task.FromResult(Result<string>.Success(request.OutputPath));

        public Task<Result<string>> CreateAsync(
            GifRequest request, IProgress<GifProgress>? progress = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Request = request;
            Calls++;
            return Behavior(request, progress, ct);
        }
    }

    private sealed class FakeStore : IGifSizeProfileStore
    {
        public GifSizeProfile Profile { get; set; } = new();

        public GifSizeProfile Load() => Profile;

        public void Save(GifSizeProfile profile) => Profile = profile;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = AppSettings.Default with { DefaultOutputFolder = @"C:\out" };

        public event EventHandler<AppSettings>? Changed;

        public void Save(AppSettings settings)
        {
            Current = settings;
            Changed?.Invoke(this, settings);
        }
    }

    private sealed class FakeHistory : IHistoryService
    {
        public List<HistoryEntry> Entries { get; } = new();

        /// <summary>Set to simulate a locked or unwritable history.json.</summary>
        public Exception? AppendThrows { get; set; }

        public event EventHandler? Changed;

        public IReadOnlyList<HistoryEntry> Query() => Entries;

        public void Append(HistoryEntry entry)
        {
            if (AppendThrows is not null)
            {
                throw AppendThrows;
            }

            Entries.Add(entry);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            Entries.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<Notification> Sent { get; } = new();

        public void Notify(Notification notification) => Sent.Add(notification);
    }

    /// <summary>A player that answers <c>Open</c> synchronously (unlike the real <c>MediaElement</c>,
    /// which answers off a message pump this headless test has none of) — so
    /// <see cref="VideoPreviewViewModel.LoadAsync"/> never hangs awaiting an event that would otherwise
    /// need a dispatcher to raise it.</summary>
    private sealed class FakePreviewPlayer : IMediaPlayer
    {
        public TimeSpan Position { get; set; }

        public TimeSpan? Duration { get; private set; }

        public bool IsPlaying { get; private set; }

        public event EventHandler? MediaOpened;

        public event EventHandler<string>? MediaFailed;

        public void Open(string path)
        {
            Duration = TimeSpan.FromSeconds(600);
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;
    }

    private sealed class FakePreviewProxies : IPreviewProxyService
    {
        public Task<Result<string>> GetOrCreateAsync(
            string sourcePath, MediaInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success(sourcePath));

        public void SweepStale()
        {
        }
    }

    // ---- helpers -----------------------------------------------------------

    private const string VideoPath = @"C:\video.mp4";

    private static MediaInfo Info(int width = 1920, int height = 1080, int fps = 30, double seconds = 10)
        => new(TimeSpan.FromSeconds(seconds), "mov,mp4,m4a",
            new VideoStreamInfo(width, height, new FrameRate(fps, 1), "h264", "yuv420p", 0), null);

    private static MediaInfo AudioOnly(double seconds = 10)
        => new(TimeSpan.FromSeconds(seconds), "mov,mp4,m4a", null, new AudioStreamInfo("aac", 48000, 2));

    private sealed record Harness(
        GifMakerViewModel Vm, FakeAnalyzer Analyzer, FakeGifService Service,
        FakeHistory History, FakeNotifications Notifications, FakeStore Store, VideoPreviewViewModel Preview);

    private static Harness Build()
    {
        var analyzer = new FakeAnalyzer();
        var service = new FakeGifService();
        var store = new FakeStore();
        var history = new FakeHistory();
        var notifications = new FakeNotifications();
        var settings = new FakeSettings();
        // The SAME analyzer backs both the GIF Maker's own probe and the preview's -- exactly how DI
        // wires it in production (AddGifMakerEngine's IMediaAnalyzer is TryAddSingleton, shared).
        var preview = new VideoPreviewViewModel(analyzer, new FakePreviewProxies(), new FakePreviewPlayer());
        var vm = new GifMakerViewModel(analyzer, service, store, settings, history, notifications, preview);
        return new Harness(vm, analyzer, service, history, notifications, store, preview);
    }

    /// <summary>A harness with a video already loaded at <see cref="VideoPath"/>.</summary>
    private static async Task<Harness> BuildLoadedAsync(
        int width = 1920, int height = 1080, int fps = 30, double seconds = 10)
    {
        var h = Build();
        h.Analyzer.Returns(VideoPath, Info(width, height, fps, seconds));
        await h.Vm.LoadVideoAsync(VideoPath);
        return h;
    }

    // ---- loading -------------------------------------------------------------

    [Fact]
    public async Task LoadVideoAsync_SetsTheBoundsFromTheVideo_AndDefaultsToItsOwnSizeAndRate()
    {
        // Deliberately unusual numbers (not 1920x1080/30fps) so a VM that hardcodes a "sensible
        // default" instead of reading Bounds cannot pass by coincidence.
        var h = Build();
        h.Analyzer.Returns(@"C:\clip.mov", Info(width: 852, height: 480, fps: 24, seconds: 12));

        await h.Vm.LoadVideoAsync(@"C:\clip.mov");

        Assert.True(h.Vm.SourceLoaded);
        Assert.Equal(new Resolution(852, 480), h.Vm.Bounds.Sizes[0]);
        Assert.Equal(new FrameRate(24, 1), h.Vm.Bounds.FrameRates[0]);
        Assert.Equal(h.Vm.Bounds.Sizes[0], h.Vm.SelectedSize);
        Assert.Equal(h.Vm.Bounds.FrameRates[0], h.Vm.SelectedFrameRate);
    }

    [Fact]
    public async Task LoadVideoAsync_RejectsAFileWithNoVideoTrack_WithTheAnalyzersOwnReason()
    {
        var h = Build();

        // Case 1: the probe FAILS outright (e.g. ffprobe.exe is missing). The exact reason must
        // survive verbatim -- never collapsed into a generic "not a video" message that blames the
        // user's perfectly good file for a missing binary (CLAUDE.md, 2026-07-12).
        h.Analyzer.Rejects(@"C:\a.mp4", "Could not run ffprobe: The system cannot find the file specified.");
        await h.Vm.LoadVideoAsync(@"C:\a.mp4");

        var first = Assert.Single(h.Notifications.Sent);
        Assert.Contains("Could not run ffprobe", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no video track", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NotificationSeverity.Warning, first.Severity);
        Assert.False(h.Vm.SourceLoaded);

        // Case 2: the probe SUCCEEDS but the file has no video track (an audio file). A different
        // problem entirely, so it must be a different message.
        h.Analyzer.Returns(@"C:\b.mp3", AudioOnly());
        await h.Vm.LoadVideoAsync(@"C:\b.mp3");

        Assert.Equal(2, h.Notifications.Sent.Count);
        var second = h.Notifications.Sent[1];
        Assert.Contains("no video track", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(h.Vm.SourceLoaded);
    }

    [Fact]
    public async Task LoadVideoAsync_DefaultsTheRangeToTheWholeVideo()
    {
        var h = Build();
        h.Analyzer.Returns(VideoPath, Info(seconds: 37));

        await h.Vm.LoadVideoAsync(VideoPath);

        Assert.Equal(TimeSpan.Zero, TrimParsing.TryParse(h.Vm.StartText));
        Assert.Equal(TimeSpan.FromSeconds(37), TrimParsing.TryParse(h.Vm.EndText));
    }

    [Fact]
    public async Task LoadVideoAsync_ForASubSecondVideo_StillDefaultsToAValidWholeVideoRange()
    {
        // m:ss truncates to whole seconds, so a naive formatter renders BOTH the default start (0)
        // and the default end (anything under 1s) as "0:00" -- an invalid zero-length range on load,
        // for the shortest of clips. The end must round-trip to the real (fractional) duration.
        var h = Build();
        h.Analyzer.Returns(VideoPath, Info(seconds: 0.6));

        await h.Vm.LoadVideoAsync(VideoPath);

        Assert.NotEqual(h.Vm.StartText, h.Vm.EndText);
        Assert.Equal(TimeSpan.Zero, TrimParsing.TryParse(h.Vm.StartText));
        Assert.Equal(TimeSpan.FromSeconds(0.6), TrimParsing.TryParse(h.Vm.EndText));
        Assert.True(h.Vm.CanCreate);
    }

    [Fact]
    public async Task Bounds_NeverOfferASizeOrRateAboveTheSource()
    {
        var h = Build();
        h.Analyzer.Returns(VideoPath, Info(width: 1920, height: 1080, fps: 30));

        await h.Vm.LoadVideoAsync(VideoPath);

        Assert.All(h.Vm.Bounds.Sizes, size => Assert.True(size.Width <= 1920 && size.Height <= 1080));
        Assert.All(h.Vm.Bounds.FrameRates, rate => Assert.True(rate.Value <= 30));
    }

    [Fact]
    public async Task SelectedSize_IgnoresANullWrite_LikeAComboBoxPushesWhileItsItemsSourceIsRebuilding()
    {
        // FIX 2 (final whole-branch review). Resolution is a REFERENCE type (a record class, unlike
        // FrameRate's `readonly record struct`), so a ComboBox really can write null through a two-way
        // SelectedItem binding the instant its ItemsSource no longer contains the current selection --
        // exactly what happens between LoadVideoAsync replacing Bounds and its own next line
        // re-defaulting SelectedSize. There is no real WPF ComboBox in a headless test, so the write is
        // simulated directly. Before the fix this reached Recompute() and threw a NullReferenceException
        // inside GifSizeEstimator -- on the UI thread, silently swallowed by WPF's binding engine.
        var h = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 10);
        var before = h.Vm.SelectedSize;

        var exception = Record.Exception(() => h.Vm.SelectedSize = null);

        Assert.Null(exception);
        Assert.Equal(before, h.Vm.SelectedSize); // the null write is ignored -- the last good selection survives
        Assert.False(string.IsNullOrEmpty(h.Vm.EstimateText)); // Recompute() was never disturbed by the null
    }

    [Fact]
    public async Task LoadVideoAsync_ASecondVideoWithADifferentResolution_ReDefaultsWithoutThrowing()
    {
        // The exact scenario the reviewer found: loading a second video whose resolution differs from
        // the first rebuilds Bounds (and so the Size ComboBox's ItemsSource) with the first video's
        // resolution no longer in the list. If SelectedSize were still a plain non-nullable Resolution
        // property (no null-tolerant projection), a real ComboBox pushing null in that instant would
        // have crashed Recompute() with a NullReferenceException.
        var h = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 10);
        h.Analyzer.Returns(@"C:\second.mp4", Info(width: 1280, height: 720, fps: 24, seconds: 8));

        var exception = await Record.ExceptionAsync(() => h.Vm.LoadVideoAsync(@"C:\second.mp4"));

        Assert.Null(exception);
        Assert.Equal(new Resolution(1280, 720), h.Vm.SelectedSize);
        Assert.Equal(new FrameRate(24, 1), h.Vm.SelectedFrameRate);
        Assert.False(string.IsNullOrEmpty(h.Vm.EstimateText));
    }

    // ---- the preview (M9) -------------------------------------------------------

    [Fact]
    public async Task LoadVideoAsync_AlsoLoadsThePreview()
    {
        var h = Build();
        h.Analyzer.Returns(VideoPath, Info(seconds: 15));

        await h.Vm.LoadVideoAsync(VideoPath);

        Assert.True(h.Preview.IsReady);
        Assert.Equal(TimeSpan.FromSeconds(15), h.Preview.Duration);
    }

    [Fact]
    public async Task CapturingAStart_WritesItIntoStartText_WithSubSecondPrecision()
    {
        // A whole-second capture would pass against a TRUNCATING formatter too -- the fraction is the
        // whole point (CLAUDE.md: FormatTime's h\:mm\:ss bug, and TrimParsing.Format exists to fix it).
        var h = await BuildLoadedAsync(seconds: 200);

        h.Preview.Position = TimeSpan.FromSeconds(83.45);
        h.Preview.CaptureStartCommand.Execute(null);

        Assert.Equal("1:23.45", h.Vm.StartText);
    }

    [Fact]
    public async Task CapturingAnEnd_WritesItIntoEndText_AndTheEstimateRecomputes()
    {
        var h = await BuildLoadedAsync(seconds: 200);
        var before = h.Vm.EstimateText;

        h.Preview.Position = TimeSpan.FromSeconds(50);
        h.Preview.CaptureEndCommand.Execute(null);

        // A LITERAL, not TrimParsing.Format(...) -- computing the expectation with the production
        // formatter makes the assertion tautological: it would pass against ANY formatter, including one
        // that truncated the fraction away.
        Assert.Equal("0:50", h.Vm.EndText);
        Assert.NotEqual(before, h.Vm.EstimateText); // shrunk from the full 200s range down to 50s
    }

    [Fact]
    public async Task CapturingAStartAfterTheEnd_IsRefused_AndExplainsWhy()
    {
        // Never silently swallowed, never silently reordered -- a capture that would invert the range
        // is refused, and the refusal says why.
        var h = await BuildLoadedAsync(seconds: 200);
        h.Vm.EndText = "0:10";
        var originalStart = h.Vm.StartText;

        h.Preview.Position = TimeSpan.FromSeconds(20); // after the current end
        h.Preview.CaptureStartCommand.Execute(null);

        Assert.Equal(originalStart, h.Vm.StartText); // untouched
        Assert.False(string.IsNullOrWhiteSpace(h.Vm.RangeHint));

        // Asserted on what only the REFUSAL says. A bare "end" also appears in the ordinary invalid-range
        // hint ("The end time must be after the start time."), which the un-refused path would produce --
        // so it could not tell a refusal apart from a silently-accepted inversion.
        Assert.Contains("invert", h.Vm.RangeHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Capture End first", h.Vm.RangeHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapturingAnEndBeforeTheStart_IsRefused_AndExplainsWhy()
    {
        var h = await BuildLoadedAsync(seconds: 200);
        h.Vm.StartText = "0:50";
        var originalEnd = h.Vm.EndText;

        h.Preview.Position = TimeSpan.FromSeconds(20); // before the current start
        h.Preview.CaptureEndCommand.Execute(null);

        Assert.Equal(originalEnd, h.Vm.EndText); // untouched
        Assert.False(string.IsNullOrWhiteSpace(h.Vm.RangeHint));
        Assert.Contains("invert", h.Vm.RangeHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Capture Start first", h.Vm.RangeHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhileRendering_CaptureIsFrozen()
    {
        // The render holds a SNAPSHOT of the request -- a page (or preview) that can still change
        // Start/End describes a job that is not the one running. Shipped twice in M8.
        var h = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 10);
        var gate = new TaskCompletionSource();
        h.Service.Behavior = async (request, _, _) =>
        {
            await gate.Task;
            return Result<string>.Success(request.OutputPath);
        };

        Assert.True(h.Preview.CanCapture); // sanity: live before the render starts
        Assert.True(h.Preview.CaptureStartCommand.CanExecute(null));

        var rendering = h.Vm.CreateCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsRendering);

        Assert.False(h.Preview.CanCapture);
        Assert.False(h.Preview.CaptureStartCommand.CanExecute(null));
        Assert.False(h.Preview.CaptureEndCommand.CanExecute(null));

        gate.SetResult();
        await rendering;

        Assert.False(h.Vm.IsRendering);
        Assert.True(h.Preview.CanCapture); // thawed
        Assert.True(h.Preview.CaptureStartCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileRendering_ACaptureThatReachesTheHandlerAnyway_IsStillRefused()
    {
        // FINDING (Task 5 review, MINOR 6). The handlers trusted their ONE current caller -- the preview's
        // CaptureStart body guard -- to have already checked. That holds only while the sole thing raising
        // StartCaptured is a RelayCommand; M10 adds a draggable range band to this same VM, and a GESTURE
        // BYPASSES CanExecute ENTIRELY. This project has shipped that exact bug TWICE (M8). So the mutator
        // defends itself: the render holds a SNAPSHOT, and a Start/End that moves under it describes a job
        // that is not the one running.
        //
        // The simulation of such a gesture: leave CanCapture true while the render is in flight, so the
        // preview's own guards pass and the captured moment reaches the GIF Maker's handler regardless.
        var h = await BuildLoadedAsync(seconds: 200);
        var gate = new TaskCompletionSource();
        h.Service.Behavior = async (request, _, _) =>
        {
            await gate.Task;
            return Result<string>.Success(request.OutputPath);
        };

        var rendering = h.Vm.CreateCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsRendering);

        var startBefore = h.Vm.StartText;
        var endBefore = h.Vm.EndText;

        h.Preview.CanCapture = true;   // the gesture never asked
        h.Preview.Position = TimeSpan.FromSeconds(7);
        h.Preview.CaptureStartCommand.Execute(null);
        h.Preview.Position = TimeSpan.FromSeconds(9);
        h.Preview.CaptureEndCommand.Execute(null);

        Assert.Equal(startBefore, h.Vm.StartText);
        Assert.Equal(endBefore, h.Vm.EndText);

        gate.SetResult();
        await rendering;
    }

    // ---- the estimate ----------------------------------------------------------

    [Fact]
    public async Task Estimate_UpdatesWhenTheRangeChanges()
    {
        var h = await BuildLoadedAsync(seconds: 20);
        var before = h.Vm.EstimateText;
        Assert.False(string.IsNullOrEmpty(before));

        h.Vm.EndText = "0:05"; // shrink from the full 20s default range down to 5s

        Assert.NotEqual(before, h.Vm.EstimateText);
    }

    [Fact]
    public async Task Estimate_UpdatesWhenTheSizeOrFrameRateChanges()
    {
        var h = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 20);

        var beforeSize = h.Vm.EstimateText;
        h.Vm.SelectedSize = h.Vm.Bounds.Sizes[^1]; // the smallest offered size
        Assert.NotEqual(beforeSize, h.Vm.EstimateText);

        var beforeRate = h.Vm.EstimateText;
        h.Vm.SelectedFrameRate = h.Vm.Bounds.FrameRates[^1]; // the slowest offered rate
        Assert.NotEqual(beforeRate, h.Vm.EstimateText);
    }

    [Fact]
    public async Task ShowSizeWarning_IsTrue_WhenTheEstimateExceedsTheThreshold()
    {
        // A full 1080p/30fps/10s GIF is comfortably over 5 MB with the seed profile.
        var big = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 10);
        Assert.True(big.Vm.ShowSizeWarning);

        // A tiny GIF is comfortably under it -- both directions must genuinely move the flag.
        var small = await BuildLoadedAsync(width: 320, height: 180, fps: 10, seconds: 1);
        Assert.False(small.Vm.ShowSizeWarning);
    }

    // ---- the range hint ----------------------------------------------------

    [Fact]
    public async Task RangeHint_ExplainsAnInvalidRange_RatherThanSilentlyDisablingCreate()
    {
        var h = await BuildLoadedAsync(seconds: 10);

        h.Vm.StartText = "garbage";
        Assert.False(h.Vm.CanCreate);
        var unparseableHint = h.Vm.RangeHint;
        Assert.False(string.IsNullOrWhiteSpace(unparseableHint));

        h.Vm.StartText = "0:05";
        h.Vm.EndText = "0:02"; // end before start
        Assert.False(h.Vm.CanCreate);
        var endBeforeStartHint = h.Vm.RangeHint;
        Assert.NotEqual(unparseableHint, endBeforeStartHint);

        h.Vm.StartText = "0:00";
        h.Vm.EndText = "0:59"; // past the 10s video
        Assert.False(h.Vm.CanCreate);
        var pastEndHint = h.Vm.RangeHint;
        Assert.NotEqual(endBeforeStartHint, pastEndHint);

        Assert.Contains("start", unparseableHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("after", endBeforeStartHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("past the end", pastEndHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RangeHint_TreatsEndEqualToStart_AsInvalid()
    {
        // The boundary the "end <= start" check exists for. A mutant that weakens it to "end < start"
        // would pass every other range test (none of them probes exactly this point) yet let a
        // zero-length range through as valid.
        var h = await BuildLoadedAsync(seconds: 10);

        h.Vm.StartText = "0:05";
        h.Vm.EndText = "0:05";

        Assert.False(h.Vm.CanCreate);
        Assert.Contains("after", h.Vm.RangeHint, StringComparison.OrdinalIgnoreCase);
    }

    // ---- CanCreate ----------------------------------------------------------

    [Fact]
    public async Task CanCreate_IsFalse_UntilAVideoIsLoadedAndTheRangeIsValid()
    {
        var h = Build();
        Assert.False(h.Vm.CanCreate); // no video yet

        h.Analyzer.Returns(VideoPath, Info(seconds: 10));
        await h.Vm.LoadVideoAsync(VideoPath);
        Assert.True(h.Vm.CanCreate); // the whole-video default range is valid

        h.Vm.EndText = "abc";
        Assert.False(h.Vm.CanCreate);

        h.Vm.EndText = "0:08";
        Assert.True(h.Vm.CanCreate);

        // The range is fine here -- CanCreate is false purely because the file name is blank. A
        // disabled Create button must still say why, whichever input caused it; before this fix,
        // RangeHint stayed "" in exactly this state and the page would have shown nothing at all.
        h.Vm.OutputFileName = "";
        Assert.False(h.Vm.CanCreate);
        Assert.False(string.IsNullOrWhiteSpace(h.Vm.RangeHint));

        h.Vm.OutputFileName = "clip.gif";
        Assert.True(h.Vm.CanCreate);
        Assert.True(string.IsNullOrWhiteSpace(h.Vm.RangeHint));
    }

    // ---- creating -----------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PassesTheChosenSizeRangeAndRate_ToTheService()
    {
        var h = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 10);
        h.Vm.SelectedSize = h.Vm.Bounds.Sizes[1]; // not the default
        h.Vm.SelectedFrameRate = h.Vm.Bounds.FrameRates[1];
        h.Vm.StartText = "0:02";
        h.Vm.EndText = "0:07";

        await h.Vm.CreateCommand.ExecuteAsync(null);

        var request = h.Service.Request;
        Assert.NotNull(request);
        Assert.Equal(VideoPath, request!.SourcePath);
        Assert.Equal(h.Vm.SelectedSize, request.Size);
        Assert.Equal(h.Vm.SelectedFrameRate, request.Fps);
        Assert.Equal(TimeSpan.FromSeconds(2), request.Start);
        Assert.Equal(TimeSpan.FromSeconds(7), request.End);
    }

    [Fact]
    public async Task CreateAsync_OnSuccess_WritesAHistoryRowWithSourceGif_AndNotifies()
    {
        var h = await BuildLoadedAsync(seconds: 10);

        await h.Vm.CreateCommand.ExecuteAsync(null);

        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(HistorySource.Gif, entry.Source);
        Assert.Equal("Completed", entry.Status);
        Assert.Equal("", entry.Url); // a GIF made from a local file has no URL
        Assert.Contains("GIF", entry.Format, StringComparison.Ordinal);

        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal(NotificationSeverity.Success, notification.Severity);
    }

    [Fact]
    public async Task CreateAsync_OnFailure_NotifiesTheServicesReason_AndWritesNoHistory()
    {
        var h = await BuildLoadedAsync(seconds: 10);
        const string reason = "The video could not be read. It may be corrupt, or not really a video.";
        h.Service.Behavior = (_, _, _) => Task.FromResult(Result<string>.Failure(reason));

        await h.Vm.CreateCommand.ExecuteAsync(null);

        Assert.Empty(h.History.Entries);
        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal(reason, notification.Message);
    }

    [Fact]
    public async Task WhileRendering_TheParametersAreFrozen()
    {
        var h = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 10);
        var gate = new TaskCompletionSource();
        h.Service.Behavior = async (request, _, _) =>
        {
            await gate.Task;
            return Result<string>.Success(request.OutputPath);
        };

        // Pick non-default values BEFORE clicking, so the later mutation (to yet other values) is
        // unambiguously distinguishable from "the defaults happened to survive by coincidence".
        h.Vm.SelectedSize = h.Vm.Bounds.Sizes[1];
        h.Vm.SelectedFrameRate = h.Vm.Bounds.FrameRates[1];
        h.Vm.OutputFileName = "before.gif";

        var clickTimeSize = h.Vm.SelectedSize;
        var clickTimeRate = h.Vm.SelectedFrameRate;
        var clickTimeOutputPath = h.Vm.OutputPath;

        var rendering = h.Vm.CreateCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsRendering);
        Assert.False(h.Vm.CanEditParameters);
        Assert.False(h.Vm.CreateCommand.CanExecute(null));
        Assert.True(h.Vm.CanCancel);

        // Mutate every parameter the render is supposed to have already snapshotted. This is the
        // regression that matters: if CreateAsync ever re-reads SelectedSize/SelectedFrameRate/
        // OutputFileName from the LIVE properties instead of the captured `request` local -- exactly
        // the merger's shipped bug, where flipping Container mid-merge rewrote the history row to name
        // a file the encode was never writing -- this is what catches it. Flipping booleans before and
        // after a render proves nothing about which values the job actually used.
        h.Vm.SelectedSize = h.Vm.Bounds.Sizes[^1];
        h.Vm.SelectedFrameRate = h.Vm.Bounds.FrameRates[^1];
        h.Vm.OutputFileName = "after.gif";

        gate.SetResult();
        await rendering;

        Assert.False(h.Vm.IsRendering);
        Assert.True(h.Vm.CanEditParameters);
        Assert.True(h.Vm.CreateCommand.CanExecute(null));

        // The SERVICE must have received exactly what was live at CLICK time, never the mutated values.
        var request = h.Service.Request;
        Assert.NotNull(request);
        Assert.Equal(clickTimeSize, request!.Size);
        Assert.Equal(clickTimeRate, request.Fps);
        Assert.Equal(clickTimeOutputPath, request.OutputPath);
        Assert.NotEqual(h.Vm.Bounds.Sizes[^1], request.Size);
        Assert.NotEqual(h.Vm.Bounds.FrameRates[^1], request.Fps);

        // And the HISTORY ROW must name the file that was actually written -- not the mutated name a
        // live re-read would now describe. Title is checked too: it used to read the LIVE
        // OutputFileName property rather than the snapshot, so the row's displayed NAME could disagree
        // with the OutputPath it actually points at -- the same bug the merger shipped, one field over.
        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(clickTimeOutputPath, entry.OutputPath);
        Assert.DoesNotContain("after.gif", entry.OutputPath, StringComparison.Ordinal);
        Assert.Equal("before.gif", entry.Title);
        Assert.NotEqual("after.gif", entry.Title);
    }

    [Fact]
    public async Task LoadVideoAsync_WhileRendering_IsRefused_AndDoesNotDisturbTheRunningJob()
    {
        // The regression this pins: LoadVideoAsync overwrites SourcePath/Bounds/SelectedSize/
        // SelectedFrameRate/StartText/EndText/OutputFileName -- every parameter of the job currently
        // rendering. A file-drop gesture never goes through CreateCommand's CanExecute, so the
        // in-method guard is load-bearing on its own, not merely a backstop for the command gate.
        var h = await BuildLoadedAsync(width: 1920, height: 1080, fps: 30, seconds: 10);
        var gate = new TaskCompletionSource();
        h.Service.Behavior = async (request, _, _) =>
        {
            await gate.Task;
            return Result<string>.Success(request.OutputPath);
        };

        var originalSourcePath = h.Vm.SourcePath;
        var originalSize = h.Vm.SelectedSize;
        var originalRate = h.Vm.SelectedFrameRate;
        var originalOutputPath = h.Vm.OutputPath;

        var rendering = h.Vm.CreateCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsRendering);

        h.Analyzer.Returns(@"C:\other.mp4", Info(width: 320, height: 180, fps: 10, seconds: 3));

        // Simulate the drop gesture: calls LoadVideoAsync directly, bypassing LoadVideoCommand.
        await h.Vm.LoadVideoAsync(@"C:\other.mp4");

        // Refused: the source and every parameter of the running job are exactly as they were.
        Assert.Equal(originalSourcePath, h.Vm.SourcePath);
        Assert.Equal(originalSize, h.Vm.SelectedSize);
        Assert.Equal(originalRate, h.Vm.SelectedFrameRate);
        Assert.Equal(originalOutputPath, h.Vm.OutputPath);
        Assert.False(h.Vm.LoadVideoCommand.CanExecute(null));

        gate.SetResult();
        await rendering;

        // And the job that WAS running still received the original source, not the dropped one.
        Assert.Equal(originalSourcePath, h.Service.Request!.SourcePath);
    }

    [Fact]
    public async Task Cancel_StopsTheService_AndThaws()
    {
        var h = await BuildLoadedAsync(seconds: 10);
        var gate = new TaskCompletionSource();
        CancellationToken? seenToken = null;
        h.Service.Behavior = async (request, _, ct) =>
        {
            seenToken = ct;
            await gate.Task.WaitAsync(ct); // throws once ct is cancelled -- never completes otherwise
            return Result<string>.Success(request.OutputPath);
        };

        var rendering = h.Vm.CreateCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsRendering);

        h.Vm.Cancel();
        await rendering;

        Assert.NotNull(seenToken);
        Assert.True(seenToken!.Value.IsCancellationRequested); // Cancel reached the SERVICE's token
        Assert.False(h.Vm.IsRendering);
        Assert.True(h.Vm.CanEditParameters); // thawed

        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal(NotificationSeverity.Info, notification.Severity); // canceled, not an error
        Assert.Empty(h.History.Entries);
    }

    [Fact]
    public async Task ABrokenHistorySink_DoesNotReportAGoodGifAsFailed()
    {
        var h = await BuildLoadedAsync(seconds: 10);
        h.History.AppendThrows = new IOException("history.json is locked");

        await h.Vm.CreateCommand.ExecuteAsync(null);

        Assert.Contains(h.Notifications.Sent, n => n.Severity == NotificationSeverity.Success);
        Assert.DoesNotContain(h.Notifications.Sent, n => n.Severity == NotificationSeverity.Error);
        Assert.Contains(h.Notifications.Sent, n => n.Severity == NotificationSeverity.Warning);
    }

    [Fact]
    public async Task AHistorySinkThatThrowsJsonException_DoesNotReportAGoodGifAsFailed()
    {
        // JsonStore<T>.Load treats a JsonException as the same class of "broken store" as IOException/
        // UnauthorizedAccessException -- Append's own serializer can throw one too, and it must be
        // caught here for the same reason: the GIF is already rendered and verified, so a history
        // write failing must never turn into a red "GIF creation failed".
        var h = await BuildLoadedAsync(seconds: 10);
        h.History.AppendThrows = new JsonException("unexpected end of JSON");

        await h.Vm.CreateCommand.ExecuteAsync(null);

        Assert.Contains(h.Notifications.Sent, n => n.Severity == NotificationSeverity.Success);
        Assert.DoesNotContain(h.Notifications.Sent, n => n.Severity == NotificationSeverity.Error);
        Assert.Contains(h.Notifications.Sent, n => n.Severity == NotificationSeverity.Warning);
    }
}
