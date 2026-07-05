# M4 — Processing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add trim/clip, subtitle embedding, and metadata + thumbnail embedding to the YouTube Downloader, all via yt-dlp flags through the pure `OptionSetBuilder`, flowing per-job through the M3 queue.

**Architecture:** A `ProcessingOptions` sub-record (with `TrimRange`) is added to `DownloadConfig`; the pure `OptionSetBuilder` gains an `ApplyProcessing` step that emits the flags; a pure `TrimParsing` helper turns user timestamp text into a `TrimRange`; the ViewModel/page expose the processing controls. Design: [`docs/superpowers/specs/2026-07-05-m4-processing-design.md`](../specs/2026-07-05-m4-processing-design.md).

**Tech Stack:** C# / .NET 9 · WPF + WPF-UI 4.3.0 · CommunityToolkit.Mvvm · YoutubeDLSharp 1.2.0 · xUnit.

## Global Constraints

- `FFMedia.Core` references **NO UI framework**; this milestone adds nothing to Core.
- All processing code lives in `FFMedia.Tools.YouTubeDownloader`. **Nullable enabled**; **one public type per file**; ViewModels use CommunityToolkit.Mvvm source generators, no logic in code-behind.
- Processing is applied **per download** through `DownloadConfig.Processing` (flows through the M3 queue unchanged).
- **Subtitles are video-only**: subtitle flags are emitted only when `Kind == Video`.
- **Defaults:** `EmbedMetadata` + `EmbedThumbnail` ON; subtitles OFF; no trim; subtitle language `"en"` (`ProcessingOptions.Default`).
- **Trim precision is per-download:** `PreciseCut` adds `--force-keyframes-at-cuts`.
- Unit tests use **plain xUnit `Assert`**, **no network**.
- **Verified YoutubeDLSharp 1.2.0 facts** (reflected + rendered against the installed package):
  - Typed `OptionSet` properties: `EmbedMetadata`/`EmbedThumbnail`/`WriteSubs`/`WriteAutoSubs`/`EmbedSubs` (bool), `SubLangs` (string), `ForceKeyframesAtCuts` (bool), `DownloadSections` (`MultiValue<string>`).
  - `options.DownloadSections = "*0-5";` compiles (implicit `string`→`MultiValue`) and renders in `OptionSet.ToString()` as `--download-sections "*0-5"`. Assert trim via `options.ToString()` (contains / does-not-contain `download-sections`); assert everything else via the typed properties.
  - Verified render of a fully-processed set: `--download-sections "*0-5" --embed-subs --embed-thumbnail --embed-metadata --force-keyframes-at-cuts --write-subs --write-auto-subs --sub-langs "en" …`.
- Existing shapes this builds on: `record DownloadConfig(OutputKind, VideoContainer, VideoResolution, AudioFormat, AudioBitrate)` with static `Default`; `OptionSetBuilder.Build(DownloadConfig, string outputFolder)` (video/audio branches); `DownloaderViewModel` (M3 add-to-queue, selections + `Jobs`).
- Whole plan on ONE branch (`feat/m4-processing`, already created off `feat/m3-queue`), delivered as one **PR for review** (CLAUDE.md Rule 3, base = `feat/m3-queue`). Keep **`SDD.md`** current (Rule 1) + progress-log (Rule 2) — Task 6.

---

## File Structure

```
src/FFMedia.Tools.YouTubeDownloader/
├─ Models/TrimRange.cs                     (Task 1) record
├─ Models/ProcessingOptions.cs             (Task 1) record + Default
├─ Models/DownloadConfig.cs                (Task 1, modified) add Processing
├─ Services/OptionSetBuilder.cs            (Task 2, modified) ApplyProcessing
├─ Services/TrimParsing.cs                 (Task 3) pure timestamp parser
├─ ViewModels/DownloaderViewModel.cs       (Task 3, modified) processing selections + assembly
└─ Views/DownloaderPage.xaml               (Task 4, modified) Processing section
src/FFMedia.Tests/
├─ YouTubeDownloader/DownloadConfigTests.cs        (Task 1, modified)
├─ YouTubeDownloader/OptionSetBuilderTests.cs      (Task 1 helper fix; Task 2 processing tests)
├─ YouTubeDownloader/TrimParsingTests.cs           (Task 3)
├─ YouTubeDownloader/DownloaderViewModelTests.cs   (Task 3, modified)
└─ Integration/YtDlpIntegrationTests.cs            (Task 5, modified)
```

