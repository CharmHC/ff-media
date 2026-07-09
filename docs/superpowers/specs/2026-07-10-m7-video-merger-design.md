# M7 — Video Standardize & Merge (design)

> **Status:** approved 2026-07-10 · **Milestone:** M7 · **Module:** `FFMedia.Tools.VideoMerger`
>
> This is the design record for FFMedia's **second tool module**. It defers to
> [`SDD.md`](../../../SDD.md), which is the single source of truth; where this
> document makes a decision, `SDD.md` is amended in the same change.

---

## 1. Purpose

Ingest a list of local video files that differ in resolution, frame rate, codec,
container, and audio layout; **standardize** them to a common target; and
**merge** them into a single output video.

Beyond the core flow, the module must let the user:

1. Define clip **order** — manually, randomly, or randomly while **locking**
   selected clips to specific indices.
2. See the **total output duration** and an **estimated merge time** *before*
   committing to the merge.
3. See **live merge progress**.
4. **Cancel** an in-flight merge.

This milestone also exists to **validate the modular seam** promised in SDD §4:
adding a tool must not modify the shell.

---

## 2. Goals & Non-Goals

### 2.1 Goals

- Probe every input clip; auto-derive a sensible standardization target; let the
  user override any field.
- Conform mismatched clips (resolution, aspect, FPS, codecs, audio layout) and
  concatenate them into one output.
- Zero re-encoding when every clip already conforms (the **fast path**).
- Pre-merge summary: exact output duration, estimated merge time (as a range),
  count of clips needing re-encode, temp disk required.
- Live weighted progress and cancellation.
- Reuse the existing `IHistoryService`, `INotificationService`, and `AppSettings`.

### 2.2 Non-Goals (this milestone)

- **Transitions / crossfades.** They force the `filter_complex xfade` path, kill
  the stream-copy fast path outright, and make output duration ≠ Σ input durations.
- **Per-clip trim (in/out points).** Blind timecode entry is a poor experience;
  doing it properly needs a preview scrubber. Deferred.
- **A merge queue.** One merge at a time — see §6.4.
- **Background music / audio replacement.** Arguably its own tool.
- Cross-platform, telemetry, cloud (unchanged from SDD §2.2).

---

## 3. Decisions ratified in this design

| # | Decision | Rationale |
|---|---|---|
| D1 | **Auto-derive the target, user-overridable per field.** | Best default UX; the user need not know their clips' parameters, but retains full control. |
| D2 | **Letterbox/pillarbox is the default aspect fit**, with a per-merge `FitMode` dropdown (Fit / Fill+Crop / Stretch). | Fit never loses content and never distorts. Fill and Stretch are opt-in, not surprises. Per-*clip* fit is deferred (UI cost, little demand). |
| D3 | **Merge time is a calibrated heuristic, shown as a range**, refined by a persisted rolling average of the user's own measured encode throughput; it is **replaced by ffmpeg's real ETA** once merging starts. | A pre-merge estimate is inherently a guess. A sample-benchmark would burn CPU before the user commits; a static table is wrong on every machine but the developer's. This is honest and self-improving. |
| D4 | **Audio is standardized; clips with no audio track get a synthesized silent track** (`anullsrc`) of their own duration. | `concat` requires an identical stream layout across segments. Dropping audio wholesale, or failing, are both worse. |
| D5 | **Approach A — normalize-then-concat**, plus a **disk-space guard**. | The only strategy that yields a free fast path, per-clip progress, per-clip failure attribution, and a computable ETA. See §6. |
| D6 | **Drop FFMpegCore.** Drive `ffmpeg`/`ffprobe` through the existing `IProcessRunner` seam with **pure parsers**. | FFMpegCore has been listed in SDD §3 since v0.1 but never referenced. It spawns its own processes, bypassing the fakeable seam that makes this codebase testable. Using `IProcessRunner` adds no dependency and matches the `FfmpegVersionParsing` precedent already in Core. **Amends SDD §3 and §8.** |
| D7 | **One merge at a time; no `IMergeManager`.** | A single merge already saturates the CPU (Phase 1 is itself concurrent). A merge *queue* would only interleave work, not finish it sooner. YAGNI — revisit on demand. |
| D8 | **In scope:** reuse of Core history/notifications/settings, and drag-to-reorder. | The seams already exist; drag-reorder is the natural gesture for ordering. |

---

## 4. Infrastructure prerequisite: `ffprobe.exe`

Probing requires **`ffprobe.exe`**, which FFMedia does not currently ship — only
`ffmpeg.exe` and `yt-dlp.exe` (SDD §9).

`ffprobe.exe` already exists **inside the pinned, SHA-256-verified BtbN zip** that
`build/fetch-binaries.ps1` downloads. The change is therefore:

- Extract a second executable from the **same, already-verified** archive — **no new
  download, no new pinned hash.**
