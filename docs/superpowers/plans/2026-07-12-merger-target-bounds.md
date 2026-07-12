# TargetBounds — Source-Bounded Output Options Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The merger's output-target override UI must offer only values at or below what the source clips actually contain, so a user cannot select a setting that is provably pointless (60 fps from 30 fps clips, 4K from 1080p, 5.1 from stereo, `1920 × 102`).

**Architecture:** A new pure `TargetBounds` record, built **from `MergeTargetDerivation`'s own maxima**, turns each ceiling into a *list of allowed values*. The derived target is always the **first entry of each list**, so the offered options and the derived target cannot drift. `MergeTarget.ClampTo(bounds)` snaps an out-of-range override down to the largest still-allowed value. The ViewModel exposes the four lists and re-clamps in the existing `Recompute()`; the page swaps four inputs for `ComboBox`es.

**Tech Stack:** C# / .NET 9, WPF + WPF-UI 4.3, CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), xUnit.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-12-merger-target-bounds-design.md`.
- **The keystone invariant:** `TargetBounds` is built from the derivation's maxima; **the derived target is always the first entry of each list**. Never let the UI compute its own ceiling — that is exactly the drift `ConformanceCheck` exists to prevent.
- **Snap-down rule (one rule, every field):** take the largest allowed value **≤** the current one; if none qualifies, take the smallest.
- **Codec × container is NOT restricted.** All 8 combinations mux cleanly in the bundled ffmpeg 8.1 (verified). MP4 + Opus gets a **warning**, never a block.
- **Already guarded — do not "fix" again:** `TargetCrf` ignores values outside 0–51; `TargetWidth`/`TargetHeight` round even via `ToEven`. Leave both in place.
- **Zero-warning bar:** `dotnet build -c Release` must report **0 warnings / 0 errors**.
- **Test gate (every task):** `dotnet test FFMedia.sln -c Release --filter "Category!=Integration"`. Baseline entering this plan: **606 passing**.
- **Branch:** `feat/m7-target-bounds` off `main`. Deliver via PR; do not merge (CLAUDE.md Rule 3).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/FFMedia.Tools.VideoMerger/Models/Resolution.cs` | **Create.** Pure `record Resolution(int Width, int Height)` + `ToString()` for the ComboBox. |
| `src/FFMedia.Tools.VideoMerger/Models/TargetBounds.cs` | **Create.** Pure record holding the four allowed-value lists + `From(IReadOnlyList<MediaInfo>)`. |
| `src/FFMedia.Tools.VideoMerger/Services/MergeTargetDerivation.cs` | **Modify.** Expose `StandardRates` so `TargetBounds` consumes the *same* list. |
| `src/FFMedia.Tools.VideoMerger/Models/MergeTarget.cs` | **Modify.** Add pure `ClampTo(TargetBounds)`. |
| `src/FFMedia.Tools.VideoMerger/ViewModels/MergerViewModel.cs` | **Modify.** Expose `Bounds` + `SelectedResolution`; clamp in `Recompute()`; add `ShowOpusInMp4Warning`. |
| `src/FFMedia.Tools.VideoMerger/Views/MergerPage.xaml` | **Modify.** Four inputs → `ComboBox`es; add the MP4+Opus note. |
| `src/FFMedia.Tests/VideoMerger/TargetBoundsTests.cs` | **Create.** Pure tests for `From` + the ladders. |
| `src/FFMedia.Tests/VideoMerger/MergeTargetClampTests.cs` | **Create.** Pure tests for the snap-down rule. |
| `src/FFMedia.Tests/VideoMerger/MergerViewModelTests.cs` | **Modify.** Lists recompute; overrides survive or snap; Opus warning. |
| `src/FFMedia.Tests/Integration/MergeIntegrationTests.cs` | **Modify.** Merge 1080p sources → 720p target, probe output dimensions. |

---

### Task 1: `Resolution` + expose `StandardRates`

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Models/Resolution.cs`
- Modify: `src/FFMedia.Tools.VideoMerger/Services/MergeTargetDerivation.cs:16-20`
- Test: `src/FFMedia.Tests/VideoMerger/TargetBoundsTests.cs`

**Interfaces:**
- Produces: `record Resolution(int Width, int Height)`; `MergeTargetDerivation.StandardRates` as `public static IReadOnlyList<FrameRate>`.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/TargetBoundsTests.cs`:

```csharp
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class TargetBoundsTests
{
    [Fact]
    public void Resolution_RendersAsWidthByHeight_ForTheComboBox()
    {
        Assert.Equal("1920 × 1080", new Resolution(1920, 1080).ToString());
    }

    [Fact]
    public void StandardRates_IsExposed_SoTargetBoundsAndDerivationCannotDrift()
    {
        // TargetBounds must offer the SAME rates the derivation snaps to. A second, copied array
        // would let the offered rate and the derived rate disagree — the drift this design exists
        // to prevent.
        Assert.Contains(new FrameRate(30, 1), MergeTargetDerivation.StandardRates);
        Assert.Contains(new FrameRate(60, 1), MergeTargetDerivation.StandardRates);
        Assert.Equal(8, MergeTargetDerivation.StandardRates.Count);
    }
}
```

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test FFMedia.sln -c Release --filter "FullyQualifiedName~TargetBoundsTests"`
Expected: FAIL — `Resolution` does not exist; `StandardRates` is inaccessible (`private`).

- [ ] **Step 3: Create `Resolution`**

`src/FFMedia.Tools.VideoMerger/Models/Resolution.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>One selectable output resolution. Exists so the page can bind a single ComboBox to a
/// width/height PAIR: two independent text boxes let the user build 1920 × 102 — positive, even,
/// encodable, and absurd. <see cref="MergeTarget"/> keeps its flat Width/Height (the engine and
/// every existing test read them); this is the UI's unit of choice.</summary>
public sealed record Resolution(int Width, int Height)
{
    public long PixelCount => (long)Width * Height;

    /// <summary>What the ComboBox shows. The × is U+00D7, not the letter x.</summary>
    public override string ToString() => $"{Width} × {Height}";
}
```