---

### Task 1: Model — `TrimRange` + `ProcessingOptions`, extend `DownloadConfig`

**Files:**
- Create: `src/FFMedia.Tools.YouTubeDownloader/Models/TrimRange.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Models/ProcessingOptions.cs`
- Modify: `src/FFMedia.Tools.YouTubeDownloader/Models/DownloadConfig.cs`
- Modify (keep compiling): `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`, `src/FFMedia.Tests/YouTubeDownloader/OptionSetBuilderTests.cs`, `src/FFMedia.Tests/Integration/YtDlpIntegrationTests.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/DownloadConfigTests.cs`

**Interfaces:**
- Consumes: existing `DownloadConfig` fields.
- Produces: `record TrimRange(TimeSpan Start, TimeSpan End)`; `record ProcessingOptions(TrimRange? Trim, bool PreciseCut, bool EmbedSubtitles, string SubtitleLanguage, bool EmbedMetadata, bool EmbedThumbnail)` with static `Default` (no trim, subs off, `"en"`, metadata+thumbnail on); `DownloadConfig` gains a 6th positional field `ProcessingOptions Processing`, and `DownloadConfig.Default` supplies `ProcessingOptions.Default`.

- [ ] **Step 1: Write the failing test**

Add to `src/FFMedia.Tests/YouTubeDownloader/DownloadConfigTests.cs` (keep the existing tests) these facts, and add `using FFMedia.Tools.YouTubeDownloader.Models;` if not present:
```csharp
    [Fact]
    public void ProcessingDefault_EmbedsMetadataAndThumbnail_SubsOff_NoTrim()
    {
        var p = ProcessingOptions.Default;
        Assert.Null(p.Trim);
        Assert.False(p.EmbedSubtitles);
        Assert.False(p.PreciseCut);
        Assert.Equal("en", p.SubtitleLanguage);
        Assert.True(p.EmbedMetadata);
        Assert.True(p.EmbedThumbnail);
    }

    [Fact]
    public void Default_UsesProcessingDefault()
    {
        Assert.Same(ProcessingOptions.Default, DownloadConfig.Default.Processing);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloadConfigTests`
Expected: FAIL — `ProcessingOptions`/`DownloadConfig.Processing` do not exist (compile error).

- [ ] **Step 3: Create the records**

Create `src/FFMedia.Tools.YouTubeDownloader/Models/TrimRange.cs`:
```csharp
namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>A clip range within a media item.</summary>
public sealed record TrimRange(TimeSpan Start, TimeSpan End);
```

Create `src/FFMedia.Tools.YouTubeDownloader/Models/ProcessingOptions.cs`:
```csharp
namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>Post-processing applied to a download: optional trim plus subtitle/metadata/thumbnail embedding.</summary>
public sealed record ProcessingOptions(
    TrimRange? Trim,
    bool PreciseCut,
    bool EmbedSubtitles,
    string SubtitleLanguage,
    bool EmbedMetadata,
    bool EmbedThumbnail)
{
    /// <summary>Metadata + thumbnail embedded; subtitles and trim off; default subtitle language "en".</summary>
    public static ProcessingOptions Default { get; } =
        new(Trim: null, PreciseCut: false, EmbedSubtitles: false, SubtitleLanguage: "en",
            EmbedMetadata: true, EmbedThumbnail: true);
}
```

- [ ] **Step 4: Extend `DownloadConfig`**

Replace `src/FFMedia.Tools.YouTubeDownloader/Models/DownloadConfig.cs` with:
```csharp
namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>
/// The user's chosen output for a download. All fields are always present; the group
/// irrelevant to <see cref="Kind"/> (video vs audio) is simply unused when options are built.
/// </summary>
public sealed record DownloadConfig(
    OutputKind Kind,
    VideoContainer Container,
    VideoResolution Resolution,
    AudioFormat AudioFormat,
    AudioBitrate Bitrate,
    ProcessingOptions Processing)
{
    /// <summary>App default: 1080p MP4 video with metadata + thumbnail embedding.</summary>
    public static DownloadConfig Default { get; } =
        new(OutputKind.Video, VideoContainer.Mp4, VideoResolution.P1080, AudioFormat.Mp3, AudioBitrate.Best,
            ProcessingOptions.Default);
}
```

