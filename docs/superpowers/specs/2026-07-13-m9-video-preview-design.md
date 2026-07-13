# M9 — Video Preview & Frame Capture (design)

> **Status:** design · **Date:** 2026-07-13 · **Milestone:** M9

## 1. The problem

Every tool in FFMedia that asks the user for a **moment in a video** asks for it as **blind
timecode text**. The GIF Maker's Start/End, the Downloader's trim boxes — you type `1:23` and
hope. You cannot see the frame you are choosing.

This is not a new observation. SDD §19 already records it, as the reason the merger's per-clip
trim was **deferred**:

> **per-clip trim** (needs a **preview scrubber** to be usable, **not blind timecode entry**)

So the project independently concluded that blind timecode entry is the wrong interaction, and
that a preview scrubber is what unblocks it. M9 builds that scrubber.

**Goal:** the user loads a video, **watches it**, **pauses** on the frame they want, and clicks
**‹ Set Start** or **Set End ›** to capture that exact moment into the range.

## 2. Scope

**In:**

- A reusable `VideoPreview` control: play/pause, seek scrubber, frame-step, current-time readout.
- **‹ Set Start** / **Set End ›** — capture the paused position into the consuming tool's range.
- Playback of **any format ffmpeg can read**, via a proxy fallback (§4).
- A `TrimParsing` precision fix (§6) — without it, capture is *broken*, not merely imprecise.
- **First consumer: the GIF Maker.**

**Out (deferred to M10):**

- The **draggable range band** on the timeline (highlighted selection with draggable Start/End
  handles). M9 ships the two capture buttons; the band is a substantially larger custom-control
  effort and is better built on plumbing that is already proven.
- **Rolling the preview out to the Merger** (per-clip trim, SDD §19) **and the Downloader** (its
  trim boxes). M9 builds the shared capability and proves it in one tool.

**Out (not planned):** audio waveform, thumbnail filmstrip.

**Housekeeping in scope:** SDD §17's **M6 row still reads "🚧 in progress"** although v1.0.0 →
v1.1.1 have all shipped. The roadmap is lying; fix it.

## 3. Two facts, verified rather than assumed

Both were **tested against the real thing** before this spec was written, because the wrong
answer to either would have shaped the design incorrectly.

### 3.1 `MediaElement` cannot play the formats our own downloader produces

WPF's built-in `MediaElement` renders through **Windows Media Foundation**, so its codec support
is Windows', not ours. Tested by hosting a real `MediaElement` on an STA thread with a real
message loop and waiting for `MediaOpened`/`MediaFailed`:

| Source (synthesized with the bundled ffmpeg) | Result |
|---|---|
| MP4 / H.264 | ✅ `MediaOpened` — 320×240, 3 s |
| MKV / H.264 | ✅ `MediaOpened` — 320×240, 3 s |
| **WebM / VP9** | ❌ **`MediaFailed`** — *"Media file download failed."* |

**WebM is a format FFMedia's own downloader offers** (M2 shipped MP4 / MKV / **WebM**). So a user
downloads a WebM *with this app*, opens the GIF Maker, and the preview is blank. Pointing
`MediaElement` at the source and calling it done would ship exactly that.

> A first attempt at this test reported the *opposite* (MKV opening, MP4 timing out). That result
> was an artifact of a busy-wait loop starving the dispatcher — a broken harness, not evidence.
> It was thrown away and the test rebuilt with a real message loop. **Bad evidence is worse than
> none.**

### 3.2 `TrimParsing.TryParse` rejects fractional seconds in the colon form

`src/FFMedia.Core/Media/TrimParsing.cs` parses a bare number with `double.TryParse` (so `83.45`
works) but parses the **colon form with `int.TryParse` on each part** — so **`1:23.45` returns
`null`.**

This is load-bearing. A capture button that writes `1:23.45` into the Start box would produce an
**unparseable** value: `RangeHint` reports an invalid range and **Create greys out**. The feature
would be broken on arrival. (The M8 Task 6 report claimed *"`TrimParsing.TryParse` already accepts
fractional-seconds text"* — true only of the bare-seconds form, and the half that is false is
exactly the half capture needs.)

And the GIF Maker's own `FormatTime` renders `m\:ss`, **truncating to whole seconds** — so even a
parseable capture would silently lose sub-second precision, in a tool whose entire job is picking
an exact moment.