- Add `ExternalBinary.Ffprobe` and resolve it through the existing
  `IBinaryProvider`.
- The `FFMedia.App` / `FFMedia.Tests` csproj copy glob is already `assets/binaries/*.exe`,
  so `ffprobe.exe` rides along into the output directory and the Velopack package with
  no build-script change.

**Amends SDD §9.**

---

## 5. Architecture

Dependencies point inward, per SDD §5. Nothing in the shell changes.

```
FFMedia.Tools.VideoMerger   (net9.0-windows, UseWPF)   ← the module
        │ uses
        ▼
FFMedia.Media               (net9.0, UI-free)          ← finally realized; shared
        │ uses
        ▼
FFMedia.Core                (IProcessRunner, IBinaryProvider, Result<T>,
                             IHistoryService, INotificationService, JsonStore<T>)
```

### 5.1 `FFMedia.Media` — realized

Tool-agnostic ffmpeg/ffprobe access. This is the layer M7 exists to prove out; a
third tool should be able to reuse it untouched.

| Type | Kind | Responsibility |
|---|---|---|
| `MediaInfo` | record | `Duration`, `ContainerFormat`, `VideoStreamInfo?` (Width, Height, FrameRate, CodecName, PixelFormat, Rotation), `AudioStreamInfo?` (CodecName, SampleRate, Channels) |
| `IMediaAnalyzer` → `FfprobeMediaAnalyzer` | service | Runs `ffprobe -v quiet -print_format json -show_format -show_streams <file>` via `IProcessRunner` → `Result<MediaInfo>` |
| `FfprobeParsing` | **pure** | ffprobe JSON → `MediaInfo`. Tested against captured fixtures. |
| `IFfmpegRunner` → `FfmpegRunner` | service | Runs ffmpeg with an arg list + `-progress pipe:1 -nostats`; streams `FfmpegProgress`; honors `CancellationToken`; captures the stderr tail on failure |
| `FfmpegProgressParsing` | **pure** | `out_time_us=…` / `speed=…` key-value lines → `FfmpegProgress(Position, Speed)` |

Both services locate their binary through `IBinaryProvider` — never the system PATH
(SDD §8).

### 5.2 `FFMedia.Tools.VideoMerger` — the module

Mirrors the YouTube Downloader's structure (`Models/`, `Services/`, `ViewModels/`,
`Views/`, `Navigation/`, `ServiceCollectionExtensions.cs`).

---

## 6. The merge engine

### 6.1 Models

- **`MergeTarget`** (record) — `Width`, `Height`, `FrameRate`, `VideoCodec`,
  `VideoQuality` (CRF), `Container`, `AudioCodec`, `AudioSampleRate`,
  `AudioChannels`, `FitMode`.
- **`FitMode`** (enum) — `Fit` (letterbox/pillarbox, default), `Fill` (scale-to-cover
  + center-crop), `Stretch` (distort).
- **`ClipItem`** (observable) — `SourcePath`, `MediaInfo?`, `IsLocked`,
  `LockedIndex`, `Conformance`, `Status`, `Progress`.
- **`Conformance`** — `IsConforming` + the list of mismatched dimensions (used for
  the per-row badge, and to explain *why* a clip will be re-encoded).
- **`MergeEstimate`** — `OutputDuration`, `LowEta`, `HighEta`, `TempBytesEstimate`,
  `IsFastPath`, `ReencodeCount`.
- **`MergeJobStatus`** (enum) — `Idle`, `Probing`, `Normalizing`, `Concatenating`,
  `Completed`, `Canceled`, `Failed`.

### 6.2 Pure functions — the testing heart

Following the `OptionSetBuilder` precedent (SDD §7.3, §14): every consequential
decision is a pure, exhaustively-tested function.

| Function | Contract |
|---|---|
| `MergeTargetDerivation.Derive(IReadOnlyList<MediaInfo>) → MergeTarget` | Largest frame **area** wins resolution; highest FPS snapped to a standard rate; audio takes max sample rate and max channel count; container/codec by majority. Deterministic. |
| `ConformanceCheck.Evaluate(MediaInfo, MergeTarget) → Conformance` | A clip conforms **iff** resolution, FPS, video codec, pixel format, **and** audio codec/rate/channels all match. **A missing audio track is non-conforming.** |
| `NormalizeArgsBuilder.Build(clip, target, outPath) → string[]` | The filter graph + codec args (see §6.3). |
| `ConcatArgsBuilder.BuildListFile(paths) → string` | `list.txt` content, with `'` escaping in paths. |
| `ConcatArgsBuilder.BuildArgs(listPath, outPath) → string[]` | `-f concat -safe 0 -i list.txt -c copy -movflags +faststart` |
| `Ordering.Shuffle(clips, seed) → IReadOnlyList<ClipItem>` | Locked clips are placed at their `LockedIndex` first; unlocked clips are Fisher–Yates-shuffled into the remaining slots. Seeded ⇒ deterministic. |
| `MergeEstimator.Estimate(clips, target, SpeedProfile) → MergeEstimate` | See §6.5. |
| `DiskSpaceGuard.Check(tempDir, requiredBytes) → Result` | Free space on the temp volume vs `requiredBytes × 1.2`. |