- [ ] **Step 5: Fix the positional construction sites so the solution compiles**

These construct `DownloadConfig` positionally and now need the 6th argument. Append `ProcessingOptions.Default` (the ViewModel's is temporary — Task 3 assembles the real value):

In `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`, the `new DownloadConfig(SelectedKind, SelectedContainer, SelectedResolution, SelectedAudioFormat, SelectedBitrate)` (in `AddToQueueAsync`) becomes:
```csharp
            var config = new DownloadConfig(
                SelectedKind, SelectedContainer, SelectedResolution, SelectedAudioFormat, SelectedBitrate,
                ProcessingOptions.Default);
```

In `src/FFMedia.Tests/YouTubeDownloader/OptionSetBuilderTests.cs`, the two helper builders become:
```csharp
    private static DownloadConfig Video(VideoContainer c, VideoResolution r) =>
        new(OutputKind.Video, c, r, AudioFormat.Mp3, AudioBitrate.Best, ProcessingOptions.Default);

    private static DownloadConfig Audio(AudioFormat f, AudioBitrate b) =>
        new(OutputKind.Audio, VideoContainer.Mp4, VideoResolution.Best, f, b, ProcessingOptions.Default);
```

In `src/FFMedia.Tests/Integration/YtDlpIntegrationTests.cs`, the `new DownloadConfig(OutputKind.Audio, VideoContainer.Mp4, VideoResolution.Best, AudioFormat.Mp3, AudioBitrate.K192)` becomes:
```csharp
            var config = new DownloadConfig(
                OutputKind.Audio, VideoContainer.Mp4, VideoResolution.Best, AudioFormat.Mp3, AudioBitrate.K192,
                ProcessingOptions.Default);
```

- [ ] **Step 6: Run tests + full suite**

Run:
```bash
dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloadConfigTests
dotnet build FFMedia.sln
dotnet test FFMedia.sln --filter "Category!=Integration"
```
Expected: new facts pass; solution builds; all unit tests green (existing `OptionSetBuilderTests` still pass — appending `ProcessingOptions.Default` adds `--embed-metadata`/`--embed-thumbnail`, which the existing assertions don't check).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(youtube): add ProcessingOptions/TrimRange to DownloadConfig"
```

---

### Task 2: `OptionSetBuilder.ApplyProcessing`

**Files:**
- Modify: `src/FFMedia.Tools.YouTubeDownloader/Services/OptionSetBuilder.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/OptionSetBuilderTests.cs`

**Interfaces:**
- Consumes: `DownloadConfig`/`ProcessingOptions`/`TrimRange` (Task 1); `OptionSet` typed props.
- Produces: `Build` now applies processing to the built `OptionSet` via a private `ApplyProcessing(OptionSet, DownloadConfig)`.

- [ ] **Step 1: Write the failing tests**

Add to `src/FFMedia.Tests/YouTubeDownloader/OptionSetBuilderTests.cs` (the `Video`/`Audio` helpers from Task 1 produce `ProcessingOptions.Default` configs). Add a processing helper + tests:
```csharp
    private static DownloadConfig WithProcessing(ProcessingOptions p, OutputKind kind = OutputKind.Video) =>
        DownloadConfig.Default with { Kind = kind, Processing = p };

    [Fact]
    public void Default_EmbedsMetadataAndThumbnail_NoTrim_NoSubs()
    {
        var o = OptionSetBuilder.Build(DownloadConfig.Default, @"C:\out");
        Assert.True(o.EmbedMetadata);
        Assert.True(o.EmbedThumbnail);
        Assert.False(o.WriteSubs);
        Assert.DoesNotContain("download-sections", o.ToString());
    }

    [Fact]
    public void Trim_Fast_SetsDownloadSections_NoForceKeyframes()
    {
        var p = ProcessingOptions.Default with { Trim = new TrimRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5)) };
        var o = OptionSetBuilder.Build(WithProcessing(p), @"C:\out");
        Assert.Contains("*0-5", o.ToString());
        Assert.False(o.ForceKeyframesAtCuts);
    }

    [Fact]
    public void Trim_Precise_SetsForceKeyframes()
    {
        var p = ProcessingOptions.Default with
        {
            Trim = new TrimRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)),
            PreciseCut = true,
        };
        var o = OptionSetBuilder.Build(WithProcessing(p), @"C:\out");
        Assert.Contains("*10-20", o.ToString());
        Assert.True(o.ForceKeyframesAtCuts);
    }

    [Fact]
    public void Subtitles_Video_SetsWriteAndEmbedAndLangs()
    {
        var p = ProcessingOptions.Default with { EmbedSubtitles = true, SubtitleLanguage = "es" };
        var o = OptionSetBuilder.Build(WithProcessing(p, OutputKind.Video), @"C:\out");
        Assert.True(o.WriteSubs);
        Assert.True(o.WriteAutoSubs);
        Assert.True(o.EmbedSubs);
        Assert.Equal("es", o.SubLangs);
    }

    [Fact]
    public void Subtitles_Audio_AreIgnored()
    {
        var p = ProcessingOptions.Default with { EmbedSubtitles = true, SubtitleLanguage = "en" };
        var o = OptionSetBuilder.Build(WithProcessing(p, OutputKind.Audio), @"C:\out");
        Assert.False(o.WriteSubs);
        Assert.False(o.EmbedSubs);
    }

    [Fact]
    public void Embed_FlagsOff_DisableMetadataAndThumbnail()
    {
        var p = ProcessingOptions.Default with { EmbedMetadata = false, EmbedThumbnail = false };
        var o = OptionSetBuilder.Build(WithProcessing(p), @"C:\out");
        Assert.False(o.EmbedMetadata);
        Assert.False(o.EmbedThumbnail);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~OptionSetBuilderTests`
Expected: FAIL — `Build` does not yet apply processing (e.g., `Trim_Fast_...` finds no `download-sections`; `Embed_FlagsOff_...` sees metadata/thumbnail still on because they aren't wired).

- [ ] **Step 3: Implement `ApplyProcessing`**

In `src/FFMedia.Tools.YouTubeDownloader/Services/OptionSetBuilder.cs`, change `Build` to apply processing, and add the helpers. Replace the `Build` method body with:
```csharp
    public static OptionSet Build(DownloadConfig config, string outputFolder)
    {
        var output = Path.Combine(outputFolder, "%(title)s.%(ext)s");
        var options = config.Kind == OutputKind.Audio
            ? BuildAudio(config, output)
            : BuildVideo(config, output);
        ApplyProcessing(options, config);
        return options;
    }
```
Then add these private methods (anywhere among the other privates in the class):
```csharp
    private static void ApplyProcessing(OptionSet options, DownloadConfig config)
    {
        var p = config.Processing;

        if (p.Trim is { } trim)
        {
            options.DownloadSections = $"*{FormatSeconds(trim.Start)}-{FormatSeconds(trim.End)}";
            if (p.PreciseCut) options.ForceKeyframesAtCuts = true;
        }

        // Subtitles only apply to video output.
        if (config.Kind == OutputKind.Video && p.EmbedSubtitles)
        {
            options.WriteSubs = true;
            options.WriteAutoSubs = true;
            options.EmbedSubs = true;
            options.SubLangs = p.SubtitleLanguage;
        }

        options.EmbedMetadata = p.EmbedMetadata;
        options.EmbedThumbnail = p.EmbedThumbnail;
    }

    private static string FormatSeconds(TimeSpan t) =>
        t.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
```

- [ ] **Step 4: Run to green + full suite**

Run:
```bash
dotnet test src/FFMedia.Tests --filter FullyQualifiedName~OptionSetBuilderTests
dotnet build FFMedia.sln
dotnet test FFMedia.sln --filter "Category!=Integration"
```
Expected: all processing tests pass; existing builder tests still pass; full suite green.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(youtube): emit trim/subtitle/metadata/thumbnail flags in OptionSetBuilder"
```

---

### Task 3: `TrimParsing` helper + ViewModel processing selections

**Files:**
- Create: `src/FFMedia.Tools.YouTubeDownloader/Services/TrimParsing.cs`
- Modify: `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/TrimParsingTests.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs`

**Interfaces:**
- Consumes: `TrimRange`/`ProcessingOptions` (Task 1), `DownloadConfig` (Task 1).
- Produces: `static class TrimParsing { TimeSpan? TryParse(string?); TrimRange? ParseRange(string? start, string? end); }`; ViewModel gains observable `TrimStart`/`TrimEnd`/`PreciseCut`/`EmbedSubtitles`/`SubtitleLanguage`/`EmbedMetadata`/`EmbedThumbnail`/`TrimHint`, and assembles `ProcessingOptions` into the `DownloadConfig` it enqueues.

- [ ] **Step 1: Write the failing `TrimParsing` tests**

Create `src/FFMedia.Tests/YouTubeDownloader/TrimParsingTests.cs`:
```csharp
using System;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class TrimParsingTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("01:02:03", 3723)]
    [InlineData("0", 0)]
    public void TryParse_ParsesSecondsAndClockFormats(string text, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TrimParsing.TryParse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1:70")]      // minutes/seconds field out of range
    [InlineData("-5")]
    public void TryParse_BlankOrInvalid_ReturnsNull(string? text)
    {
        Assert.Null(TrimParsing.TryParse(text));
    }

    [Fact]
    public void ParseRange_ValidPair_ReturnsRange()
    {
        var r = TrimParsing.ParseRange("0:05", "0:10");
        Assert.NotNull(r);
        Assert.Equal(TimeSpan.FromSeconds(5), r!.Start);
        Assert.Equal(TimeSpan.FromSeconds(10), r.End);
    }

    [Theory]
    [InlineData("0:10", "0:05")]   // end <= start
    [InlineData("0:05", "0:05")]   // end == start
    [InlineData("", "0:10")]        // one side blank
    [InlineData("x", "0:10")]       // one side invalid
    public void ParseRange_InvalidOrIncomplete_ReturnsNull(string start, string end)
    {
        Assert.Null(TrimParsing.ParseRange(start, end));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~TrimParsingTests`
Expected: FAIL — `TrimParsing` does not exist.

- [ ] **Step 3: Implement `TrimParsing`**

Create `src/FFMedia.Tools.YouTubeDownloader/Services/TrimParsing.cs`:
```csharp
using System.Globalization;
using System.Linq;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Parses user-entered trim timestamps ("HH:MM:SS", "MM:SS", or plain seconds).</summary>
public static class TrimParsing
{
    /// <summary>Parses a timestamp; returns null when blank or unparseable.</summary>
    public static TimeSpan? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);

        var parts = text.Split(':');
        if (parts.Length is 2 or 3 &&
            parts.All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            var n = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            var (h, m, s) = parts.Length == 3 ? (n[0], n[1], n[2]) : (0, n[0], n[1]);
            if (h >= 0 && m is >= 0 and < 60 && s is >= 0 and < 60)
                return new TimeSpan(h, m, s);
        }

        return null;
    }

    /// <summary>A <see cref="TrimRange"/> only when both parse and End &gt; Start; otherwise null.</summary>
    public static TrimRange? ParseRange(string? start, string? end)
    {
        return TryParse(start) is { } s && TryParse(end) is { } e && e > s
            ? new TrimRange(s, e)
            : null;
    }
}
```

- [ ] **Step 4: Run to green**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~TrimParsingTests`
Expected: PASS (all cases).

- [ ] **Step 5: Write the failing ViewModel tests**

Add to `src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs` (the `FakeManager`/`FakePlaylistProbe`/`Vm` helpers already exist from M3):
```csharp
    [Fact]
    public async Task AddToQueue_AssemblesProcessingOptions_FromSelections()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        vm.Url = "https://u";
        vm.TrimStart = "0:05";
        vm.TrimEnd = "0:10";
        vm.PreciseCut = true;
        vm.EmbedSubtitles = true;
        vm.SubtitleLanguage = "es";
        vm.EmbedMetadata = false;
        vm.EmbedThumbnail = false;

        await vm.AddToQueueCommand.ExecuteAsync(null);

        var p = Assert.Single(mgr.Enqueued).Config.Processing;
        Assert.Equal(new TrimRange(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), p.Trim);
        Assert.True(p.PreciseCut);
        Assert.True(p.EmbedSubtitles);
        Assert.Equal("es", p.SubtitleLanguage);
        Assert.False(p.EmbedMetadata);
        Assert.False(p.EmbedThumbnail);
    }

    [Fact]
    public async Task AddToQueue_BlankTrim_ProducesNoTrim()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        vm.Url = "https://u";
        await vm.AddToQueueCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(mgr.Enqueued).Config.Processing.Trim);
    }

    [Fact]
    public void InvalidTrim_SetsHint_BlankClearsIt()
    {
        var vm = Vm(new FakePlaylistProbe(), new FakeManager());
        vm.TrimStart = "abc";
        vm.TrimEnd = "0:10";
        Assert.NotEqual(string.Empty, vm.TrimHint);
        vm.TrimStart = string.Empty;
        vm.TrimEnd = string.Empty;
        Assert.Equal(string.Empty, vm.TrimHint);
    }

    [Fact]
    public void EmbedDefaults_MetadataAndThumbnailOn()
    {
        var vm = Vm(new FakePlaylistProbe(), new FakeManager());
        Assert.True(vm.EmbedMetadata);
        Assert.True(vm.EmbedThumbnail);
    }