## 4. The playback engine — fast path, proxy fallback

**Point `MediaElement` at the source. If it fails, transcode a proxy and play that.**

```
LoadAsync(path)
  └─ probe (IMediaAnalyzer)         → duration, fps, dimensions
  └─ hand the SOURCE to MediaElement
       ├─ MediaOpened  → done. No transcode, no wait.            ← the common case (MP4/MKV H.264)
       └─ MediaFailed  → build a proxy with ffmpeg, play THAT.   ← VP9 / AV1 / exotic
```

This is **the merger's `ConformanceCheck` discipline, reused**: input that already conforms takes
the fast path; input that does not gets normalized. It costs nothing for the videos that already
play, and is correct for **everything ffmpeg can read**.

### 4.1 The proxy's one hard rule: it must not move the timeline

The captured timestamp is read from the *player's* `Position`. If the proxy's timeline differed
from the source's by even a little, **every captured time would be a lie** — and the GIF would be
cut somewhere other than where the user saw.

So the proxy **rescales only**. It never re-times:

- Scale down to a preview size (long edge ≈ 640 px), preserving aspect.
- **Keep the source's frame rate and duration.** No `-r`, no `-ss`, no `-t`, no filter that drops
  or duplicates frames.
- H.264 / `yuv420p` in MP4 — a format `MediaElement` is now *proven* to open (§3.1).
- Encode fast (`-preset ultrafast`), because this is a disposable preview, not a deliverable.
- Keep audio (transcoded to AAC): the user is scrubbing to *find a moment*, and sound is often how
  a human finds it.

An **integration test against real ffmpeg pins the invariant**: a WebM/VP9 source becomes a proxy
that (a) `MediaElement` can open and (b) has a duration **equal to the source's** within a frame.

### 4.2 Proxy lifecycle

- Written under the app's temp root (the same root the merger and GIF Maker already use).
- **Cached per source** (keyed by full path + last-write-time + size), so re-opening the same video
  does not re-transcode it.
- Generation is **async, cancellable, and reports progress** — a long source can take a while, and a
  frozen UI with no explanation is not acceptable. The user can cancel and fall back to typing a
  timecode.
- **Swept on startup**, like the merger's temp directories. A hard kill must not leak proxies
  forever.
- If proxy generation **fails**, the preview degrades to a clear message and the timecode boxes
  remain fully usable. **The preview is an aid; it must never become a gate.**

## 5. The control

### 5.1 `FFMedia.Ui` — a new shared layer

The preview must be shared: the GIF Maker needs it now, the Merger and Downloader need it in M10.
But **a tool module must never reference another tool** (SDD §5), and it cannot live in
`FFMedia.App` either — tools cannot depend on the WinExe. There is no shared *UI* layer today.

So M9 adds one:

| Project | Holds |
|---|---|
| **`FFMedia.Ui`** *(new — net9.0-windows, UseWPF)* | The `VideoPreview` control + its headless `VideoPreviewViewModel`. |
| `FFMedia.Media` *(existing)* | `IPreviewProxyService` / `PreviewProxyService` — proxy generation, beside `IFfmpegRunner`/`IMediaAnalyzer`, where the ffmpeg work already lives. |

Tools reference `FFMedia.Ui` exactly as they reference Core and Media. Building it shared *now* is
the whole point — it means M10 adopts it rather than performing a promotion task (the way
`Resolution` and `TrimParsing` had to be promoted in M8).

### 5.2 `VideoPreviewViewModel` — headless, so it is testable

`MediaElement` cannot be driven in a headless test. So the ViewModel owns **all** the logic and
talks to the player through a narrow interface (`IMediaPlayer`: `Source`, `Position`, `Play`,
`Pause`, `Duration`, `Opened`/`Failed`), which the control implements over a real `MediaElement`
and the tests implement with a fake.

Surface (indicative):

- `LoadAsync(path)` · `Play()` · `Pause()` · `StepForward()` / `StepBack()` (one frame, from the
  probed fps) · `Position` · `Duration` · `IsPlaying` · `IsPreparingProxy` / `ProxyPercent` ·
  `StatusMessage`
- `CaptureStartCommand` / `CaptureEndCommand`

### 5.3 Capture semantics

