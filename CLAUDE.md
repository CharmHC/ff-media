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

## 📓 Progress Log

_Newest first. One entry per completed task/session._

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