```
Add `using System;` at the top of the test file if not already present (needed for `TimeSpan`).

- [ ] **Step 6: Run to verify the VM tests fail**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloaderViewModelTests`
Expected: FAIL — `TrimStart`/`PreciseCut`/`EmbedMetadata`/`TrimHint`/etc. don't exist (compile error).

- [ ] **Step 7: Add the processing selections + assembly to the ViewModel**

In `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`:

(a) Add these observable properties alongside the existing selection fields:
```csharp
    [ObservableProperty] private string _trimStart = string.Empty;
    [ObservableProperty] private string _trimEnd = string.Empty;
    [ObservableProperty] private bool _preciseCut;
    [ObservableProperty] private bool _embedSubtitles;
    [ObservableProperty] private string _subtitleLanguage = "en";
    [ObservableProperty] private bool _embedMetadata = true;
    [ObservableProperty] private bool _embedThumbnail = true;
    [ObservableProperty] private string _trimHint = string.Empty;

    partial void OnTrimStartChanged(string value) => UpdateTrimHint();
    partial void OnTrimEndChanged(string value) => UpdateTrimHint();

    private void UpdateTrimHint()
    {
        var requested = !(string.IsNullOrWhiteSpace(TrimStart) && string.IsNullOrWhiteSpace(TrimEnd));
        TrimHint = requested && TrimParsing.ParseRange(TrimStart, TrimEnd) is null
            ? "Enter valid Start/End (HH:MM:SS or seconds), End after Start."
            : string.Empty;
    }

    private ProcessingOptions BuildProcessing() => new(
        TrimParsing.ParseRange(TrimStart, TrimEnd),
        PreciseCut, EmbedSubtitles, SubtitleLanguage, EmbedMetadata, EmbedThumbnail);
```

