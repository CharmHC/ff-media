# GIF Maker — FFMedia's third tool

> **Status:** approved design · **Date:** 2026-07-12 · **Milestone:** M8
> **Module:** `FFMedia.Tools.GifMaker`

---

## 1. What it is

Load one video, choose a time range, a size and a frame rate, and get a GIF.

The tool is a **single-item editor, not a queue**. Making a GIF is iterative — you tune until it is
small enough — so the feedback loop matters more than throughput. One video at a time, with a **live
estimated file size** that updates as you tune. (The merger is likewise one-at-a-time; the downloader
is a queue because a playlist genuinely is a batch.)

**Parameters (v1, and only these):**

| Parameter | Notes |
|---|---|
| **Start** / **End** | `0:12`, `1:05:00`, or seconds. Parsed by the shared `TrimParsing` (§4). |
| **Size** | A width, at the **source's aspect ratio**. Height is derived — never a second box (§3.2). |
| **Frame rate** | Frames per second of the GIF. |

Deliberately **not** in v1: quality/dither preset, loop count, playback speed, crop, reverse. Each is a
plausible follow-up; none is needed to make a GIF, and every knob is another thing to explain.

---

## 2. The one thing that decides whether this looks good: two passes

The obvious command — `ffmpeg -i in.mp4 out.gif` — quantizes to a **generic** 256-colour palette and
produces visibly banded, dirty output. The right way is to analyse the clip, build an **optimal**
palette for *its* colours, then apply it:

```
pass 1 (palette):  -ss S -to E -i SRC
                   -vf "fps=F,scale=W:-2:flags=lanczos,palettegen=stats_mode=diff"
                   -y PALETTE.png

pass 2 (render):   -ss S -to E -i SRC -i PALETTE.png
                   -lavfi "fps=F,scale=W:-2:flags=lanczos[x];[x][1:v]paletteuse=diff_mode=rectangle"
                   -loop 0 -y OUT.gif
```

**Verified against the bundled ffmpeg 8.1:** `palettegen` and `paletteuse` are both present, and the
two-pass route costs about **3× the time** of the naive one — 0.47 s vs 0.15 s on a 3-second clip.
Irrelevant at this scale, so there is no reason to offer the bad one.

Two details in those arguments are load-bearing:

- **`scale=W:-2`** derives the height from the source aspect **and forces it even**. `-1` would allow an
  odd height; the merger already learned that odd dimensions are rejected outright by libx264, and while
  GIF itself tolerates them, keeping the guarantee uniform costs nothing.
- **`-ss` and `-to` go BEFORE `-i`** — this seeks rather than decoding the whole file and throwing most
  of it away, which for a GIF cut from a long video is the difference between instant and unusable.
  **Both semantics were verified against the bundled ffmpeg 8.1 rather than assumed**, because both are
  widely mis-stated:
  - **`-to` is ABSOLUTE**, i.e. a position on the source's timeline — not a duration measured from the
    seek point. `-ss 2 -to 5` yields exactly **3.0 s**. So the builder passes Start and End straight
    through; it does **not** compute a duration.
  - **The seek is frame-accurate, not keyframe-snapped.** With keyframes 5 s apart, `-ss 2` produced
    exactly 30 frames at 10 fps = 3.000 s. (The old folklore that input seeking snaps to a keyframe has
    not been true for years — ffmpeg decodes and discards to land exactly.) The cut therefore starts
    where the user asked, and **no `-accurate_seek` flag or output-side `-ss` is needed**.

**Measured, and worth stating plainly: two passes is not automatically *smaller*** (0.57 MB vs 0.38 MB
in the same test) — the dithering that buys smooth gradients also adds noise that compresses worse.
Quality and size are a real trade-off here, not a free win. v1 takes the quality side; a dither preset
is the natural first follow-up if that turns out to be the wrong default.

---

## 3. Bounds: the tool cannot offer a pointless setting

### 3.1 Width and frame rate are capped at the source

