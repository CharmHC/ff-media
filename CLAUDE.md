# CLAUDE.md — Working Rules & Progress Log for FFMedia

## Project

FFMedia is an all-in-one **Windows media toolbox** (C# / .NET 9, WPF + WPF-UI).
The v1 tool is a **YouTube Downloader** orchestrating **yt-dlp** (download) +
**ffmpeg** (transcode/mux). Architecture is modular — an app shell hosts pluggable
`ITool` modules.

**[`SDD.md`](SDD.md) is the single source of truth** for architecture, scope, and
milestones. Read it before making design decisions.

---

## 🔴 Standing Rules (always follow)

1. **Keep [`SDD.md`](SDD.md) up to date.** Whenever a design decision, scope,
   convention, dependency, or milestone changes — update `SDD.md` in the same
   change, and bump its version + Changelog entry. The SDD must never lag reality.
2. **Record progress after every task** in the [Progress Log](#-progress-log)
   below. Append a dated entry describing what was done, what changed, and what's
   next. Newest entries go at the top.
3. **Branch per task; deliver via PR.** Never commit task work directly to `main`.
   For each task, branch off the latest `main` (e.g. `feat/…`, `fix/…`, `docs/…`),
   commit there, push, and open a **PR for the user to review**. Do not merge — the
   user reviews and merges.
4. When these rules conflict with anything else, these rules win unless the user
   says otherwise.

---

---

## ▶️ RESUME HERE (next session)

**M9 is COMPLETE and delivered by PR. Nothing is mid-flight.** The branch `feat/m9-video-preview` is
pushed with all 7 tasks done, reviewed, and green; **the user reviews and merges it** (Rule 3).

### The one thing waiting on a human

**Nobody has ever clicked through the preview.** This environment is headless, so a real playing
`MediaElement` cannot be driven at all. Specifically unproven — and each of these is *invisible to the
entire suite*:

- Does the **readout and slider visibly advance** while a real video plays?
- After navigating away from the GIF Maker and back, does the **restored frame actually come back**?
  (`MediaElement.Position` does not move without a Media Foundation session.)
- Does a preview left **playing** actually **stop** when you navigate away? (`MediaElement.Close()` has
  **no observable public effect** headlessly — it does **not** reset `Source`; I assumed it did, and was
  wrong. No test was written rather than one that could not fail.)
- The **GIF Maker page layout / dark mode / tooltips**. Its `DynamicResource` lookups **fail silently**
  on a typo (unlike `StaticResource`, which throws at load).

### Next milestone: M10

Rollout to the **Merger** (per-clip trim) and **Downloader**, plus the **draggable range band**. Before
adding a second preview, read the M9 minors roll-up in
[`.superpowers/sdd/progress.md`](.superpowers/sdd/progress.md) — **two of them are M10 blockers**:

1. **The lifetimes do not survive a second preview.** One singleton `MediaElementPlayer` + one singleton
   `VideoPreviewViewModel` shared by a Merger preview *and* a GIF Maker preview means two `VideoPreview`
   controls fighting over one `MediaElement` — **last `Attach` wins**. M9 is single-preview, so this is
   latent. **M10 must fix the lifetimes before it adds the second one.**
2. **`DownloaderViewModel` still formats trims with `.ToString(@"hh\:mm\:ss")`** — the *identical*
   24-hours-vanish bug Task 1 exists to retire (`hh` is the hours **component**, not total hours), still
   live, and it truncates sub-second precision. **Replace it with `TrimParsing.Format`.**

Also note the **draggable range band is a gesture, not a command** — so it **bypasses `CanExecute`
entirely**. The capture handlers already carry their own `IsRendering` body guards for exactly this.
That bug shipped **twice** in M8; do not make it three.

### Facts already settled — do not re-derive them

1. **WPF's `MediaElement` cannot play VP9/WebM** (verified against real files; MP4/MKV H.264 both open).
   It renders through Windows Media Foundation, so its codec support is *Windows'*, not ours — **and WebM
   is a format our own downloader produces.** Hence *fast path + ffmpeg proxy fallback*.
2. **The proxy rescales only. It NEVER re-times.** No `-r`, `-ss`, `-t`, `-vsync`, no frame-dropping
   filter. The captured timestamp is read from the *player's* position, so a proxy whose timeline drifted
   would make **every captured time a lie**.
3. **`TrimParsing` (Task 1) now parses and formats the fractional colon form** (`1:23.45`) and round-trips.
   The `Format`/`TryParse` pair is the single formatter — the GIF Maker's private `FormatTime` is **gone**,
   and with it a latent bug where `TimeSpan`'s `h` specifier rendered *hours-of-day*, silently vanishing
   24 hours on any 24h+ VOD.
4. **`MediaElement.ScrubbingEnabled` is load-bearing.** Without it, seeking while paused does not render
   the new frame — the user would capture a timestamp for **a frame they never saw**.
5. **A gesture that is not a command bypasses `CanExecute` entirely.** Guard in *both* the `CanExecute`
   and the method body. That bug shipped **twice** in M8.

### Deferred M8 minors, logged for triage

The progress bar stalls during the palette pass (palettegen emits one `out_time` line); `_profiles.Load()`
runs a sync JSON read **per keystroke**; nothing enforces a `.gif` extension on the output name; no sweeper
for stranded `palette-*.png`; spec §6.1's disk-space guard was silently dropped; `GifErrors`' stderr strings
are unverified against real ffmpeg.

---

## 📓 Progress Log

_Newest first. One entry per completed task/session._

### 2026-07-14 — M9 complete: pause the video, click Set Start, and the frame you're looking at lands in the box

- **Done:** M9's last three gates — the **Task 5 review** (the one the previous session could not run), Task 6
  (a **real-ffmpeg** proof from a real **VP9/WebM** source), and Task 7 (docs). FFMedia's tools no longer ask
  you to guess a timecode: **play, pause on the frame you want, click Set Start / Set End.** The GIF Maker is
  the first consumer; M10 rolls it out to the Merger and Downloader, which is why the control lives in a **new
  shared `FFMedia.Ui` layer** rather than inside the tool (a tool must never reference another tool, and it
  cannot reference the WinExe either — so a shared *UI* layer was the only correct home).
- **Why `MediaElement` is not enough, and why the proxy is the whole design.** WPF's `MediaElement` renders
  through **Windows Media Foundation**, so its codec support is *Windows'*, not ours — it **cannot play
  VP9/WebM**, and **WebM is a format our own downloader produces**. Pointing it at the source and calling it
  done would show a **blank preview for videos FFMedia itself made**. So: play the source; if it refuses,
  transcode a small H.264 **proxy** and play that. The merger's `ConformanceCheck` discipline reused. **The
  proxy rescales but NEVER re-times** — the captured timestamp is read from the *player's* position, so a
  proxy whose timeline drifted would make **every captured time a lie**, and the GIF would be cut somewhere
  other than where the user saw.
- **The review is the gate. Not the green suite. This session is the proof.** Task 5 was committed, green, and
  **never independently reviewed** — the previous session verified it itself and said plainly that *a
  controller's spot-check is not the gate*. It was right. The review found a **Critical that no single task
  could have seen, because it lives in the composition of their DI lifetimes**: `MediaElementPlayer` is a
  **singleton**, `VideoPreview` is **transient**. So navigating to Settings and back built a fresh control → a
  fresh `MediaElement` → `Attach()` on the *same* singleton player, whose remembered source had already been
  consumed. The new element had **no `Source`**. The preview went **black — while the ViewModel still reported
  `IsReady`**, leaving the transport and capture buttons **armed over a dead player**. A click then read
  `Position` from the empty element and wrote **`0:00`** into Start. *A captured timestamp for a frame the user
  never saw* — the exact failure the entire no-retiming discipline exists to prevent, arrived at from the other
  side. **795 tests were green.** That is now the third milestone running where every task passed its own
  review and the *composition* was still broken (M7: the clip list stayed editable during a merge; M8: the GIF
  Maker deleted the user's finished GIF).
- **The same review found the sweeper was never wired.** Task 2 *built and tested* `SweepStale()`; Task 5 — the
  task that wires the service up — did not call it. Every fallback transcode leaked a proxy into `%Temp%`
  **forever**. *A tested API with no caller is not a feature; it is dead code with a passing test.*
- **A test that could not fail, one level up: the instrument was wrong, not the fixture.** The plan's keystone
  integration test — the one pinning *the proxy never re-times*, the rule the milestone hinges on — compared
  **durations**. The reviewer ran the rule's own forbidden operations against **real ffmpeg** instead of
  reasoning about them, and found an **`fps=12` filter throws away HALF THE FRAMES while leaving the duration
  bit-identical** (4.000000 s either way). A duration check is **structurally incapable** of failing on it —
  and `fps=` inside the `-vf` value is *precisely* how a future *"let's also normalize the preview frame rate"*
  edit would arrive. `-r 24` and `-itsscale 1.02` also walked straight through the 0.2 s tolerance: **2.4×
  too loose to catch the flag the rule is named after.** Worse, the tolerance's own demonstration mutant failed
  by **1.8 × 10⁻¹⁶** of floating-point margin — *a coincidence, not a proof.* The **frame rate must now survive
  the transcode unchanged**, which kills every `fps=`/`-r` mutant whatever it does to the duration. **The
  fixture lesson has a sibling: a fixture can vary correctly along the right axis and still prove nothing, if
  the instrument does not measure that axis.**
- **And the plan's own fixture could not fail either** — caught before dispatch. It synthesized a **640×360**
  source and asserted the proxy's `Width <= 640`. But the cap **is** 640, so the assertion passed **even with
  the scale filter deleted entirely**. The source is now **wider than the cap** (1280×720) and the *exact*
  output dimensions are asserted. *An inequality a no-op satisfies is not an assertion.*
- **The zero-warning gate had been passing without ever compiling the project the warnings were in.** A
  **clean** (`--no-incremental`) build emits **3 `CS0067`s** — in M9's own preview test stubs. Every "0
  warnings" claim on this branch, mine included, came from an **incremental** build that skipped recompiling
  `FFMedia.Tests`. **Always build `--no-incremental` when claiming the gate.** *A green check that skipped the
  thing it was checking is not a green check.*
- **I was wrong about a library again, and the test told me so.** I "fixed" a discarded `MediaElement` playing
  on after navigation by calling `Close()`, and wrote a test asserting it clears `Source`. **It does not** —
  the element still reports its old `Uri`. The fix is still right (it stops the audio, and releases the file
  handle an abandoned element would otherwise hold **against `SweepStale`**), but it has **no observable public
  effect headlessly**, so I deleted the test rather than ship one that passes either way. *Do not reason about
  what a library "surely" does — and when you cannot prove a fix, say so instead of writing a test that cannot
  fail.*
- **The final whole-branch review found a Critical, for the third milestone running — and it was in the
  FORMATTER, not the video code.** `TrimParsing.Format` built the whole part by **truncation** and the
  fraction by **rounding**, so any fraction ≥ 0.9995 rounded up to a bare `1` that was **glued onto the
  truncated seconds** — the carry never reached them. **`Format(1.9996s)` emitted `"0:011"`, which parses
  back as ELEVEN SECONDS.** A user who paused at 1.9996 s and clicked **Set Start** got a GIF cut from
  **11 s** — silently, with the range still valid and Create still enabled. Neighbouring values (`"0:301"`,
  `"0:591"`) are **unparseable outright**, so a probed source duration landing in that band greys Create out
  on a **perfectly good freshly-loaded video** and blames the user's end time. **Both** values M9 newly
  feeds through `Format` — the player's **live position** and ffprobe's **probed duration** — are arbitrary
  *machine-produced* sub-second numbers, so the band is hit about **one capture in 2500**, not never.
  **Task 1 tested `Format` only against values a HUMAN types** (`.45`, `.5`, `.25`) and its round-trip
  theory stopped at `0.999` — **one step short of the 0.9995 carry boundary**. Task 5 is what first piped a
  machine value through it. *The fixture must vary along the axis the invariant is about* — **the fifth time
  on this project**, and this time the axis was "a value nobody would ever type."
- **Three more from the same review, each a composition the tasks could not see alone.** (1) **A killed
  proxy transcode poisoned that video's preview for seven days.** The transcode wrote **straight to its
  permanent cache key**, and the cache check is only *exists && non-empty* — so quitting the app while
  *"Preparing a preview…"* is on screen left a half-written file that is a **cache hit forever**
  (`+faststart` writes the moov atom **last**, so it is unplayable). The fallback is **dead for that file**,
  with no recovery. **This was the one place on the branch not applying the project's own hardest-won rule** —
  `GifService` and `MergeService` both write to a **sibling**, verify, and only then move it into place. It
  now does too. (2) **A preview left playing kept playing after you navigated away** — `UnloadedBehavior` is
  `Manual`, so nothing stops it; my earlier "fix" for this landed in `Attach`, which is only on the *return*
  trip. **I shipped that one deliberately untested because `Close()` has no headless observable — and the
  half-fix went straight through.** (3) Reaching the end of a video left `IsPlaying` **true forever**, so the
  200 ms poll timer never stopped.
- **Real ffmpeg caught a trap inside one of the fixes, and no fake could have.** The proxy's sibling was
  first named `preview-<hash>.mp4.part` — every unit test passed, and the integration test failed:
  *"Unable to choose an output format for '…mp4.part'"*. **ffmpeg picks its muxer from the output
  extension** — the exact constraint `GifService`/`MergeService` already state for *their* siblings. It is
  now `preview-<hash>.part.mp4`.
- **Verified:** clean Release build **0 warnings / 0 errors**; **822/822** unit tests; **12/12** integration
  tests against real ffmpeg — a real **VP9/WebM** source yields a playable **h264** proxy at exactly
  **640×360** whose **duration *and* frame rate both match the source**.
- **NOT verified — a human has not clicked through the preview.** Headless environment; a real playing
  `MediaElement` cannot be driven. Unproven: whether the readout/slider **visibly advance**, whether the
  **restored frame comes back** after a page revisit, and whether a preview left **playing** actually **stops**
  when you navigate away. See **▶️ RESUME HERE**.
- **Next:** user reviews and merges the PR, then clicks through. SDD → **v0.27**, M9 ✅. Then **M10** — and
  read the minors roll-up first: **the singleton lifetimes will not survive a second preview** (last `Attach`
  wins), and the **Downloader still carries the 24-hour-vanishing `hh` format bug** Task 1 exists to retire.

### 2026-07-13 — M9 Tasks 1–5: the preview exists, and capture now feeds the GIF Maker

- **Done:** five of M9's seven tasks, on `feat/m9-video-preview` (HEAD `38f4596`). (1) `TrimParsing`
  gained the fractional colon form + a round-trippable `Format`. (2) The **preview proxy** in
  `FFMedia.Media` — pure args, a cache key, the service, a sweeper. (3) The new **`FFMedia.Ui`** project,
  the `IMediaPlayer` seam, and the headless `VideoPreviewViewModel` **including the proxy fallback, which
  is the whole design**. (4) The `VideoPreview` control + `MediaElementPlayer`. (5) Wiring it into the GIF
  Maker: **pause on a frame, click Set Start / Set End, and the paused frame's timestamp lands in the
  box.** Blind timecode entry is over — for the GIF Maker; M10 rolls it out to the Merger and Downloader.
- **The bug each review caught, and why the review is the gate — not the green suite.** Task 3 shipped a
  **UI-thread hang**: the pending open lived in *one shared field* while the player's handlers were wired
  once in the constructor, so loading a second video before the first opened left **the first caller's
  task never completing**. Task 4 shipped a readout **frozen at `0:00` forever**: `PositionText` never
  raised `PropertyChanged`, and **WPF only refreshes a binding whose exact path was notified** — capture
  still read the player's live position, so the *values* were right and the control merely **looked broken
  and told the user nothing**. Both suites were green. **Neither bug was findable from a passing test.**
- **Fixtures that could not fail, twice more.** Task 2's failure-path test passed **whether or not the
  cleanup ran** (the fake only wrote its output on *success*, so there was never a half-written file to
  clean up). Task 3's frame-step test ran every fixture at **25 fps** — where 1/fps is *exactly* the 40 ms
  a hardcoded mutant returns. *A test only pins an invariant if the fixture varies along the axis the
  invariant is about.* That sentence has now earned its place in this log four milestones running.
- **The no-retiming rule was pinned only by a list of flag names** — and a future *"let's also normalize
  the frame rate"* edit would express itself as **`fps=` inside the `-vf` value** and walk straight past
  every `DoesNotContain`. The filter *value* is now checked. This matters more than it looks: the captured
  timestamp is read from the **player's** position, so a proxy whose timeline drifted from the source's by
  even a little would make **every captured time a lie**, and the GIF would be cut somewhere other than
  where the user saw.
- **A real bug in my own plan's code, found by the reviewer running it.** `TimeSpan`'s custom **`h`
  specifier renders hours-of-day (0–23), not total hours** — so `Format(25h)` emitted `"1:00:00"`, which
  parses back as **one hour: twenty-four hours vanished**, no exception, no failing test. Reachable from
  the Downloader's trim on any 24h+ VOD. **The GIF Maker's private `FormatTime` had the identical bug**;
  Task 5 deletes it in favour of `TrimParsing.Format`, retiring both at once. *A plan is not evidence.*
- **Capture refuses to invert the range, and says so.** A moment at or after the current End is **not**
  silently swallowed and **not** silently reordered — it explains itself in `RangeHint`. *A control that
  does nothing and says nothing is a dead end.* And capture **freezes while a GIF renders** (the render
  holds a *snapshot* of the request), gated **both** by `CanExecute` **and** in the method body — because
  **a gesture that is not a command bypasses `CanExecute` entirely.** That bug shipped twice in M8.
- **Verified:** Release build **0 warnings / 0 errors**; **795/795** unit tests; 11 integration tests
  untouched. **Not verified — and this is the handoff's most important line: Task 5 has had NO
  independent review.** Its implementer finished but the session ended before the reviewer ran; I checked
  the work myself and committed it so it could not be lost. **A controller's spot-check is not the gate.**
- **Next:** **review Task 5** (`724f679..38f4596`), then Task 6 (a real-ffmpeg test proving a **VP9/WebM**
  source yields a proxy of identical duration), then Task 7 (docs, SDD → **v0.27**), then the final
  whole-branch review → PR. See **▶️ RESUME HERE**.

### 2026-07-13 — M9: implementation plan (no code)

- **Done:** wrote [`docs/superpowers/plans/2026-07-13-m9-video-preview.md`](docs/superpowers/plans/2026-07-13-m9-video-preview.md)
  — **7 TDD tasks**, each with the actual code and the actual test bodies: (1) `TrimParsing` gains a
  fractional colon form + a round-trippable `Format`; (2) the **preview proxy** in `FFMedia.Media`
  (pure args + cache key + service + sweeper); (3) the new **`FFMedia.Ui`** project, the `IMediaPlayer`
  seam, and the **headless** `VideoPreviewViewModel` (including the fallback, which is the whole design);
  (4) the `VideoPreview` control + `MediaElementPlayer`; (5) wiring it into the GIF Maker; (6) a
  **real-ffmpeg** integration test from a **real VP9 source**; (7) docs + PR.
- **Written to be picked up cold.** Opens with a **🚦 START HERE** section — repo state, the standing
  rules, the verification gate, and every exact signature it builds against.
- **The seam that makes it testable:** `MediaElement` cannot be driven headlessly, and the behaviour that
  most needs a test — *the source fails, so fall back to a proxy* — cannot be triggered on demand with a
  real one. So the VM talks to **`IMediaPlayer`**, and the tests supply a fake that **refuses a nominated
  path** on cue. The real `MediaElement` lives behind exactly one adapter.
- **Facts pre-verified so the next agent inherits no guesses:** `FFMedia.Media` targets **plain `net9.0`**
  (no WPF, and `TreatWarningsAsErrors`), which is *why* the control needs its own layer; the
  `SymbolRegular` names `Play24`/`Pause24`/`ChevronLeft24`/`ChevronRight24` all **exist**; and
  `MediaElement.ScrubbingEnabled` is **load-bearing** — without it, seeking while paused does not render
  the new frame, so the user would capture a timestamp for **a frame they never saw**.
- **A trap the plan pins with its own test:** the proxy's scale filter contains `min(640,iw)`, and a
  **bare comma inside it would SPLIT THE FILTERGRAPH** (ffmpeg separates filters with commas), turning the
  whole `-vf` argument into garbage. It must be escaped (`\,`).
- **Two of my own tools nearly handed me false facts, and both were caught by distrusting them.** A
  `MediaElement` codec probe first reported the *opposite* result (MKV opening, MP4 failing) because a
  busy-wait loop starved the dispatcher; and an icon check reported every name **MISSING** because
  `strings` was not installed. **A tool that is broken does not report that it is broken — it reports a
  wrong answer.** Both were re-run against the authoritative source (a real message loop; `Enum.TryParse`).
- **Verified:** nothing to build — **documentation only, no code touched**.
- **Next:** execute the plan (`superpowers:subagent-driven-development`). Delivered via branch
  `docs/m9-plan` → PR.

### 2026-07-13 — M9 designed: Video Preview & Frame Capture (spec only, no code)

- **Done:** specced **M9** → [`docs/superpowers/specs/2026-07-13-m9-video-preview-design.md`](docs/superpowers/specs/2026-07-13-m9-video-preview-design.md).
  The user asked to **preview the video, pause it, and capture the current frame's timestamp** as
  Start or End. Every tool here asks for a moment in a video as **blind timecode text** — you type
  `1:23` and hope. **§19 had already reached the same conclusion from the other side**, recording it
  as the reason the merger's per-clip trim was deferred: *"needs a preview scrubber to be usable, not
  blind timecode entry."* So this is a **shared** capability, not a GIF Maker feature — new
  **`FFMedia.Ui`** layer (a tool must never reference another tool, and cannot reference the WinExe
  either). Building it shared **now** means M10 *adopts* it instead of doing a promotion task, the way
  `Resolution`/`TrimParsing` had to in M8.
- **The finding that decides the whole design — and I tested it instead of assuming.** WPF's
  `MediaElement` renders through **Windows Media Foundation**, so its codec support is *Windows'*, not
  ours. Hosted a real `MediaElement` on an STA thread with a real message loop: **MP4/H.264 ✅,
  MKV/H.264 ✅, WebM/VP9 ❌ `MediaFailed`.** **WebM is a format our own downloader produces** (M2) — so
  pointing `MediaElement` at the source and calling it done would show a **blank preview for videos
  FFMedia itself made.** Hence **fast path + proxy fallback**: play the source; if it fails, transcode
  a small H.264 proxy and play that. That is the merger's **`ConformanceCheck` discipline reused** —
  conforming input takes the fast path, non-conforming gets normalized — costing nothing for videos
  that already play, and correct for everything ffmpeg can read.
- **My first run of that test said the opposite** (MKV opened, MP4 timed out) because a busy-wait loop
  starved the dispatcher. That was a **broken harness, not evidence**, and I threw it away and rebuilt
  it with a real message loop. **Bad evidence is worse than none** — it would have "proven" a false
  premise and shaped the whole milestone around it.
- **The proxy's one hard rule: it rescales, it never re-times.** The captured timestamp is read from
  the *player's* position, so a proxy whose timeline drifted from the source's by even a little would
  make **every captured time a lie** — the GIF cut somewhere other than where the user saw. No `-r`,
  no `-ss`, no `-t`, no frame-dropping filter. An integration test pins it: a WebM/VP9 source must
  yield a proxy whose duration equals the source's within a frame.
- **The second finding — the feature would have been broken on arrival.**
  **`TrimParsing.TryParse` rejects `1:23.45`**: the bare-seconds form goes through `double.TryParse`
  (so `83.45` works), but the **colon form parses each part with `int.TryParse`**. So a capture button
  writing `1:23.45` into the Start box produces an **unparseable** value → the range goes invalid →
  **Create greys out.** (M8's Task 6 report claimed *"`TryParse` already accepts fractional-seconds
  text"* — true only of the bare form, and the half that is false is exactly the half capture needs.)
  And the GIF Maker's `FormatTime` renders `m\:ss`, **truncating to whole seconds** — so even a
  parseable capture would silently lose up to a second, in a tool whose entire job is picking an exact
  moment. `TryParse` gains fractional support in the colon form, plus a round-trippable `Format`.
- **Applied M8's lesson without being asked:** the capture buttons **freeze while a GIF is rendering**
  (the render holds a *snapshot*), gated **both** by `CanExecute` **and** inside the command — because
  **a gesture that is not a command bypasses `CanExecute` entirely.** That bug shipped **twice** in M8.
- **Scope cut, at the user's call:** the **draggable range band** (a real custom control) moves to
  **M10**, along with rollout to the Merger and Downloader. M9 ships the two capture buttons on
  plumbing that M10 can then build on.
- **Also:** §17's **M6 row still said "🚧 in progress"** — through **four shipped releases** (v1.0.0 →
  v1.1.1). Corrected. *A roadmap that lags reality is not a stale note; it is a false claim.*
- **Verified:** the `MediaElement` codec matrix (against real synthesized files) and the `TrimParsing`
  gap (by reading the code). **No build/tests run — documentation only, no code touched.**
- **Next:** user reviews the spec → `writing-plans`. SDD → **v0.26**. Delivered via branch
  `docs/m9-video-preview` → PR.

### 2026-07-13 — M8 whole-branch review: the merger's data-loss bug, un-relearned

- **The bug worth remembering — every task passed its own review, and the composition was still
  destructive.** `GifService` wrote the render **straight to `request.OutputPath`** and deleted that path
  on cancel, render-failure and verify-failure — and `FfmpegRunner` prepends **`-y`**, so the render also
  **truncated whatever was already there** the instant it opened it. Harmless in a vacuum; catastrophic
  given what this tool *is*. The GIF Maker's whole workflow is **iterative**: load once, tune,
  **re-render to the same filename**, look, tune again. So the ordinary path — *make a GIF → it's 8 MB,
  too big to send → drop the size → re-render → hit **Cancel** because it's slow* — **deleted the good
  GIF from sixty seconds earlier, leaving the user with neither.** Cancelling during the *palette* pass
  destroyed a file ffmpeg had **never even opened**.
- **Why no per-task review could catch it, which is the whole argument for the final one.** Task 5 built
  `GifService` against fakes; Tasks 6–7 built the UI that makes same-filename re-rendering the *default
  gesture*. Neither task alone ever shows you a user who **already has** a `clip.gif`. This is the second
  milestone running where each task was individually correct and the *whole* was broken (M7: the clip
  list stayed editable during a merge).
- **And the suite was green because it pinned the bug as correct.** `GifServiceTests` asserted the
  destination was **gone** after a cancel — and **no test ever placed a pre-existing file there**, so
  nothing could distinguish *"cleaned up its own half-written GIF"* (the intent) from *"deleted the
  user's finished GIF"* (the behaviour). Three tests were **inverted**: they now put a real GIF at the
  destination and assert it **survives byte-for-byte**. *A test that encodes the bug is worse than no
  test — it actively defends it.*
- **The fix is the merger's, which was sitting one project over the whole time.** Render to a **sibling**
  (same directory, so `File.Move` is a free rename; same `.gif` extension, since ffmpeg picks its muxer
  from it), verify **that**, and only move a proven-whole GIF into place. **Nothing the user already had
  is touched until we have something worth replacing it with.** CLAUDE.md has recorded that sentence
  since M7. It did not stop us doing it again — *a lesson written down is not a lesson applied.*
- **Three more the composition hid:**
  1. **The re-probe only half-existed.** `VerifyAsync` checked exists / non-empty / readable / has a video
     stream — but **never the duration**. So a GIF ffmpeg wrote **short** (a filtergraph that dies after
     the first frame, a truncated write) probed clean and was reported a **success**. The re-probe proved
     *"a file came out"*, not *"the file I asked for came out"* — and *"ffmpeg exits 0 having silently
     produced less than you asked for"* is **exactly** the failure this project already shipped via the
     concat demuxer. The tolerance is **proportional** (15 %, floor 0.5 s, clamped to half the request),
     because a false positive here **deletes a healthy GIF** — the trap the merger's flat 1 s tolerance
     hit.
  2. **`SelectedSize` was a non-nullable `Resolution`** — a `record`, therefore a **reference type** —
     two-way bound to a `ComboBox.SelectedItem`. **WPF writes `null` through that binding while
     `ItemsSource` rebuilds**, so loading a **second** video threw a `NullReferenceException` inside the
     estimator, on the UI thread, **silently swallowed by the binding engine**. Invisible only because
     **no test ever loaded two videos**. This is verbatim the M7 lesson (*"the null a ComboBox pushes
     while its ItemsSource is being rebuilt"*) — and `MergerViewModel` already had the nullable
     projection that fixes it.
  3. **A missing output folder was reported as *"The video could not be found."*** Nothing created the
     destination directory, so ffmpeg failed with *"No such file or directory"*, which `GifErrors` matched
     on its **first** rule — **blaming the user's perfectly good `.mp4` for a typo'd destination folder.**
     Word for word the M7 mistake (blaming a good mp4 for a missing binary). Also: the final `File.Move`
     was **unguarded**, so a destination held open by another process — *the user is very likely looking
     at the GIF they just made* — orphaned the pending render **in the user's own output folder**.
- **Verified:** Release build **0 warnings / 0 errors**; **730/730** unit tests; **11/11** integration
  tests against real ffmpeg (the 4 merge tests unaffected). Every fix is **mutation-proven** — reverting
  it fails the test that claims to catch it.
- **Not verified:** **a human has not clicked through the page.** This environment is headless. Worth
  knowing for that click-through: the page's two **`DynamicResource`** lookups **fail silently** on a
  typo (unlike `StaticResource`, which throws at load), so no test covers them.
- **Next:** user reviews PR. SDD → **v0.25**.

### 2026-07-13 — M8 GIF Maker: real-ffmpeg proof + doc sync — **M8 complete**

- **Done:** FFMedia's **third tool**, `FFMedia.Tools.GifMaker`, is implemented end to end (Tasks 1–7 of
  the plan) and this task adds the last piece: a real-ffmpeg integration test, plus the docs. **The
  shell was not modified** — the tool registers `ITool`/`IToolPage` and the shell discovers it, the same
  modular seam M7 proved, now proven a third time in a row. Everything up to this task was proven with
  fakes; `GifIntegrationTests.CreateAsync_MakesARealGif_OfTheRightSizeAndLength` synthesizes a real
  1280×720/30fps/6s clip with ffmpeg's own `testsrc`, runs it through the real `GifService` (both real
  ffmpeg passes, real `FfprobeMediaAnalyzer`), and — because ffmpeg's exit code is exactly what cannot be
  trusted — **probes the output** rather than trusting the `Result`.
- **The two-pass decision, and why it's non-negotiable.** The obvious `ffmpeg -i in.mp4 out.gif`
  quantizes to a *generic* 256-colour palette and looks visibly banded and dirty; `palettegen` +
  `paletteuse` builds the palette from the clip's **own** colours instead, at a cost of ~0.47 s vs
  0.15 s on a 3 s clip — so the bad route is simply never offered. **But two passes is NOT
  automatically smaller** (0.57 MB vs 0.38 MB in the spec's own measurement): the dithering that buys
  smooth gradients also adds noise that compresses worse. Quality vs size is a genuine trade-off here,
  not a free win — v1 takes the quality side.
- **The claim this test exists to pin: `-to` is ABSOLUTE, and the seek is frame-accurate.** Both are
  widely mis-stated (the natural reading of `-ss 2 -to 5` is "5 seconds starting at 2", i.e. ending at
  7), and both were verified against real ffmpeg back at design time, not assumed. This is the only
  test in the whole module that checks that reading against **real** ffmpeg rather than a fake that
  would accept either interpretation identically: it asserts the GIF from `-ss 2 -to 5` on a 6 s source
  is **3.0 s, not 5.0 s**. A wrong reading here is the single likeliest regression this module could
  ship, because a fake analyzer literally cannot tell the difference.
- **The bounds rule, reused rather than reinvented:** size and frame rate are capped at the source — a
  GIF wider or faster than its source contains no new information — and the source is always the
  **first entry** of every offered list, the same discipline that keeps the merger's `TargetBounds` from
  ever drifting from what derivation would actually pick. Height is derived from source aspect via
  `scale=W:-2`, never a second box — the exact `1920 × 102` hole the merger shipped once.
- **The output is re-probed before success is reported, and deleted if it fails the re-probe** — the
  rule this whole service exists to enforce, because ffmpeg's exit code is exactly what cannot be
  trusted (the merger's `concat` demuxer taught this project that the hard way: it exits 0 having
  silently dropped segments).
- **What the review rounds on this branch found — worth naming, because each is a lesson that repeats:**
  (a) **the re-probe itself was, for a while, pinned by nothing.** The "ffmpeg lies" unit test's fake
  analyzer failed on *every* path, so the call failed at `PreflightAsync`'s probe of the *source* and
  never reached `VerifyAsync` at all — deleting the entire re-probe left every test green. Fixed by
  making the fake path-aware (succeed on the source, fail only on the output), so the test actually
  exercises the step it claims to. A test only pins an invariant if the fixture can distinguish the
  invariant holding from it not holding. (b) **The merger's shipped bug pattern reappeared twice** in
  `GifMakerViewModel`: `LoadVideoAsync` had no freeze guard, so swapping the source video mid-render
  silently overwrote the running job — and a drop gesture bypasses `CanExecute` entirely, which is
  exactly why the merger learned to guard in *both* the command and the mutator, not just one. Then the
  history row's `Title` read the **live** filename property instead of a snapshot taken at render time,
  so editing the output name mid-render produced a history entry naming a file it doesn't point to.
  (c) `gif-size.json` is user-visible and hand-editable, and `JsonStore` quarantines only **malformed
  JSON**, not semantically-invalid values — a syntactically valid `"SampleCount": -1` deserialized
  cleanly, divided by zero downstream, and **persisted a `NaN`** that would have poisoned every future
  estimate silently. All three fixed and covered before this task closed.
- **Verified:** Release build **0 warnings / 0 errors**; **722/722** unit tests (`Category!=Integration`);
  **11/11** integration tests (the 4 pre-existing merge tests unaffected, the 1 new gif test, and 6
  pre-existing queue/yt-dlp tests unaffected — the brief's expectation of "5" only counted merge+gif;
  the full integration suite is larger and all of it stayed green).
- **Not verified: a human has not clicked through the GIF Maker page.** This environment is headless, so
  the XAML is checked by build + the page-load test only. Worth flagging specifically: the page's two
  `DynamicResource` lookups fail **silently** on a typo, unlike `StaticResource` (which throws at page
  load) — nothing in the suite would catch a broken one.
- **Next:** the branch is ready for the user's PR review. SDD → **v0.24** (§5 dependency-rule note on
  the two promotions, §17 M8 row → complete, Changelog). Delivered via branch `feat/m8-gif-maker`
  → PR (opened by the controller, not this task).

### 2026-07-12 — M8 GIF Maker: implementation plan (no code)

- **Done:** wrote [`docs/superpowers/plans/2026-07-12-m8-gif-maker.md`](docs/superpowers/plans/2026-07-12-m8-gif-maker.md)
  — **8 TDD tasks**, each with the actual code and the actual test bodies: (1) promote `Resolution` →
  `FFMedia.Media` and `TrimParsing.TryParse` → `FFMedia.Core` (a tool must never reference another tool);
  (2) `GifBounds`; (3) `GifArgsBuilder` (the two passes); (4) the calibrated size estimate; (5)
  `GifService` (preflight → two passes → **re-probe the output** → cleanup on every path); (6)
  `GifMakerViewModel`; (7) the page + `ITool` + DI + tooltips; (8) a real-ffmpeg integration test + docs.
- **Written to be picked up cold.** The plan opens with a **🚦 START HERE** section — repo state, the
  standing rules, the verification gate, the hard-won lessons (ffmpeg's exit code cannot be trusted; a
  `Style` with no `BasedOn` silently discards WPF-UI's theming; a `Page` must not nest a `ScrollViewer`),
  and every exact signature it will build against. `CLAUDE.md` now also has a **▶️ RESUME HERE** section
  at the top pointing straight at it.
- **Facts resolved so the next agent inherits none of my guesses:** `JsonStore<T>` lives in
  `FFMedia.Core.Persistence` (**not** `Storage`) and its `Load` takes a **factory**, not a parameterless
  call — I had both wrong in the first draft and corrected them against `SpeedProfileStore` before
  committing. Also pre-verified: `SymbolRegular.Gif24` exists, `palettegen`/`paletteuse` exist, `-to` is
  absolute, and the seek is frame-accurate.
- **Verified:** nothing to build — **documentation only, no code touched**.
- **Next:** execute the plan (`superpowers:subagent-driven-development`). Delivered via branch
  `docs/gif-maker-design` → PR #26 (the plan rides with the spec).

### 2026-07-12 — M8 GIF Maker: design (spec only, no code)

- **Done:** brainstormed and specced FFMedia's **third tool**, `FFMedia.Tools.GifMaker` →
  `docs/superpowers/specs/2026-07-12-gif-maker-design.md`. One video → one GIF, with **start / end /
  size / frame rate** and nothing else in v1. A **single-item editor with a live estimated size**, not a
  queue: making a GIF is iterative — you tune until it is small enough — so the feedback loop matters
  more than throughput.
- **The decision that determines whether it looks good:** **always two-pass**
  `palettegen` + `paletteuse`. The obvious `ffmpeg -i in.mp4 out.gif` quantizes to a *generic*
  256-colour palette and produces visibly banded, dirty output; the two-pass route builds a palette from
  the clip's **own** colours. Verified present in the bundled ffmpeg 8.1, and it costs ~3× of a fraction
  of a second (**0.47 s vs 0.15 s** on a 3 s clip) — so the bad route is simply not offered.
- **Measured, and it contradicted my own instinct:** two passes is **not automatically smaller**
  (0.57 MB vs 0.38 MB in the same test) — the dithering that buys smooth gradients also adds noise that
  compresses *worse*. Quality vs size is a real trade-off here, not a free win. v1 takes the quality
  side; a dither preset is the first deferred follow-up.
- **The spec's own claims, verified rather than asserted.** I had written that `-ss` before `-i` "seeks
  to the nearest keyframe" and left `-to` unstated. Both are widely mis-stated and both were wrong/vague,
  so I tested them: **`-to` is ABSOLUTE** on the source timeline, not a duration from the seek point
  (`-ss 2 -to 5` → exactly **3.0 s**), and **input seeking is frame-accurate, not keyframe-snapped**
  (30 frames at 10 fps, with keyframes 5 s apart). So the builder passes Start/End straight through and
  needs no `-accurate_seek`. Corrected before the spec was committed.
- **Reuses the rules the last two tools earned:** size and frame rate are **capped at the source** (the
  `TargetBounds` rule — a GIF wider or faster than its source contains no new information); **height is
  derived from the source aspect**, never a second box (the `1920 × 102` hole the merger shipped); the
  finished GIF is **re-probed before being called a success** (ffmpeg's exit code is exactly what cannot
  be trusted); size is estimated as a **range** calibrated from the user's own past GIFs (`gif-size.json`
  — the `SpeedProfile` pattern for the same class of unknowable).
- **Two promotions, because a tool must never reference another tool:** `Resolution` →
  `FFMedia.Media`, `TrimParsing` → `FFMedia.Core`. Both pure moves.
- **Verified:** `palettegen`/`paletteuse` exist in the bundled ffmpeg; `Gif24` **exists** in
  `SymbolRegular` (checked — the shell degrades an unparseable icon name to `Apps24` *silently*); the
  `-ss`/`-to` semantics above. No build/tests run — **documentation only, no code touched**.
- **Next:** user reviews the spec → `writing-plans` for the implementation. SDD → **v0.23** (§17 gains
  M8 + Changelog). Delivered via branch `docs/gif-maker-design` → PR.

### 2026-07-12 — Delta updates were never actually being built (four releases shipped full-only)

- **The bug worth remembering — "the workflow passed" is not evidence the thing was produced.** SDD has
  claimed "installer + **delta** auto-update" since v0.9. The machinery is real. **But no release has
  ever shipped a delta.** `vpk pack` builds one by diffing against the **previous release's `.nupkg`**,
  and if that file is not in the output directory it emits a **full package only — with no error and no
  warning**. A CI runner starts from an empty checkout, so there was never anything to diff against:
  **v1.0.0 → v1.1.1 all shipped full-only**, and every update was a **~190 MB download** — including
  v1.1.1, which changed nothing but tooltip strings.
- **How it was found:** by *reading the published manifest* after tagging v1.1.1 (`"Type":"Full"`, and
  nothing else) rather than stopping at the green workflow. Pack succeeds either way. That is precisely
  why it survived four releases — there was no failure to notice, only an absence, and nobody was
  looking for an absence.
- **Fix:** `release.yml` now runs **`vpk download github` before `vpk pack`** (Velopack's documented
  flow), plus a step that **asserts a `*-delta.nupkg` exists** and raises a CI warning if it does not.
  The assertion is the real fix: the original defect was trusting that a successful command had done
  the thing it was supposed to do. `continue-on-error` on the download, because the first release on a
  channel legitimately has nothing to diff against.
- **Proven before merging, not after.** `vpk` installs locally, so this did not have to wait for a real
  tag to be believed: downloaded v1.1.1, packed a hypothetical 1.1.2, and Velopack logged
  `Building delta 1.1.1 -> 1.1.2`. **190.8 MB full → 18.6 MB delta (≈90 % smaller)**, with the manifest
  now advertising both a `Full` and a `Delta` asset.
- **Also done:** rewrote the **v1.1.0 and v1.1.1 GitHub release descriptions** to match the user-facing
  style of v1.0.0/v1.0.1 (feature/fix sections, install + update instructions, bundled-versions footer).
- **Verified:** the delta build, locally and end-to-end. `release.yml` itself is only proven by the
  **next** tag — the CI path cannot be exercised without one, and the new assertion step exists exactly
  so that a silent regression there becomes loud.
- **Next:** user reviews the PR; the next release will be the first to actually ship a delta.
  SDD → **v0.22** (§15). Delivered via branch `fix/delta-updates` → PR.

### 2026-07-12 — Plain-English tooltips on every parameter (Downloader, Merger, Settings)

- **The ask:** casual users cannot understand the parameters. True — they are the vocabulary of video
  encoding (container, CRF, bitrate, fit mode, sample rate) plus two raw tool names (`yt-dlp`, `ffmpeg`)
  the app simply leaks at the user. The **Downloader had one tooltip**; the Merger's thirteen were
  written for an engineer (*"ffmpeg picks its muxer from that"*); **Settings had none**.
- **The rule that makes them useful: name the TRADE-OFF, not the definition.** *"Lower is better quality
  and a bigger file. 20 is a good default; above 28 it starts to look blocky."* **A setting you cannot
  weigh is a setting you cannot choose** — a tooltip that only expands the jargon leaves the user exactly
  as stuck.
- **Three details that decide whether they work at all:** (1) attach the tooltip to the **label + control
  row**, not the control — a user who does not know what "Container" means points at the *word*, and a
  tooltip on the ComboBox alone says nothing there; (2) set `ToolTipService.ShowDuration` on the page
  root, because **WPF hides a tooltip after 5 SECONDS** and a two-sentence explanation vanishes
  mid-sentence; (3) explain the jargon the app itself leaks — *"yt-dlp is the tool that does the
  downloading. YouTube changes how it works fairly often, which breaks it until it is updated."*
- **`TooltipCoverageTests`** walks the real pages and fails if any input has no tooltip. It **deliberately
  does not filter on `IsVisible`** — half the Downloader's parameters live in rows collapsed until the
  matching output kind is chosen, and skipping hidden controls would have let exactly those ship bare
  with the test green. Mutation-proven: deleting the (hidden) Bitrate tooltip fails it, naming
  `ComboBox (Bitrate:)`.
- **The bug adding a second WPF test class exposed.** WPF allows **one `Application` per AppDomain**,
  owned by its creating thread. Every WPF test class was starting its own STA thread and calling
  `Application.Current ?? new Application()` — which worked *only* while `MergerPageLoadTests` was the
  sole such class. The new class made them race: *"Cannot create more than one Application instance"*,
  and a `XamlParseException` far from its cause. A shared **`WpfHost`** now owns the one Application and
  STA dispatcher (xUnit `wpf` collection), with **`ShutdownMode = OnExplicitShutdown`** — the default,
  `OnLastWindowClose`, lets the first test that closes a window tear the Application down under every
  test after it. Latent all along; the second class merely triggered it.
- **Verified:** Release build **0 warnings / 0 errors**; **642/642** unit tests (was 640); **4/4** merge
  integration tests against real ffmpeg. **Not verified:** the wording on screen — whether the tooltips
  actually land for a casual reader is a judgement only a human hovering them can make. **Settings is not
  covered by the test** (it lives in the WinExe, which Tests does not reference); build + eye only.
- **Next:** user reviews and hovers. SDD → **v0.21** (§13 tooltip rule + §14 WpfHost/coverage). Delivered
  via branch `feat/plain-english-tooltips` → PR.

### 2026-07-12 — Repoint the update feed at the canonical repo (pre-release supply-chain fix)

- **Context:** preparing the **v1.1.0** release (first release since v1.0.1; the whole Video Merger
  tool landed in between). Before tagging, checked the release machinery — and found the repo had been
  **renamed** `ChamHC-dev/ff-media` → **`CharmHC/ff-media`**, while both `VelopackUpdateService.RepoUrl`
  and `release.yml`'s `--repoUrl` still named the **old owner**.
- **Why it looked fine, and why that is the trap.** GitHub's rename redirect answers the old path: an
  anonymous `GET /repos/ChamHC-dev/ff-media/releases` returns **200** (verified with curl — 1 redirect
  followed). Nothing was broken, nothing logged an error. **But the redirect is dropped the moment
  anyone creates a repository at the abandoned name.** This app *downloads and installs executables*
  from that URL, so a stale feed is not a broken link — it is a **supply-chain hole**: an attacker
  squatting `ChamHC-dev/ff-media` would be serving the auto-update to every installed client.
- **Done:** both URLs now name the canonical owner. SDD §15 records the rule so it cannot rot back.
  **Existing v1.0.1 installs are unaffected** — they ship the old URL and still resolve through the
  redirect, so they will find v1.1.0 normally.
- **Note for the user:** the local git remote still points at the old name (pushes work via the
  redirect). Repointing it changes where every future push goes, so it is left as an explicit call:
  `git remote set-url origin https://github.com/CharmHC/ff-media.git`.
- **Verified:** Release build **0 warnings / 0 errors**; **640/640** unit tests. The redirect behaviour
  was verified against the live GitHub API, not assumed.
- **Next:** merge, then tag **v1.1.0** — the tag-gated workflow packs (Velopack) and publishes the
  GitHub release. SDD → **v0.20**. Delivered via branch `fix/canonical-repo-url` → PR.

### 2026-07-12 — Merger clip list: column headers, and the checkbox that lied

- **The bug worth remembering — an affordance is a promise.** The per-clip control was a **`CheckBox`**,
  and a checkbox in a list means *"include this row"* in every UI anyone has ever used. The user
  reasonably concluded it chose **which clips get merged**. It never did — it only exempts a row from
  **Shuffle**, and every clip in the list is merged whether ticked or not. No tooltip rescues a control
  whose *shape* says the wrong thing. It is now a **pin `ToggleButton`** (`PinOff24` → `Pin24`), and
  `MergeClipViewModel.PinTooltip` states the one thing it affects ("…Shuffle won't move it. It is merged
  either way.") and names the pinned position **1-based** for a human. Shuffle's tooltip: "locked" →
  "pinned". **Code names (`IsLocked`/`LockedIndex`/`SetLock`) deliberately unchanged** — internal, and
  renaming them churns ~30 tests without changing anything the user sees. SDD §13 gains the rule.
- **Column headers (Pin · Clip · Status · Actions).** The conformance badge especially read as an
  unexplained chip. The load-bearing detail is the alignment: **`Auto` columns in *separate* Grids size
  independently**, so a header Grid naively placed above the rows drifts out of alignment as file names
  change. `Grid.IsSharedSizeScope` on the page root + a matching `SharedSizeGroup` on each `Auto` column
  in *both* the header and the row template makes them negotiate one width.
- **The XAML trap avoided (twice-shipped from this exact page).** The glyph swap is a
  `DataTemplate.Trigger` on a named element — **not** a `Style`. A `Style` with a `TargetType` and no
  `BasedOn` silently discards WPF-UI's implicit style; a `BasedOn` pointing at a key WPF-UI does not
  register throws `XamlParseException` at page **load** (compiles clean, passes every parse-only test,
  crashes in front of the user). Both have happened here. `Pin24`/`PinOff24` were **verified to exist**
  in `SymbolRegular` before being written into the XAML.
- **A dead trigger is invisible** to the compiler *and* to the parse-only page-load test — the toggle
  would simply look permanently unpinned however often it was clicked. So `MergerPageLoadTests` gained a
  test that **realizes the row in a real visual tree and reads the glyph back**. Mutation-proven:
  deleting the `Setter` fails it and nothing else.
- **Verified:** Release build **0 warnings / 0 errors**; **640/640** unit tests; **4/4** merge
  integration tests against the real bundled ffmpeg. **Not verified:** a human has not seen the header
  alignment or the pin on screen — `IsSharedSizeScope` alignment in particular is a *pixel* claim that
  only a screenshot settles.
- **Next:** user reviews the PR and clicks through. SDD → **v0.19** (§13 + Changelog). Delivered via
  branch `feat/merger-clip-list-header` → PR.

### 2026-07-12 — Merger: `TargetBounds` implemented + proven against real ffmpeg

- **Done:** implemented the design from the same-day "design only" entry below. New pure
  `TargetBounds` (`FFMedia.Tools.VideoMerger.Models`) exposes four allowed-value lists — resolutions,
  frame rates, sample rates, channel counts — built from `MergeTargetDerivation`'s own maxima via
  `TargetBounds.From(clips)`. `MergeTarget.ClampTo(TargetBounds)` forces an out-of-range override back
  inside bounds. `MergerViewModel` gained `Bounds` (recomputed whenever the clip list changes),
  `SelectedResolution` (backs a new `Resolution`-bound ComboBox that replaces the old free-text
  width/height boxes), `ShowOpusInMp4Warning`, and `HasClips`. Landed across five prior tasks on this
  branch (`feat/m7-target-bounds`); this last task added the real-ffmpeg proof and synced the docs.
- **The keystone invariant:** the derived target is always the **first entry** of every `TargetBounds`
  list, because the lists are built from derivation's own maxima rather than recomputed independently
  by the UI. The offered ComboBox options and the value `MergeTargetDerivation.Derive` actually picks
  therefore cannot drift apart — the same discipline `ConformanceCheck` already enforces for the fast
  path (if estimator and merge engine ever disagreed on "does this clip conform", the ETA would
  describe a different plan than the one that runs; here, if bounds and derivation ever disagreed, the
  dropdown could offer a value derivation itself would never choose).
- **The snap-down rule:** `ClampTo` takes the **largest allowed value not exceeding** the current
  override, falling back to the *smallest* allowed value only when every option exceeds it (e.g. the
  user deleted the one 1080p clip and only 720p clips remain). It **never snaps up** — that would
  silently reintroduce the upscaling this whole feature exists to forbid.
- **Codec × container is deliberately NOT restricted.** All 8 combinations (2 containers × 2 video
  codecs × 2 audio codecs) were verified to mux cleanly against the real bundled ffmpeg 8.1 back when
  this was designed. MP4 + Opus is a **playability** problem (VLC/Chrome play it fine; QuickTime and
  most TVs do not), not a validity one — ffmpeg itself raises no error — so `MergerViewModel` surfaces
  `ShowOpusInMp4Warning` rather than blocking the combination. A blocked option in this feature always
  means "provably pointless" (upscaling, odd dimensions, an aspect ratio nothing produced); it never
  means "we'd rather you didn't."
- **Proof against real ffmpeg, not mocks:** added
  `MergeAsync_ClampedTo720pTarget_ProducesAReal720pFile` to `MergeIntegrationTests` — adapted from the
  plan's draft (which assumed fields `_analyzer`/`_merger`/`_temp` that don't exist in this file) to
  the file's actual shape: its `NewService()` factory, `MakeClipAsync`/`ProbeAsync`/`AnalyzeAsync`
  helpers, and `_dir` temp directory. It synthesizes two 1080p `testsrc` clips, derives+clamps+
  overrides the target to 720p, merges through the real `MergeService`, and — because ffmpeg's concat
  demuxer **exits 0 even when it silently drops a segment** — probes the *output file* rather than
  trusting the exit code, asserting real 1280×720 dimensions and ~4 s duration (proving both clips
  actually landed in the file, not just one).
- **What the whole-branch review caught (each task passed its own review; the composition still had a
  gap).** The reviewer hosted the **real page in the real WPF presenter** and drove a 7-step sequence —
  and could not break the keystone: `Bounds` and `Target` never diverged, a portrait clip correctly
  clamped `1280×720` → `608×1080` without rotating, and a 12 fps source produced a one-entry list rather
  than an empty ComboBox. But it found that **the Output panel stayed editable during a merge**. The
  merge holds a *snapshot* of the target, so flipping Container to MKV mid-merge rewrote
  `OutputFileName` to `merged.mkv` while the encode still wrote `merged.mp4` — and the **history row
  then named a file that does not exist, in a format never produced**. Fixed with
  `CanEditTarget => HasClips && !IsMerging` (the `CanEditClips` precedent, one level up), pinned by a
  test that is mutation-proven: removing the `IsMerging` gate fails it. Also fixed: the two audio
  ComboBoxes bound `SelectedItem` to non-nullable `int`s, so the null a ComboBox pushes while its
  ItemsSource rebuilds was a silently-swallowed binding error (now nullable projections, matching
  `SelectedResolution`); and the "no clips" gate had frozen **File name / Folder / Browse** too — those
  are now always live, since choosing where a merge lands is meaningful before adding a clip.
- **Verified:** Release build **0 warnings / 0 errors**; **637/637** unit tests pass
  (`Category!=Integration`); **4/4** merge integration tests pass against the real bundled
  ffmpeg/ffprobe (3 pre-existing + the new 720p one). SDD → **v0.18** (the v0.17 row's closing
  "design only — no code in this change" is corrected; it is no longer true).
- **Not verified:** a human has not clicked through the new bounded ComboBoxes in the running app —
  this dev environment is headless, so the UI is verified by `MergerViewModel` unit tests and a clean
  build only, consistent with every other M7 UI change in this log.
- **Next:** none pending for the merger; user reviews and merges the PR for branch
  `feat/m7-target-bounds`.

### 2026-07-12 — Merger: bound the output options to the source (`TargetBounds`) — design only

- **Done:** brainstormed and specced the fix for *"the output option should not allow any invalid
  options"* → `docs/superpowers/specs/2026-07-12-merger-target-bounds-design.md`. `MergeTargetDerivation`
  already takes the **maximum** across the clips (largest dimensions, fastest fps, highest sample rate,
  most channels) — the override UI just ignored that ceiling, so the user could pick **60 fps from
  all-30 fps clips** (ffmpeg duplicates every frame: bigger file, longer encode, *zero* new
  information), 4K from 1080p, 5.1 from stereo, or `1920 × 102` (width and height are *independent*
  free-text boxes). **CRF and odd dimensions turned out to be already guarded** — my first draft of the
  spec claimed both were reachable, and reading `MergerViewModel` before planning against it proved me
  wrong. The real gap is the **ceiling**, plus the aspect-ratio hole two independent boxes leave open.
- **The design:** a new pure **`TargetBounds`**, built *from the derivation's own maxima*, turns each
  maximum into a **list of allowed values** — and **the derived target is always the first entry of each
  list**, so the offered options and the derived target *cannot drift*. That is the same keystone
  discipline `ConformanceCheck` already enforces, and it is the whole reason not to let the UI compute
  its own ceiling. Resolution becomes a **dropdown of standard steps at source aspect** instead of two
  free text boxes, which makes upscaling, **odd dimensions** (the libx264 bug that already bit us once)
  and absurd aspect ratios *unrepresentable* rather than validated. One rule handles the ceiling moving
  as clips are added/removed: **snap down to the largest allowed value ≤ the current one**, silently,
  keeping the override.
- **The finding worth remembering — I was wrong, and testing said so.** I proposed blocking "invalid"
  codec × container pairs (Opus-in-MP4 being the obvious one). **So I tested all 8 combinations against
  the real bundled ffmpeg 8.1 — and every single one muxes cleanly**, Opus-in-MP4 included. There is no
  invalid combination to block. MP4 + Opus is a **playability** problem (VLC/Chrome play it; QuickTime
  and most TVs do not), *not* a validity one, and blocking it would have invented a restriction ffmpeg
  does not have. It gets a **warning**, not a block — which keeps the promise sharp: **a blocked option
  means "provably pointless", never "we would rather you didn't".** This is the third time this session
  that checking a belief against the real tool changed the design; the previous two were assumptions
  about WPF-UI that shipped a crash.
- **Verified:** the 8-combination mux matrix, against real ffmpeg. No build/tests run — **documentation
  only, no code touched**.
- **Next:** user reviews the spec → `writing-plans` for the implementation. SDD → **v0.17** (§13 + the
  Changelog). Delivered via branch `docs/m7-target-bounds` → PR.

### 2026-07-12 — M7 PR 2 follow-ups: the six bugs the headed click-through found

- **Context:** PR 2 shipped green (597 tests) with one honest caveat — *"the page itself is not
  verified; the layout, both drag gestures and the dark-mode rendering need the user's visual
  check."* The user did that check. It found **six** defects. **Four were invisible to the entire
  suite, for one reason: nothing ever instantiated the page.** PR #18 was merged before these fixes
  existed, so they land as a follow-up branch off `main`.
- **The bug worth remembering — Shuffle could pin a row forever.** `ShuffleSeed` was seeded once at
  construction and **never re-seeded**, even though the comment right above it claimed *"the UI
  re-seeds it from the clock on every Shuffle click."* Nothing did. So every click built
  `new Random(sameSeed)` and replayed the **identical permutation** — and any index that permutation
  maps to itself is a row that can **never move**, however many times the user clicks. Reproduced
  against the real `Ordering.Shuffle`: **~63% of seeds pin at least one row forever**, 17–33% pin the
  **2nd row** specifically (exactly what the user saw), and the list only ever cycles through ~3
  distinct orders. `Ordering.Shuffle` itself was flawless — a textbook unbiased Fisher–Yates being fed
  a constant. **A correct algorithm and a correct call site still composed into a broken feature.**
- **The recurring test lesson, a third time.** Every shuffle test assigned `ShuffleSeed` immediately
  before each `Shuffle()` call — so the suite **simulated a re-seeding UI that did not exist**. No test
  ever clicked Shuffle twice. A test only pins an invariant if the fixture **varies along the axis the
  invariant is about**. Mutation-proven: deleting the fix fails exactly the new test and nothing else.
- **I broke the app once, mid-fix.** My first dark-mode fix was
  `BasedOn="{StaticResource {x:Type ListViewItem}}"` — a key I *assumed* existed. `StaticResource`
  resolves at **page-load**, not compile time, so it built clean, passed all 603 tests, and threw
  `XamlParseException` the moment the user clicked the nav item. Enumerating `ControlsDictionary` at
  runtime gave the real answer and **corrected my original diagnosis too**: WPF-UI keys its implicit
  styles to its **own subclasses** (`Wpf.Ui.Controls.ListView`) and ships **nothing** for the plain
  WPF `ListView` — so the box was never going to be dark, `BasedOn` or not. Fix: use `ui:ListView`.
  **Lesson: verify the resource key exists; do not reason about what a library "surely" registers.**
- **The scroll bug, found by measuring instead of guessing.** Wheel did nothing outside the clip list.
  Hosting the page in the real `NavigationViewContentPresenter` and reading the numbers:
  shell's `ScrollViewer` `Scrollable=185`, page's own `Scrollable=0`. **WPF-UI already wraps every page
  in a `ScrollViewer`** — which is why no other page in the app has one. MergerPage added a second; the
  outer one hands the inner unbounded height so it can never scroll, **yet WPF's `ScrollViewer` marks
  wheel events handled even when it cannot move**. It swallowed every tick while the shell's scroller,
  with 185 px to give, never saw one. Removed it.
- **Also fixed:** the clip list was wiped by navigation (`MergerViewModel` was `AddTransient`; the
  merger's clips live *in the VM*, unlike the downloader's queue which lives in a singleton
  `DownloadManager`) → now **singleton**, deliberately reversing PR 2's tested decision; a **Clear all**
  button beside Shuffle (frozen during a merge, like every other mutator); and an outline on the clip
  box (`ui:ListView` ships no border, so the drop target had no visible edge).
- **The durable fix — `MergerPageLoadTests`.** A module's `Page` lives in the *module*, not the WinExe,
  so the test project **can** instantiate it. It now builds the real page on an STA thread, against the
  real `ThemesDictionary`+`ControlsDictionary`, inside the real `NavigationViewContentPresenter`. A bad
  resource key and a nested scroller now **fail `dotnet test`** instead of failing in front of the user;
  both are mutation-proven (re-introducing each bug fails exactly its own test). Any new tool page gets
  the same pair.
- **A seventh, found after the branch was pushed — and the most instructive.** The user reported
  *"Not a video: x.mp4 could not be read as a video"* on a perfectly good mp4. **Root cause: `ffprobe.exe`
  was not in `assets/binaries`.** The binaries are git-ignored, M7 PR 1 added ffprobe as a **new** required
  binary, and `fetch-binaries.ps1` had only ever been re-run **inside the worktree** — so every test and
  every merge passed there while the user's main checkout could not probe a single file. **But the real
  bug is the message, not the missing file:** `MergerViewModel` collapsed *"the probe failed"* and *"the
  probe succeeded but the file has no video track"* into one notification and **discarded `probe.Error`**.
  The analyzer was already reporting `"Could not run ffprobe: The system cannot find the file specified."`
  — the ViewModel threw it away and blamed the user's file for a missing binary, sending them to inspect
  their mp4. The two cases are now separate notifications, and the failure path surfaces the analyzer's
  own reason. **A new required binary is invisible to a checkout whose git-ignored `assets/binaries` is
  already populated — anyone pulling this branch must re-run `build/fetch-binaries.ps1`.**
- **Verified:** Release build **0/0**; **606/606** unit tests (597 → 606, 9 new); **3/3** merge
  integration tests pass against the real bundled ffmpeg **in the main checkout** — which they could not
  have done before, since ffprobe was missing there. **Not verified:** the user's re-check on screen.
- **Next:** user clicks through the six fixes; then delete the `feat/m7-merger-ui` branch + worktree.
  SDD → **v0.16** (§13 gains two new rules: *use `ui:` controls, never their plain WPF namesakes*, and
  *a `Page` must not contain its own `ScrollViewer`*; §14 gains page-load tests). Delivered via branch
  `fix/m7-merger-ui-followups` → PR.

### 2026-07-12 — M7 PR 2: Video Merger UI — **M7 complete**

- **Done:** the merger's UI module — `MergeClipViewModel` + `MergerViewModel` (headless, every
  dependency an interface, unit-tested with fakes), `MergerPage`, `VideoMergerTool : ITool`, and
  `AddVideoMerger()`. **The shell was not modified**: the tool registers `ITool`/`IToolPage` and the
  shell discovers it — which is the entire point of the modular seam, and M7's real purpose was to
  prove that seam holds for a second tool. It does. The page has the clip list (drag files in to add,
  drag rows to reorder, Move Up/Down, lock-to-index, remove), Shuffle honoring locks, the
  auto-derived-but-fully-overridable target, the §6.5 summary line, per-clip + overall progress, and
  Merge/Cancel, wired to history and notifications.
- **Four engine changes deliberately reopened** (PR 1 was merged; these were worth the churn):
  1. **Per-clip progress** (user-chosen) — `MergeProgress` gained `ClipPercents`. A **conforming clip
     reads 100 from the first report**: it has no encoding work, and showing it as pending would be a lie.
  2. **`HistoryEntry.Source`** (user-chosen) — a merge is now a first-class history row rather than a
     download with a blank URL. Old `history.json` files still load through a tolerant converter:
     unknown name, null, or a number all degrade to `Download`. **Degrade the field, never destroy the file.**
  3. **`MergeService` now verifies its own output.** See below — this was the serious one.
  4. **Container ↔ file extension reconciled.** `ConcatArgsBuilder` emits no `-f`, so **ffmpeg picks
     its muxer from the output file's extension**, while `Target.Container` only gated
     `-movflags +faststart`. A derived **MKV** target therefore wrote a real **MP4** named `.mp4` —
     the user picked MKV and got MP4. The two are now kept in lockstep both ways.
- **The bug worth remembering:** ffmpeg's `concat` demuxer does **not** fail on a segment it cannot
  open. It **drops that segment and every one after it, and exits 0**. So `MergeService` as shipped in
  PR 1 would report a successful merge and hand the user a **silently truncated video** — and this is
  trivially reachable on the **fast path**, where the concat list holds the user's *own* file paths
  (a clip moved, renamed, or on a disconnected drive between adding it and clicking Merge). Fixed with
  an open-every-segment preflight **+** a post-merge duration check against the expected total **+**
  deleting the misleading partial output. `-xerror` is **not** the fix — it fails healthy merges.
- **Decisions:** `SortOrder = 20`, not the spec's `2` — ordering is *ascending* and the downloader is
  `10`, so `2` would sort the merger *above* it, inverting the spec's own intent. **No `IErrorMapper`**
  (spec §8 cites one; it has never existed here) — `MergeErrors`, a static per-module mapper matching
  `YtDlpErrors`, instead. `IconGlyph = "VideoClipMultiple24"`, pinned by a test that parses it back to
  a real `SymbolRegular`, because the shell falls back to `Apps24` on an unparseable name and a typo
  would degrade **silently**.
- **The composition bug — and why the final whole-branch review exists.** Every task passed its own
  review; the *whole* was still broken. **The clip list stayed editable during a merge.** Task 5 built
  the list commands, Task 7's threading argument *assumed the list was frozen* (it says so in a
  comment), and Task 8 added a drag gesture that bypasses commands entirely. Nobody was wrong — the
  composition was. Reordering mid-merge puts clip M's progress on row N (`ClipPercents` is indexed by
  the request snapshot taken at click time); a removed clip is still in the output; and worst,
  `OnMergeProgress` runs on **ffmpeg's stdout callback thread** and indexes `Clips`, so a concurrent
  UI-thread mutation throws inside a `Process.OutputDataReceived` handler — which has **no catch
  anywhere up the stack**, taking the app down. The list is now frozen while merging, gated *both* by
  `CanExecute` (so buttons grey out) *and* by an explicit guard in every mutator, because the drop and
  drag gestures never reach a command at all.