(b) In `AddToQueueAsync`, replace the temporary `new DownloadConfig(..., ProcessingOptions.Default)` (from Task 1 Step 5) with the assembled processing:
```csharp
            var config = new DownloadConfig(
                SelectedKind, SelectedContainer, SelectedResolution, SelectedAudioFormat, SelectedBitrate,
                BuildProcessing());
```
(`FFMedia.Tools.YouTubeDownloader.Services` is already imported in the ViewModel from M3.)

- [ ] **Step 8: Run to green + full suite**

Run:
```bash
dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloaderViewModelTests
dotnet build FFMedia.sln
dotnet test FFMedia.sln --filter "Category!=Integration"
```
Expected: VM tests pass; solution builds; all unit tests green.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(youtube): add trim parsing and processing selections to DownloaderViewModel"
```

---

### Task 4: `DownloaderPage` — Processing section

**Files:**
- Modify: `src/FFMedia.Tools.YouTubeDownloader/Views/DownloaderPage.xaml`

**Interfaces:**
- Consumes: the Task-3 ViewModel — `TrimStart`, `TrimEnd`, `PreciseCut`, `EmbedSubtitles`, `SubtitleLanguage`, `EmbedMetadata`, `EmbedThumbnail`, `TrimHint`.
- Produces: XAML only — no new tests (verified by build + the Task-5 app run).

- [ ] **Step 1: Add a "Processing" block to the page**

In `src/FFMedia.Tools.YouTubeDownloader/Views/DownloaderPage.xaml`, insert this block **between** the audio-options `StackPanel` (the one bound to `IsAudio`) and the `StackPanel` that holds the **Add to queue** button:
```xml
        <StackPanel Margin="0,16,0,0">
            <TextBlock Text="Processing" FontWeight="SemiBold" />
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                <TextBlock Text="Trim start:" VerticalAlignment="Center" Margin="0,0,8,0" />
                <ui:TextBox Text="{Binding TrimStart, UpdateSourceTrigger=PropertyChanged}" Width="90" PlaceholderText="0:00" />
                <TextBlock Text="end:" VerticalAlignment="Center" Margin="16,0,8,0" />
                <ui:TextBox Text="{Binding TrimEnd, UpdateSourceTrigger=PropertyChanged}" Width="90" PlaceholderText="1:30" />
                <CheckBox Content="Precise cut" IsChecked="{Binding PreciseCut}" Margin="16,0,0,0" VerticalAlignment="Center" />
            </StackPanel>
            <TextBlock Text="{Binding TrimHint}" Foreground="{DynamicResource SystemFillColorCautionBrush}"
                       Margin="0,4,0,0" TextWrapping="Wrap" />
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                <CheckBox Content="Embed subtitles" IsChecked="{Binding EmbedSubtitles}" VerticalAlignment="Center" />
                <TextBlock Text="Language:" VerticalAlignment="Center" Margin="16,0,8,0" />
                <ui:TextBox Text="{Binding SubtitleLanguage}" Width="60" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                <CheckBox Content="Embed metadata" IsChecked="{Binding EmbedMetadata}" Margin="0,0,16,0" />
                <CheckBox Content="Embed thumbnail" IsChecked="{Binding EmbedThumbnail}" />
            </StackPanel>
        </StackPanel>