- [ ] **Step 4: Expose `StandardRates`**

In `MergeTargetDerivation.cs`, change the field (keep the existing XML comment above it verbatim):

```csharp
    public static IReadOnlyList<FrameRate> StandardRates { get; } =
    [
        new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001),
        new(30, 1), new(50, 1), new(60000, 1001), new(60, 1),
    ];
```

`Snap` already does `foreach (var standard in StandardRates)` — that still compiles against `IReadOnlyList<FrameRate>`, no change needed there.

- [ ] **Step 5: Run tests**

Run: `dotnet test FFMedia.sln -c Release --filter "Category!=Integration"`
Expected: PASS — 608 passing (606 + 2).

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Models/Resolution.cs src/FFMedia.Tools.VideoMerger/Services/MergeTargetDerivation.cs src/FFMedia.Tests/VideoMerger/TargetBoundsTests.cs
git commit -m "feat(merger): add Resolution and expose the shared StandardRates"
```

---

### Task 2: `TargetBounds.From` — the allowed-value lists

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Models/TargetBounds.cs`
- Test: `src/FFMedia.Tests/VideoMerger/TargetBoundsTests.cs` (extend)

**Interfaces:**
- Consumes: `Resolution` (Task 1), `MergeTargetDerivation.StandardRates` (Task 1), `MediaInfo`/`VideoStreamInfo`/`AudioStreamInfo`/`FrameRate` (from `FFMedia.Media`).
- Produces: `TargetBounds` with `IReadOnlyList<Resolution> Resolutions`, `IReadOnlyList<FrameRate> FrameRates`, `IReadOnlyList<int> SampleRates`, `IReadOnlyList<int> ChannelCounts`; `static TargetBounds From(IReadOnlyList<MediaInfo> clips)`; `static TargetBounds Empty { get; }`.

- [ ] **Step 1: Write the failing tests**

Append to `TargetBoundsTests.cs` (inside the class):

```csharp
    private static MediaInfo Clip(
        int width = 1920, int height = 1080, int fpsNum = 30, int fpsDen = 1,
        int sampleRate = 48_000, int channels = 2)
        => new(TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(width, height, new FrameRate(fpsNum, fpsDen), "h264", "yuv420p", 0),
            new AudioStreamInfo("aac", sampleRate, channels));

    [Fact]
    public void From_PutsTheDerivedTargetFirstInEveryList()
    {
        // THE KEYSTONE. The derived target must be the head of each list, or the UI offers options
        // that disagree with what derivation picks — and the "you have overridden the derived
        // target" hint starts lying.
        var clips = new[] { Clip(1920, 1080, 30, 1, 48_000, 2), Clip(1280, 720, 24, 1, 44_100, 1) };
        var derived = MergeTargetDerivation.Derive(clips);
        var bounds = TargetBounds.From(clips);

        Assert.Equal(new Resolution(derived.Width, derived.Height), bounds.Resolutions[0]);
        Assert.Equal(derived.FrameRate, bounds.FrameRates[0]);
        Assert.Equal(derived.AudioSampleRate, bounds.SampleRates[0]);
        Assert.Equal(derived.AudioChannels, bounds.ChannelCounts[0]);
    }

    [Fact]
    public void From_NeverOffersAResolutionLargerThanTheSource()
    {
        var bounds = TargetBounds.From([Clip(1280, 720)]);

        Assert.All(bounds.Resolutions, r => Assert.True(r.PixelCount <= 1280L * 720));
    }

    [Fact]
    public void From_ResolutionLadderKeepsTheSourceAspectRatio_AndStaysEven()
    {
        var bounds = TargetBounds.From([Clip(1920, 1080)]); // 16:9

        Assert.All(bounds.Resolutions, r =>
        {
            Assert.Equal(0, r.Width % 2);
            Assert.Equal(0, r.Height % 2);
            // 16:9 within a pixel of rounding.
            Assert.InRange(r.Width / (double)r.Height, 16 / 9.0 - 0.02, 16 / 9.0 + 0.02);
        });
        Assert.Contains(new Resolution(1280, 720), bounds.Resolutions);
    }

    [Fact]
    public void From_ResolutionLadderHandlesAVerticalSource()
    {
        // A phone clip. The ladder steps DOWN the long edge; it must not silently rotate the video.
        var bounds = TargetBounds.From([Clip(1080, 1920)]);

        Assert.Equal(new Resolution(1080, 1920), bounds.Resolutions[0]);
        Assert.All(bounds.Resolutions, r => Assert.True(r.Height > r.Width, $"{r} is not portrait"));
    }

    [Fact]
    public void From_NeverOffersAFrameRateFasterThanTheFastestClip()
    {
        var bounds = TargetBounds.From([Clip(fpsNum: 30), Clip(fpsNum: 24)]);

        Assert.All(bounds.FrameRates, r => Assert.True(r.Value <= 30.0 + 0.001));
        Assert.DoesNotContain(new FrameRate(60, 1), bounds.FrameRates);
        Assert.Contains(new FrameRate(24, 1), bounds.FrameRates);
    }

    [Fact]
    public void From_OffersANonStandardSourceRate_RatherThanAnEmptyList()
    {
        // A 12 fps clip snaps to no standard rate, and every standard rate is FASTER than it. Filtering
        // "standard rates <= 12" alone would yield an EMPTY list and a ComboBox with nothing in it.
        var bounds = TargetBounds.From([Clip(fpsNum: 12)]);

        Assert.NotEmpty(bounds.FrameRates);
        Assert.Equal(new FrameRate(12, 1), bounds.FrameRates[0]);
    }

    [Fact]
    public void From_CapsSampleRateAndChannelsAtTheSource()
    {
        var bounds = TargetBounds.From([Clip(sampleRate: 44_100, channels: 1)]);

        Assert.Equal(44_100, bounds.SampleRates[0]);
        Assert.DoesNotContain(48_000, bounds.SampleRates);
        Assert.Equal(new[] { 1 }, bounds.ChannelCounts);
    }

    [Fact]
    public void From_OnClipsWithNoAudio_StillOffersTheDefaultAudioSpec()
    {
        // Derivation falls back to 48 kHz stereo for silent clips (anullsrc), so the bounds must
        // offer at least that — otherwise the ComboBox is empty and the page cannot bind.
        var silent = new MediaInfo(TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0), null);

        var bounds = TargetBounds.From([silent]);

        Assert.Equal(48_000, bounds.SampleRates[0]);
        Assert.Equal(2, bounds.ChannelCounts[0]);
    }

    [Fact]
    public void Empty_HasNoOptions_SoAPageWithNoClipsBindsToNothing()
    {
        Assert.Empty(TargetBounds.Empty.Resolutions);
        Assert.Empty(TargetBounds.Empty.FrameRates);
    }
```

