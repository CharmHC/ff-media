using System.ComponentModel;
using System.Globalization;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using FFMedia.Tools.VideoMerger.ViewModels;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergeClipViewModelTests
{
    private static MergeClip Clip(
        string path,
        int width = 1280,
        int height = 720,
        string codec = "vp9",
        double seconds = 32,
        FrameRate? frameRate = null)
        => new(path, new MediaInfo(
            TimeSpan.FromSeconds(seconds), "matroska,webm",
            new VideoStreamInfo(width, height, frameRate ?? new FrameRate(60, 1), codec, "yuv420p", 0),
            new AudioStreamInfo("aac", 48000, 2)));

    /// <summary>A clip that matches <see cref="MergeTarget.Default"/> in every respect.</summary>
    private static MergeClip ConformingClip()
        => new(@"C:\clips\intro.mp4", new MediaInfo(
            TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
            new AudioStreamInfo("aac", 48000, 2)));

    private static T WithCulture<T>(string culture, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Details_SummarizeTheProbedStreams()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));

        Assert.Equal(@"C:\clips\holiday.webm", vm.SourcePath);
        Assert.Equal("holiday.webm", vm.FileName);
        Assert.Equal("1280x720 · 60 fps · vp9 · 0:32", vm.Details);
    }

    [Fact]
    public void Details_UsesInvariantCulture_SoACommaDecimalLocaleStillReads5994Fps()
    {
        // A German machine formats 59.94 as "59,94" — the details line must not follow the locale.
        var details = WithCulture("de-DE", () =>
            new MergeClipViewModel(Clip(@"C:\clips\ntsc.mp4", frameRate: new FrameRate(60000, 1001))).Details);

        Assert.Equal("1280x720 · 59.94 fps · vp9 · 0:32", details);
    }

    [Fact]
    public void Details_UsesAnHourComponentOnlyForClipsAtLeastAnHourLong()
    {
        var justUnder = new MergeClipViewModel(Clip(@"C:\a.mp4", seconds: 3599)).Details;
        var justOver = new MergeClipViewModel(Clip(@"C:\a.mp4", seconds: 3723)).Details;

        Assert.Equal("1280x720 · 60 fps · vp9 · 59:59", justUnder);
        Assert.Equal("1280x720 · 60 fps · vp9 · 1:02:03", justOver);
    }

    [Fact]
    public void Details_ForAClipWithNoVideoTrack_SaysSo_RatherThanThrowing()
    {
        var audioOnly = new MergeClip(@"C:\clips\voiceover.m4a", new MediaInfo(
            TimeSpan.FromSeconds(32), "mov,mp4,m4a", null, new AudioStreamInfo("aac", 48000, 2)));

        var vm = new MergeClipViewModel(audioOnly);

        Assert.Equal("no video track · 0:32", vm.Details);
    }

    [Fact]
    public void BeforeATargetIsApplied_TheRowMakesNoClaim()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));

        Assert.False(vm.IsConforming);
        Assert.Equal("", vm.Badge);
        Assert.Equal("", vm.BadgeTooltip);
        Assert.Equal("", vm.ProgressText);
        Assert.False(vm.ShowProgressBar);
    }

    [Fact]
    public void ApplyTarget_MarksANonConformingClipForReencode_AndExplainsWhy()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));

        vm.ApplyTarget(MergeTarget.Default); // 1920x1080 @30 h264

        Assert.False(vm.IsConforming);
        Assert.Equal("Re-encode", vm.Badge);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "resolution 1280x720 != 1920x1080",
                "frame rate 60 != 30",
                "video codec vp9 != h264"),
            vm.BadgeTooltip);
        Assert.True(vm.ShowProgressBar);
    }

    [Fact]
    public void ApplyTarget_MarksAConformingClipAsSuch()
    {
        var vm = new MergeClipViewModel(ConformingClip());

        vm.ApplyTarget(MergeTarget.Default);

        Assert.True(vm.IsConforming);
        Assert.Equal("Conforms", vm.Badge);
        Assert.Equal("", vm.BadgeTooltip);
    }

    [Fact]
    public void ApplyTarget_ReevaluatesFromScratch_WhenTheTargetIsEdited()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));
        vm.ApplyTarget(MergeTarget.Default);
        Assert.False(vm.IsConforming);

        // The user retargets 720p60 vp9 — the very shape of this clip.
        var matching = MergeTarget.Default with
        {
            Width = 1280,
            Height = 720,
            FrameRate = new FrameRate(60, 1),
            VideoCodec = MergeVideoCodec.H264,
        };
        vm.ApplyTarget(matching with { Width = 1280, Height = 720 });

        // vp9 is still not h264, so it is still a re-encode — but the resolution/fps reasons are gone.
        Assert.False(vm.IsConforming);
        Assert.Equal("video codec vp9 != h264", vm.BadgeTooltip);

        var vp9Vm = new MergeClipViewModel(ConformingClip());
        vp9Vm.ApplyTarget(MergeTarget.Default with { Width = 640, Height = 480 });
        Assert.False(vp9Vm.IsConforming);
        vp9Vm.ApplyTarget(MergeTarget.Default);
        Assert.True(vp9Vm.IsConforming);
        Assert.Equal("Conforms", vp9Vm.Badge);
        Assert.Equal("", vp9Vm.BadgeTooltip);
    }

    [Fact]
    public void ApplyTarget_RaisesPropertyChangedForEveryDerivedProperty()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ApplyTarget(MergeTarget.Default);

        Assert.Contains(nameof(MergeClipViewModel.IsConforming), raised);
        Assert.Contains(nameof(MergeClipViewModel.Badge), raised);
        Assert.Contains(nameof(MergeClipViewModel.BadgeTooltip), raised);
        Assert.Contains(nameof(MergeClipViewModel.ShowProgressBar), raised);
        Assert.Contains(nameof(MergeClipViewModel.ProgressText), raised);
    }

    [Fact]
    public void AConformingRow_NeverReadsAsDone_NorShowsAPermanentlyFullBar()
    {
        // The engine reports ClipPercents[i] == 100 from the FIRST report for a conforming clip:
        // it has no re-encode work. A label derived from the percentage would read "Done" before a
        // single byte of output existed.
        var vm = new MergeClipViewModel(ConformingClip());
        vm.ApplyTarget(MergeTarget.Default);

        vm.Percent = 100;

        Assert.Equal("No re-encode needed", vm.ProgressText);
        Assert.False(vm.ShowProgressBar);
    }

    [Fact]
    public void AReencodingRow_ReportsItsRealProgress()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));
        vm.ApplyTarget(MergeTarget.Default);

        Assert.Equal("Waiting to re-encode", vm.ProgressText);

        vm.Percent = 42.4;
        Assert.Equal("Re-encoding… 42%", vm.ProgressText);

        vm.Percent = 100;
        Assert.Equal("Re-encoded", vm.ProgressText);

        Assert.True(vm.ShowProgressBar);
    }

    [Fact]
    public void ProgressText_UsesInvariantCulture()
    {
        var text = WithCulture("de-DE", () =>
        {
            var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));
            vm.ApplyTarget(MergeTarget.Default);
            vm.Percent = 7.8;
            return vm.ProgressText;
        });

        Assert.Equal("Re-encoding… 7%", text);
    }

    [Fact]
    public void Percent_RaisesPropertyChangedForProgressText()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\clips\holiday.webm"));
        vm.ApplyTarget(MergeTarget.Default);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Percent = 50;

        Assert.Contains(nameof(MergeClipViewModel.Percent), raised);
        Assert.Contains(nameof(MergeClipViewModel.ProgressText), raised);
    }

    [Fact]
    public void SetLock_PinsTheRowToAnIndex()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\a.webm"));

        vm.SetLock(locked: true, index: 3);
        Assert.True(vm.IsLocked);
        Assert.Equal(3, vm.LockedIndex);

        vm.SetLock(locked: false, index: 3);
        Assert.False(vm.IsLocked);
        Assert.Null(vm.LockedIndex); // an unlocked row must not still claim an index
    }

    [Fact]
    public void ClearingIsLockedDirectly_AlsoReleasesTheIndex()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\a.webm"));
        vm.SetLock(locked: true, index: 2);

        vm.IsLocked = false;

        Assert.Null(vm.LockedIndex);
    }

    [Fact]
    public void SetLock_RejectsANegativeIndex()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\a.webm"));

        Assert.Throws<ArgumentOutOfRangeException>(() => vm.SetLock(locked: true, index: -1));
        Assert.False(vm.IsLocked);
        Assert.Null(vm.LockedIndex);
    }

    [Fact]
    public void LockedIndex_FeedsOrderingShuffle_PinningOnlyTheLockedRows()
    {
        var rows = new[]
        {
            new MergeClipViewModel(Clip(@"C:\a.webm")),
            new MergeClipViewModel(Clip(@"C:\b.webm")),
            new MergeClipViewModel(Clip(@"C:\c.webm")),
        };
        rows[2].SetLock(locked: true, index: 0); // c is pinned first; a and b are free

        var shuffled = Ordering.Shuffle(rows, r => r.LockedIndex, seed: 1234);

        Assert.Same(rows[2], shuffled[0]);
        Assert.Equal(
            new[] { "a.webm", "b.webm" },
            shuffled.Skip(1).Select(r => r.FileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Constructor_RejectsANullClip()
        => Assert.Throws<ArgumentNullException>(() => new MergeClipViewModel(null!));

    [Fact]
    public void ApplyTarget_RejectsANullTarget()
    {
        var vm = new MergeClipViewModel(Clip(@"C:\a.webm"));

        Assert.Throws<ArgumentNullException>(() => vm.ApplyTarget(null!));
    }
}