Exactly the rule `TargetBounds` established for the merger, applied to a new tool. A GIF wider than its
source video, or at a higher frame rate, contains **no more information** — the extra pixels are
invented and the extra frames are duplicates. Bigger file, longer encode, nothing gained. So those
values are **not offered**.

```csharp
public sealed record GifBounds(
    IReadOnlyList<Resolution> Sizes,     // source width first, then standard steps below, at source aspect
    IReadOnlyList<FrameRate> FrameRates) // source rate first, then standard steps below
{
    public static GifBounds From(MediaInfo source);
}
```

- **Sizes:** the source resolution, then standard widths below it — 640, 480, 320, 240 — each scaled to
  the source's aspect ratio and rounded even. Entries ≥ the source are dropped.
- **Frame rates:** the source rate, then 30, 24, 20, 15, 12, 10 — filtered to below it. The source's own
  rate always heads the list, even when non-standard (a 12 fps clip), or the list could be empty.

**The keystone invariant, same as the merger's:** the **defaults are the head of each list**, because
they are derived from the source, not recomputed by the UI. Bounds and defaults cannot drift.

### 3.2 Height is never a separate box

Two independent width/height boxes let a user build `480 × 31`: positive, even, encodable, absurd. The
merger shipped exactly that hole. Here, **size is one choice** — a `Resolution` picked from a list — and
the height follows the source aspect. The bad state is unrepresentable, not validated.

### 3.3 The range must be inside the video

`End` must be > `Start`, and both must lie within the source's duration. A range that runs past the end
of the video is rejected **at the point of editing**, not discovered minutes later.

---

## 4. Two promotions to shared layers

A third tool makes two existing types genuinely shared. **A tool module must never reference another
tool module** (SDD §5), so:

| Type | From | To | Why |
|---|---|---|---|
| `Resolution` | `VideoMerger.Models` | **`FFMedia.Media`** | A width×height pair is a media concept, not a merger concept. The GIF Maker needs the identical type. |
| `TrimParsing` | `YouTubeDownloader` | **`FFMedia.Core`** | Parsing `1:30` → `TimeSpan` is not downloader-specific. The GIF Maker needs exactly it, and duplicating a parser is how two parsers drift. |

Both are pure moves — no behaviour change, existing tests follow the type.

---

## 5. Estimating the size honestly

GIF size depends on **content**: a static talking head compresses far better than confetti. A single
number would be false precision, so the UI shows a **range**.

```csharp
public static GifEstimate Estimate(int frames, long pixelsPerFrame, GifSizeProfile profile);
// → (LowBytes, HighBytes)
```

`frames × pixelsPerFrame × bytesPerPixelPerFrame`, where the constant starts at a default and is
**refined from the GIFs the user actually makes** — a persisted rolling average in `gif-size.json`.
This is the `SpeedProfile` pattern the merger already uses for encode throughput: the same class of
unknowable, solved the same way, and it gets better with use.

The page warns when the estimate exceeds a soft threshold, and names the three levers that fix it:

> **Estimated GIF: 3–6 MB** (6.0 s · 480×270 · 15 fps = 90 frames)
> ⚠ Likely over 5 MB. Shorten the range, reduce the size, or lower the frame rate.

After creating, it reports the **actual** size — which is also what feeds the profile.

---

## 6. Trust nothing ffmpeg reports

The merger learned this the hard way and it is now a project rule: **ffmpeg's exit code is exactly what
cannot be trusted** (its `concat` demuxer exits 0 having silently dropped segments; see SDD Changelog
0.15). So `GifService`:

1. **Preflights** — the source exists and is readable; the range lies inside its duration; the temp
   volume has room.
2. Runs the two passes, writing the palette to a temp file.
3. **Re-probes the finished GIF** with ffprobe: it must be a real GIF, non-empty, of roughly the
   expected duration. If it is not, the file is **deleted** rather than handed over as a corrupt result.
4. **Cleans up the temp palette on every exit path** — success, failure and cancel alike.

