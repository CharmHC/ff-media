using System.Globalization;
using FFMedia.Core.History;
using FFMedia.Core.Notifications;
using FFMedia.Core.Results;
using FFMedia.Core.Settings;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using FFMedia.Tools.VideoMerger.ViewModels;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergerViewModelTests
{
    // ---- fakes -------------------------------------------------------------

    private sealed class FakeAnalyzer : IMediaAnalyzer
    {
        private readonly Dictionary<string, Result<MediaInfo>> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Probed { get; } = new();

        public void Returns(string path, MediaInfo info) => _byPath[path] = Result<MediaInfo>.Success(info);

        public void Rejects(string path, string error) => _byPath[path] = Result<MediaInfo>.Failure(error);

        public Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Probed.Add(filePath);
            return Task.FromResult(_byPath.TryGetValue(filePath, out var r)
                ? r
                : Result<MediaInfo>.Failure("not configured"));
        }
    }

    private sealed class FakeMergeService : IMergeService
    {
        public MergeRequest? Request { get; private set; }

        public int Calls { get; private set; }

        /// <summary>The token the ViewModel handed us, kept so a test can prove Cancel really reached
        /// the engine and did not merely flip a flag on the ViewModel.</summary>
        public CancellationToken Token { get; private set; }

        /// <summary>Scriptable outcome. Default: succeed, writing to the requested path.</summary>
        public Func<MergeRequest, IProgress<MergeProgress>?, CancellationToken, Task<Result<string>>> Behavior
        { get; set; } = (request, _, _) => Task.FromResult(Result<string>.Success(request.OutputPath));

        public Task<Result<string>> MergeAsync(
            MergeRequest request, IProgress<MergeProgress>? progress = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Request = request;
            Token = ct;
            Calls++;
            return Behavior(request, progress, ct);
        }
    }

    private sealed class FakeSpeedStore : ISpeedProfileStore
    {
        public SpeedProfile Profile { get; set; } = new();

        public SpeedProfile Load() => Profile;

        public void Save(SpeedProfile profile) => Profile = profile;
    }

    /// <summary>Mirrors the real <see cref="ISettingsService"/>: <c>Changed</c> is
    /// <c>EventHandler&lt;AppSettings&gt;</c>, not a bare <c>EventHandler</c>.</summary>
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

        public event EventHandler? Changed;

        public IReadOnlyList<HistoryEntry> Query() => Entries;

        public void Append(HistoryEntry entry)
        {
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

    // ---- helpers -----------------------------------------------------------

    private static MediaInfo Info(int width = 1920, int height = 1080, string codec = "h264", double seconds = 5)
        => new(TimeSpan.FromSeconds(seconds), "mov,mp4,m4a",
            new VideoStreamInfo(width, height, new FrameRate(30, 1), codec, "yuv420p", 0),
            new AudioStreamInfo("aac", 48000, 2));

    /// <summary>A probe that succeeds but found no video track — an audio file.</summary>
    private static MediaInfo AudioOnly()
        => new(TimeSpan.FromSeconds(30), "mov,mp4,m4a", null, new AudioStreamInfo("aac", 48000, 2));

    private sealed record Harness(
        MergerViewModel Vm, FakeAnalyzer Analyzer, FakeMergeService Merger,
        FakeHistory History, FakeNotifications Notifications, FakeSettings Settings);

    private static Harness Build()
    {
        var analyzer = new FakeAnalyzer();
        var merger = new FakeMergeService();
        var history = new FakeHistory();
        var notifications = new FakeNotifications();
        var settings = new FakeSettings();
        var vm = new MergerViewModel(
            analyzer, merger, new FakeSpeedStore(), settings, history, notifications);
        return new Harness(vm, analyzer, merger, history, notifications, settings);
    }

    /// <summary>A harness holding exactly these clips, in this order, by file name.</summary>
    private static async Task<Harness> BuildWithAsync(params string[] names)
    {
        var h = Build();
        foreach (var name in names)
        {
            h.Analyzer.Returns($@"C:\{name}", Info());
        }

        await h.Vm.AddClipsAsync(names.Select(name => $@"C:\{name}"));
        return h;
    }

    private static async Task<Harness> BuildWithClipsAsync(int count)
    {
        var h = Build();
        for (var i = 0; i < count; i++)
        {
            h.Analyzer.Returns($@"C:\{i}.mp4", Info());
        }

        await h.Vm.AddClipsAsync(Enumerable.Range(0, count).Select(i => $@"C:\{i}.mp4"));
        return h;
    }

    private static List<string> Names(MergerViewModel vm) => vm.Clips.Select(c => c.FileName).ToList();

    // ---- adding ------------------------------------------------------------

    [Fact]
    public async Task AddClipsAsync_ProbesEachFileAndAddsItInOrder()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720, "vp9"));

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        Assert.Equal(new[] { "a.mp4", "b.mp4" }, Names(h.Vm));
        Assert.Equal(new[] { @"C:\a.mp4", @"C:\b.mp4" }, h.Analyzer.Probed);
        Assert.Equal(@"C:\b.mp4", h.Vm.Clips[1].SourcePath);
        Assert.Equal("1280x720 · 30 fps · vp9 · 0:05", h.Vm.Clips[1].Details); // the probe really reached the row
        Assert.Empty(h.Notifications.Sent);
    }

    [Fact]
    public void Constructor_SeedsTheOutputFolderFromSettings()
    {
        var h = Build();

        Assert.Equal(@"C:\out", h.Vm.OutputFolder);
    }

    [Fact]
    public async Task AddClipsAsync_RejectsAFileTheAnalyzerCannotRead_RatherThanPoisoningTheMerge()
    {
        // Spec §8: a bad file is rejected AT ADD TIME. Letting it into the list would fail the
        // whole merge later, after the user has spent minutes ordering clips.
        var h = Build();
        h.Analyzer.Returns(@"C:\good.mp4", Info());
        h.Analyzer.Rejects(@"C:\notes.txt", "no video stream");

        await h.Vm.AddClipsAsync([@"C:\good.mp4", @"C:\notes.txt"]);

        Assert.Equal(new[] { "good.mp4" }, Names(h.Vm));
        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal("Not a video", notification.Title);
        Assert.Equal("notes.txt could not be read as a video and was not added.", notification.Message);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
    }

    [Fact]
    public async Task AddClipsAsync_RejectsAnAudioFile_EvenThoughTheProbeSucceeded()
    {
        // The probe reads voiceover.m4a perfectly well — it simply has no video track. Concat would
        // fail on it (or silently mangle the layout), so it must never reach the list.
        var h = Build();
        h.Analyzer.Returns(@"C:\good.mp4", Info());
        h.Analyzer.Returns(@"C:\voiceover.m4a", AudioOnly());

        await h.Vm.AddClipsAsync([@"C:\voiceover.m4a", @"C:\good.mp4"]);

        Assert.Equal(new[] { "good.mp4" }, Names(h.Vm));
        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal("voiceover.m4a could not be read as a video and was not added.", notification.Message);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
    }

    [Fact]
    public async Task AddClipsAsync_IgnoresAFileAlreadyInTheList_WithoutReprobingIt()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());

        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);
        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);

        Assert.Single(h.Vm.Clips);
        Assert.Single(h.Analyzer.Probed);
        Assert.Empty(h.Notifications.Sent); // a duplicate is not an error — say nothing
    }

    [Fact]
    public async Task AddClipsAsync_IgnoresADuplicateWithinTheSameBatch()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\A.MP4"]); // Windows paths are case-insensitive

        Assert.Single(h.Vm.Clips);
        Assert.Single(h.Analyzer.Probed);
    }

    [Fact]
    public async Task AddClipsAsync_RejectsNullPaths()
    {
        var h = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Vm.AddClipsAsync(null!));
    }

    [Fact]
    public void Constructor_RejectsEveryNullDependency()
    {
        var analyzer = new FakeAnalyzer();
        var merger = new FakeMergeService();
        var speeds = new FakeSpeedStore();
        var settings = new FakeSettings();
        var history = new FakeHistory();
        var notifications = new FakeNotifications();

        Assert.Throws<ArgumentNullException>(() =>
            new MergerViewModel(null!, merger, speeds, settings, history, notifications));
        Assert.Throws<ArgumentNullException>(() =>
            new MergerViewModel(analyzer, null!, speeds, settings, history, notifications));
        Assert.Throws<ArgumentNullException>(() =>
            new MergerViewModel(analyzer, merger, null!, settings, history, notifications));
        Assert.Throws<ArgumentNullException>(() =>
            new MergerViewModel(analyzer, merger, speeds, null!, history, notifications));
        Assert.Throws<ArgumentNullException>(() =>
            new MergerViewModel(analyzer, merger, speeds, settings, null!, notifications));
        Assert.Throws<ArgumentNullException>(() =>
            new MergerViewModel(analyzer, merger, speeds, settings, history, null!));
    }

    // ---- reordering --------------------------------------------------------

    [Fact]
    public async Task MoveUpAndDown_ReorderTheList_AndStopAtTheEnds()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        h.Vm.MoveDown(h.Vm.Clips[0]);
        Assert.Equal(new[] { "b.mp4", "a.mp4" }, Names(h.Vm));

        h.Vm.MoveUp(h.Vm.Clips[1]);
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, Names(h.Vm));

        h.Vm.MoveUp(h.Vm.Clips[0]);   // already first — a no-op, not a crash
        h.Vm.MoveDown(h.Vm.Clips[1]); // already last
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, Names(h.Vm));
    }

    [Fact]
    public async Task MoveUp_AtTheTop_DoesNothing_RatherThanWrappingToTheBottom()
    {
        // Asserted on its own, with THREE clips. The previous test could not catch a wrap-around:
        // with two clips, a wrapping MoveUp followed by a wrapping MoveDown cancel out, and the
        // list looks untouched. The bug it hides is loud — clicking "move up" on the top clip
        // teleports it to the bottom.
        var h = await BuildWithAsync("a.mp4", "b.mp4", "c.mp4");

        h.Vm.MoveUp(h.Vm.Clips[0]);

        Assert.Equal(new[] { "a.mp4", "b.mp4", "c.mp4" }, Names(h.Vm));
    }

    [Fact]
    public async Task MoveDown_AtTheBottom_DoesNothing_RatherThanWrappingToTheTop()
    {
        var h = await BuildWithAsync("a.mp4", "b.mp4", "c.mp4");

        h.Vm.MoveDown(h.Vm.Clips[2]);

        Assert.Equal(new[] { "a.mp4", "b.mp4", "c.mp4" }, Names(h.Vm));
    }

    [Fact]
    public async Task Shuffle_HonorsALockSetByBindingIsLockedDirectly_NotJustSetLock()
    {
        // The page's lock toggle two-way binds a checkbox to IsLocked — the obvious thing to do, and
        // it does NOT go through SetLock, so LockedIndex stays null. Ordering.Shuffle reads only
        // LockedIndex, so without a resync the "locked" row would be shuffled like any other and the
        // lock then re-pinned to wherever it randomly landed: worse than the lock doing nothing.
        var h = await BuildWithAsync("a.mp4", "b.mp4", "c.mp4", "d.mp4", "e.mp4", "f.mp4");
        h.Vm.Clips[2].IsLocked = true; // straight at the property, as a binding would
        Assert.Null(h.Vm.Clips[2].LockedIndex);

        for (var seed = 0; seed < 25; seed++)
        {
            h.Vm.ShuffleSeed = seed;
            h.Vm.Shuffle();

            Assert.Equal("c.mp4", h.Vm.Clips[2].FileName);
            Assert.Equal(6, Names(h.Vm).Distinct().Count());
        }
    }

    [Fact]
    public async Task MoveUpOrDown_OnAClipThatIsNotInTheList_IsANoOp()
    {
        var h = await BuildWithClipsAsync(2);
        var stranger = new MergeClipViewModel(new MergeClip(@"C:\stranger.mp4", Info()));

        h.Vm.MoveUp(stranger);
        h.Vm.MoveDown(stranger);

        Assert.Equal(new[] { "0.mp4", "1.mp4" }, Names(h.Vm));
    }

    [Fact]
    public async Task RemoveClip_DropsIt()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        h.Vm.RemoveClip(h.Vm.Clips[0]);

        Assert.Equal(new[] { "b.mp4" }, Names(h.Vm));
    }

    [Fact]
    public async Task RemoveClip_LetsTheSameFileBeAddedAgain()
    {
        var h = await BuildWithClipsAsync(1);

        h.Vm.RemoveClip(h.Vm.Clips[0]);
        await h.Vm.AddClipsAsync([@"C:\0.mp4"]);

        Assert.Equal(new[] { "0.mp4" }, Names(h.Vm));
        Assert.Equal(2, h.Analyzer.Probed.Count); // it is genuinely re-probed, not resurrected
    }

    // ---- shuffle -----------------------------------------------------------

    [Fact]
    public async Task Shuffle_KeepsLockedClipsAtTheirIndex()
    {
        var h = await BuildWithClipsAsync(6);

        h.Vm.Clips[2].SetLock(locked: true, index: 2);
        h.Vm.Clips[5].SetLock(locked: true, index: 5);

        for (var seed = 0; seed < 25; seed++)
        {
            h.Vm.ShuffleSeed = seed;
            h.Vm.Shuffle();

            Assert.Equal("2.mp4", h.Vm.Clips[2].FileName);
            Assert.Equal("5.mp4", h.Vm.Clips[5].FileName);
            Assert.Equal(6, h.Vm.Clips.Select(c => c.FileName).Distinct().Count()); // nothing lost or duplicated
        }
    }

    [Fact]
    public async Task Shuffle_ActuallyReordersTheUnlockedClips()
    {
        // Guards the direction the lock test cannot: a Shuffle() that did nothing at all would
        // satisfy every "locked clip stayed put" assertion above.
        var h = await BuildWithClipsAsync(6);

        var orders = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 25; seed++)
        {
            h.Vm.ShuffleSeed = seed;
            h.Vm.Shuffle();
            orders.Add(string.Join(",", Names(h.Vm)));
        }

        Assert.True(orders.Count > 1, "Shuffle never changed the order.");
    }

    [Fact]
    public async Task Shuffle_IsDeterministicForAGivenSeed()
    {
        var a = await BuildWithClipsAsync(6);
        var b = await BuildWithClipsAsync(6);

        a.Vm.ShuffleSeed = 4242;
        a.Vm.Shuffle();
        b.Vm.ShuffleSeed = 4242;
        b.Vm.Shuffle();

        Assert.Equal(Names(a.Vm), Names(b.Vm));
    }

    [Fact]
    public async Task Shuffle_OnZeroOrOneClip_IsANoOp()
    {
        var empty = Build();
        empty.Vm.ShuffleSeed = 7;
        empty.Vm.Shuffle();
        Assert.Empty(empty.Vm.Clips);

        var single = await BuildWithClipsAsync(1);
        single.Vm.ShuffleSeed = 7;
        single.Vm.Shuffle();
        Assert.Equal(new[] { "0.mp4" }, Names(single.Vm));
    }

    // ---- the lock/index invariant: locks pin an OCCUPIED slot ---------------

    [Fact]
    public async Task RemovingAClipAboveALockedOne_ResyncsTheLock_SoShuffleDoesNotPinAStaleSlot()
    {
        // "5.mp4" is locked to index 5. Delete "0.mp4" and it slides to index 4 — but its lock still
        // says 5. Shuffle would then pin it to a row the user can see it is not in (or, with a second
        // lock, pin two clips to one slot and throw).
        var h = await BuildWithClipsAsync(6);
        h.Vm.Clips[5].SetLock(locked: true, index: 5);

        h.Vm.RemoveClip(h.Vm.Clips[0]);

        Assert.Equal(4, h.Vm.Clips[4].LockedIndex);

        for (var seed = 0; seed < 25; seed++)
        {
            h.Vm.ShuffleSeed = seed;
            h.Vm.Shuffle(); // must not throw
            Assert.Equal("5.mp4", h.Vm.Clips[4].FileName);
            Assert.Equal(5, h.Vm.Clips.Count);
        }
    }

    [Fact]
    public async Task RemovingAClip_CannotLeaveTwoLocksOnTheSameIndex()
    {
        // Without a resync, deleting index 0 leaves locks on 2 and 3 pointing at 2 and 3 while the
        // rows now sit at 1 and 2 — and the next structural edit collapses them onto one slot.
        // Ordering.Shuffle throws ArgumentException when two clips claim the same index.
        var h = await BuildWithClipsAsync(5);
        h.Vm.Clips[2].SetLock(locked: true, index: 2);
        h.Vm.Clips[3].SetLock(locked: true, index: 3);

        h.Vm.RemoveClip(h.Vm.Clips[0]);
        h.Vm.ShuffleSeed = 1;
        h.Vm.Shuffle();

        Assert.Equal(new[] { 1, 2 }, h.Vm.Clips.Where(c => c.IsLocked).Select(c => c.LockedIndex!.Value).ToArray());
        Assert.Equal("2.mp4", h.Vm.Clips[1].FileName);
        Assert.Equal("3.mp4", h.Vm.Clips[2].FileName);
    }

    [Fact]
    public async Task MovingAClipPastALockedOne_ResyncsTheLockToWhereTheRowNowSits()
    {
        // The lock pins the SLOT, not the clip: dragging a neighbour past a locked row moves that
        // row, and the lock must follow it to its new index or the next shuffle teleports it back.
        var h = await BuildWithClipsAsync(3);
        h.Vm.Clips[1].SetLock(locked: true, index: 1); // "1.mp4" pinned in the middle

        h.Vm.MoveUp(h.Vm.Clips[1]); // the user drags the locked row itself up

        Assert.Equal("1.mp4", h.Vm.Clips[0].FileName);
        Assert.Equal(0, h.Vm.Clips[0].LockedIndex);

        for (var seed = 0; seed < 25; seed++)
        {
            h.Vm.ShuffleSeed = seed;
            h.Vm.Shuffle();
            Assert.Equal("1.mp4", h.Vm.Clips[0].FileName);
        }
    }

    [Fact]
    public async Task AddingAClip_LeavesExistingLocksWhereTheyAre()
    {
        // Appends land below, so no existing row shifts — but the resync must not invent a lock on an
        // unlocked row either.
        var h = await BuildWithClipsAsync(3);
        h.Vm.Clips[0].SetLock(locked: true, index: 0);
        h.Analyzer.Returns(@"C:\new.mp4", Info());

        await h.Vm.AddClipsAsync([@"C:\new.mp4"]);

        Assert.Equal(0, h.Vm.Clips[0].LockedIndex);
        Assert.Equal(1, h.Vm.Clips.Count(c => c.IsLocked));
        Assert.All(h.Vm.Clips.Skip(1), c => Assert.Null(c.LockedIndex));
    }

    [Fact]
    public async Task UnlockingARow_FreesItToMove()
    {
        var h = await BuildWithClipsAsync(6);
        h.Vm.Clips[0].SetLock(locked: true, index: 0);
        h.Vm.Clips[0].SetLock(locked: false, index: 0);

        var orders = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 25; seed++)
        {
            h.Vm.ShuffleSeed = seed;
            h.Vm.Shuffle();
            orders.Add(h.Vm.Clips[0].FileName);
        }

        Assert.True(orders.Count > 1, "An unlocked row stayed pinned to index 0.");
    }

    // ---- the target: derived, then overridable ------------------------------

    [Fact]
    public async Task Target_IsDerivedFromTheClips()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1280, 720));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1920, 1080)); // largest area wins

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        Assert.Equal(1920, h.Vm.Target.Width);
        Assert.Equal(1080, h.Vm.Target.Height);
        Assert.False(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public void Target_WithNoClips_IsTheDefault_AndIsNotAnOverride()
    {
        var h = Build();

        Assert.Same(MergeTarget.Default, h.Vm.Target);
        Assert.False(h.Vm.IsTargetOverridden);
        Assert.Equal("Add at least two clips to merge.", h.Vm.Summary);
    }

    [Fact]
    public async Task Target_IsReDerived_WhenAClipIsRemoved()
    {
        // Not merely "survives a removal": drop the 4K clip and the proposal must come back DOWN.
        var h = Build();
        h.Analyzer.Returns(@"C:\hd.mp4", Info(1920, 1080));
        h.Analyzer.Returns(@"C:\uhd.mp4", Info(3840, 2160));
        await h.Vm.AddClipsAsync([@"C:\hd.mp4", @"C:\uhd.mp4"]);
        Assert.Equal(3840, h.Vm.Target.Width);

        h.Vm.RemoveClip(h.Vm.Clips[1]);

        Assert.Equal(1920, h.Vm.Target.Width);
        Assert.Equal(1080, h.Vm.Target.Height);
        Assert.False(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public async Task Target_OnceOverridden_SurvivesAddingAnotherClip()
    {
        // The user deliberately chose 4K. Adding a 720p clip must not silently undo that.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720));
        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);

        h.Vm.Target = h.Vm.Target with { Width = 3840, Height = 2160 };
        Assert.True(h.Vm.IsTargetOverridden);

        await h.Vm.AddClipsAsync([@"C:\b.mp4"]);

        Assert.Equal(3840, h.Vm.Target.Width);
        Assert.Equal(2160, h.Vm.Target.Height);
        Assert.True(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public async Task Target_OnceOverridden_SurvivesRemovingAClipToo()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720));
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        h.Vm.Target = h.Vm.Target with { Width = 3840, Height = 2160 };
        h.Vm.RemoveClip(h.Vm.Clips[1]);

        Assert.Equal(3840, h.Vm.Target.Width);
        Assert.True(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public async Task ResetTargetToDerived_RestoresTheProposal()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080));
        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);
        h.Vm.Target = h.Vm.Target with { Width = 640, Height = 480 };

        h.Vm.ResetTargetToDerived();

        Assert.Equal(1920, h.Vm.Target.Width);
        Assert.Equal(1080, h.Vm.Target.Height);
        Assert.False(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public async Task ResetTargetToDerived_ThenAddingAClip_ReDerivesAgain()
    {
        // Reset must genuinely re-arm the derivation, not just restore one value.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1280, 720));
        h.Analyzer.Returns(@"C:\b.mp4", Info(3840, 2160));
        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);
        h.Vm.Target = h.Vm.Target with { Width = 640, Height = 480 };
        h.Vm.ResetTargetToDerived();

        await h.Vm.AddClipsAsync([@"C:\b.mp4"]);

        Assert.Equal(3840, h.Vm.Target.Width);
        Assert.False(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public async Task Rederiving_NeverFlickersIsTargetOverriddenTrue()
    {
        // A re-derivation writes the same property the user's edit does, so without the suppression
        // guard the flag flips false -> true -> false on every single add. The end state is right,
        // but the view sees the notification: the "target overridden — reset?" affordance would
        // blink into existence and out again each time a clip is dropped in.
        var h = await BuildWithClipsAsync(1);
        h.Analyzer.Returns(@"C:\big.mp4", Info(3840, 2160));

        var overriddenNotifications = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MergerViewModel.IsTargetOverridden))
            {
                overriddenNotifications++;
            }
        };

        await h.Vm.AddClipsAsync([@"C:\big.mp4"]); // re-derives 1080p -> 4K

        Assert.Equal(3840, h.Vm.Target.Width); // the derivation really did move the target
        Assert.False(h.Vm.IsTargetOverridden);
        Assert.Equal(0, overriddenNotifications);
    }

    [Fact]
    public async Task EditingTheTarget_RefreshesEveryClipBadge()
    {
        // The clip conforms to the derived 1080p target, and stops conforming when the user
        // switches to 4K. Nothing is re-probed — the badge just recomputes.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080));
        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);
        Assert.True(h.Vm.Clips[0].IsConforming);
        Assert.Equal("Conforms", h.Vm.Clips[0].Badge);

        h.Vm.Target = h.Vm.Target with { Width = 3840, Height = 2160 };

        Assert.False(h.Vm.Clips[0].IsConforming);
        Assert.Equal("Re-encode", h.Vm.Clips[0].Badge);
        Assert.Equal("resolution 1920x1080 != 3840x2160", h.Vm.Clips[0].BadgeTooltip);
        Assert.Single(h.Analyzer.Probed); // and the file was probed exactly once, at add-time
    }

    [Fact]
    public async Task AddingAClip_AppliesTheTargetToTheBadgesImmediately()
    {
        // Before Task 6 the badge was blank until something else nudged it. A row with no verdict
        // is the one thing the list must never show: the user cannot tell "conforms" from "unknown".
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720, "vp9"));

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        Assert.Equal(new[] { "Conforms", "Re-encode" }, h.Vm.Clips.Select(c => c.Badge).ToArray());
        Assert.Equal(new[] { false, true }, h.Vm.Clips.Select(c => c.ShowProgressBar).ToArray());
    }

    [Fact]
    public async Task Target_RaisesPropertyChangedForItselfAndSelectedFitMode()
    {
        var h = await BuildWithClipsAsync(2);
        var raised = new List<string>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        h.Vm.Target = h.Vm.Target with { Width = 3840, Height = 2160 };

        Assert.Contains("Target", raised);
        Assert.Contains("SelectedFitMode", raised);
        Assert.Contains("IsTargetOverridden", raised);
        Assert.Contains("Summary", raised);
    }

    [Fact]
    public async Task ResetTargetToDerived_AlsoRaisesPropertyChangedForTarget()
    {
        // SetProperty on the backing field is how the re-derivation dodges the "user overrode it"
        // hook — but it must still tell the view the value moved, or the panel shows a stale target.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080));
        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);
        h.Vm.Target = h.Vm.Target with { Width = 640, Height = 480 };

        var raised = new List<string>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        h.Vm.ResetTargetToDerived();

        Assert.Contains("Target", raised);
        Assert.Contains("SelectedFitMode", raised);
        Assert.Contains("IsTargetOverridden", raised);
    }

    // ---- fit mode ----------------------------------------------------------

    [Fact]
    public void FitModes_OffersEveryMode_InDeclarationOrder()
    {
        var h = Build();

        Assert.Equal(new[] { FitMode.Fit, FitMode.Fill, FitMode.Stretch }, h.Vm.FitModes.ToArray());
        Assert.Equal(FitMode.Fit, h.Vm.SelectedFitMode);
    }

    [Fact]
    public async Task SelectedFitMode_WritesThroughToTheTarget_ButIsNotATargetOverride()
    {
        // Fit mode says nothing about the resolution/codec/rate we are aiming at — only how a
        // mismatched clip gets there. Treating it as an override would freeze the target.
        var h = await BuildWithClipsAsync(2);
        Assert.False(h.Vm.IsTargetOverridden);

        h.Vm.SelectedFitMode = FitMode.Fill;

        Assert.Equal(FitMode.Fill, h.Vm.Target.FitMode);
        Assert.Equal(FitMode.Fill, h.Vm.SelectedFitMode);
        Assert.False(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public async Task ChoosingAFitModeBeforeAddingClips_DoesNotFreezeTheTargetAtTheDefault()
    {
        // The disaster this guards. Fit mode is a merge-wide preference, so setting it FIRST is a
        // natural order. If that latched IsTargetOverridden, derivation would never run: these 4K
        // clips would land against the 1080p default, be silently downscaled, and take the slow
        // path — no error, no warning, just a worse merge than the user asked for.
        var h = Build();
        h.Vm.SelectedFitMode = FitMode.Fill;

        h.Analyzer.Returns(@"C:\a.mp4", Info(3840, 2160));
        h.Analyzer.Returns(@"C:\b.mp4", Info(3840, 2160));
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        Assert.Equal(3840, h.Vm.Target.Width);
        Assert.Equal(2160, h.Vm.Target.Height);
        Assert.Equal(FitMode.Fill, h.Vm.Target.FitMode); // and the preference survived derivation
        Assert.True(h.Vm.Clips[0].IsConforming);         // so the clips are NOT needlessly re-encoded
    }

    [Fact]
    public async Task ReDerivingTheTarget_KeepsTheChosenFitMode()
    {
        var h = await BuildWithClipsAsync(2);
        h.Vm.SelectedFitMode = FitMode.Stretch;

        h.Analyzer.Returns(@"C:\extra.mp4", Info(1280, 720));
        await h.Vm.AddClipsAsync([@"C:\extra.mp4"]); // triggers a re-derivation

        Assert.Equal(FitMode.Stretch, h.Vm.SelectedFitMode);
    }

    [Fact]
    public async Task ResetTargetToDerived_KeepsTheFitMode_WhichDerivationCannotInfer()
    {
        var h = await BuildWithClipsAsync(2);
        h.Vm.SelectedFitMode = FitMode.Fill;
        h.Vm.Target = h.Vm.Target with { Width = 640, Height = 480 };

        h.Vm.ResetTargetToDerived();

        Assert.Equal(1920, h.Vm.Target.Width);          // the resolution went back to the proposal
        Assert.Equal(FitMode.Fill, h.Vm.SelectedFitMode); // the preference did not
    }

    [Fact]
    public async Task SelectedFitMode_SetToItsCurrentValue_IsNotAnOverride()
    {
        // A ComboBox echoes its own value back on load. That must not be mistaken for a user edit,
        // or merely opening the page would freeze the target at whatever the first clip implied.
        var h = await BuildWithClipsAsync(2);

        h.Vm.SelectedFitMode = FitMode.Fit; // already Fit

        Assert.False(h.Vm.IsTargetOverridden);
    }

    // ---- output path -------------------------------------------------------

    [Fact]
    public void OutputPath_CombinesTheFolderAndTheFileName()
    {
        var h = Build();

        Assert.Equal("merged.mp4", h.Vm.OutputFileName);
        Assert.Equal(@"C:\out\merged.mp4", h.Vm.OutputPath);
    }

    [Fact]
    public void OutputPath_TracksBothTheFolderAndTheFileName()
    {
        var h = Build();
        var raised = new List<string>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        h.Vm.OutputFileName = "holiday.mp4";
        Assert.Equal(@"C:\out\holiday.mp4", h.Vm.OutputPath);

        h.Vm.OutputFolder = @"D:\videos";
        Assert.Equal(@"D:\videos\holiday.mp4", h.Vm.OutputPath);

        Assert.Equal(2, raised.Count(name => name == "OutputPath"));
    }

    // ---- the summary line (spec §6.5) --------------------------------------

    [Fact]
    public async Task Summary_CountsClipsDurationAndReencodes()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080, seconds: 120));  // conforms
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720, "vp9", 132));     // needs re-encoding

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        // The ETA is fully determined: an unmeasured SpeedProfile yields the seeded HD1080 factor
        // (3.5x) and the widest ±35% band, so this whole line is pinned, en-dash and all.
        Assert.Equal(
            "2 clips · 4:12 output · 1 needs re-encoding · est. 0:25–0:51",
            h.Vm.Summary);
    }

    [Fact]
    public async Task Summary_PluralizesTwoOrMoreReencodes()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080, seconds: 60));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720, "vp9", 60));
        h.Analyzer.Returns(@"C:\c.mp4", Info(1280, 720, "vp9", 60));

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4", @"C:\c.mp4"]);

        Assert.StartsWith("3 clips · 3:00 output · 2 need re-encoding · est. ", h.Vm.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_SaysSoOnTheFastPath()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080, seconds: 120));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1920, 1080, seconds: 132));

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        Assert.Equal("2 clips · 4:12 output · all clips conform · est. under 5s", h.Vm.Summary);
    }

    [Fact]
    public async Task Summary_ShowsHoursOnceTheOutputPassesAnHour()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080, seconds: 3600));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1920, 1080, seconds: 305));

        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        Assert.Equal("2 clips · 1:05:05 output · all clips conform · est. under 5s", h.Vm.Summary);
    }

    [Fact]
    public async Task Summary_IsInvariant_OnACommaDecimalMachine()
    {
        // Durations and counts are technical figures, not localized prose. A German machine must
        // read back exactly the same line — no thousands separators, no comma decimals.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080, seconds: 120));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720, "vp9", 132));
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        var original = CultureInfo.CurrentCulture;
        try
        {
            // No await inside the scope: the culture must not leak onto a pooled thread.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            h.Vm.ResetTargetToDerived(); // rebuilds the summary under the German culture
            Assert.Equal(
                "2 clips · 4:12 output · 1 needs re-encoding · est. 0:25–0:51",
                h.Vm.Summary);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Summary_RevertsToThePromptWhenTheListDropsBelowTwo()
    {
        var h = await BuildWithClipsAsync(2);
        Assert.NotEqual("Add at least two clips to merge.", h.Vm.Summary);

        h.Vm.RemoveClip(h.Vm.Clips[0]);

        Assert.Equal("Add at least two clips to merge.", h.Vm.Summary);
    }

    [Fact]
    public async Task Summary_RecomputesWhenTheTargetIsEdited()
    {
        // The fast path is only "fast" against the target in force. Raising it to 4K makes both
        // clips non-conforming, and the line must say so — with no re-probe.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080, seconds: 120));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1920, 1080, seconds: 132));
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        Assert.Equal("2 clips · 4:12 output · all clips conform · est. under 5s", h.Vm.Summary);

        h.Vm.Target = h.Vm.Target with { Width = 3840, Height = 2160 };

        Assert.StartsWith("2 clips · 4:12 output · 2 need re-encoding · est. ", h.Vm.Summary, StringComparison.Ordinal);
        Assert.Equal(2, h.Analyzer.Probed.Count);
    }

    // ---- CanMerge ----------------------------------------------------------

    [Fact]
    public async Task CanMerge_RequiresAtLeastTwoClips()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());

        Assert.False(h.Vm.CanMerge);

        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);
        Assert.False(h.Vm.CanMerge); // merging one clip is a copy, not a merge

        await h.Vm.AddClipsAsync([@"C:\b.mp4"]);
        Assert.True(h.Vm.CanMerge);

        h.Vm.RemoveClip(h.Vm.Clips[0]);
        Assert.False(h.Vm.CanMerge);
    }

    [Fact]
    public async Task CanMerge_IsFalseWhileAMergeIsRunning()
    {
        var h = await BuildWithClipsAsync(2);
        var raised = new List<string>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        Assert.True(h.Vm.CanMerge);

        h.Vm.IsMerging = true;

        Assert.False(h.Vm.CanMerge);
        Assert.Contains("CanMerge", raised);

        h.Vm.IsMerging = false;
        Assert.True(h.Vm.CanMerge);
    }

    [Fact]
    public async Task CanMerge_NotifiesWhenTheClipCountCrossesTheThreshold()
    {
        var h = await BuildWithClipsAsync(1);
        var raised = new List<string>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        h.Analyzer.Returns(@"C:\second.mp4", Info());

        await h.Vm.AddClipsAsync([@"C:\second.mp4"]);

        Assert.Contains("CanMerge", raised);
    }

    // ---- merging -----------------------------------------------------------

    [Fact]
    public async Task MergeCommand_CanExecute_TracksCanMerge_AsClipsComeAndGo()
    {
        // The wiring that decides whether the button is clickable at all. CanMerge alone is not
        // enough: ICommand caches its verdict until told otherwise, so without a
        // NotifyCanExecuteChanged() from Recompute() the Merge button stays greyed out forever —
        // the user adds the second clip and nothing happens.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        Assert.False(h.Vm.MergeCommand.CanExecute(null));

        var canExecuteChanges = 0;
        h.Vm.MergeCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);
        Assert.False(h.Vm.MergeCommand.CanExecute(null));

        await h.Vm.AddClipsAsync([@"C:\b.mp4"]);
        Assert.True(h.Vm.MergeCommand.CanExecute(null));

        h.Vm.RemoveClip(h.Vm.Clips[0]);
        Assert.False(h.Vm.MergeCommand.CanExecute(null));

        Assert.Equal(3, canExecuteChanges); // and the view was actually told, each time
    }

    [Fact]
    public async Task Merge_SendsTheClipsInListOrder_WithTheChosenTargetAndOutputPath()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        h.Vm.MoveDown(h.Vm.Clips[0]);          // order is now b, a
        h.Vm.SelectedFitMode = FitMode.Fill;
        h.Vm.OutputFileName = "holiday.mp4";

        await h.Vm.MergeCommand.ExecuteAsync(null);

        var request = h.Merger.Request;
        Assert.NotNull(request);
        Assert.Equal(new[] { @"C:\b.mp4", @"C:\a.mp4" }, request.Clips.Select(c => c.SourcePath).ToArray());
        Assert.Equal(@"C:\out\holiday.mp4", request.OutputPath);
        Assert.Equal(h.Vm.Target, request.Target);
        Assert.Equal(FitMode.Fill, request.Target.FitMode); // the fit mode really rides along
        Assert.Equal(1, h.Merger.Calls);
    }

    [Fact]
    public async Task Merge_OnSuccess_WritesHistoryAndNotifies()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        h.Vm.OutputFileName = "holiday.mp4";

        await h.Vm.MergeCommand.ExecuteAsync(null);

        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(HistorySource.Merge, entry.Source);
        Assert.Equal("holiday.mp4", entry.Title);
        Assert.Equal("", entry.Url);                 // a merge has no URL
        Assert.Equal("Completed", entry.Status);
        Assert.Equal(@"C:\out\holiday.mp4", entry.OutputPath);
        Assert.Equal("MP4 · 1920x1080 · 30 fps", entry.Format);

        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal("Merge complete", notification.Title);
        Assert.Equal(@"Saved to C:\out\holiday.mp4", notification.Message);
        Assert.Equal(NotificationSeverity.Success, notification.Severity);

        Assert.False(h.Vm.IsMerging);
        Assert.Equal(100, h.Vm.OverallPercent);
        Assert.Equal("Merge complete.", h.Vm.StatusMessage);
    }

    [Fact]
    public async Task Merge_OnFailure_NotifiesTheFriendlyMessage_AndWritesNoHistory()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        h.Merger.Behavior = (_, _, _) => Task.FromResult(
            Result<string>.Failure("ffmpeg failed (exit 1):\nav_write(): No space left on device"));

        await h.Vm.MergeCommand.ExecuteAsync(null);

        Assert.Empty(h.History.Entries); // a merge that did not finish is not a merge
        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal("Merge failed", notification.Title);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal("The disk filled up during the merge. Free some space and try again.", notification.Message);
        Assert.Equal("The disk filled up during the merge. Free some space and try again.", h.Vm.StatusMessage);
        Assert.False(h.Vm.IsMerging);
        Assert.True(h.Vm.MergeCommand.CanExecute(null)); // and the user can try again
    }

    [Fact]
    public async Task Merge_WhenTheEngineThrows_IsStillReportedAsAFailure_AndTheButtonComesBack()
    {
        // The engine promises never to throw for an expected failure — but a bug in it (or in a
        // future one) must not leave the page wedged at IsMerging = true with a dead Merge button
        // and an unobserved exception on the command.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        h.Merger.Behavior = (_, _, _) => throw new InvalidOperationException("the engine exploded");

        await h.Vm.MergeCommand.ExecuteAsync(null);

        Assert.Empty(h.History.Entries);
        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal("Merge failed", notification.Title);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal("the engine exploded", notification.Message); // verbatim — it is all we have
        Assert.False(h.Vm.IsMerging);
        Assert.True(h.Vm.MergeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Merge_WhenCanceled_IsNotReportedAsAFailure()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        var tokenWasCanceled = false;
        h.Merger.Behavior = (_, _, ct) =>
        {
            h.Vm.CancelCommand.Execute(null);
            tokenWasCanceled = ct.IsCancellationRequested; // Cancel reached the ENGINE, not just a flag
            return Task.FromResult(Result<string>.Failure("Merge canceled."));
        };

        await h.Vm.MergeCommand.ExecuteAsync(null);

        Assert.True(tokenWasCanceled);
        Assert.Empty(h.History.Entries);
        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal("Merge canceled", notification.Title);
        Assert.Equal("Merge canceled.", notification.Message);
        Assert.Equal(NotificationSeverity.Info, notification.Severity);
        Assert.Equal("Merge canceled.", h.Vm.StatusMessage);
        Assert.False(h.Vm.IsMerging);
    }

    [Fact]
    public async Task Merge_WhenCanceled_IsNotReportedAsAFailure_EvenIfTheEngineThrowsTheCancellation()
    {
        // The engine returns a failure Result today, but an OperationCanceledException escaping it is
        // the ordinary shape of a cancelled Task — and it must reach the SAME "Information, no
        // history, no red toast" path, not the crash handler.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        h.Merger.Behavior = (_, _, ct) =>
        {
            h.Vm.CancelCommand.Execute(null);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result<string>.Success("unreachable"));
        };

        await h.Vm.MergeCommand.ExecuteAsync(null);

        Assert.Empty(h.History.Entries);
        var notification = Assert.Single(h.Notifications.Sent);
        Assert.Equal("Merge canceled", notification.Title);
        Assert.Equal(NotificationSeverity.Info, notification.Severity);
        Assert.Equal("Merge canceled.", h.Vm.StatusMessage);
        Assert.False(h.Vm.IsMerging);
    }

    [Fact]
    public async Task CancelCommand_IsEnabledOnlyWhileMerging()
    {
        var h = await BuildWithClipsAsync(2);
        Assert.False(h.Vm.CancelCommand.CanExecute(null));

        var gate = new TaskCompletionSource();
        h.Merger.Behavior = async (request, _, _) =>
        {
            Assert.True(h.Vm.CancelCommand.CanExecute(null));
            await gate.Task;
            return Result<string>.Success(request.OutputPath);
        };

        var merging = h.Vm.MergeCommand.ExecuteAsync(null);
        gate.SetResult();
        await merging;

        Assert.False(h.Vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void CancelCommand_WithNoMergeRunning_IsASilentNoOp()
    {
        var h = Build();

        h.Vm.CancelCommand.Execute(null); // must not NullReference on the absent CTS

        Assert.Empty(h.Notifications.Sent);
        Assert.False(h.Vm.IsMerging);
    }

    [Fact]
    public async Task Merge_ForwardsProgressToTheOverallBarAndEachClipRow()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720, "vp9")); // b is the one that must be re-encoded
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        var seen = new List<string>();
        h.Merger.Behavior = (request, progress, _) =>
        {
            Assert.NotNull(progress);
            progress.Report(new MergeProgress(MergeJobStatus.Normalizing, 40, "b.mp4", [100, 30]));
            seen.Add(h.Vm.StatusMessage);
            seen.Add(h.Vm.Clips[1].ProgressText);
            Assert.Equal(40, h.Vm.OverallPercent);
            Assert.Equal(30, h.Vm.Clips[1].Percent);

            progress.Report(new MergeProgress(MergeJobStatus.Concatenating, 97, null, [100, 100]));
            seen.Add(h.Vm.StatusMessage);
            return Task.FromResult(Result<string>.Success(request.OutputPath));
        };

        await h.Vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Standardizing b.mp4…", "Re-encoding… 30%", "Joining clips…" }, seen.ToArray());
        Assert.Equal(100, h.Vm.OverallPercent); // success pins the bar to full
        Assert.Equal(100, h.Vm.Clips[0].Percent);
        Assert.Equal(100, h.Vm.Clips[1].Percent);
    }

    [Fact]
    public async Task Merge_ResetsEveryRowsProgress_BeforeItStarts()
    {
        // A second merge must not open with the first one's bars already full.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info(1280, 720, "vp9"));
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);
        h.Merger.Behavior = (request, progress, _) =>
        {
            progress!.Report(new MergeProgress(MergeJobStatus.Normalizing, 50, "b.mp4", [100, 100]));
            return Task.FromResult(Result<string>.Success(request.OutputPath));
        };
        await h.Vm.MergeCommand.ExecuteAsync(null);
        Assert.Equal(100, h.Vm.Clips[1].Percent);

        var atStart = new List<double>();
        h.Merger.Behavior = (request, _, _) =>
        {
            atStart.AddRange(h.Vm.Clips.Select(c => c.Percent));
            return Task.FromResult(Result<string>.Success(request.OutputPath));
        };

        await h.Vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 0d, 0d }, atStart.ToArray());
        Assert.Equal(2, h.Merger.Calls);
    }

    [Fact]
    public async Task Merge_ToleratesAProgressReportWithFewerPercentsThanRows()
    {
        // Defensive: the engine indexes ClipPercents by MergeRequest.Clips, so a short list would be
        // a bug — but an IndexOutOfRange thrown on a progress callback would take the merge down with
        // it, turning a cosmetic defect into a failed merge.
        var h = await BuildWithClipsAsync(2);
        h.Merger.Behavior = (request, progress, _) =>
        {
            progress!.Report(new MergeProgress(MergeJobStatus.Normalizing, 10, "0.mp4", [55]));
            return Task.FromResult(Result<string>.Success(request.OutputPath));
        };

        await h.Vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal(55, h.Vm.Clips[0].Percent);
        Assert.Equal(0, h.Vm.Clips[1].Percent);
        Assert.Equal(100, h.Vm.OverallPercent);
    }

    [Fact]
    public async Task Merge_IsDisabledWhileMerging()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info());
        h.Analyzer.Returns(@"C:\b.mp4", Info());
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        var gate = new TaskCompletionSource();
        h.Merger.Behavior = async (request, _, _) =>
        {
            Assert.True(h.Vm.IsMerging);
            Assert.False(h.Vm.CanMerge);
            Assert.False(h.Vm.MergeCommand.CanExecute(null)); // one merge at a time (spec D8)
            await gate.Task;
            return Result<string>.Success(request.OutputPath);
        };

        var merging = h.Vm.MergeCommand.ExecuteAsync(null);
        gate.SetResult();
        await merging;

        Assert.True(h.Vm.MergeCommand.CanExecute(null));
        Assert.Equal(1, h.Merger.Calls);
    }
}