Capture reads the player's current `Position` and hands it to the consuming tool. In the GIF Maker
it writes the tool's **existing** `StartText`/`EndText` — so the live size estimate, `RangeHint`
and `CanCreate` all recompute **for free**, through the code paths M8 already built and pinned.

- Capturing a Start **after** the current End (or an End **before** Start) is **refused with an
  explanation**, not silently swallowed and not silently reordered. *A disabled or no-op control
  with no explanation is a dead end* (SDD §13).
- **The capture buttons freeze while a GIF is rendering.** The render holds a **snapshot** of the
  request; a page that can still mutate Start/End describes a job that is not the one running.
  This exact bug shipped **twice** in M8 (`LoadVideoAsync` had no freeze guard; the history row's
  `Title` read a live property). Capture is gated by the same `CanEditParameters` — **and guarded
  in the command itself**, because *a gesture that is not a command bypasses `CanExecute` entirely*.

## 6. Precision — a Core fix

`FFMedia.Core.Media.TrimParsing`:

- **`TryParse` accepts fractional seconds in the colon form** — `1:23.45`, `0:05.5`,
  `1:02:03.25` — alongside everything it already accepts. Existing behaviour is unchanged;
  this is purely additive, and the existing tests must all still pass untouched.
- **A matching `Format(TimeSpan)`** renders a timestamp *round-trippably*, with sub-second
  precision when it is non-zero (`1:23.45`) and without when it is not (`1:23`) — so a hand-typed
  `1:23` still looks like `1:23` after a round trip, and a captured moment keeps its fraction.

**Pinned round-trip:** `Format` → `TryParse` → the same instant, to the millisecond. Without this,
capture is either unparseable (§3.2) or silently truncated by up to a second.

`InvariantCulture` throughout — a `,` decimal separator on a German locale must not change what
parses (the same trap `GifArgsBuilder` already guards).

## 7. Error handling

| Situation | Behaviour |
|---|---|
| Source unreadable | The **analyzer's own reason**, never a generic "not a video". (M7 blamed a user's perfectly good `.mp4` for a *missing ffprobe*; M8's `GifErrors` blamed it for a *missing output folder*. Twice is enough.) |
| `MediaElement` fails on the source | Silent, expected — build the proxy. Not an error the user should ever see. |
| Proxy generation fails | Explain it; keep the timecode boxes usable. The preview is an aid, never a gate. |
| Proxy cancelled | Same — no debris, no half-written proxy. |
| Capture would invert the range | Refused, with a hint saying why. |

## 8. Testing

| Layer | How |
|---|---|
| `TrimParsing` | Pure. Round-trip `Format`→`TryParse`; the existing suite must pass **unchanged**. |
| `PreviewProxyService` | Unit-tested with a fake `IFfmpegRunner`; **plus a real-ffmpeg integration test** proving a **WebM/VP9** source yields a proxy `MediaElement` can open, with a duration equal to the source's within one frame. |
| `VideoPreviewViewModel` | Headless, with a fake `IMediaPlayer` — including the **fallback path** (player fails → proxy is built → the proxy is played), which is the whole design and must not be provable only by hand. |
| `VideoPreview` control | Page-load test on the WPF `wpf` collection: real resource dictionaries, no nested `ScrollViewer`, no unverified `StaticResource` key (all three have shipped as bugs here). Tooltips covered by `TooltipCoverageTests`. |
| Capture ↔ GIF Maker | The captured position lands in `StartText`/`EndText` and the estimate/`RangeHint`/`CanCreate` recompute; capture is **refused while rendering**. |

**A test only pins an invariant if the fixture varies along the axis the invariant is about.** The
proxy tests must use a source `MediaElement` genuinely *cannot* play — a fixture that uses an MP4
proves nothing about the fallback, because the fast path would carry it.

## 9. Not verified by this spec

- **How it feels.** Whether the scrubber is precise enough to land on a frame by hand, and whether
  frame-step is fast enough to be usable, are judgements only a human at the running app can make.
  This environment is headless.
- **Proxy transcode time on a long source.** A 3 s clip is not a 90-minute one. The progress +
  cancel affordance exists precisely because that number is unknown; it should be measured during
  implementation and the preview size tuned if it is bad.

## 10. Deferred

- The **draggable range band** with Start/End handles (M10).
- Preview in the **Merger** (unblocks per-clip trim, SDD §19) and the **Downloader** (M10).
- Audio waveform; thumbnail filmstrip.