`ConformanceCheck` is the keystone: **one** function drives the fast path, the
per-clip UI badge, **and** the ETA.

### 6.3 Filter graphs

| `FitMode` | Video filter |
|---|---|
| `Fit` | `scale=W:H:force_original_aspect_ratio=decrease,pad=W:H:(ow-iw)/2:(oh-ih)/2` |
| `Fill` | `scale=W:H:force_original_aspect_ratio=increase,crop=W:H` |
| `Stretch` | `scale=W:H` |

All three are suffixed with `,fps=R,setsar=1`.

A clip with **no audio track** additionally gets
`-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000` and `-shortest`, so
the synthesized silence matches the clip's own duration.

### 6.4 Execution — `IMergeService.MergeAsync(MergeRequest, IProgress<MergeProgress>, CancellationToken)`

**Phase 0 — preflight.** `DiskSpaceGuard` compares free space on the temp volume
against `TempBytesEstimate × 1.2`. Insufficient ⇒ fail fast with a friendly message,
*before* any encoding work begins. (D5.)

**Phase 1 — normalize.** Only **non-conforming** clips are re-encoded, each to
`%Temp%\FFMedia\merge-<guid>\NNN.mkv`. **Conforming clips are referenced in place** —
zero copy, zero temp disk. Clips are independent, so they run concurrently under a
`SemaphoreSlim` capped at `min(Environment.ProcessorCount / 2, AppSettings.MaxConcurrency)`
— the same bounded-concurrency pattern as SDD §12. Per-clip progress is
`FfmpegProgress.Position / clip.Duration`.

**Phase 2 — concat.** A stream-copy mux of the (possibly mixed in-place/temp) segment
list. **If Phase 1 was empty, this is the entire merge** — roughly one second.

- **Progress** is weighted: encode ≈ 95%, concat ≈ 5%, so the bar neither stalls
  nor leaps.
- **Cancellation** — one `CancellationTokenSource` per merge; `IProcessRunner`
  already kills child processes when the token trips.
- **Temp cleanup** runs in a `finally` on **every** exit path (success, cancel,
  failure), best-effort and logged. A startup sweep removes orphaned `merge-*`
  directories older than 24 h.
- **No `IMergeManager`** (D7). The concurrency lives *inside* Phase 1.

### 6.5 The estimate

`SpeedProfile` persists at `%AppData%\FFMedia\encode-speed.json` through the
**existing `JsonStore<T>`** (atomic write + corrupt-file quarantine, SDD §10). It
holds a rolling average of measured encode throughput, keyed by
`(videoCodec, pixelBucket)`, seeded with conservative constants and updated from
ffmpeg's real `speed=` output after every merge — so the estimate **improves with use**.

```
encodeSeconds ≈ Σ(nonConforming.Duration) / speedFactor(codec, pixelBucket)
concatSeconds ≈ totalOutputBytes / copyThroughput
[LowEta, HighEta] = point estimate ± 35 %   (band narrows as samples accumulate)
```

`OutputDuration` is **exact** (Σ clip durations), because there are no transitions.

Pre-merge, the summary line reads either:

```
Output 14:32  ·  Merge ~2–4 min  ·  7 of 20 clips re-encode  ·  1.2 GB temp
Output 14:32  ·  Merge <5 s (fast path — no re-encoding needed)
```

Once merging starts, the guess is **replaced** by ffmpeg's real speed/ETA.

---

## 7. UI

`MergerPage.xaml`, sections top to bottom:

1. **Clips** — list with **drag-to-reorder** + Move Up/Down, a 🔒 lock toggle
   (showing the pinned index), a per-row conformance badge, per-row progress, and
   remove. Files are added via picker or drag-drop onto the list.
2. **Order** — `Shuffle` (honors locks).
3. **Output** — the auto-derived target, every field overridable, plus the `FitMode`
   dropdown, output folder (defaulting from `AppSettings`), and filename.
4. **Summary** — the line from §6.5.
5. **Actions** — Merge (disabled while merging) / Cancel, plus the overall progress bar.

The page root sets `Foreground="{DynamicResource TextFillColorPrimaryBrush}"`, per
SDD §13 — WPF's `Frame` isolates property-value inheritance, so the window-level
foreground never reaches page content.