- **The data-loss bug:** the default output name is the constant `merged.mp4`, so merging twice aims
  at the same path — and ffmpeg gets `-y`. A failed second merge **overwrote the first merge's good
  video and then deleted the wreckage**, leaving the user with *neither* file. The concat now writes a
  **sibling** (same directory, so `File.Move` is a free rename rather than a copy; same extension,
  because ffmpeg picks its muxer from it), verifies *that*, and only moves a proven-whole merge into
  place. Nothing the user already had is touched until we have something worth replacing it with.
- **Also caught:** the override UI accepted **odd dimensions** (yuv420p's 2×2 chroma subsampling makes
  libx264 reject them outright — `ToEven` guarded the *derived* path but the new per-field projections
  reopened the hole); and the duration tolerance was a **flat 1 s**, where a false positive *deletes a
  healthy merge* — it now scales with clip count, clamped to half the shortest clip so a genuinely
  dropped clip is still caught.
- **The recurring test lesson, twice more:** a test only pins an invariant if the fixture **varies
  along the axis the invariant is about**. The filename tests only fed `holiday.mov`-shaped names —
  every case where replacing the last dot-segment is *correct* — so they could not see that
  `Path.ChangeExtension` was truncating `Trip 2026.07.11` to `Trip 2026.07.mp4`. And the dropped-clip
  test used 5 s clips, where the clamped and unclamped tolerances give the same answer, so deleting the
  clamp left all 596 tests green.