Cancellation returns `Canceled`, not `Failed`, and leaves nothing behind.

---

## 7. Structure

```
FFMedia.Tools.GifMaker/
  Models/      GifRequest · GifBounds · GifEstimate · GifSizeProfile · GifProgress · GifJobStatus
  Services/    GifArgsBuilder (pure) · GifBoundsDerivation (pure) · GifSizeEstimator (pure)
               GifSizeProfileStore · GifErrors · IGifService / GifService
  ViewModels/  GifMakerViewModel
  Views/       GifMakerPage
  GifMakerTool.cs           ITool: IconGlyph "Gif24" (verified to exist), SortOrder 30
  ServiceCollectionExtensions.cs   AddGifMakerEngine() + AddGifMaker()
```

`SortOrder = 30` — the pane sorts **ascending**, and the downloader is 10, the merger 20. (The merger's
spec said `2` and shipped `20` for precisely this reason; do not repeat the mistake.)

`IconGlyph = "Gif24"` — **verified to exist** in `SymbolRegular`. The shell falls back to `Apps24` on an
unparseable name, so a typo degrades *silently*; a test pins it, as `VideoMergerTool`'s does.

**Reused, not rebuilt:** `IMediaAnalyzer`, `IFfmpegRunner`, `Result`, `IHistoryService`,
`INotificationService`, `ISettingsService`, `JsonStore<T>`.

`HistorySource` gains **`Gif`**. Old `history.json` files still load — `TolerantHistorySourceConverter`
already degrades an unknown value to `Download` rather than destroying the file.

---

## 8. UI

One page, top to bottom: **source** (drag a video in, or *Choose video…*, showing its
resolution/fps/duration) · **range** (start, end, with the resulting length) · **output** (size, frame
rate — both bounded) · **the estimate line + warning** · **file name + folder** · **Create GIF /
Cancel** + progress.

- Every parameter carries a **plain-English tooltip naming the trade-off** (SDD §13) — enforced by
  `TooltipCoverageTests`, which gains this page as its third.
- The page must **not** contain its own `ScrollViewer` (SDD §13: the shell provides one; a nested one
  silently eats the mouse wheel).
- Merges and downloads already appear in **History**; GIFs join them, with the usual notifications.

---

## 9. Testing

**Pure, exhaustive, no WPF:** `GifArgsBuilder` (both passes, exact argv), `GifBoundsDerivation` (the
head-of-list keystone; never offers above the source; portrait sources stay portrait), `GifSizeEstimator`
(range, profile refinement).

**ViewModel with fakes:** bounds recompute when a new video is loaded; an out-of-range value snaps down;
the estimate updates as parameters change; the range is validated against the source duration; the UI is
frozen while rendering.

**Page:** joins `MergerPageLoadTests`' `WpfHost` (the XAML genuinely loads against the real resource
dictionaries — a `StaticResource` that does not resolve compiles clean and throws in front of the user)
and `TooltipCoverageTests`.

**Integration (trait-gated, real ffmpeg):** make a real GIF from a synthesized `testsrc` clip and
**probe the output** — dimensions, frame count, duration. Then assert the palette temp file is gone.

---

## 10. Decisions (user-approved)

1. **Single-item editor with a live estimated size**, not a queue.
2. **Estimate is a range**, calibrated from the user's own past GIFs (`gif-size.json`).
3. **Four parameters only:** start, end, size, frame rate.
4. **Size and frame rate are capped at the source** (the `TargetBounds` rule), and **height is derived**
   from the source aspect — never an independent box.
5. **Two-pass palettegen/paletteuse**, always. The naive single-pass route is not offered.

## 11. Deferred

- Quality / dither preset (the real quality-vs-size lever, and the likeliest first follow-up).
- Loop count, playback speed, crop, reverse/boomerang.
- Batch / queue of GIF jobs.
- WebP or APNG output (smaller and better than GIF — but *GIF* is what was asked for, and what pastes
  everywhere).