`VideoMergerTool : ITool` — `Id = "video-merger"`, `DisplayName = "Video Merger"`,
`IconGlyph = "VideoClipMultiple24"`, `SortOrder = 2`. Registered by
`AddVideoMerger(IServiceCollection)`; the shell is **not modified**.

> `IconGlyph` was verified against `Wpf.Ui.dll` 4.3.0: `MergeDuplicate24` **does not
> exist**; `VideoClipMultiple24`, `Merge24`, and `VideoClip24` do. The shell already
> falls back to `Apps24` on an unparseable name (SDD §4.1).

Recomputation is cheap and synchronous: adding/removing a clip, or editing any target
field, re-runs `ConformanceCheck` + `MergeEstimator` over the in-memory list. No probe
is repeated.

---

## 8. Error handling

- `Result<T>` throughout (SDD §11). ffmpeg's stderr **tail** is captured on failure and
  mapped to friendly text (unsupported codec, no space left, corrupt/unreadable input,
  permission denied) via an extended `IErrorMapper`.
- **Unlike downloads, one failed clip fails the merge.** A merge silently missing a
  clip is wrong output, not partial output. The failing clip is named in the message,
  and the queue analogy does *not* apply.
- Probing a file that isn't a video fails at add-time, with the file rejected from the
  list rather than poisoning the merge.
- The existing global exception handler (SDD §11) covers everything else.

---

## 9. Testing (SDD §14)

**Pure unit tests** — `MergeTargetDerivation`, `ConformanceCheck`,
`NormalizeArgsBuilder` (all three fit modes; silent-clip `anullsrc` path),
`ConcatArgsBuilder` (**list-file `'` escaping** — a genuine bug source),
`Ordering.Shuffle` (**every lock honored; every clip present exactly once; seeded
determinism**), `MergeEstimator`, `SpeedProfile` rolling average, `DiskSpaceGuard`,
`FfprobeParsing`, `FfmpegProgressParsing`.

**`MergeService` against a fake `IProcessRunner`** — the fast path skips
normalization entirely; cancel mid-Phase-1 transitions to `Canceled` and kills
children; temp cleanup happens on success, cancel, **and** failure; the disk guard
rejects before any work; overall progress is monotonic.

**`MergerViewModel`** — headless, with fakes.

**Integration (trait-gated, off in CI)** — merge three real clips generated by
ffmpeg's `testsrc` at differing resolution and FPS; assert the output's probed
duration and parameters.

---

## 10. Delivery

Two PRs, per the M5/M6 precedent:

- **PR 1 — engine.** `FFMedia.Media` (analyzer, runner, both parsers) + `ffprobe`
  binary plumbing (§4) + the pure engine (§6.2) + `MergeService` with fake-runner
  tests. **No UI.**
- **PR 2 — module.** `MergerViewModel` + `MergerPage` + `ITool`/nav registration +
  history/notifications/settings wiring + the integration test.

---

## 11. Documentation changes (applied in this change, SDD → v0.13)

| Document | Change |
|---|---|
| SDD §3 Technology Stack | **FFMpegCore removed** and moved to *rejected alternatives*; media processing is ffmpeg/ffprobe via `IProcessRunner` + pure parsers (D6). |
| SDD §4 Architecture diagram | `FFMedia.Media` relabelled; `ffprobe.exe` added to the bundled-binaries box; the "(future)" merge tool becomes the M7 module. |
| SDD §5 Project Structure | Add `src/FFMedia.Tools.VideoMerger/`; `FFMedia.Media` now references `FFMedia.Core` **only** (no third-party media library). |
| SDD §7.1 | Drop the stale "FFMpegCore trim wrapper is reserved" clause. |
| SDD §8 Media Processing | Rewritten: `FFMedia.Media` realized as analyzer + runner + pure parsers. |
| SDD §9 Binary Management | `ffprobe.exe` extracted from the same pinned, hash-verified BtbN zip; `ExternalBinary.Ffprobe` added. |
| SDD §10 Data & Persistence | Add `encode-speed.json` (`SpeedProfile`). |
| SDD §13 UI / UX | Add the **Video Merger screen** (clip list + locks, shuffle, target overrides, summary line, merge/cancel). |
| SDD §16 Security & Legal | **The GPL ffmpeg build is now load-bearing** — the merger's normalize phase re-encodes with x264/x265, so the LGPL variant is no longer a drop-in alternative. `ffprobe.exe` carries the same GPL obligation. |
| SDD §17 Milestones | M7 moves from *(future)* to a defined row. |
| SDD §19 Open Questions | Record the deferrals: transitions, per-clip trim, per-clip fit mode, background music, merge queue. |
| `README.md` | Tech stack (no ffmpeg wrapper library) + M7 roadmap row. |
| `THIRD-PARTY-NOTICES.md` | `ffprobe.exe` is covered by the same GPL FFmpeg build as `ffmpeg.exe`. |