- [ ] **Step 2: Run and verify they fail**

Run: `dotnet test FFMedia.sln -c Release --filter "FullyQualifiedName~TargetBoundsTests"`
Expected: FAIL — `TargetBounds` does not exist.

- [ ] **Step 3: Implement `TargetBounds`**

Create `src/FFMedia.Tools.VideoMerger/Models/TargetBounds.cs`:

```csharp
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Services;

namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>What the user is ALLOWED to choose, given the clips they added.
///
/// <para>Derivation takes the maximum across the clips (largest frame, fastest rate, highest sample
/// rate, most channels) — a deliberate "never degrade a source" rule. Anything ABOVE that ceiling is
/// not merely suboptimal, it is pointless: 60 fps from 30 fps clips duplicates every frame, 4K from
/// 1080p invents pixels, 5.1 from stereo adds four silent channels. Bigger file, longer encode, no
/// new information. So those values are not offered at all.</para>
///
/// <para><b>The derived target is always the first entry of each list.</b> These lists are built from
/// the derivation's own maxima rather than recomputed, so the options the UI offers and the target
/// derivation picks cannot drift — the discipline <c>ConformanceCheck</c> already enforces for the
/// fast path.</para></summary>
public sealed record TargetBounds(
    IReadOnlyList<Resolution> Resolutions,
    IReadOnlyList<FrameRate> FrameRates,
    IReadOnlyList<int> SampleRates,
    IReadOnlyList<int> ChannelCounts)
{
    /// <summary>Standard heights the ladder steps down through, tallest first.</summary>
    private static readonly int[] StandardHeights = [2160, 1440, 1080, 900, 720, 540, 480, 360, 240];

    private static readonly int[] StandardSampleRates = [96_000, 48_000, 44_100, 32_000, 22_050];

    private static readonly int[] StandardChannelCounts = [8, 6, 2, 1];

    /// <summary>No clips, so no source to bound against — the page disables the Output section.</summary>
    public static TargetBounds Empty { get; } = new([], [], [], []);

    /// <param name="clips">The probed clips. Must contain at least one with a video stream — the same
    /// precondition <see cref="MergeTargetDerivation.Derive"/> enforces.</param>
    public static TargetBounds From(IReadOnlyList<MediaInfo> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        if (clips.Count == 0 || clips.All(c => c.Video is null))
        {
            return Empty;
        }

        // Ask DERIVATION for the ceiling; never recompute it here. This is the whole invariant.
        var derived = MergeTargetDerivation.Derive(clips);

        return new TargetBounds(
            ResolutionLadder(derived.Width, derived.Height),
            RatesUpTo(derived.FrameRate),
            [.. Descending(StandardSampleRates, derived.AudioSampleRate)],
            [.. Descending(StandardChannelCounts, derived.AudioChannels)]);
    }

    /// <summary>The source resolution, then standard heights below it — each scaled to the SOURCE's
    /// aspect ratio and rounded even. Stepping the height and deriving the width (rather than
    /// offering a fixed 16:9 table) is what keeps a 9:16 phone clip portrait instead of silently
    /// rotating it.</summary>
    private static List<Resolution> ResolutionLadder(int width, int height)
    {
        var ladder = new List<Resolution> { new(width, height) };
        var aspect = width / (double)height;

        foreach (var stepHeight in StandardHeights)
        {
            if (stepHeight >= height)
            {
                continue; // never offer a step at or above the source: that is the upscaling we exist to forbid
            }

            var stepWidth = ToEven((int)Math.Round(stepHeight * aspect));
            var step = new Resolution(stepWidth, ToEven(stepHeight));
            if (stepWidth >= 2 && !ladder.Contains(step))
            {
                ladder.Add(step);
            }
        }

        return ladder;
    }

    /// <summary>Every standard rate at or below <paramref name="fastest"/>, fastest first — with
    /// <paramref name="fastest"/> itself at the head. A 12 fps source snaps to NO standard rate and is
    /// slower than all of them, so filtering the standard list alone would leave the ComboBox empty.</summary>
    private static List<FrameRate> RatesUpTo(FrameRate fastest)
    {
        var rates = new List<FrameRate> { fastest };

        foreach (var rate in MergeTargetDerivation.StandardRates
                     .Where(r => r.Value < fastest.Value)
                     .OrderByDescending(r => r.Value))
        {
            rates.Add(rate);
        }

        return rates;
    }

    /// <summary><paramref name="ceiling"/> first, then every standard value strictly below it.
    /// The ceiling is included even when it is not a standard value (an oddball 37 kHz source).</summary>
    private static IEnumerable<int> Descending(int[] standards, int ceiling)
        => new[] { ceiling }.Concat(standards.Where(s => s < ceiling).OrderByDescending(s => s));

    /// <summary>Rounds DOWN to even: yuv420p's 2×2 chroma subsampling makes libx264 reject an odd
    /// width or height outright. Mirrors <c>MergeTargetDerivation.ToEven</c>.</summary>
    private static int ToEven(int dimension) => Math.Max(2, dimension - (dimension & 1));
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test FFMedia.sln -c Release --filter "Category!=Integration"`
Expected: PASS — 617 passing (608 + 9).

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Models/TargetBounds.cs src/FFMedia.Tests/VideoMerger/TargetBoundsTests.cs
git commit -m "feat(merger): TargetBounds — the options the sources actually allow"
```

---

### Task 3: `MergeTarget.ClampTo` — the snap-down rule

**Files:**
- Modify: `src/FFMedia.Tools.VideoMerger/Models/MergeTarget.cs`
- Test: `src/FFMedia.Tests/VideoMerger/MergeTargetClampTests.cs` (create)

**Interfaces:**
- Consumes: `TargetBounds` (Task 2).
- Produces: `MergeTarget MergeTarget.ClampTo(TargetBounds bounds)` — pure; returns `this` unchanged when every field is already allowed.

- [ ] **Step 1: Write the failing tests**

Create `src/FFMedia.Tests/VideoMerger/MergeTargetClampTests.cs`:

```csharp
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergeTargetClampTests
{
    private static MediaInfo Clip(int width, int height, int fps = 30, int sampleRate = 48_000, int channels = 2)
        => new(TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(width, height, new FrameRate(fps, 1), "h264", "yuv420p", 0),
            new AudioStreamInfo("aac", sampleRate, channels));

    [Fact]
    public void ClampTo_LeavesAnOverrideThatIsStillAllowed_Untouched()
    {
        // Sources are 1080p; the user deliberately chose 720p. That intent must survive.
        var bounds = TargetBounds.From([Clip(1920, 1080)]);
        var target = MergeTarget.Default with { Width = 1280, Height = 720 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(1280, clamped.Width);
        Assert.Equal(720, clamped.Height);
    }

    [Fact]
    public void ClampTo_SnapsAnOversizedOverrideDownToTheCeiling()
    {
        // The user picked 1080p, then deleted the only 1080p clip. 1080p is now unreachable.
        var bounds = TargetBounds.From([Clip(1280, 720)]);
        var target = MergeTarget.Default with { Width = 1920, Height = 1080 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(1280, clamped.Width);
        Assert.Equal(720, clamped.Height);
    }

    [Fact]
    public void ClampTo_SnapsToTheLargestAllowedValueNotExceedingTheCurrentOne()
    {
        // The ladder shifted (the source aspect changed), so the user's exact choice is not on it.
        // Snap DOWN to the nearest allowed — never up, which would be the upscaling we forbid.
        var bounds = TargetBounds.From([Clip(1920, 1080)]);
        var target = MergeTarget.Default with { Width = 1600, Height = 900 };

        var clamped = target.ClampTo(bounds);

        Assert.True(
            (long)clamped.Width * clamped.Height <= 1600L * 900,
            $"snapped UP to {clamped.Width}x{clamped.Height}");
        Assert.Contains(new Resolution(clamped.Width, clamped.Height), bounds.Resolutions);
    }

    [Fact]
    public void ClampTo_SnapsFrameRateDown_NeverUp()
    {
        var bounds = TargetBounds.From([Clip(1920, 1080, fps: 30)]);
        var target = MergeTarget.Default with { FrameRate = new FrameRate(60, 1) };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(new FrameRate(30, 1), clamped.FrameRate);
    }

    [Fact]
    public void ClampTo_SnapsSampleRateAndChannelsDown()
    {
        var bounds = TargetBounds.From([Clip(1920, 1080, sampleRate: 44_100, channels: 1)]);
        var target = MergeTarget.Default with { AudioSampleRate = 96_000, AudioChannels = 6 };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(44_100, clamped.AudioSampleRate);
        Assert.Equal(1, clamped.AudioChannels);
    }

    [Fact]
    public void ClampTo_LeavesTheFreeFieldsAlone()
    {
        // Nothing about the SOURCES makes H.265, CRF 28, MKV or Fill an invalid choice.
        var bounds = TargetBounds.From([Clip(1920, 1080)]);
        var target = MergeTarget.Default with
        {
            VideoCodec = MergeVideoCodec.H265,
            AudioCodec = MergeAudioCodec.Opus,
            Container = MergeContainer.Mkv,
            Crf = 28,
            FitMode = FitMode.Fill,
        };

        var clamped = target.ClampTo(bounds);

        Assert.Equal(MergeVideoCodec.H265, clamped.VideoCodec);
        Assert.Equal(MergeAudioCodec.Opus, clamped.AudioCodec);
        Assert.Equal(MergeContainer.Mkv, clamped.Container);
        Assert.Equal(28, clamped.Crf);
        Assert.Equal(FitMode.Fill, clamped.FitMode);
    }

    [Fact]
    public void ClampTo_EmptyBounds_ReturnsTheTargetUnchanged()
    {
        // No clips → nothing to bound against. Do not mangle the target into a zero-size one.
        var target = MergeTarget.Default with { Width = 1280, Height = 720 };

        Assert.Equal(target, target.ClampTo(TargetBounds.Empty));
    }
}
```

- [ ] **Step 2: Run and verify they fail**

Run: `dotnet test FFMedia.sln -c Release --filter "FullyQualifiedName~MergeTargetClampTests"`
Expected: FAIL — `MergeTarget` has no `ClampTo`.

- [ ] **Step 3: Implement `ClampTo`**

Add to `MergeTarget` (in `MergeTarget.cs`, inside the record body):

```csharp
    /// <summary>Forces this target inside <paramref name="bounds"/>.
    ///
    /// <para>ONE rule, applied to every source-bounded field: <b>take the largest allowed value ≤ the
    /// current one; if none qualifies, take the smallest.</b> That single rule covers both cases the
    /// UI can produce — the ceiling dropped beneath the user's choice (they deleted the only 1080p
    /// clip), and the ladder shifted so their exact choice is no longer on it (the source aspect
    /// changed). It never snaps UP: that would silently reintroduce the upscaling this whole design
    /// exists to forbid.</para>
    ///
    /// <para>Codec, container, CRF and FitMode are untouched — nothing about the source clips makes
    /// H.265 or CRF 28 an invalid choice.</para></summary>
    public MergeTarget ClampTo(TargetBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        if (bounds.Resolutions.Count == 0)
        {
            return this; // no clips: nothing to bound against, and a zeroed target helps no one
        }

        var resolution = LargestNotExceeding(
            bounds.Resolutions, r => r.PixelCount, (long)Width * Height);
        var frameRate = LargestNotExceeding(
            bounds.FrameRates, r => r.Value, FrameRate.Value);
        var sampleRate = LargestNotExceeding(bounds.SampleRates, r => r, AudioSampleRate);
        var channels = LargestNotExceeding(bounds.ChannelCounts, c => c, AudioChannels);

        return this with
        {
            Width = resolution.Width,
            Height = resolution.Height,
            FrameRate = frameRate,
            AudioSampleRate = sampleRate,
            AudioChannels = channels,
        };
    }

    /// <summary>The allowed value with the greatest weight not exceeding <paramref name="current"/>,
    /// or — when every allowed value is larger — the smallest one. Never returns null: the caller has
    /// already established the list is non-empty.</summary>
    private static T LargestNotExceeding<T, TWeight>(
        IReadOnlyList<T> allowed, Func<T, TWeight> weight, TWeight current)
        where TWeight : IComparable<TWeight>
        => allowed
               .Where(a => weight(a).CompareTo(current) <= 0)
               .OrderByDescending(weight)
               .FirstOrDefault()
           ?? allowed.MinBy(weight)!;
}
```

> **Note for the implementer:** `FirstOrDefault()` on a reference type (`Resolution`) yields `null`,
> but on a value type (`FrameRate`, `int`) it yields `default` — **not** null — so `?? MinBy(...)`
> would never fire for rates or ints. Write the helper so it works for both. The simplest correct
> form is to materialise and test `Count`:
>
> ```csharp
>     private static T LargestNotExceeding<T, TWeight>(
>         IReadOnlyList<T> allowed, Func<T, TWeight> weight, TWeight current)
>         where TWeight : IComparable<TWeight>
>     {
>         var eligible = allowed.Where(a => weight(a).CompareTo(current) <= 0).ToList();
>         return eligible.Count > 0 ? eligible.MaxBy(weight)! : allowed.MinBy(weight)!;
>     }
> ```
>
> Use this second form. The first is shown only to explain why it is wrong.

Add `using FFMedia.Media;` to `MergeTarget.cs` if it is not already there (it is — `FrameRate`).

- [ ] **Step 4: Run tests**

Run: `dotnet test FFMedia.sln -c Release --filter "Category!=Integration"`
Expected: PASS — 624 passing (617 + 7).

- [ ] **Step 5: Mutation-check the snap-down rule**

Temporarily change `MaxBy(weight)` to `MinBy(weight)` in the eligible branch and re-run
`MergeTargetClampTests`. Expected: `ClampTo_LeavesAnOverrideThatIsStillAllowed_Untouched` **FAILS**
(it would snap 720p down to the smallest rung). Revert. A snap-down rule that passes whether it picks
the largest or the smallest allowed value is not pinned at all.

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Models/MergeTarget.cs src/FFMedia.Tests/VideoMerger/MergeTargetClampTests.cs
git commit -m "feat(merger): MergeTarget.ClampTo — snap an out-of-range override down"
```

---

### Task 4: Wire the bounds into `MergerViewModel`

**Files:**
- Modify: `src/FFMedia.Tools.VideoMerger/ViewModels/MergerViewModel.cs`
- Test: `src/FFMedia.Tests/VideoMerger/MergerViewModelTests.cs`

**Interfaces:**
- Consumes: `TargetBounds.From` (Task 2), `MergeTarget.ClampTo` (Task 3).
- Produces on `MergerViewModel`: `TargetBounds Bounds` (`[ObservableProperty]`), `Resolution? SelectedResolution` (get/set), `bool ShowOpusInMp4Warning`, `bool HasClips`.

**Context the implementer needs:** `MergerViewModel` already has a private `Recompute()` that runs on
every clip-list change; it re-derives the target (unless overridden), rebuilds the summary and raises
`CanMerge`. `SetTarget(target, overridden)` assigns `Target` while controlling the
`IsTargetOverridden` latch; `OverrideTarget(target)` commits a user edit and latches the flag. Read
those three before editing. **Do not** add a second re-derivation path.

- [ ] **Step 1: Write the failing tests**

Append to `MergerViewModelTests.cs` (inside the class):

```csharp
    // ---- bounded output options -------------------------------------------

    [Fact]
    public async Task Bounds_OfferNothingAboveTheSource()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1280, 720));
        await h.Vm.AddClipsAsync([@"C:\a.mp4"]);

        Assert.Equal(new Resolution(1280, 720), h.Vm.Bounds.Resolutions[0]);
        Assert.All(h.Vm.Bounds.Resolutions, r => Assert.True(r.PixelCount <= 1280L * 720));
        Assert.DoesNotContain(new FrameRate(60, 1), h.Vm.Bounds.FrameRates);
    }

    [Fact]
    public async Task Bounds_Recompute_WhenAClipIsAdded()
    {
        var h = Build();
        h.Analyzer.Returns(@"C:\small.mp4", Info(1280, 720));
        h.Analyzer.Returns(@"C:\big.mp4", Info(1920, 1080));

        await h.Vm.AddClipsAsync([@"C:\small.mp4"]);
        Assert.Equal(new Resolution(1280, 720), h.Vm.Bounds.Resolutions[0]);

        await h.Vm.AddClipsAsync([@"C:\big.mp4"]);
        Assert.Equal(new Resolution(1920, 1080), h.Vm.Bounds.Resolutions[0]);
    }

    [Fact]
    public async Task RemovingTheLargestClip_SnapsAnOversizedOverrideDown()
    {
        // The user overrode to 1080p, then deleted the only 1080p clip. The target must not keep
        // claiming 1080p — every remaining clip would be UPSCALED into it.
        var h = Build();
        h.Analyzer.Returns(@"C:\big.mp4", Info(1920, 1080));
        h.Analyzer.Returns(@"C:\small.mp4", Info(1280, 720));
        await h.Vm.AddClipsAsync([@"C:\big.mp4", @"C:\small.mp4"]);

        h.Vm.SelectedResolution = new Resolution(1920, 1080);
        Assert.True(h.Vm.IsTargetOverridden);

        h.Vm.RemoveClip(h.Vm.Clips.Single(c => c.FileName == "big.mp4"));

        Assert.Equal(1280, h.Vm.Target.Width);
        Assert.Equal(720, h.Vm.Target.Height);
    }

    [Fact]
    public async Task RemovingAClip_LeavesAStillValidOverrideAlone()
    {
        // The user deliberately chose to go SMALLER. Removing an unrelated clip must not undo that.
        var h = Build();
        h.Analyzer.Returns(@"C:\a.mp4", Info(1920, 1080));
        h.Analyzer.Returns(@"C:\b.mp4", Info(1920, 1080));
        await h.Vm.AddClipsAsync([@"C:\a.mp4", @"C:\b.mp4"]);

        h.Vm.SelectedResolution = new Resolution(1280, 720);
        h.Vm.RemoveClip(h.Vm.Clips[0]);

        Assert.Equal(1280, h.Vm.Target.Width);
        Assert.True(h.Vm.IsTargetOverridden);
    }

    [Fact]
    public async Task SelectedResolution_ReflectsTheTarget()
    {
        var h = await BuildWithClipsAsync(2);

        Assert.Equal(new Resolution(h.Vm.Target.Width, h.Vm.Target.Height), h.Vm.SelectedResolution);
    }

    [Fact]
    public async Task OpusInMp4_WarnsButDoesNotBlock()
    {
        // All 8 codec x container combinations mux cleanly in ffmpeg 8.1 — this is a PLAYABILITY
        // problem (QuickTime and most TVs cannot decode Opus in MP4), not a validity one. Warn; do
        // not forbid.
        var h = await BuildWithClipsAsync(2);

        h.Vm.SelectedContainer = MergeContainer.Mp4;
        h.Vm.SelectedAudioCodec = MergeAudioCodec.Opus;
        Assert.True(h.Vm.ShowOpusInMp4Warning);
        Assert.True(h.Vm.CanMerge); // still allowed

        h.Vm.SelectedContainer = MergeContainer.Mkv;
        Assert.False(h.Vm.ShowOpusInMp4Warning);

        h.Vm.SelectedContainer = MergeContainer.Mp4;
        h.Vm.SelectedAudioCodec = MergeAudioCodec.Aac;
        Assert.False(h.Vm.ShowOpusInMp4Warning);
    }

    [Fact]
    public void HasClips_IsFalse_WhenTheListIsEmpty_SoThePageCanDisableTheOutputSection()
    {
        Assert.False(Build().Vm.HasClips);
    }
```

The existing `Info(...)` helper already takes `width`/`height` as its first two parameters — no new
helper is needed.

- [ ] **Step 2: Run and verify they fail**

Run: `dotnet test FFMedia.sln -c Release --filter "FullyQualifiedName~MergerViewModelTests"`
Expected: FAIL — `Bounds`, `SelectedResolution`, `ShowOpusInMp4Warning`, `HasClips` do not exist.

- [ ] **Step 3: Add the properties**

In `MergerViewModel`, beside the existing `Target`/`IsTargetOverridden` fields:

```csharp
    /// <summary>What the user may choose, given the clips. Rebuilt on every clip-list change; the
    /// page binds each ComboBox straight at these lists, so a pointless value is not merely rejected
    /// — it is never shown.</summary>
    [ObservableProperty] private TargetBounds _bounds = TargetBounds.Empty;

    /// <summary>False with an empty clip list: there is no source to bound the output against, so the
    /// page disables the Output section (a merge needs two clips anyway).</summary>
    public bool HasClips => Clips.Count > 0;

    /// <summary>MP4 + Opus muxes fine (verified against ffmpeg 8.1 — all 8 codec/container pairs do),
    /// but QuickTime and most TVs cannot decode it. A playability footgun, not an invalid option:
    /// warn, never block.</summary>
    public bool ShowOpusInMp4Warning
        => Target.Container == MergeContainer.Mp4 && Target.AudioCodec == MergeAudioCodec.Opus;

    /// <summary>The output resolution as a single choice. Two independent width/height boxes let the
    /// user build 1920 x 102 — positive, even, encodable, absurd. A pair cannot.</summary>
    public Resolution? SelectedResolution
    {
        get => new(Target.Width, Target.Height);
        set
        {
            if (value is null || (value.Width == Target.Width && value.Height == Target.Height))
            {
                return;
            }

            OverrideTarget(Target with { Width = value.Width, Height = value.Height });
        }
    }
```

- [ ] **Step 4: Raise the new properties when the target changes**

`MergeTarget` is a record assigned wholesale, so `OnTargetChanged` is where every dependent property
must be re-raised. Find the existing `partial void OnTargetChanged(MergeTarget value)` and add:

```csharp
        OnPropertyChanged(nameof(SelectedResolution));
        OnPropertyChanged(nameof(ShowOpusInMp4Warning));
```

(The existing body already re-raises `TargetWidth`, `SelectedFitMode`, etc. — follow that pattern
exactly and add these two alongside them.)

- [ ] **Step 5: Rebuild the bounds and clamp, in `Recompute()`**

In `Recompute()`, **after** the clip list has settled and **before** the summary is built, rebuild the
bounds and force the target inside them:

```csharp
        var infos = Clips.Select(c => c.Clip.Info).ToList();
        Bounds = TargetBounds.From(infos);

        // The ceiling MOVES as clips come and go. An override that was legal a moment ago can now be
        // above it — the user deleted the only 1080p clip and their 1080p target would upscale every
        // remaining clip. Snap it down silently, keeping the override: their intent to go SMALLER is
        // still theirs.
        var clamped = Target.ClampTo(Bounds);
        if (clamped != Target)
        {
            SetTarget(clamped, IsTargetOverridden);
        }

        OnPropertyChanged(nameof(HasClips));
```

**Important:** use `SetTarget(clamped, IsTargetOverridden)` — **not** `OverrideTarget` — so clamping
never *invents* an override on a target the user never touched.

**If the existing `Recompute()` re-derives the target when `!IsTargetOverridden`, the clamp must run
AFTER that re-derivation** (a freshly derived target is already inside the bounds, so the clamp is a
no-op — but ordering it the other way would clamp a target that is about to be replaced).

`MergeClipViewModel` exposes the probed `MediaInfo` — check its actual property name (`Clip.Info` or
similar) and use it; do not guess.

- [ ] **Step 6: Run tests**

Run: `dotnet test FFMedia.sln -c Release --filter "Category!=Integration"`
Expected: PASS — 631 passing (624 + 7).

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/ViewModels/MergerViewModel.cs src/FFMedia.Tests/VideoMerger/MergerViewModelTests.cs
git commit -m "feat(merger): bound the ViewModel's output options to the source clips"
```

---

### Task 5: The page — ComboBoxes, not free text

**Files:**
- Modify: `src/FFMedia.Tools.VideoMerger/Views/MergerPage.xaml`
- Test: `src/FFMedia.Tests/VideoMerger/MergerPageLoadTests.cs` (already exists — it must keep passing)

**Interfaces:**
- Consumes: `Bounds`, `SelectedResolution`, `ShowOpusInMp4Warning`, `HasClips` (Task 4).

**Read first:** the Output section of `MergerPage.xaml` — three `UniformGrid`s holding
`ui:TextBox`es (Width, Height, CRF, AudioSampleRate, AudioChannels) and `ComboBox`es (Frame rate,
Container, Video codec, Audio codec, Fit mode). The page already declares
`xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"` and a `BooleanToVisibilityConverter` keyed
`BoolToVisibility`.

- [ ] **Step 1: Replace the Width and Height boxes with one resolution ComboBox**

Delete the two `ui:TextBox`es bound to `TargetWidth` and `TargetHeight`, and put in their place:

```xml
<StackPanel>
    <TextBlock Text="Resolution" />
    <ComboBox ItemsSource="{Binding Bounds.Resolutions}"
              SelectedItem="{Binding SelectedResolution}"
              ToolTip="Only resolutions your clips can actually fill — upscaling adds no detail" />
</StackPanel>
```

`Resolution.ToString()` (Task 1) renders each item as `1920 × 1080`, so no `ItemTemplate` is needed.

- [ ] **Step 2: Point the frame-rate ComboBox at the bounded list**

Change its `ItemsSource` from the full standard-rate list to `{Binding Bounds.FrameRates}`, leaving
its `SelectedItem` binding as it is.

- [ ] **Step 3: Replace the sample-rate and channels TextBoxes with ComboBoxes**

```xml
<StackPanel>
    <TextBlock Text="Audio sample rate" />
    <ComboBox ItemsSource="{Binding Bounds.SampleRates}"
              SelectedItem="{Binding TargetAudioSampleRate}" />
</StackPanel>
<StackPanel>
    <TextBlock Text="Audio channels" />
    <ComboBox ItemsSource="{Binding Bounds.ChannelCounts}"
              SelectedItem="{Binding TargetAudioChannels}" />
</StackPanel>
```

The existing `TargetAudioSampleRate` / `TargetAudioChannels` setters already commit through
`OverrideTarget` — they need no change. **Leave the CRF `ui:TextBox` exactly as it is:** it already
ignores anything outside 0–51.

- [ ] **Step 4: Add the MP4 + Opus warning, and disable the section with no clips**

Directly beneath the audio-codec ComboBox:

```xml
<TextBlock Text="MP4 + Opus plays in VLC and Chrome, but not QuickTime or most TVs. MKV or AAC is safer."
           Visibility="{Binding ShowOpusInMp4Warning, Converter={StaticResource BoolToVisibility}}"
           Foreground="{DynamicResource SystemFillColorCautionBrush}"
           TextWrapping="Wrap" FontSize="12" Margin="0,4,0,0" />
```

Set `IsEnabled="{Binding HasClips}"` on the **Output section's** root `StackPanel` — with no clips
there is no source to bound against, and every ComboBox would be empty.

> `SystemFillColorCautionBrush` **is verified to exist** in WPF-UI 4.3 (`#FF9D5D00`, amber) — resolved
> against the real `ThemesDictionary` + `ControlsDictionary`, not assumed. `SettingsPage.xaml:27`
> already uses its sibling `SystemFillColorCriticalBrush` for the restart-required note.

- [ ] **Step 5: Run the page-load test**

Run: `dotnet test FFMedia.sln -c Release --filter "FullyQualifiedName~MergerPageLoadTests"`
Expected: PASS — 2 tests. This proves the new XAML **actually parses and binds** against the real
resource dictionaries inside the real `NavigationViewContentPresenter`. It is the test that would have
caught the `StaticResource` crash this page shipped once already.

- [ ] **Step 6: Full suite + build**

Run: `dotnet build FFMedia.sln -c Release` → **0 warnings / 0 errors**
Run: `dotnet test FFMedia.sln -c Release --no-build --filter "Category!=Integration"` → 631 passing.

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Views/MergerPage.xaml
git commit -m "feat(merger): offer only the resolutions/rates the sources allow"
```

---

### Task 6: Prove it against real ffmpeg, then ship

**Files:**
- Modify: `src/FFMedia.Tests/Integration/MergeIntegrationTests.cs`
- Modify: `SDD.md`, `CLAUDE.md`

**Interfaces:**
- Consumes: everything above.

**Read first:** `MergeIntegrationTests` already has a `MakeClipAsync(...)` helper that synthesizes a
clip with ffmpeg's `lavfi` `testsrc`, and probes outputs with the real `FfprobeMediaAnalyzer`. Reuse
both; do not write new process plumbing.

- [ ] **Step 1: Write the failing integration test**

Add to `MergeIntegrationTests`:

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Merging1080pClipsToA720pTarget_ProducesAReal720pFile()
    {
        // A resolution ladder that yields something ffmpeg REJECTS — an odd height, a broken aspect —
        // is precisely the failure this feature would be embarrassed by. And the exit code is exactly
        // what cannot be trusted (concat exits 0 on a truncated merge), so PROBE the output.
        var a = await MakeClipAsync("a.mp4", "1920x1080", 30, seconds: 2);
        var b = await MakeClipAsync("b.mp4", "1920x1080", 30, seconds: 2);

        var infos = new List<MediaInfo>();
        foreach (var path in new[] { a, b })
        {
            var probe = await _analyzer.AnalyzeAsync(path);
            Assert.True(probe.IsSuccess, probe.Error);
            infos.Add(probe.Value!);
        }

        var bounds = TargetBounds.From(infos);
        var target = MergeTargetDerivation.Derive(infos)
            .ClampTo(bounds) with { Width = 1280, Height = 720 };

        Assert.Contains(new Resolution(1280, 720), bounds.Resolutions);

        var output = Path.Combine(_temp, "merged720.mp4");
        var result = await _merger.MergeAsync(
            new MergeRequest([.. infos.Select((info, i) => new MergeClip(i == 0 ? a : b, info))],
                target, output));

        Assert.True(result.IsSuccess, result.Error);

        var merged = await _analyzer.AnalyzeAsync(output);
        Assert.True(merged.IsSuccess, merged.Error);
        Assert.Equal(1280, merged.Value!.Video!.Width);
        Assert.Equal(720, merged.Value.Video.Height);
        Assert.Equal(4, merged.Value.Duration.TotalSeconds, 1); // both clips are really there
    }
```

**Match the real signatures.** `MakeClipAsync`, `MergeRequest`, `MergeClip` and the field names
(`_analyzer`, `_merger`, `_temp`) must match what the file already declares — read them and adapt this
test to them rather than assuming. If `MergeRequest` takes different arguments, use the real ones.

- [ ] **Step 2: Run it**

Run: `dotnet test FFMedia.sln -c Release --filter "Category=Integration&FullyQualifiedName~MergeIntegrationTests"`
Expected: PASS — 4 tests (3 existing + 1 new). If ffprobe is missing, run `build/fetch-binaries.ps1`
first (SDD §9: a new required binary is invisible to an already-populated, git-ignored checkout).

- [ ] **Step 3: Update the docs**

`SDD.md`: bump to **v0.18**, and rewrite the v0.17 Changelog row's closing line — it currently says
"Design only — no code in this change." That is no longer true. Add a v0.18 row recording that
`TargetBounds` + `ClampTo` + the ComboBox UI are **implemented**, with the final test counts.

`CLAUDE.md`: add a dated Progress Log entry at the **top** (Rule 2) covering what was built, the
keystone invariant (derived target = first entry of every list), the snap-down rule, and the
verification numbers.

- [ ] **Step 4: Full verification**

```bash
dotnet build FFMedia.sln -c Release                                              # 0 warnings / 0 errors
dotnet test FFMedia.sln -c Release --no-build --filter "Category!=Integration"   # 631 passing
dotnet test FFMedia.sln -c Release --no-build --filter "Category=Integration&FullyQualifiedName~MergeIntegrationTests"  # 4 passing
git status --short                                                               # no .exe, no bin/, no obj/
```

- [ ] **Step 5: Commit and open the PR**

```bash
git add -A
git commit -m "test(merger): merge 1080p sources to a 720p target against real ffmpeg; sync docs"
git push -u origin feat/m7-target-bounds
gh pr create --base main --title "feat(merger): output options bounded by the source (TargetBounds)" --body "..."
```

Do **not** merge — the user reviews (CLAUDE.md Rule 3).

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §3.1 `TargetBounds` + the four lists | 2 |
| §3.1 keystone: derived target = first entry | 2 (asserted directly) |
| §3.1 `StandardRates` must be shared, not copied | 1 |
| §3.2 snap-down rule | 3 |
| §3.3 resolution ComboBox replaces W/H | 5 |
| §3.3 frame rate / sample rate / channels bounded | 4, 5 |
| §3.3 CRF unchanged (already guarded) | — (explicitly *not* changed; stated in Global Constraints) |
| §3.3 MP4+Opus warning, never a block | 4, 5 |
| §3.3 empty clip list disables Output | 4 (`HasClips`), 5 |
| §3.4 `Resolution` record | 1 |
| §4 pure tests | 2, 3 |
| §4 ViewModel tests | 4 |
| §4 page-load test still passes | 5 |
| §4 integration: 1080p → 720p, probe output | 6 |

**Type consistency:** `TargetBounds.From` / `.Empty`; `MergeTarget.ClampTo`; `Resolution(Width,
Height)` + `PixelCount` + `ToString()`; `MergerViewModel.Bounds` / `SelectedResolution` /
`ShowOpusInMp4Warning` / `HasClips`; `MergeTargetDerivation.StandardRates`. Used consistently in
Tasks 1–6.

**Resolved before writing this plan (do not re-derive):**
- `MergeClipViewModel.Clip` is a `MergeClip`, and `MergeClip` is `record MergeClip(string SourcePath,
  MediaInfo Info)` — so `c.Clip.Info` in Task 4 Step 5 is correct.
- `SystemFillColorCautionBrush` exists in WPF-UI 4.3 (`#FF9D5D00`), verified against the real
  dictionaries.

**Known unknowns the implementer must resolve by reading, not guessing** (each is called out in its
task): the exact body of `Recompute()` and `OnTargetChanged` in `MergerViewModel`; and
`MergeIntegrationTests`' real helper/field signatures (`MakeClipAsync`, `MergeRequest`, `_merger`,
`_temp`) — Task 6's test is written against their *expected* shape and must be adapted to their actual
one.