```

- [ ] **Step 2: Build**

Run: `dotnet build FFMedia.sln`
Expected: builds. Every new `{Binding …}` names a real ViewModel member (`TrimStart`, `TrimEnd`, `PreciseCut`, `TrimHint`, `EmbedSubtitles`, `SubtitleLanguage`, `EmbedMetadata`, `EmbedThumbnail`).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(youtube): add Processing section (trim/subtitles/embed) to DownloaderPage"
```

---

### Task 5: Integration test + run verification

**Files:**
- Modify: `src/FFMedia.Tests/Integration/YtDlpIntegrationTests.cs`

**Interfaces:**
- Consumes: `YtDlpDownloadService`/`YoutubeDlFactory` (M1), `DownloadConfig`/`ProcessingOptions`/`TrimRange` (Task 1).
- Produces: a trait-gated test proving a trimmed + metadata/thumbnail-embedding download succeeds and produces a file.

- [ ] **Step 1: Add the trait-gated processing test**

Add this test to the existing `YtDlpIntegrationTests` class (it already has `RealBinaries()`, `TestUrl`, and `using` directives for Models/Services):
```csharp
    [Fact]
    public async Task Download_TrimmedWithEmbeds_ProducesFile()
    {
        var binaries = RealBinaries();
        Assert.True(binaries.Exists(ExternalBinary.Ffmpeg), "Run build/fetch-binaries.ps1 first.");
        var outDir = Path.Combine(Path.GetTempPath(), "ffmedia-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var svc = new YtDlpDownloadService(new YoutubeDlFactory(binaries));
            var processing = new ProcessingOptions(
                Trim: new TrimRange(TimeSpan.Zero, TimeSpan.FromSeconds(5)),
                PreciseCut: false, EmbedSubtitles: false, SubtitleLanguage: "en",
                EmbedMetadata: true, EmbedThumbnail: true);
            var config = DownloadConfig.Default with { Processing = processing };
            var progress = new Progress<DownloadUpdate>(_ => { });

            var result = await svc.DownloadAsync(new DownloadRequest(TestUrl, outDir, config), progress, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error);
            Assert.NotEmpty(Directory.GetFiles(outDir, "*.mp4"));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the unit suite (no network)**

Run: `dotnet build FFMedia.sln && dotnet test FFMedia.sln --filter "Category!=Integration"`
Expected: builds; all unit tests green; integration excluded.

- [ ] **Step 3: Run integration locally (network; after `build/fetch-binaries.ps1`)**

Run: `dotnet test FFMedia.sln --filter "Category=Integration"`
Expected: PASS — the trimmed + embedding download produces an `.mp4` (plus the pre-existing probe/MP4/MP3/queue tests).

- [ ] **Step 4: Run the app and sanity-check**

Run: `MSYS_NO_PATHCONV=1 timeout 25 dotnet run --project src/FFMedia.App; echo "exit=$?"`
Expected: launches; no `Fatal`/`XamlParse` in `%AppData%\FFMedia\logs`. Confirm the downloader page now shows the **Processing** section (trim start/end, precise cut, embed subtitles + language, embed metadata/thumbnail). (Full click-through — set a trim and toggles, add a URL, confirm the trimmed file — is the human acceptance check.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "test(youtube): add trait-gated trimmed+embedding integration test"
```