- **Verified:** Release build **0/0**; **597/597** unit tests pass (`Category!=Integration`); and the
  merger is finally proven **end-to-end** by 3 trait-gated integration tests against the real bundled
  ffmpeg — three mismatched `testsrc` clips normalized and merged (**probing the output**, since the
  exit code is exactly what can't be trusted), the fast path proven to *never enter the normalize
  phase*, and a cancelled merge proven to strand no temp debris and no half-written output. Each task
  was reviewed by an independent agent that mutation-tested the tests, and every fix above is itself
  mutation-proven.
- **Not verified:** the page itself. This environment is headless and `FFMedia.Tests` doesn't reference
  the WinExe, so the XAML is checked by build + review only — **the layout, both drag gestures, and the
  dark-mode text rendering need the user's visual check.** (Dark-mode text has bitten this project twice;
  the page root sets `Foreground` per SDD §13, but that's an argument, not a screenshot.)
- **Next:** user reviews and merges the PR, then does a headed click-through of the merger page.
  SDD → **v0.15**, M7 ✅ complete. Delivered via branch `feat/m7-merger-ui` → PR.

### 2026-07-11 — M7 PR 1: Video Merger engine (no UI)

- **Done:** realized `FFMedia.Media` — `IMediaAnalyzer`/`FfprobeMediaAnalyzer` over ffprobe,
  `IFfmpegRunner`/`FfmpegRunner` with `-progress` streaming + a stderr tail on failure, and the pure
  `FfprobeParsing` / `FfmpegProgressAccumulator` — and built the new `FFMedia.Tools.VideoMerger`
  engine: target derivation, `ConformanceCheck`, normalize/concat arg builders, seeded shuffle with
  locked indices, `MergeEstimator` + `SpeedProfile` (`encode-speed.json`), `DiskSpaceGuard`,
  `TempDirectorySweeper`, and `MergeService` (preflight → bounded-concurrency normalize →
  stream-copy concat, temp cleanup on **every** exit path). Added `ExternalBinary.Ffprobe` +
  `fetch-binaries.ps1` extraction from the same pinned, SHA-256-verified zip, and a non-generic
  `Result` in Core. `AddVideoMergerEngine` wires it all up.
- **The keystone invariant:** `MergeEstimator` and `MergeService` both **call** `ConformanceCheck`
  rather than re-implementing "does this clip need re-encoding". If they ever disagreed, the ETA,
  the fast-path promise and the disk reservation would describe a different plan than the one that
  runs. A false *conforming* is the dangerous direction — it stream-copies a mismatched clip into
  `concat` and corrupts the output.
- **Caught by the final whole-branch review (each task was individually correct):** `MediaInfo`
  modelled only the **first** video + **first** audio stream, so a clip with an embedded subtitle
  track looked *fully conforming*, took the fast path, and got stream-copied. ffmpeg's concat matches
  segments by stream **index** — the next clip's audio lands on this clip's subtitle slot, ffmpeg
  **exits 0**, and the user gets a merge whose later clips are **silently mute** (reproduced against
  real ffmpeg 8.1). Not hypothetical: **our own downloader writes such files** when "embed subtitles"
  is on. `MediaInfo` now carries `ExtraStreamCount` and `ConformanceCheck` treats extras as a
  mismatch, so those clips are re-encoded (normalization maps only `0:v:0` + one audio, dropping
  them). `Conformance.IsConforming` is now *derived* from `Mismatches`, so the two cannot drift.
- **The bug worth remembering:** cancelling a token does **not** synchronously dequeue a pending
  `SemaphoreSlim.WaitAsync` waiter — the cancellation's node-removal continuation is queued to the
  thread pool, so a `Release()` microseconds later still hands the permit to the cancelled waiter,
  which then launches a **full ffmpeg encode** for a merge the user already cancelled. It showed up
  as a 1-in-5 flake; a 200-iteration harness proved 197/200 runs launched the extra encode. Fixed by
  re-checking the token *after* acquiring the gate.
- **Verified:** Release build **0/0**; **425/425** unit tests pass (`Category!=Integration`). Each
  task was reviewed by an independent agent that mutation-tested the tests — which caught three
  suites that passed against a deliberately broken implementation (a reversed stderr tail, a biased
  Fisher–Yates, a per-progress-line speed sample). Argv was additionally validated end-to-end against
  a real ffmpeg 8.1 for both the normalize and concat phases.
- **Not verified:** a real end-to-end merge driven from the app — there is **no UI in this PR** (no
  ViewModels, no XAML, no `ITool`/nav registration, deliberately: the shell must not navigate to a
  page that does not exist) and no integration test. That lands with PR 2.
- **Next:** M7 PR 2 — `MergerViewModel`, `MergerPage`, `ITool`/nav registration, history +
  notifications wiring, the override UI (which must expose Opus — `Derive` never votes for it), and
  a trait-gated integration test merging three real `testsrc` clips. SDD → **v0.14**. Delivered via
  branch `feat/m7-merge-engine` → PR.

### 2026-07-10 — M7 Video Merger: design (spec only, no code)

- **Done:** brainstormed and specced FFMedia's **second tool module**,
  `FFMedia.Tools.VideoMerger` → `docs/superpowers/specs/2026-07-10-m7-video-merger-design.md`.
  Flow: ingest local clips → probe → **auto-derived, user-overridable** standardization
  target → normalize **only non-conforming** clips to temp intermediates (bounded
  concurrency, the §12 `SemaphoreSlim` pattern) → **stream-copy `concat`**. When every clip
  already conforms, normalization is skipped and the merge is a ~1 s copy (**fast path**).
- **Decisions (user-approved):** aspect mismatch → **letterbox/pillarbox** default with a
  per-merge `FitMode` (Fit/Fill+Crop/Stretch); clips with **no audio** get a synthesized
  `anullsrc` silent track (so `concat`'s identical-stream-layout requirement holds); merge-time
  estimate is a **calibrated heuristic shown as a range**, backed by a persisted `SpeedProfile`
  rolling average of the user's own measured throughput (new `encode-speed.json`), and
  **replaced by ffmpeg's real ETA** once merging starts; **disk-space guard** fails fast before
  any encoding; ordering is manual / random / **random-with-locks** (seeded Fisher–Yates ⇒
  deterministic tests); **one merge at a time** (no `IMergeManager` — the concurrency lives
  inside the normalize phase); reuse existing history/notifications/settings; drag-to-reorder in.
  **Deferred:** transitions/crossfades, per-clip trim, per-clip fit mode, background music.
- **Two consequential findings:** (1) **FFMpegCore dropped** — listed in SDD §3 since v0.1 but
  never referenced, and it manages its own child processes, which would bypass the
  `IProcessRunner` seam the codebase is tested through; `FFMedia.Media` (an empty shell until
  now) is instead realized as `IMediaAnalyzer`/`IFfmpegRunner` over `IProcessRunner` + pure
  `FfprobeParsing`/`FfmpegProgressParsing`. (2) **`ffprobe.exe` is not currently shipped** —
  it lives inside the *same* pinned, SHA-256-verified BtbN zip, so it's a second extraction,
  **no new download or hash**.
- **Verified:** `MergeDuplicate24` (my first icon pick) **does not exist** in `Wpf.Ui.dll`
  4.3.0 — checked the assembly; spec uses `VideoClipMultiple24`. No build/tests run: this
  change is documentation only, no code touched.
- **Docs updated:** SDD → **v0.13** (§3 stack, §4 diagram, §5 structure + dep rule, §7.1,
  §8 rewritten, §9 ffprobe, §10 `encode-speed.json`, §16 **GPL build is now load-bearing**
  since the merger re-encodes with x264/x265, §17 M7 row, §19 deferrals, Changelog);
  README (tech stack + roadmap); `THIRD-PARTY-NOTICES.md` (ffprobe under the same GPL build).
- **Next:** user reviews the spec → then `writing-plans` for the M7 PR 1 (engine) implementation
  plan. Delivered via branch `docs/m7-video-merger-design` → PR.

### 2026-07-10 — Post-v1 UI fixes round 2 (dark-mode page text, blank launch content)

- **Two bugs reported after installing v1:**
  1. **Dark-mode text still black** on page content ("YouTube Downloader" header, "Output:",
     "Container:", Settings labels, …). **Root cause:** the v0.12 fix set `FluentWindow.Foreground`,
     which themes only the chrome (title bar / nav pane) — WPF's `Frame` (which `NavigationView`
     hosts pages in) **isolates property-value inheritance**, so it never reaches page content.
     WPF-UI 4.3.0 ships **no implicit keyless `TextBlock` style** (only keyed ones like
     `BodyTextBlockStyle`), so plain `TextBlock`s fall back to WPF's default **black**. **Fix:**
     set `Foreground="{DynamicResource TextFillColorPrimaryBrush}"` on **each `Page` root**
     (`WelcomePage`, `DownloaderPage`, `HistoryPage`, `SettingsPage`) — page-local inheritance
     themes all plain text (a blanket implicit `TextBlock` style was rejected: it would also
     override button/combo template foregrounds, e.g. white-on-accent primary buttons).
  2. **Blank "main interface" on launch** until the user clicked a pane item. **Root cause:**
     `NavigationView` selects nothing by default and nothing navigated at startup; the
     purpose-built `WelcomePage` was never wired. **Fix:** registered `WelcomePage` in DI and
     navigate to it from `MainWindow` once `RootNavigation` is `Loaded`.
- **Verified:** Release build **0/0**, **189/189** unit tests pass. **Not verified (headless
  env):** the actual dark-mode text rendering and the WelcomePage landing — needs a user
  visual check. SDD → v0.12.2 (§13 + Changelog).
- **Next:** user confirms visually; delivered via branch `fix/dark-mode-text-and-default-page` → PR.

### 2026-07-08 — Docs: "personal project" scope note

- **Done:** added a note that FFMedia, though public, is developed primarily for the author's
  personal use and shipped as-is (no maintenance/support commitment). Placed a `> [!NOTE]`
  callout under the README intro + a bullet in the README Legal section, and a scope note in
  SDD §1. SDD → v0.12.1. No code change.
- **Next:** none pending for this; unchanged from prior (user's headed dry-run of the M6/UI work).

### 2026-07-08 — Post-v1 UI fixes (dark-mode text, footer icons, title bar)

- **Done:** three shell fixes reported after installing v1.0.0.
  1. **Dark-mode font was black** — page `TextBlock`s had no explicit `Foreground`, so they
     inherited WPF's default **black** (fine on light, invisible on dark). `MainWindow`
     (`FluentWindow`) now sets `Foreground="{DynamicResource TextFillColorPrimaryBrush}"`;
     inheritance themes all page text and buttons keep their own template foreground. This
     also makes the (previously black-on-dark) title text visible.
  2. **History/Settings icons missing** — swapped the raw-glyph `FontIcon`s for WPF-UI
     `SymbolIcon` (`SymbolRegular.History24`/`Settings24`), which use the bundled icon font
     (no dependency on an OS-installed Segoe icon font).
  3. **Title bar** — added the logo at top-left via `ui:TitleBar.Icon` + the "FFMedia"
     title; **removed the title-bar theme toggle** (theme already lives in Settings → Theme
     combo). Dropped `MainWindowViewModel`'s now-dead `ToggleThemeCommand` and its unused
     `ISettingsService`/`ThemeService` ctor deps.
- **Second round (same branch/PR):**
  4. **YouTube Downloader nav icon missing** — the tool icon was still a raw-glyph `FontIcon`
     (same unreliable path as the footer). Reinterpreted `ITool.IconGlyph` as a WPF-UI
     **`SymbolRegular` name** (`YouTubeDownloaderTool` → `"ArrowDownload24"`); the shell now
     `Enum.TryParse`s it into a `SymbolIcon` (fallback `Apps24`). Core stays UI-agnostic (still
     just a string).
  5. **Settings Save button removed** — settings now **auto-save** on change (`On<Property>Changed`
     → `Persist()`); theme applies live; **max concurrency** (read once at construction, §12)
     shows a **red "takes effect after you restart"** reminder when changed from the launch value.
     Folder box saves on focus-loss (dropped `UpdateSourceTrigger=PropertyChanged`).
  6. **History open-file/folder feedback** — `HistoryViewModel` gained `INotificationService`;
     a missing file/folder now raises a `Warning` notification (and, if only the file is gone,
     opens its parent folder), plus an `Error` notification if `Process.Start` throws.
- **Verified:** Release build **0/0**, **189/189** unit tests pass. **Not verified (headless
  env):** the actual dark-mode appearance, all icon rendering, title-bar layout, settings
  auto-save UX, and the History notifications — needs a user visual check. SDD → v0.12.
- **Next:** user confirms the fixes visually; delivered via branch
  `fix/ui-dark-theme-titlebar-icons` → PR #11.

### 2026-07-08 — Public-repo audit + licensing & disclaimers

- **Context:** repo was made public (to fix anonymous Velopack update checks), so audited
  for anything that shouldn't be exposed and for missing legal disclaimers.
- **Audit result (clean):** no secrets/credentials/keys in tracked files (only false
  positives like `CancellationToken`, built-in `secrets.GITHUB_TOKEN`); no machine paths
  (`C:\Users\…`) or PII in source; `.gitignore` correctly excludes binaries/logs/artifacts.
  Docs are professional (the "DRM/bypass" hits are appropriate non-goal disclaimers or the
  `-ExecutionPolicy Bypass` flag). Binaries are git-ignored, so the **repo** ships no GPL
  binary — only the **release installer** does.
- **Done:** added **`LICENSE`** (MIT, user-chosen) and **`THIRD-PARTY-NOTICES.md`** (yt-dlp
  Unlicense; bundled FFmpeg GPL-3.0 `win64-gpl` build with source links + trademark/
  non-affiliation notes; NuGet deps + licenses). Expanded README **License** + **Legal &
  disclaimer** sections (responsible use, no DRM circumvention, non-affiliation, no-warranty)
  and fixed the tech-stack (FFMpegCore is planned, not yet used). SDD §16 + Changelog → v0.11.
- **Advice pending user decision (asked, chose "advise me"):** keep the **GPL** ffmpeg build
  (current; supports x264/x265 re-encode incl. `PreciseCut`, GPL notice is easy to satisfy)
  vs switch to the **LGPL** build (lighter obligations, but loses GPL-only encoders). My
  recommendation: **keep GPL + comply via the notices file** unless minimizing GPL exposure
  matters more than re-encode support. `fetch-binaries.ps1` left unchanged.
- **Resolved:** user chose to **keep the GPL ffmpeg build** (comply via `THIRD-PARTY-NOTICES.md`);
  `fetch-binaries.ps1` unchanged. Note: the personal git-author email is in commit history
  (standard; only changeable going forward via a noreply address).

### 2026-07-08 — Fix: in-app "Update check failed" after first release

- **Symptom:** after installing v1.0.0, Settings → "Check for updates now" showed
  "Update check failed. See logs." **Root cause (from the Serilog file log at
  `%AppData%\FFMedia\logs`):** `Velopack.Sources.GithubSource.GetReleases` → **HTTP 404**.
  The repo `ChamHC-dev/ff-media` was **private**, but the update check runs **anonymously**
  (`VelopackUpdateService` uses `GithubSource(..., accessToken: null, ...)`); GitHub returns
  404 (not 403) to anonymous callers on a private repo. Not a code bug — the v1.0.0 release
  itself was complete (`RELEASES`, `releases.win.json`, full nupkg, Setup.exe all present).
- **Fix:** made the repo **public** (user-approved). Verified the exact chain the app walks
  is now anonymous-200: `GET /releases` → 200, and the `releases.win.json` asset → 200. A
  distributed desktop app can't ship a GitHub token safely (extractable from the `.exe`), so
  public is the correct distribution model. SDD §15 updated with this requirement.
- **Note:** the installed app is already on the latest stable (v1.0.0), so "Check for updates
  now" will now report **"You're up to date"** — to exercise the banner, publish a higher tag
  (e.g. `v1.0.1`). The `v0.9.0.0` **pre-release** is ignored by the stable channel.
- **Next:** unchanged — user's headed dry-run of M6 PR 2 (Binaries section, real `yt-dlp -U`,
  logo surfaces); publish `v1.0.1` when there's a change to ship to see the update loop.

### 2026-07-08 — M6 Ship v1 (PR 2: binary updates + app logo)

- **Done:** yt-dlp self-update + pinned binaries + app logo. Core gained `IProcessRunner`/
  `ProcessRunner` (the process seam, SDD §6) and `IBinaryUpdateService`/`BinaryUpdateService`
  (installed versions via `--version`/`-version`, `yt-dlp -U` self-update, and a GitHub
  latest-version check). A singleton `BinaryUpdateViewModel` drives a Settings **Binaries**
  section (yt-dlp + ffmpeg versions, "Update yt-dlp", "check yt-dlp on startup" toggle) and a
  fire-and-forget startup check that notifies (never auto-applies). `AppSettings` → schema
  **v3** (`CheckYtDlpForUpdatesOnStartup`, default true). `fetch-binaries.ps1` now pins
  yt-dlp **2026.07.04** and ffmpeg BtbN **autobuild-2026-07-07-13-44** and **verifies SHA-256**
  (throws on mismatch). `logo.png` moved to `assets/branding/`, converted to a committed
  multi-res `app.ico` (via `build/make-icon.ps1`), and wired as the exe/window/taskbar/
  installer icon + in-app branding (title bar, left of the theme toggle, + welcome page). **Verified:** Release build
  0/0, all **172/172** unit tests pass (`Category!=Integration`), pinned `fetch-binaries.ps1`
  runs and verifies clean. **Not verified (pending user dry-run):** headed GUI smoke of the
  Binaries section, the real `yt-dlp -U`, and the logo surfaces. SDD → v0.10.
- **Review fixes (whole-branch review before opening the PR):** the GitHub latest-version
  check now surfaces the remote tag only when **strictly newer** than installed — a new pure,
  unit-tested `YtDlpVersion.IsNewer` (component-wise compare of the dot date tags, tolerant of
  zero-padding skew) replaces the prior "any inequality" check that could nag forever on a
  locally-newer install; the Core `HttpClient` gained an explicit 10 s timeout; and the
  latest-check failure paths (HTTP error, malformed JSON, installed-is-newer) are now tested.
  Re-verified: Release build **0/0**, **189/189** unit tests pass. Reviewer's remaining notes
  (no `ProcessRunner` timeout, vestigial `AppSettings.Version`, `make-icon.ps1` path style)
  were triaged as out-of-scope Minors and left for later.
- **Decisions:** yt-dlp self-update via `yt-dlp -U`; ffmpeg has no self-update (rides app
  releases); startup check notifies only; both binaries pinned + hash-verified (ffmpeg hash
  computed once from the pinned zip); logo used everywhere. App-layer VMs verified by build +
  manual per the M5/M6 precedent.
- **Next:** user performs the headed dry-run; the public **v1.0.0** tag (machinery proven in
  PR 1) is user-initiated.

### 2026-07-07 — M6 Ship v1 (PR 1: packaging + app auto-update)

- **Done:** Velopack packaging + delta auto-update. Explicit `Program.Main` runs
  `VelopackApp.Build().Run()` before WPF (App.xaml switched to a `Page`, `<StartupObject>`
  set). Core `IUpdateService`/`AppUpdateInfo` realized in App by `VelopackUpdateService`
  (Velopack `UpdateManager` + GitHub `GithubSource`, stable channel; safe no-op when
  uninstalled/dev). Singleton `UpdateViewModel` drives a dismissible shell **update banner**
  (Update & restart / Later) and a Settings **"Check for updates now"** action + current-version
  display; a new `AppSettings.CheckForUpdatesOnStartup` (schema **v2**) gates a fire-and-forget
  startup check that never blocks/crashes launch. `build/pack.ps1` (publish self-contained +
  `vpk pack`, unsigned) + tag-gated `.github/workflows/release.yml` (`vpk upload github`).
  Velopack pinned at **1.2.0** (NuGet package + `vpk` CLI, matched versions).
  **Verified:** solution builds Release **0 warnings / 0 errors**, all **152/152** unit tests
  pass (`Category!=Integration`), and `build/pack.ps1` was run for real and produced an actual
  `FFMedia-win-Setup.exe` (~147 MB) + delta nupkg + `RELEASES` metadata locally — the pack
  machinery is proven end-to-end. `vpk`/`vpk upload github` flags were confirmed against the
  installed CLI's `--help` output. **Not verified (pending user dry-run):** the interactive
  install → pack 0.9.1 → banner appears → "Update & restart" → relaunch onto 0.9.1 loop, and a
  GUI smoke of the shell update banner and the Settings update section — this build environment
  is headless and can't drive a GUI, so these were reviewed by code/build inspection only, not
  exercised. SDD → v0.9.
- **Decisions:** update feed = GitHub Releases; UX = check-on-startup + manual (no silent
  installs); unsigned for v1 (SmartScreen accepted; `--signParams` seam left in `pack.ps1`);
  the real public **v1.0.0** tag is left to the user (machinery + local pack dry-run only, no
  tag pushed, no GitHub Actions release run performed). App-layer VMs
  (`UpdateViewModel`/`SettingsViewModel`) verified by build + manual per the M5 precedent
  (Tests doesn't reference the WinExe); only `AppSettings` migration is unit-tested.
- **Next:** M6 PR 2 — yt-dlp self-update (`IProcessRunner` + `IBinaryUpdateService`), binary
  version display in Settings, pinned `fetch-binaries.ps1` with hash checks. Before that: user
  performs the pending interactive dry-run (install → banner → update → relaunch) and GUI smoke
  of the banner/Settings controls; a whole-branch review runs before this PR is opened.

### 2026-07-06 — M5 Experience (PR 2: presets, history, notifications)

- **Done:** Presets — `IPresetService`/`PresetService` (Core, JSON-backed via
  `JsonStore<T>` at `presets.json`, `Changed` event) + module `PresetMapping`
  (serializes/deserializes `DownloadConfig` to an opaque payload string, tolerant of
  blank/malformed input) + `DownloaderViewModel` save/apply/delete preset commands +
  an inline Presets section (dropdown, Apply/Delete, "save as") on the Downloader page
  — no separate presets screen. History — `IHistoryService`/`HistoryService` (Core,
  JSON-backed at `history.json`, newest-first, `Changed` event) + a `DownloadManager`
  completion hook (two optional trailing ctor params, `IHistoryService?`/
  `INotificationService?`): `Completed` appends a `HistoryEntry` + notifies success,
  `Failed` notifies only (no history row), `Canceled` does neither; dispatched inside
  `RunAndTrackAsync` before the idle signal, wrapped in try/catch so a broken sink
  can't break the queue. A new **History** screen (footer nav, above Settings) shows a
  filterable list with per-row open file/open folder + a clear-history action.
  Notifications — `INotificationService` realized in the App layer as
  `SnackbarNotificationService`, wrapping WPF-UI's `ISnackbarService` via a
  `SnackbarPresenter` overlaying the shell (severity → Success/Caution/Danger/Info).
  SDD → v0.8, M5 marked complete.
- **Decisions:** re-download from history **deferred** — needs a cross-page seeding
  seam (`DownloaderViewModel` is DI-transient, so there's no existing channel for the
  History page to hand it a config) and a richer `HistoryEntry` that stores the
  serialized `DownloadConfig`, not just the `Format` label; failed jobs are notified
  but not written to history (only `Completed` rows persist); preset payload
  deserialization is tolerant (blank/malformed → `DownloadConfig.Default`); native
  Windows toast notifications stay deferred to M6 (in-app snackbar only, per PR 1's
  decision). Known follow-up (non-blocking): `HistoryViewModel` subscribes to
  `IHistoryService.Changed` with no unsubscribe and is DI-transient, so repeated page
  visits accumulate handlers — candidate fixes are a singleton VM or detaching on
  `Unloaded`.
- **Next:** M6 — Velopack installer + delta auto-update, yt-dlp/ffmpeg update flow,
  v1 release.

### 2026-07-06 — M5 Experience (PR 1: foundation)

- **Done:** Persistence foundation + settings + theming. `JsonStore<T>` (Core) does
  atomic temp-file writes and quarantines a corrupt file to `.bak` before returning a
  default. `AppSettings` (`Version`/`DefaultOutputFolder`/`MaxConcurrency`/`Theme`) +
  `ISettingsService`/`SettingsService` persist to `%AppData%\FFMedia\settings.json`;
  `AddFFMediaCore` gained a `dataDirectory` param and registers the service. App gained
  `ThemeService` (light/dark/system via WPF-UI `ApplicationThemeManager`), a **Settings**
  screen (footer nav) with folder/concurrency/theme, a title-bar theme toggle, and
  startup theme application. Wired into behavior: downloader output folder seeded from
  settings; `DownloadManager` concurrency cap read from settings at construction. SDD → v0.7.
- **Decisions:** history stored as JSON (resolves §19); notifications in-app only
  (Windows toast deferred to M6); concurrency applied at launch (live re-tuning deferred);
  App-layer VMs verified by build + manual run (Tests doesn't reference the WinExe; UI is
  thin per §14). Presets/history/notifications land in PR 2 (`feat/m5-presets-history`).
- **Next:** M5 PR 2 — presets (inline), history + screen, in-app notifications, and the
  `DownloadManager` completion hook.

### 2026-07-05 — M4 Processing

- **Done:** `ProcessingOptions` (`TrimRange?` Trim, `PreciseCut`, `EmbedSubtitles`,
  `SubtitleLanguage`, `EmbedMetadata`, `EmbedThumbnail`; default metadata+thumbnail ON,
  subs+trim off, language "en") added to `DownloadConfig.Processing`. Pure
  `OptionSetBuilder.ApplyProcessing` emits: trim → `--download-sections "*<start>-<end>"`
  (keyframe-fast), `PreciseCut` additionally emits `--force-keyframes-at-cuts`; subtitles
  **video-only** → `--write-subs --write-auto-subs --embed-subs --sub-langs <lang>`;
  `--embed-metadata`/`--embed-thumbnail` from the flags. Pure `TrimParsing` parses
  HH:MM:SS / MM:SS / seconds into a `TimeSpan`, producing a range only when both ends
  parse and End > Start. `DownloaderViewModel` gained processing selections (+ live
  `TrimHint` validation) that assemble `ProcessingOptions` per job; the page gained a
  "Processing" section (trim start/end + precise cut, embed subtitles + language, embed
  metadata/thumbnail). All processing flows per-job through the M3 queue. SDD synced to
  v0.6 (§7.3 processing flags, §8 trim-via-yt-dlp note, §17 M4 row).
- **Decisions:** precise-cut is a per-download toggle (not global); subtitles are
  video-only (ignored for audio downloads); metadata + thumbnail default ON; embedding a
  thumbnail is container-dependent — works for mp4/mkv/mp3/m4a, yt-dlp warns (but still
  proceeds) for webm/opus; trim uses yt-dlp's own `--download-sections` rather than a
  post-download `FFMedia.Media`/FFMpegCore pass — the FFMpegCore trim wrapper stays
  reserved for future tools that need frame-accurate cutting independent of yt-dlp.
- **Next:** M5 — settings, presets, history, notifications, dark/light theming.

### 2026-07-05 — M3 Queue

- **Done:** Download queue engine in `FFMedia.Tools.YouTubeDownloader`: `DownloadJob`
  (observable `Status`/`Progress`/`ProgressText`/`ErrorMessage`/`OutputPath` + per-job
  `CancellationTokenSource`), `JobStatus { Queued, Downloading, Processing, Completed,
  Canceled, Failed }`, `RetryPolicy` (transient-error classification + exponential
  backoff, default 3 attempts/1s), and `IDownloadManager`/`DownloadManager` (bounded
  concurrency via `SemaphoreSlim`, default cap 3; auto-start on enqueue; per-job cancel
  + cancel-all; clear-completed; failure isolation; `IdleAsync()` for deterministic
  tests). Added playlist/channel expansion (`IPlaylistProbe`/`YtDlpPlaylistProbe` +
  pure `PlaylistMapping`/`MediaEntry`) so a playlist URL becomes one job per entry at
  add-time. `DownloaderViewModel` restructured from single probe/download to
  "add to queue" with a bound `Jobs` list; the page shows the queue with per-job
  progress/cancel plus cancel-all/clear-completed. Trait-gated queue integration test
  added. SDD synced to v0.5 (§6 queue placement, §7.2 realized state machine, §12
  concurrency model, §17 M3 row, §19 resolutions).
- **Decisions:** auto-start on add (no separate "start" step); transient-only
  auto-retry with exponential backoff, permanent errors fail fast; probing/playlist
  expansion happens at add-time so `DownloadManager` stays a pure download engine (no
  `Fetching` state inside it); cancel-only for M3 (no pause/resume — stays a stretch
  goal per §19); concurrency cap = 3 is a constant this milestone (user-configurable
  deferred to M5); queue lives in the YouTube Downloader module, not `FFMedia.Core`
  (it orchestrates the module's own `IMediaProbe`/`IDownloadService`) — the generic
  bounded-concurrency shape can move to Core if a second tool needs it.
- **Next:** M4 — trim/clip, subtitles, and metadata + thumbnail embedding.

### 2026-07-05 — M2 Formats

- **Done:** Full format matrix — a pure, exhaustively-tested `OptionSetBuilder` maps a
  `DownloadConfig` to yt-dlp options: video MP4/MKV/WebM at a resolution cap, or audio-only
  MP3/WAV/M4A/Opus/FLAC with a bitrate (lossless ignores it). ViewModel exposes the
  selections; the page gained Video/Audio + format/quality dropdowns. SDD §7.3 finalized.
- **Changed:** dropped M1's `RecodeVideo` (re-encode) for `MergeOutputFormat` (mux); removed
  `DownloadOptions.Mp4`; `DownloadRequest` now carries a `DownloadConfig`. Audio bitrate uses
  `OptionSet.AddCustomOption("--audio-quality", …)` (typed `AudioQuality` is 0–10 VBR only).
- **Next:** M3 — download queue, bounded concurrency, playlist/channel support.

### 2026-07-05 — M1 fix: crash on Probe (missing binaries + no error isolation)

- **Bug:** Clicking **Probe** closed the app with no dialog/log. Root cause (found via
  systematic debugging + a reproduction test): (A) nothing copied `assets/binaries/*`
  into `FFMedia.App`'s output, so the resolved `yt-dlp.exe` path didn't exist, and
  (B) the resulting `Win32Exception` from `Process.Start` was unhandled — services
  didn't catch it, and no global exception handler existed (SDD §11 unimplemented).
- **Fix:** (A) App/Tests csproj now copy `assets/binaries/*.exe` to output; (B)
  `YtDlpMediaProbe`/`YtDlpDownloadService` catch exceptions → `Result.Failure`
  (friendly "run fetch-binaries" message for missing binaries; cancellation still
  propagates), and `App` wires `DispatcherUnhandledException` +
  `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` → Serilog
  `Fatal` + dialog. Added `YtDlpServiceErrorTests`. Verified: build clean, 20 unit
  tests pass, **both integration tests (real probe + real MP4 download) pass**, app
  boots with no Fatal.
- **Next:** M2 — full format matrix (unchanged).

### 2026-07-05 — M1 Vertical Slice

- **Done:** YouTube Downloader tool end-to-end — paste URL → probe (`IMediaProbe`) →
  download single MP4 with live progress + cancel (`IDownloadService`) via
  YoutubeDLSharp; `DownloaderViewModel` unit-tested with fakes; tool page + nav
  wiring so it appears in the shell's `NavigationView`; trait-gated yt-dlp
  integration test (excluded in CI); `Result<T>` + `IToolPage` added to Core.
  Build green, 18 unit tests pass. SDD synced to v0.3.
- **Changed:** `FFMedia.Tools.YouTubeDownloader` + `FFMedia.Tests` retargeted to
  `net9.0-windows` (UseWPF) so ViewModels are unit-testable headlessly; CI test
  step filters `Category!=Integration`.
- **Next:** M2 — full format matrix (video containers + audio-only
  wav/mp3/m4a/opus/flac + quality/resolution); `OptionSet` builder fully tested.

### 2026-07-04 — M0 Foundation

- **Done:** Solution skeleton (Core/Media/Tools/App/Tests); Core `ITool`/`IToolRegistry`,
  `IBinaryProvider`, `AddFFMediaCore` (all unit-tested); WPF-UI Fluent shell with Generic
  Host + Serilog + `NavigationView` seam; `build/fetch-binaries.ps1`; GitHub Actions CI.
- **Changed:** `ITool.Icon` → `string IconGlyph` (keeps Core UI-agnostic); assertions use
  plain xUnit `Assert` (FluentAssertions v8 is paid) — SDD updated to v0.2.
- **Next:** M1 — vertical slice: URL → probe → download single MP4 with progress + cancel.

### 2026-07-04 — Add branch-per-task / PR-review workflow rule

- **Done:** Added standing Rule 3 — always branch off `main` per task and deliver
  via a PR for user review (never commit task work to `main`, never self-merge).
  This change itself delivered on branch `docs/pr-workflow-rule` via PR.
- **Note:** `gh` CLI not yet installed, so PRs are teed up via a GitHub compare
  link rather than opened automatically. Install `gh` to let me open PRs directly.
- **Next:** M0 (Foundation) implementation plan — on its own branch + PR.

### 2026-07-04 — Project bootstrap & design

- **Done:**
  - Initialized git repo, wired `origin` → `github.com/ChamHC-dev/ff-media.git`,
    branch `main` (pushed).
  - Ran research (YoutubeDLSharp, FFMpegCore vs Xabe, WPF vs WinUI 3, Velopack).
  - Brainstormed & locked v1 decisions: WPF + WPF-UI / .NET 9, bundled binaries,
    full-featured downloader, modular shell.
  - Wrote **`SDD.md`** (single source of truth): architecture, stack, downloader
    design, testing strategy, milestones M0–M7.
  - Added brainstorming spec record (`docs/superpowers/specs/`), `README.md`,
    `.gitignore`.
  - Created this `CLAUDE.md` with standing rules + progress log.
- **State:** Design phase complete. No code/solution scaffolded yet.
- **Next:** Turn **Milestone M0 (Foundation)** into a detailed implementation plan,
  then scaffold the solution (shell + Core + DI + binary management).