---

### Task 6: SDD + docs

**Files:**
- Modify: `SDD.md` (§7.3 processing rows; §8 note; §17 M4 row; header → v0.6; Changelog)
- Modify: `CLAUDE.md` (Progress Log)

- [ ] **Step 1: Expand `SDD.md` §7.3** — add rows for the M4 processing flags actually emitted by `ApplyProcessing`:
```markdown
| Trim (fast) | `--download-sections "*<start>-<end>"` (seconds; keyframe cut, no re-encode) |
| Trim (precise) | as above + `--force-keyframes-at-cuts` (exact, re-encodes around the cut) |
| Subtitles (video only) | `--write-subs --write-auto-subs --embed-subs --sub-langs <lang>` |
| Embed metadata | `--embed-metadata` |
| Embed thumbnail | `--embed-thumbnail` (mp4/mkv/mp3/m4a; yt-dlp warns and proceeds for webm/opus) |
```
Add a note: M4 processing is applied per-download via `DownloadConfig.Processing` through `OptionSetBuilder.ApplyProcessing`; subtitles are emitted only for video output.

- [ ] **Step 2: Update `SDD.md` §8** — note that M4 trims via yt-dlp `--download-sections` (the FFMpegCore trim path in `FFMedia.Media` stays reserved for future tools).

- [ ] **Step 3: Update `SDD.md` §17 + header + Changelog** — annotate the M4 row "✅ delivered (branch `feat/m4-processing`)"; bump header `Version:` to `0.6`, `Last updated:` to the date; add a Changelog row summarizing trim/subtitles/metadata+thumbnail via `ProcessingOptions`/`ApplyProcessing`.

- [ ] **Step 4: Append a `CLAUDE.md` progress-log entry** (newest first) describing the processing options (trim w/ precise toggle, embed subtitles manual+auto, metadata + thumbnail), the pure builder/parse tests, and Next = M5 (settings, presets, history, notifications, theming).

- [ ] **Step 5: Build and commit**

```bash
dotnet build FFMedia.sln
git add SDD.md CLAUDE.md
git commit -m "docs: sync SDD to v0.6 and log M4 progress"
```

---

## Definition of Done (M4)

- `dotnet build FFMedia.sln` clean; `dotnet test --filter "Category!=Integration"` all green.
- App: set a trim (fast or precise), toggle embed-subtitles + language, and metadata/thumbnail checkboxes; the download reflects those options.
- `OptionSetBuilder.ApplyProcessing` and `TrimParsing` are pure and covered by a full test matrix; `FFMedia.Core` stays UI-free.
- Trait-gated integration (trim + embed) passes locally; CI excludes it and stays green.
- SDD updated to v0.6 (with §7.3 processing flags); CLAUDE.md progress logged.
- Delivered as a single PR (`feat/m4-processing` → `feat/m3-queue`).
