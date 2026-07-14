# FFMedia — Software Design Document (SDD)

> **Status:** Living document · **Version:** 0.27 · **Last updated:** 2026-07-14
>
> **This document is the single source of truth for the FFMedia project.** Any
> architectural decision, scope change, or convention lives here first. Code and
> plans defer to this document; when they disagree, this document wins (and is
> updated to reflect the agreed change).

---

## 1. Overview & Vision

**FFMedia** is a Windows desktop application that serves as an **all-in-one media
toolbox**. It bundles a growing set of media-related "tools" behind a single,
modern UI.

The **first tool** is a **YouTube Downloader**: paste a URL, choose a target
format/quality (mp4, mkv, mp3, wav, m4a, opus, flac, …), and download it locally
with progress and cancellation.

Additional tools are planned (out of scope for v1) — for example: ingest multiple
videos of differing resolutions/formats/frame-rates, standardize them, and merge
into a single video. **Because more tools are coming, the architecture is modular
from day one:** an application shell hosts independent, self-contained tool
modules.

### 1.1 Core technical reality

FFmpeg **cannot** download from YouTube on its own — YouTube uses rotating
signatures, throttling, and DASH/HLS manifests. FFMedia therefore orchestrates
**two external binaries**:

- **`yt-dlp`** — extraction & download of YouTube (and 1000+ other sites') media.
- **`ffmpeg`** — muxing, transcoding, trimming, and post-processing.

FFMedia is, at its heart, a **polished orchestrator** over `yt-dlp` + `ffmpeg`.

> **Project scope:** FFMedia is a **personal project** — built primarily for the author's
> own use and published publicly as-is (MIT, see §16). It is **not** a commercial or
> officially supported product; there is no commitment to maintenance, support, or
> acting on external issues/feature requests. This framing is reflected in the README.

---

## 2. Goals & Non-Goals

### 2.1 v1 Goals (YouTube Downloader tool — full-featured)

- Paste one or more URLs (video, playlist, or channel).
- Probe metadata (title, thumbnail, duration, available formats, playlist entries).
- Choose output: video containers (mp4/mkv/webm) or audio-only (mp3/wav/m4a/opus/flac).
- Choose quality/resolution.
- Download **queue** with **bounded concurrency**.
- **Live progress** (%, speed, ETA) and **cancel** per job.
- **Trim/clip** a section of the media.
- **Embed** metadata + thumbnail; download **subtitles**.
- Persistent **settings**, **presets**, and **download history**.
- In-app **notifications** and dark/light **theming**.
- Bundled `yt-dlp` + `ffmpeg`; **auto-update** for the app and yt-dlp.

### 2.2 Non-Goals (v1)

- Additional tools (video standardize/merge, etc.) — architected for, not built.
- Cross-platform (Windows-only for v1).
- Cloud sync, accounts, or telemetry servers.
- In-app media playback/editing beyond trim.
- Circumventing DRM or paywalls.

---

## 3. Technology Stack

| Concern | Choice | Rationale |
|---|---|---|
| Language / runtime | **C# / .NET 9** | Modern, LTS-adjacent, native Windows. |
| UI framework | **WPF** + **[WPF-UI](https://github.com/lepoco/wpfui)** | Mature, deep ecosystem, best MVVM tooling, Fluent/Win11 look (Mica, dark/light). |
| MVVM | **CommunityToolkit.Mvvm** | Source-generated `[ObservableProperty]` / `[RelayCommand]`. |
| App host / DI | **Microsoft.Extensions.Hosting** | Generic Host → DI, config, logging, module registration. |
| YouTube | **[YoutubeDLSharp](https://github.com/Bluegrams/YoutubeDLSharp)** (≥1.2.0) | Wraps `yt-dlp`; built-in `Progress<DownloadProgress>` + `CancellationToken`. |
| Media processing | **`ffmpeg`/`ffprobe` via `IProcessRunner`** + pure parsers (`FFMedia.Media`) | No new dependency; keeps all process work behind the fakeable seam that makes orchestration testable. **FFMpegCore was dropped in M7** — it spawns its own processes, bypassing `IProcessRunner`, and had never been referenced. |
| Logging | **Serilog** (file + in-app sink) | Diagnose yt-dlp/ffmpeg failures from user logs. |
| Persistence | **System.Text.Json** (settings/presets/history) | Simple; migrate history to SQLite only if it grows. |
| Packaging / update | **[Velopack](https://velopack.io/)** (pinned **1.2.0** — NuGet package + `vpk` CLI tool, matched versions) | Installer + delta auto-update, no UAC prompt; can update bundled yt-dlp. |
| Testing | **xUnit** | Tests use xUnit. Assertion library deferred — FluentAssertions v8+ is a paid commercial license; evaluate **Shouldly** / **AwesomeAssertions** (both free) when richer assertions are needed. M0 uses plain `Assert`. |

> **Rejected alternatives:** WinUI 3 (rougher windowing/packaging for a solo dev),
> Xabe.FFmpeg (CC BY-NC-SA / non-commercial), Electron/Tauri (heavier, non-native),
> Python/PyQt (weaker native Windows packaging story), **FFMpegCore** (MIT, but it
> manages its own child processes — using it would bypass the `IProcessRunner` seam
> that the rest of the codebase is tested through; see §8).

---

## 4. High-Level Architecture

FFMedia is an **application shell** that discovers and hosts **tool modules**.
Each tool is independent, communicates through well-defined `FFMedia.Core`
abstractions, and can be developed/tested in isolation.

```
┌─────────────────────────────────────────────────────────┐
│  FFMedia.App  (WPF shell)                                │
│  • Generic Host + DI composition root                    │
│  • WPF-UI NavigationView ── discovers registered ITools  │
│  • Global exception handler, theming, Serilog bootstrap  │
└───────────────┬─────────────────────────────────────────┘
                │ resolves ITool modules via DI
      ┌─────────┴───────────┬───────────────────────┐
      ▼                     ▼                        ▼
┌──────────────┐   ┌──────────────────┐   ┌───────────────────┐
│ YouTube      │   │ Video Merger     │   │ (future) more     │
│ Downloader   │   │ standardize+merge│   │ tools…            │
│ (v1 module)  │   │ (M7 module)      │   │                   │
└──────┬───────┘   └──────────────────┘   └───────────────────┘
       │ uses
       ▼
┌──────────────────────────────────────────────────────────┐
│ FFMedia.Core  (UI-agnostic services & abstractions)      │
│  ITool · IBinaryProvider · ISettingsService ·            │
│  IHistoryService · INotificationService ·                │
│  IProcessRunner                                          │
├──────────────────────────────────────────────────────────┤
│ FFMedia.Media — ffmpeg/ffprobe over IProcessRunner       │
├──────────────────────────────────────────────────────────┤
│ Bundled binaries:  assets/binaries/yt-dlp.exe            │
│                    assets/binaries/ffmpeg.exe            │
│                    assets/binaries/ffprobe.exe           │
└──────────────────────────────────────────────────────────┘
```

### 4.1 The module contract (`ITool`)

```csharp
public interface ITool
{
    string Id { get; }              // stable, e.g. "youtube-downloader"
    string DisplayName { get; }     // "YouTube Downloader"
    string Description { get; }
    string IconGlyph { get; }       // WPF-UI SymbolRegular name (e.g. "ArrowDownload24"); string keeps Core UI-agnostic
    int SortOrder { get; }
}
```

- Each tool registers its `ITool`, its root `ViewModel`, and its services in a
  module-owned `IServiceCollection` extension (`AddYouTubeDownloader(...)`).
- The shell enumerates all registered `ITool`s, builds the `NavigationView`, and
  hosts the selected tool's view. **Adding a tool never modifies the shell.**
- Views are matched to ViewModels by naming convention (`FooViewModel` → `FooView`)
  via a `ViewLocator`.
- A tool advertises its root page to the shell via `IToolPage { string ToolId; Type PageType; }`
  (Core, `System.Type` only — keeps Core UI-agnostic). The shell joins registered
  `ITool`s with their `IToolPage`s to build the `NavigationView` items.

---

## 5. Solution / Project Structure

```
ff-media/
├─ FFMedia.sln
├─ SDD.md                        ← this document (single source of truth)
├─ README.md
├─ .gitignore
├─ assets/
│  └─ binaries/                  ← bundled yt-dlp.exe, ffmpeg.exe, ffprobe.exe (git-ignored; fetched by build script)
├─ build/                        ← packaging scripts (Velopack), binary-fetch script
├─ docs/
│  └─ superpowers/specs/         ← brainstorming spec record (points here)
└─ src/
   ├─ FFMedia.App/               ← WPF shell (composition root, shell views, theming)
   ├─ FFMedia.Core/              ← abstractions + services, NO WPF references
   ├─ FFMedia.Media/             ← ffmpeg/ffprobe access + pure parsers (shared media ops)
   ├─ FFMedia.Ui/                ← M9 shared WPF layer: controls + VMs reusable by ANY tool
   ├─ FFMedia.Tools.YouTubeDownloader/  ← v1 tool module (VMs, Views, orchestration)
   ├─ FFMedia.Tools.VideoMerger/ ← M7 tool module (standardize + merge)
   ├─ FFMedia.Tools.GifMaker/    ← M8 tool module (video → GIF)
   └─ FFMedia.Tests/             ← xUnit tests (targets Core + module logic)
```

**Target frameworks:** `FFMedia.Core` and `FFMedia.Media` target `net9.0` and stay
UI-framework-free. **Tool modules that hold WPF Views/ViewModels**
(e.g. `FFMedia.Tools.YouTubeDownloader`), **and `FFMedia.Ui`**, target
**`net9.0-windows` with `UseWPF=true`**. **`FFMedia.Tests` targets `net9.0-windows`**
so it can reference the module and unit-test ViewModels headlessly (no window is shown).

**Dependency rules (enforced by project references):**

- `FFMedia.Core` references **no** UI framework. It is the testable heart.
- `FFMedia.Media` references `FFMedia.Core` only (no third-party media library).
- **`FFMedia.Ui` references `FFMedia.Core` + `FFMedia.Media` only.** It must never
  reference a tool, and no tool may reference `FFMedia.App`.
- Tool modules reference `FFMedia.Core` (+ `FFMedia.Media`, **`FFMedia.Ui`**, WPF-UI).
  They **do not** reference `FFMedia.App`. Critically, **a tool must never reference
  another tool** — shared code moves down into `Core`/`Media`/`Ui` instead (see the
  M8 and M9 notes immediately below).
- `FFMedia.App` references `FFMedia.Core` + each tool module (composition root only).
- Dependencies point **inward** toward `Core`; `Core` depends on nothing app-specific.

> **M9 note — why `FFMedia.Ui` exists.** The video preview is a **shared capability, not
> a GIF Maker feature**: every tool here asks the user for a moment in a video as *blind
> timecode text*, and §19 had already recorded the same conclusion from the other side,
> deferring the merger's per-clip trim because it *"needs a preview scrubber to be usable,
> not blind timecode entry."* A `UserControl` cannot live in `FFMedia.Media` (which is
> plain `net9.0` with no WPF, deliberately) and it cannot live in a tool (a tool must never
> reference another tool) and it cannot live in `FFMedia.App` (no tool may reference the
> WinExe). So it gets its own layer. Building it **shared from the start** is what lets M10
> *adopt* the preview rather than run a promotion task, the way `Resolution` and
> `TrimParsing` had to in M8.

> **M8 note:** `Resolution` (previously living in the Video Merger module) moved to
> `FFMedia.Media`, and `TrimParsing.TryParse` (previously in the YouTube Downloader
> module) moved to `FFMedia.Core.Media`. Both are pure, tool-agnostic types that the
> GIF Maker also needed — promoting them was the only way to reuse them without the
> GIF Maker referencing another tool module, which the rule above forbids.

---

## 6. Core Abstractions & Services

All defined in `FFMedia.Core`, injected via DI, and fakeable in tests.

| Service | Responsibility |
|---|---|
| `IProcessRunner` | Launch a child process, stream stdout/stderr, honor `CancellationToken`. The seam that makes orchestration testable without real binaries. |
| `IBinaryProvider` | Resolve/verify bundled `yt-dlp.exe` & `ffmpeg.exe` paths; report versions; trigger yt-dlp self-update. |
| `ISettingsService` | Load/save app settings (JSON in `%AppData%\FFMedia`). |
| `IPresetService` | CRUD saved download presets. |
| `IHistoryService` | Append/query completed-download history. |
| `INotificationService` | In-app snackbar/toast + optional Windows toast. |
| `IErrorMapper` | Map raw yt-dlp/ffmpeg stderr to friendly, actionable messages. |

> **M3 note:** the download queue (`IDownloadManager`/`DownloadJob`, plus `RetryPolicy`
> and `IPlaylistProbe`) was **not** built in `FFMedia.Core` as originally sketched above.
> It orchestrates the YouTube Downloader module's own `IMediaProbe`/`IDownloadService`,
> so it lives in `FFMedia.Tools.YouTubeDownloader` instead (see §7 and §12). The generic
> bounded-concurrency pattern (`SemaphoreSlim` cap + per-job `CancellationTokenSource`)
> may be lifted into `FFMedia.Core` if a second tool needs the same shape — YAGNI for now.

> **M5 PR 1 note:** `ISettingsService` is now **realized** in `FFMedia.Core` — a
> JSON-backed `SettingsService` (built on a generic `JsonStore<T>`) persists
> `AppSettings` to `%AppData%\FFMedia\settings.json`. `IPresetService`, `IHistoryService`,
> and `INotificationService` remain **planned**, targeted for M5 PR 2.

> **M5 PR 2 note:** `IPresetService`, `IHistoryService`, and `INotificationService` are
> now **realized**. `PresetService` and `HistoryService` are JSON-backed (same
> `JsonStore<T>` foundation, `presets.json`/`history.json`), each exposing a `Changed`
> event for UI refresh. `INotificationService` is realized in the App layer as
> `SnackbarNotificationService`, wrapping WPF-UI's `ISnackbarService` (in-app snackbar
> only; native Windows toast remains deferred to M6, per §13).

> **M6 PR 1 note:** a new Core abstraction `IUpdateService` (with `AppUpdateInfo`) is
> **realized** in the App layer as `VelopackUpdateService`, wrapping Velopack's
> `UpdateManager` + a GitHub `GithubSource` (stable channel). `FFMedia.Core` stays
> packaging-free — it only defines the interface/DTO; `VelopackUpdateService` is a
> safe no-op (`CheckForUpdatesAsync` returns "no update") when the app is not an
> installed Velopack app (e.g. running via `dotnet run`/dev), so nothing crashes
> outside an installed context.

> **M6 PR 2 note:** `IProcessRunner`/`ProcessRunner` (`FFMedia.Core.Processes`) and
> `IBinaryUpdateService`/`BinaryUpdateService` (`FFMedia.Core.Binaries`) are now
> **realized**. `IProcessRunner` is the generic child-process seam (launch, stream
> stdout/stderr, honor `CancellationToken`) that makes binary orchestration testable
> without real binaries. `BinaryUpdateService` reads the installed `yt-dlp`/`ffmpeg`
> versions via `--version`/`-version` (the latter parsed by the pure
> `FfmpegVersionParsing`), self-updates yt-dlp via `yt-dlp -U`, and additionally
> queries the GitHub API (`releases/latest` on `yt-dlp/yt-dlp`) to report whether a
> newer yt-dlp is available (see §9).

---

## 7. YouTube Downloader Module (detailed)

### 7.1 Data flow

1. **Input** — user pastes one or more URLs.
2. **Probe** — `YoutubeDLSharp.RunVideoDataFetch` → title, thumbnail, duration,
   available formats, playlist entries. UI shows a preview card per URL.
3. **Configure** — output kind (video/audio), container/codec, quality/resolution,
   optional trim range, subtitles, embed metadata+thumbnail, output folder.
   Config may be seeded from a **preset**.
4. **Enqueue** — a `DownloadJob` is created and pushed to `IDownloadManager`.
5. **Run** — a worker builds a yt-dlp `OptionSet` from the config, executes via
   `YoutubeDLSharp`, forwards `Progress<DownloadProgress>` to the ViewModel, and
   passes the job's `CancellationToken`.
6. **Post-process** — yt-dlp performs recode / audio-extract / trim / subtitle &
   metadata/thumbnail embed. Trim is realized via yt-dlp `--download-sections`
   (`--force-keyframes-at-cuts` for precise cuts) rather than a `FFMedia.Media` pass
   (see §8).
7. **Complete** — notify, write to history, expose "Open folder" / "Open file".

### 7.2 Job state machine

**M3-realized state machine** (`JobStatus`, `FFMedia.Tools.YouTubeDownloader`):

```
Queued ─▶ Downloading ─▶ Processing ─▶ Completed
   │            │              │
   └────────────┴──────────────┴────▶ Canceled
                │
                └──────────────────▶ Failed  (+ retry on transient network, same job)
```

- **Fetching happens at add-time, before a job exists.** `IPlaylistProbe.ExpandAsync`
  resolves a URL into one (`MediaEntry`) per video, or N for a playlist/channel, when
  the user adds it. Each resolved entry becomes a `DownloadJob` (`Url`/`Title`/
  `DownloadConfig`/`OutputFolder` already known) and is handed to `IDownloadManager`,
  which is therefore a pure download engine over `Queued → Downloading → Processing →
  {Completed | Canceled | Failed}` — no separate `Fetching` state inside the manager.
- **Failure isolation:** each job runs in its own tracked task; a failed/canceled job
  never stalls the queue or affects siblings.
- **Retry policy (`RetryPolicy`):** transient network errors (timeout, connection
  reset, 5xx, DNS failure, …) are retried **on the same job** with exponential backoff
  (`baseDelay · 2^(attempt-1)`), default **3 attempts / 1s base**; non-transient errors
  (private/removed/geo-blocked/etc.) fail fast with no retry. Classification is a pure,
  unit-tested function (`RetryPolicy.IsTransient`); cancellation is never retried.

> **M5 PR 2 amendment:** `DownloadManager` now performs **terminal-transition side
> effects** through two optional, Core-only abstractions injected via its constructor
> (`IHistoryService?`, `INotificationService?`, both `null` by default so Core-only
> hosts/tests are unaffected): on `Completed` it appends a `HistoryEntry` and raises a
> success `Notification`; on `Failed` it raises an error `Notification` only (no history
> row); `Canceled` raises neither. The dispatch happens inside `RunAndTrackAsync` after
> `RunAsync` completes and before the idle signal, so `IdleAsync()` observes the side
> effects deterministically, and the call is wrapped in its own try/catch so a throwing
> history/notification implementation can never break the queue's active-count/idle
> bookkeeping. This is a **best-effort side effect**, not a state in the machine above —
> the `Queued → Downloading → Processing → {Completed | Canceled | Failed}` shape, the
> `SemaphoreSlim` concurrency cap, and per-job cancellation are all unchanged. `Download-
> Manager` still has **no direct UI dependency** — it depends only on the Core
> abstractions, never on WPF-UI or a ViewModel.

### 7.3 Output format matrix

The `OptionSet` builder is a **pure function** `DownloadConfig → yt-dlp args`
(heavily unit-tested). Representative mappings:

| User choice | yt-dlp options produced (M2/M4, via `OptionSetBuilder`) |
|---|---|
| MP4, cap ≤N | `-f "bv*[height<=N][ext=mp4]+ba[ext=m4a]/b[height<=N][ext=mp4]/bv*[height<=N]+ba/b[height<=N]" --merge-output-format mp4` |
| MP4, Best | as above without any `[height<=N]` filter |
| MKV, cap ≤N | `-f "bv*[height<=N]+ba/b[height<=N]" --merge-output-format mkv` |
| WebM, cap ≤N | `-f "bv*[height<=N][ext=webm]+ba[ext=webm]/b[height<=N][ext=webm]/bv*[height<=N]+ba/b[height<=N]" --merge-output-format webm` |
| Audio MP3/M4A/Opus | `-x --audio-format <fmt> -f "ba/b"` (+ `--audio-quality <n>K` when a specific bitrate is chosen) |
| Audio WAV/FLAC | `-x --audio-format <fmt> -f "ba/b"` (lossless — bitrate ignored) |
| All | `--no-playlist -o "<folder>/%(title)s.%(ext)s"` |
| Trim (fast) | `--download-sections "*<start>-<end>"` (seconds; keyframe cut, no re-encode) |
| Trim (precise) | as above + `--force-keyframes-at-cuts` (exact, re-encodes around the cut) |
| Subtitles (video only) | `--write-subs --write-auto-subs --embed-subs --sub-langs <lang>` |
| Embed metadata | `--embed-metadata` |
| Embed thumbnail | `--embed-thumbnail` (mp4/mkv/mp3/m4a; yt-dlp warns and proceeds for webm/opus) |

> **M2 decisions:** downloads **mux** into the container via `--merge-output-format` (no
> re-encode; M1's `--recode-video` was dropped). Resolution is a **cap** (`[height<=N]`), not a
> per-video format-list selection. Audio bitrate is emitted via `OptionSet.AddCustomOption`
> ("--audio-quality") because the typed `AudioQuality` is the 0–10 VBR scale, not a bitrate.

> **M4 note:** processing (trim, subtitles, metadata, thumbnail) is applied **per-download** via
> `DownloadConfig.Processing` (`ProcessingOptions`) through `OptionSetBuilder.ApplyProcessing`,
> a pure function alongside `Build`. Subtitles are emitted **only for video output** (`OutputKind.Video`) —
> ignored for audio-only downloads.

---

## 8. Media Processing (`FFMedia.Media`)

The shared, UI-free layer for operations FFMedia performs **directly** with
`ffmpeg`/`ffprobe` (as opposed to delegating to yt-dlp). It is realized in **M7**
and is deliberately tool-agnostic — a third tool should reuse it untouched.

| Type | Kind | Responsibility |
|---|---|---|
| `MediaInfo` | record | `Duration`, `ContainerFormat`, `VideoStreamInfo?` (w/h, frame rate, codec, pixel format, rotation), `AudioStreamInfo?` (codec, sample rate, channels), `ExtraStreamCount` (streams beyond the first video + first audio) |
| `IMediaAnalyzer` → `FfprobeMediaAnalyzer` | service | `ffprobe -v error -print_format json -show_format -show_streams` → `Result<MediaInfo>` |
| `FfprobeParsing` | **pure** | ffprobe JSON → `MediaInfo`; returns `null` for anything unusable, never throws |
| `IFfmpegRunner` → `FfmpegRunner` | service | Runs ffmpeg with an arg list + `-progress pipe:1 -nostats`; streams progress; honors `CancellationToken`; captures the stderr tail on failure |
| `FfmpegProgressAccumulator` | **pure** | `out_time_us=…` / `speed=…` lines → one `FfmpegProgress(Position, Speed)` snapshot per `progress=` terminator |

Both services locate their binary through `IBinaryProvider` (no PATH assumption).

> **`-v error`, not `-v quiet`.** `quiet` suppresses the very stderr text the non-zero-exit
> path reports back, degrading "Invalid data found when processing input" to a bare
> "exit code 1". The analyzer's whole value on a corrupt clip is naming what is wrong with it.

> **`ExtraStreamCount` is load-bearing.** `Video`/`Audio` describe only the **first** stream of each
> kind, so without a count the rest of the file is invisible to `ConformanceCheck` — and a clip with
> an embedded subtitle track would look *fully conforming*, take the fast path, and be stream-copied.
> ffmpeg's concat matches segments by stream **index**: the next clip's audio lands on this clip's
> subtitle slot, ffmpeg **exits 0**, and the user gets an output whose later clips are silently mute.
> Concat's identical-layout requirement (§D4) is about **all** streams, not just the two we model.
> Not hypothetical — FFMedia's own YouTube Downloader writes such files when `--embed-subs` is on
> (M4). Any clip with extras is simply **non-conforming**; normalization re-encodes it mapping only
> `0:v:0` + one audio stream, which drops them.

> **Accumulator, not parser.** `FfmpegProgressAccumulator` carries state across lines: ffmpeg
> emits a block of `key=value` lines terminated by `progress=continue|end`, so it folds each
> line in and emits exactly one snapshot per terminator. Still pure — deterministic and IO-free.
> (Note ffmpeg reports `out_time_ms` in **microseconds**, despite the name.)

> **M7 decision — FFMpegCore dropped.** Earlier versions of this document planned
> thin wrappers over **FFMpegCore**. It was never referenced in code, and it manages
> its **own** child processes — adopting it would bypass `IProcessRunner`, the seam
> that makes every other orchestration path in this codebase testable without real
> binaries (§6, §14). Driving `ffmpeg`/`ffprobe` through `IProcessRunner` with **pure
> parsers** adds no dependency and matches the existing `FfmpegVersionParsing`
> precedent in Core. §3 updated to match.

> **M4 note:** the YouTube Downloader's trim/clip feature (§7.3) is realized via yt-dlp's
> own `--download-sections` (+ `--force-keyframes-at-cuts` for a precise cut) rather than a
> post-download `FFMedia.Media` pass — it's simpler and avoids a redundant re-encode.
> Frame-accurate trimming independent of yt-dlp remains a candidate addition to this
> layer if a future tool needs it.

---

## 9. Binary Management

- **Bundling:** `yt-dlp.exe`, `ffmpeg.exe`, and `ffprobe.exe` ship in the installer under
  `assets/binaries/`. They are **git-ignored**; a `build/fetch-binaries` script
  downloads pinned versions for local dev and CI.
- **⚠️ Adding a NEW required binary is invisible to existing checkouts.** `assets/binaries`
  is git-ignored, so a developer whose folder is already populated pulls the code that *needs*
  the new binary without ever receiving it — nothing in the build fails, and the feature dies
  at runtime instead. This happened with **`ffprobe.exe`** (added in M7 PR 1): the merger
  probes every clip with it, and in a checkout that never re-ran the fetch script **every file
  failed to probe**. Whenever this list grows, **re-run `build/fetch-binaries.ps1`** — and say
  so in the PR. Corollary: a missing binary must **never** be reported to the user as a problem
  with their *file* (§11).
- **Resolution:** `IBinaryProvider` resolves the app-relative binary path at
  runtime (`AppContext.BaseDirectory/assets/binaries`); never relies on the system
  PATH. The **`FFMedia.App` and `FFMedia.Tests` builds copy `assets/binaries/*.exe`
  into their output** so `dotnet run` and the integration tests find the binaries
  (no-op when the folder is empty — run `build/fetch-binaries.ps1` first).
- **Updating:**
  - **App + ffmpeg** update via **Velopack** releases. ffmpeg has no independent
    update path — it rides the app's own release cadence (rebundled whenever the
    app is repackaged), same as before.
  - **yt-dlp** additionally supports in-app self-update (`yt-dlp -U`) because it
    breaks frequently against YouTube changes and must update independently of app
    releases. Update checks are user-initiated or on a configurable schedule.
- **Pinned versions (v1):** `yt-dlp` **2026.07.04** (SHA-256
  `52fe3c26dcf71fbdc85b528589020bb0b8e383155cfa81b64dd447bbe35e24b8`) and `ffmpeg`
  from the BtbN builds, tag **autobuild-2026-07-07-13-44**, asset
  `ffmpeg-n8.1.2-22-g94138f6973-win64-gpl-8.1.zip` (zip SHA-256
  `f9fdfc417d5091cb3a3487b484ee824bce4fd6fa92dc85a412142f2911b7a22c`). Both are
  verified by `build/fetch-binaries.ps1` (throws on hash mismatch), satisfying the
  "integrity checks in the build script" requirement in §16.

> **M7 note:** media probing (`IMediaAnalyzer`, §8) requires **`ffprobe.exe`**, which
> earlier milestones did not ship. It already lives **inside the same pinned,
> SHA-256-verified BtbN zip** as `ffmpeg.exe`, so `fetch-binaries.ps1` simply extracts a
> second executable from the already-verified archive — **no new download and no new
> pinned hash**. `ExternalBinary` gains an `Ffprobe` member; the `FFMedia.App` /
> `FFMedia.Tests` copy glob is already `assets/binaries/*.exe`, so `ffprobe.exe` reaches
> the output directory and the Velopack package with no build-script change.

> **M6 PR 1 note:** the **app** update path is now **realized**. `VelopackUpdateService`
> performs a Velopack check-on-startup (gated by `AppSettings.CheckForUpdatesOnStartup`,
> fire-and-forget, never blocks/crashes launch) and a manual "Check for updates now"
> from Settings, both against a GitHub Releases feed (`GithubSource`, stable channel).
> When an update is found, the shell shows a dismissible banner ("Update & restart" /
> "Later") that downloads and applies the update via Velopack, then restarts the app.
> **yt-dlp self-update** (`IProcessRunner` + a dedicated `IBinaryUpdateService`) is
> **not** part of this PR — it's scoped to **M6 PR 2**, unchanged from the plan above.

> **M6 PR 2 note:** the **yt-dlp** update path is now **realized**. `BinaryUpdateService`
> self-updates via `yt-dlp -U` and additionally checks the GitHub API
> (`releases/latest` on `yt-dlp/yt-dlp`) so the Settings screen (§13) can show whether a
> newer yt-dlp is available before the user updates. The check surfaces the remote tag only
> when it is **strictly newer** than the installed one (`YtDlpVersion.IsNewer`, a pure
> component-wise compare of the dot-separated date tags), so a locally-nightly install never
> triggers a perpetual "update available" nag. `pack.ps1`/`release.yml` (§15)
> re-bundle the **pinned** yt-dlp (§9) on every app release, so a fresh app update will
> **revert a prior in-app yt-dlp self-update** back to the pinned version — expected
> behavior; the user can simply re-run "Update yt-dlp" afterward.

---

## 10. Data & Persistence

All under `%AppData%\FFMedia\`:

| File | Content | Format |
|---|---|---|
| `settings.json` | Default output folder, concurrency, theme, update prefs | JSON |
| `presets.json` | Named download presets | JSON |
| `history.json` | Completed downloads (title, url, path, format, timestamp) | JSON → SQLite if it grows |
| `encode-speed.json` | `SpeedProfile` — rolling average of measured encode throughput per `(videoCodec, pixelBucket)`, used for the M7 merge-time estimate | JSON |
| `logs/ffmedia-*.log` | Rolling Serilog logs | text |

Schema changes carry a `version` field for forward migration.

> **M5 PR 1 note:** `settings.json` now exists, written by the generic `JsonStore<T>`
> (atomic temp-file write + corrupt-file quarantine to `.bak`, defaulting on read
> failure). `AppSettings` carries a `Version` field for forward migration, per the
> convention above.

> **M6 PR 1 note:** `AppSettings.Version` moves to **2** with the addition of
> `CheckForUpdatesOnStartup` (`bool`, default `true`), covered by unit tests
> (`AppSettingsUpdateFlagTests`).

> **M6 PR 2 note:** `AppSettings.Version` moves to **3** with the addition of
> `CheckYtDlpForUpdatesOnStartup` (`bool`, default `true`), covered by unit tests
> (`AppSettingsYtDlpFlagTests`).

---

## 11. Error Handling & Logging

- **`IErrorMapper`** translates common yt-dlp/ffmpeg stderr signatures into
  user-friendly, actionable messages: *video unavailable, private, removed,
  geo-blocked, format unavailable, network error, binary missing/outdated*.
- **Per-job isolation** — errors are captured on the job, surfaced in the UI, and
  logged; the queue keeps running.
- **Global exception handler** (`DispatcherUnhandledException` +
  `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`) →
  Serilog + friendly dialog. **No silent crashes.**
- All external-process invocations log the exact (redacted) command line at debug
  level for reproducibility.

---

## 12. Concurrency Model

**Realized in M3** (`DownloadManager`, `FFMedia.Tools.YouTubeDownloader`):

- A single `SemaphoreSlim(maxConcurrency, maxConcurrency)` caps simultaneous
  downloads — **default 3**, a constructor parameter with a `= 3` default. **M5 PR 1:**
  the app composition root now reads `MaxConcurrency` from `ISettingsService` and passes
  it into `DownloadManager`'s constructor at launch, so the cap is user-configurable via
  the Settings screen (§13); it is applied once at construction, not re-tuned live while
  the app is running. No `Channel` is used: each `Enqueue` starts a fire-and-forget
  tracked `Task` that awaits a slot, so "queued" jobs are just tasks blocked on the
  semaphore rather than items sitting in a channel.
- **Auto-start on add:** `Enqueue` adds the job (`Queued`) and immediately schedules
  its run task; there is no separate "start" action.
- Each `DownloadJob` owns its own `CancellationTokenSource`. `Cancel(job)` cancels one
  job's token; `CancelAll()` cancels every non-terminal job's token individually (no
  shared/linked parent token). A job canceled while still waiting for a slot never
  acquires one and transitions straight to `Canceled`.
- `IdleAsync()` gives a deterministic "all done" signal (completes when no job is
  running or queued) — used by tests to avoid wall-clock sleeps, and available for
  future "all done" UX.
- Progress is reported **synchronously** on the calling (worker) thread via a small
  `IProgress<T>` adapter (not the ThreadPool-posting `Progress<T>`), so a late
  callback can never race past a job's terminal status. `DownloadJob`'s
  `[ObservableProperty]` setters rely on WPF data binding's cross-thread
  `PropertyChanged` marshaling to reach the UI; there is no separate dispatcher hop
  in the manager itself.

---

## 13. UI / UX

- **Shell:** WPF-UI `FluentWindow` with a left **`NavigationView`** listing tools;
  title bar shows the app logo (`ui:TitleBar.Icon`) + "FFMedia" title at top-left; Mica
  backdrop. Theme is chosen in **Settings** (no title-bar toggle). On startup the shell
  navigates to the `WelcomePage` (once `RootNavigation` is loaded) so the content frame is
  never blank — `NavigationView` selects no item by default.
- **Dark-mode text:** WPF-UI 4.3.0 ships no implicit `TextBlock` style, so plain
  `TextBlock`s fall back to WPF's default black (invisible in dark mode). The window sets
  `Foreground="{DynamicResource TextFillColorPrimaryBrush}"` for the chrome, but that does
  **not** reach page content — WPF's `Frame` (the `NavigationView`'s page host) isolates
  property-value inheritance. Each **`Page` root** therefore also sets that `Foreground`, so
  page-local inheritance themes all plain text (control templates still override their own).
- **Use `ui:` controls, never their plain WPF namesakes.** WPF-UI's `ControlsDictionary` keys
  its implicit styles to **its own subclasses** (`Wpf.Ui.Controls.ListView`, …) and ships
  *nothing* for `System.Windows.Controls.ListView`. A plain `ListView` therefore keeps WPF's
  built-in **light** chrome and renders as a white box on a dark page. Corollary: a
  `<Style TargetType="X">` with **no `BasedOn`** does not inherit the implicit style either —
  it derives from WPF's default light one. Base it on `{StaticResource {x:Type ui:X}}`; note
  that lookup resolves at **page-load** time, so a wrong key compiles clean and throws
  `XamlParseException` in front of the user (it did — see Changelog 0.16).
- **A checkbox in a list means "include this row".** Do not use one for anything else. The merger's
  per-clip pin shipped as a `CheckBox`, and the user read it exactly as every list UI has taught them
  to — as *"choose which clips get merged"*. It never did that: it only exempts a row from **Shuffle**,
  and every clip in the list is merged either way. It is a **pin toggle** (`Pin24` / `PinOff24`) now,
  and its tooltip states the one thing it affects. A control's affordance is a promise; if the promise
  is wrong, no tooltip rescues it.
- **A `Page` must NOT contain its own `ScrollViewer`.** WPF-UI's
  `NavigationViewContentPresenter` already wraps every page in one. A nested `ScrollViewer` is
  handed unbounded height by the outer one, so it can never scroll (`ScrollableHeight = 0`) —
  yet WPF's `ScrollViewer` marks mouse-wheel events **handled even when it cannot move**. It
  silently swallows every wheel tick and the shell's scroller, which *does* have room, never
  sees them: the page appears frozen. Let the shell scroll.
- **Downloader screen:**
  - URL input + "Add" (accepts multiple / paste-list).
  - Preview cards (thumbnail, title, duration) after probe.
  - Format/quality selector + options (trim, subs, embed) + output folder.
  - **Queue list** with per-item progress bar, speed/ETA, pause? (stretch), cancel.
  - Footer: global actions (start all, clear completed, open folder).
  - **Presets:** inline dropdown (apply/delete) + "save current as" — no separate screen.
- **Settings screen:** default folder, concurrency, theme, update cadence, binary
  versions + "Update yt-dlp". **Changes save immediately** (no Save button); a setting that
  can't apply live (e.g. **max concurrency**, read once at construction per §12) shows a red
  "takes effect after you restart" reminder while it differs from the launch value.
- **History screen:** searchable list with "open file/folder" (both give a notification when
  the file/folder no longer exists) and "re-download".
- **Video Merger screen (M7):** clip list — **column headers** (Pin · Clip · Status · Actions, aligned
  to the rows via `Grid.IsSharedSizeScope`, since `Auto` columns in separate Grids size independently)
  · **drag-to-reorder** + move up/down · a per-clip **pin toggle** · conformance badge · per-clip
  progress · remove · Shuffle (leaves pinned clips in place) · output target (auto-derived; every field overridable, but only **within the
  source's own ceiling** — `TargetBounds`, below — plus `FitMode`, folder, filename) · a summary line
  (**exact output duration**, **estimated merge time as a range**, count of clips needing re-encode,
  temp space) · Merge / Cancel + overall progress.
- **The output target can never exceed the sources (`TargetBounds`).** Derivation takes the *maximum*
  across the clips (largest dimensions, fastest fps, highest sample rate, most channels), and the
  override UI offers **only values at or below that ceiling** — upscaling 30 → 60 fps merely
  duplicates frames, and 1080p → 4K invents pixels: bigger file, longer encode, **no new
  information**. Resolution is therefore a **dropdown of standard steps at the source's aspect
  ratio**, not two free text boxes, which makes upscaling, odd dimensions (libx264 rejects them) and
  absurd aspect ratios all *unrepresentable* rather than merely validated. The ceiling moves as clips
  are added and removed, so an override that falls out of range **snaps silently down** to the largest
  still-allowed value. **The derived target is always the first entry of each list** — `TargetBounds`
  is built from the derivation's own maxima, so the offered options and the derived target cannot
  drift (the `ConformanceCheck` discipline). **Codec × container is deliberately NOT restricted:** all
  8 combinations mux cleanly in the bundled ffmpeg 8.1 (verified), so MP4 + Opus — a *playability*
  problem, not a validity one — gets a warning, never a block. A blocked option always means *"this is
  provably pointless"*, never *"we would rather you didn't"*. Spec:
  `docs/superpowers/specs/2026-07-12-merger-target-bounds-design.md`.
- **Every control the user can SET carries a plain-English tooltip.** FFMedia's parameters are the
  vocabulary of video encoding — container, CRF, bitrate, fit mode, sample rate — and to the person who
  just wants to save a video they mean nothing. **A setting you cannot weigh is a setting you cannot
  choose**, so each tooltip names the **trade-off**, not just the definition ("Lower is better quality
  and a bigger file; 20 is a good default"). Rules: attach the tooltip to the **label + control row**,
  not the control alone (a user who does not know what "Container" means points at the *word*); set
  `ToolTipService.ShowDuration` on the page root, because WPF hides tooltips after **5 seconds** — not
  long enough to read two sentences; and explain the jargon the app itself leaks (`yt-dlp`, `ffmpeg`).
  Enforced for the tool pages by `TooltipCoverageTests` (§14). Buttons that merely *act* (Move up,
  Remove) are exempt — their label is the explanation.
- Accessibility: keyboard navigation, sufficient contrast in both themes.

> **M7 PR 2 note:** the **Video Merger screen** now exists, exactly as specced above — including
> **both** drag gestures (files dropped onto the page to add; rows dragged within the list to
> reorder) alongside Move Up/Down. Its page root sets
> `Foreground="{DynamicResource TextFillColorPrimaryBrush}"` per the §13 rule below — WPF's `Frame`
> isolates property-value inheritance, so a page that omits it renders black-on-black in dark mode.
> **The user's visual check has now happened, and it found six defects the 597-test suite could
> not see** (Changelog 0.16) — four of them XAML/shell-integration bugs, because nothing ever
> instantiated the page. `MergerPage` also gained a **Clear all** button beside Shuffle, and its
> ViewModel is now a **singleton** so the clip list survives navigation (§5). XAML is no longer
> "verifiable only by build + human eye": `MergerPageLoadTests` builds the real page against the
> real resource dictionaries inside the real `NavigationViewContentPresenter` (§14).

> **M5 PR 1 note:** the **Settings screen** now exists (footer nav item) with default
> output folder, max concurrency, and theme controls (light/dark/system, via WPF-UI
> `ApplicationThemeManager`), backed by `ISettingsService`; the persisted theme is applied
> at startup. Update cadence and binary version display remain planned (M5 PR 2 / M6).
> _(Post-v1 UI fix: the earlier title-bar theme toggle was removed — theme lives only in
> Settings — and the shell gained the logo/title in the title bar; see Changelog 0.10.1.)_

> **M5 PR 2 note:** **inline presets** are delivered on the Downloader screen — a
> dropdown (`Presets`/`SelectedPreset`) plus Apply/Delete buttons and a "save current
> config as a named preset" text box + button, all bound directly on
> `DownloaderViewModel` (no separate presets screen, per §5 of the PR 2 spec). The
> **History screen** is delivered (new footer nav item, above Settings) — a filterable
> list (title/url/format substring match) backed by `IHistoryService`, with per-row
> "open file" / "open folder" and a "clear history" action; **re-download is not yet
> wired** (see §19). **In-app notifications** are delivered via a WPF-UI
> `SnackbarPresenter` overlaying the shell, driven by `SnackbarNotificationService`
> (severity → `ControlAppearance`: Success/Caution/Danger/Info); **native Windows toast
> notifications remain deferred to M6**, unchanged from the plan.

> **M6 PR 1 note:** the shell gains a dismissible **update banner** (driven by a
> singleton `UpdateViewModel`) offering "Update & restart" / "Later" when a newer
> version is available. The **Settings screen** gains a "check for updates on
> startup" toggle (bound to `AppSettings.CheckForUpdatesOnStartup`), a "Check for
> updates now" action, and a current-version display. Both the banner and the
> Settings update section were verified by build + manual code review only — the
> interactive install → update → relaunch loop and a GUI smoke of these controls are
> **pending a user dry-run** in the headless dev/CI environment (see Changelog 0.9).

> **M6 PR 2 note:** the **Settings screen** gains a **Binaries** section — a
> singleton `BinaryUpdateViewModel` displays the installed `yt-dlp`/`ffmpeg` versions,
> an "Update yt-dlp" action (`yt-dlp -U`), and a "check yt-dlp for updates on startup"
> toggle (bound to `AppSettings.CheckYtDlpForUpdatesOnStartup`); a fire-and-forget
> startup check **notifies only** (via `INotificationService`) when a newer yt-dlp is
> available — it never auto-applies the update. Separately, the app now has a real
> **logo**: `assets/branding/logo.png` is converted to a committed multi-res
> `app.ico` (`build/make-icon.ps1`) and wired as the exe/window/taskbar/installer icon,
> plus in-app in the **title bar** (left of the theme toggle) and the welcome page. Both the Binaries
> section and the real `yt-dlp -U` are verified by build + unit tests only — a headed
> GUI smoke is **pending a user dry-run** (see Changelog 0.10).

---

## 14. Testing Strategy

- **Unit (no network, fast):** `OptionSet` builder (`config → args`), job state
  machine, queue/concurrency, `IErrorMapper`, settings/preset/history services —
  all backed by a **fake `IProcessRunner`** and fake yt-dlp responses.
- **ViewModel tests:** headless (Core has no WPF dep), assert command/state logic.
- **Page-load tests (a tool module's XAML):** a module's `Page` lives in the *module*, not the
  WinExe, so `FFMedia.Tests` **can** instantiate it. `MergerPageLoadTests` builds the real page
  on an **STA thread**, against the same `ThemesDictionary` + `ControlsDictionary` App.xaml
  merges, hosted in the real `NavigationViewContentPresenter`. This is not decoration: a
  `StaticResource` that does not resolve compiles clean, passes every other test, and throws
  only when a human clicks the nav item; and a nested `ScrollViewer` silently eats the mouse
  wheel (§13). **Both shipped to the user, and both are now caught by `dotnet test`.** Any new
  tool page gets the same pair of tests.
- **All WPF tests share ONE `WpfHost` (the `wpf` xUnit collection).** WPF permits exactly **one
  `Application` per AppDomain**, owned by the thread that created it — so a test class that spins up its
  own STA thread and calls `new Application()` works only while it is the *only* such class. The moment
  a second appears they race, and the loser dies with *"Cannot create more than one Application
  instance"* or a `XamlParseException` far from the cause. `WpfHost` owns the one Application, the one
  STA dispatcher, and the merged dictionaries; it sets `ShutdownMode = OnExplicitShutdown`, because the
  default (`OnLastWindowClose`) lets the first test that closes a window tear the Application down under
  every test after it. New page tests join the collection; they do not build their own host.
- **`TooltipCoverageTests`** walks the real Downloader and Merger pages and fails if any
  `ComboBox`/`CheckBox`/`TextBox`/`ToggleButton` has no tooltip (§13). It deliberately does **not** skip
  hidden controls: half the Downloader's parameters live in rows collapsed until the matching output kind
  is chosen, and skipping them would let exactly those ship bare with the test green. **`SettingsPage` is
  NOT covered** — it lives in `FFMedia.App`, the WinExe, which the test project does not reference; its
  tooltips are verified by build and eye only.
- **Integration (opt-in, trait-gated, off in CI):** hit one stable known video to
  smoke-test the real yt-dlp/ffmpeg pipeline; for the merger, merge real `testsrc` clips and
  **probe the output** (ffmpeg's exit code is exactly what cannot be trusted).
- **Coverage priority:** the orchestration/argument-building logic is the highest
  risk and gets the most tests; UI is thin by design — but "thin" is not "unverifiable", and
  treating it as such is what let six defects reach the user in M7 PR 2.
- **A test only pins an invariant if the fixture varies along the axis the invariant is about.**
  This has now bitten three times (Changelog 0.15, 0.16). The shuffle suite re-seeded the RNG
  before *every* call, so it simulated a re-seeding UI that did not exist and could not see that
  clicking Shuffle twice replayed one permutation forever.
- TDD is the default workflow for Core logic.

---

## 15. Packaging & Distribution

- **Velopack** produces the installer and delta auto-updates.
- **A delta needs the PREVIOUS release present at pack time.** `vpk pack` builds a delta by diffing
  against the prior release's `.nupkg` — and if that file is not sitting in the output directory it
  simply emits a **full package only**, with *no error and no warning*. A CI runner starts from an
  empty checkout, so that is precisely what happened: **v1.1.0 and v1.1.1 both shipped full-only**, and
  every update was a ~190 MB download even when the release changed nothing but a few strings. The
  release workflow therefore runs **`vpk download github` before `vpk pack`**, and then asserts a
  `*-delta.nupkg` exists (emitting a CI warning if not), because "pack succeeded" is not evidence that
  a delta was built. Measured: **190.8 MB full → 18.6 MB delta (≈90 % smaller).**
- Bundled `yt-dlp.exe`, `ffmpeg.exe` + `ffprobe.exe` are included in the release package.
- Release channel + update feed configured in `build/`.
- **The GitHub repo must be public.** The in-app update check uses
  `GithubSource(..., accessToken: null, ...)` (anonymous), and GitHub returns **404** to
  anonymous callers on a *private* repo — surfacing as "Update check failed." A distributed
  desktop app can't ship a token safely (extractable from the `.exe`), so public is the
  distribution model. The repo was made public on 2026-07-08 for this reason.
- **The update feed must name the repo's CANONICAL owner — `CharmHC/ff-media`.** The repo was
  renamed from `ChamHC-dev/ff-media`, and GitHub's rename redirect still answers the old path, so a
  stale URL *appears* to work. It is not safe to rely on: **the redirect is dropped the moment anyone
  creates a repository at the abandoned name.** This app downloads and installs executables from that
  URL, so a stale feed is not a broken link — it is a **supply-chain hole**, with an attacker
  squatting the old name serving the update. Both `VelopackUpdateService.RepoUrl` and
  `release.yml`'s `--repoUrl` point at the canonical owner.
- Self-contained .NET publish (no framework prerequisite for end users).
- CI builds on every push; release workflow tags → Velopack pack + publish.

> **M6 PR 1 note (realized):** `build/pack.ps1` does a self-contained
> `dotnet publish` of `FFMedia.App`, then `vpk pack` (Velopack 1.2.0 CLI) into an
> unsigned `FFMedia-win-Setup.exe` + delta nupkg + `RELEASES` metadata (no
> `--signParams` supplied for v1; the seam is left in the script for later signing).
> `.github/workflows/release.yml` is **tag-gated** (`v*`), needs
> `permissions: contents: write`, and runs the same publish + `vpk pack` before
> `vpk upload github` to attach the release assets to the GitHub Release matching the
> tag. The real, public **v1.0.0** tag is **user-initiated** — not pushed by this PR.
> Locally, `build/pack.ps1` was run end-to-end and produced a real
> `FFMedia-win-Setup.exe` (~147 MB) + nupkg + `RELEASES` file, proving the pack
> machinery; the full tag → GitHub Actions → `vpk upload github` → install →
> in-app update path has **not** been run for real (pending the user's first
> release) and the interactive install/update/relaunch loop is a pending
> user dry-run (see Changelog 0.9).

---

## 16. Security, Legal & Privacy

- **No telemetry**; all data stays local.
- App displays a **disclaimer**: users are responsible for complying with content
  owners' rights and YouTube's Terms of Service; FFMedia is a general-purpose tool.
- No DRM circumvention, paywall bypass, or credential harvesting.
- External binaries are pinned to known versions and fetched over HTTPS with
  integrity checks in the build script.
- **Licensing (repo is public):** FFMedia's own source is **MIT** (`LICENSE`).
  Bundled third-party binaries are licensed separately and documented in
  `THIRD-PARTY-NOTICES.md`: **yt-dlp** (Unlicense) and **FFmpeg** — the pinned BtbN
  build is the **`win64-gpl`** variant, so the shipped `ffmpeg.exe` is **GPL-3.0**.
  FFmpeg is invoked as a **separate process** (not linked), so FFMedia's MIT code is
  mere aggregation with it; the GPL obligation (keep the notice + make FFmpeg's
  corresponding source available) attaches to redistributing the `ffmpeg.exe` binary
  in the installer, satisfied by the links in the notices file. Switching to the BtbN
  `win64-lgpl` build would lighten this to an LGPL notice but drops GPL-only encoders
  (e.g. x264/x265) — only relevant if a tool ever **re-encodes** video (the current
  downloader muxes/stream-copies, but `PreciseCut` can trigger a re-encode).
  **As of M7 this is no longer hypothetical:** the Video Merger's normalize phase
  re-encodes non-conforming clips with x264/x265, so the **GPL build is load-bearing**
  and the LGPL variant is no longer a drop-in alternative. `ffprobe.exe` (added in M7,
  §9) comes from the same GPL build and carries the same obligation — recorded in
  `THIRD-PARTY-NOTICES.md`.

> **M6 PR 2 note (realized):** `build/fetch-binaries.ps1` pins `yt-dlp` **2026.07.04**
> and the `ffmpeg` BtbN build **autobuild-2026-07-07-13-44**, downloads both over
> HTTPS, and **verifies each against a known SHA-256** before use (throws on
> mismatch) — see the pinned values in §9.

---

## 17. Milestones & Roadmap

Each milestone is a **vertical, shippable increment**.

| # | Milestone | Deliverable |
|---|---|---|
| **M0** | Foundation | ✅ delivered (branch `feat/m0-foundation`) — Repo + solution scaffold, `.gitignore`, CI build, `IBinaryProvider` + binary-fetch script, WPF-UI shell with empty `NavigationView`, DI/host wiring, Serilog. |
| **M1** | Vertical slice | ✅ delivered (branch `feat/m1-vertical-slice`) — Paste URL → probe → download single **MP4** with **live progress + cancel**. End-to-end through all layers. |
| **M2** | Formats | ✅ delivered (branch `feat/m2-formats`) — Full format matrix: video containers + audio-only (**wav/mp3**/m4a/opus/flac) + quality/resolution. `OptionSet` builder fully tested. |
| **M3** | Queue | ✅ delivered (branch `feat/m3-queue`) — Download **queue** (`IDownloadManager`/`DownloadJob`, module-owned) with bounded **concurrency** (`SemaphoreSlim` cap 3), transient-only retry with exponential backoff, and **playlist/channel** expansion at add-time (one job per entry). |
| **M4** | Processing | ✅ delivered (branch `feat/m4-processing`) — **Trim/clip** (fast keyframe cut or precise re-encode), **subtitles** (video-only, manual + auto), **metadata + thumbnail** embedding. |
| **M5** | Experience | ✅ delivered (branches `feat/m5-foundation`, `feat/m5-presets-history`) — **PR 1:** settings persistence + theming foundation (`JsonStore<T>`, `ISettingsService`, Settings screen, dark/light/system theming). **PR 2:** presets (`IPresetService`, inline Downloader UI), history (`IHistoryService`, `DownloadManager` completion hook, History screen), and in-app snackbar notifications (`INotificationService`/`SnackbarNotificationService`). Re-download from history deferred (§19). |
| **M6** | Ship v1 | ✅ **complete** — **PR 1 delivered** (branch `feat/m6-packaging-autoupdate`): Velopack packaging (`build/pack.ps1`, tag-gated `release.yml`) + app delta auto-update (`IUpdateService`/`VelopackUpdateService`, shell update banner, Settings toggle + check-now). **PR 2 delivered** (branch `feat/m6-binary-updates`): yt-dlp self-update (`IProcessRunner`/`IBinaryUpdateService`), Settings **Binaries** section, pinned + SHA-256-verified `fetch-binaries.ps1`, and the app logo. **Shipped:** **v1.0.0** was tagged and released, followed by v1.0.1, v1.1.0 and **v1.1.1** (all live on GitHub Releases). *(This row read "🚧 in progress" until 2026-07-13 — four releases after it was actually done. A roadmap that lags reality is worse than no roadmap: it is a claim, and it was false.)* |
| **M7** | Video Merger | ✅ **complete** ([spec](docs/superpowers/specs/2026-07-10-m7-video-merger-design.md)) — second tool module (`FFMedia.Tools.VideoMerger`), **validating the modular seam: the shell was not modified**, the tool registers `ITool` + `IToolPage` and the shell discovers it. **PR 1** (branch `feat/m7-merge-engine`): the engine + `FFMedia.Media` (§8) + `ffprobe.exe` (§9) — target derivation, `ConformanceCheck`, normalize/concat arg builders, seeded shuffle with locked indices, estimator + `SpeedProfile`, disk guard, temp sweeper, and `MergeService` (preflight → bounded-concurrency normalize → stream-copy concat, temp cleanup on every exit path). **PR 2** (branch `feat/m7-merger-ui`): `MergerViewModel` + `MergerPage` + `VideoMergerTool`/nav registration, per-clip progress bars, history (`HistoryEntry.Source`) + notifications wiring, the override UI, and the first **end-to-end integration test** — three real `testsrc` clips merged and the *output probed*. |

| **M8** | GIF Maker | ✅ **complete** ([spec](docs/superpowers/specs/2026-07-12-gif-maker-design.md)) — **third** tool module (`FFMedia.Tools.GifMaker`): one video → one GIF, with **start / end / size / frame rate** and a **live estimated file size**. A single-item editor, not a queue (making a GIF is iterative — the feedback loop matters more than throughput). **The shell was not modified** — the tool registers `ITool`/`IToolPage` and the shell discovers it, the same modular seam M7 proved, now proven a third time. **Two-pass `palettegen` + `paletteuse`**, always: the naive single-pass route quantizes to a *generic* palette and looks visibly dirty, and the right way costs only ~3× of a fraction of a second — but two passes is **not** automatically smaller (the dithering that buys smooth gradients also compresses worse), so quality vs size is a genuine trade-off, not a free win. **Size and frame rate are capped at the source** (the `TargetBounds` rule — a GIF wider or faster than its own source contains no more information), and **height is derived** from the source aspect rather than being a second box (the `1920 × 102` hole the merger shipped). Size is estimated as a **range**, calibrated from the user's own past GIFs (`gif-size.json`, the `SpeedProfile` pattern) — content makes GIF size genuinely unpredictable, and a single number would be false precision. The finished GIF is **re-probed** before being called a success (§6: ffmpeg's exit code is exactly what cannot be trusted), and deleted if it does not read back as a valid GIF. Promoted `Resolution` → `FFMedia.Media` and `TrimParsing.TryParse` → `FFMedia.Core.Media`, since a **tool must never reference another tool** (§5). **The render never writes the destination directly** — it targets a **sibling** and only `File.Move`s it into place once verified whole, because the workflow is iterative (tune, re-render to the *same* filename) and ffmpeg's `-y` would otherwise truncate the good GIF the user made a minute ago; the verification checks the output's **duration**, not merely that it reads back as a video (§6). **Proven against real ffmpeg**: a new integration test synthesizes a 1280×720/30fps/6s `testsrc` clip, makes a GIF with `-ss 2 -to 5` at 480×270/15fps, and probes the *output* — confirming the derived 270 px height, and confirming the duration is **3.0 s, not 5.0 s**, because `-to` is absolute on the source timeline, not relative to the seek point. Release build **0 warnings / 0 errors**; **730/730** unit tests; **11/11** integration tests (4 merge + 1 gif + 6 pre-existing queue/yt-dlp, all unaffected). **Not verified:** a human has not clicked through the page — this environment is headless. |
| **M9** | Video Preview & Frame Capture | ✅ **delivered** (branch `feat/m9-video-preview`; [spec](docs/superpowers/specs/2026-07-13-m9-video-preview-design.md), [plan](docs/superpowers/plans/2026-07-13-m9-video-preview.md)) — a **shared** preview control in a new **`FFMedia.Ui`** layer: play/pause, seek, frame-step, and **‹ Set Start / Set End ›** buttons that capture the paused frame's timestamp into the tool's range. Ends **blind timecode entry** — the interaction §19 already named as the unblock for the merger's deferred per-clip trim. **Plays any format ffmpeg can read, via a fast path + proxy fallback:** WPF's `MediaElement` renders through Windows Media Foundation and **cannot play VP9/WebM** (verified against real files — and **WebM is a format our own downloader produces**, so a naive `MediaElement` would show a blank preview for videos FFMedia itself made). An unplayable source is transcoded to a small H.264 proxy that **rescales only and never re-times** — the captured timestamp is read from the *player's* position, so a proxy whose timeline drifted would make **every captured time a lie**. That is the merger's `ConformanceCheck` discipline reused: conforming input takes the fast path, non-conforming gets normalized. Also fixes `TrimParsing.TryParse`, which **rejects `1:23.45`** (the colon form parses each part with `int.TryParse`) — so a capture button would otherwise write an **unparseable** value and silently grey out Create. **First consumer: the GIF Maker.** **Deferred to M10:** the draggable range band, and rollout to the Merger + Downloader. |

---

## 18. Coding Conventions

- Nullable reference types **on**; treat warnings as errors in `Core`.
- One public type per file; file name matches type.
- `async`/`await` end-to-end for I/O and process work; no blocking `.Result`.
- ViewModels: `CommunityToolkit.Mvvm` source generators; no code-behind logic.
- Keep files focused — a growing file signals a responsibility that should split.
- Match surrounding style; comment density mirrors neighboring code.

---

## 19. Open Questions

- ~~Final default concurrency value~~ — **resolved (M3, refined M5 PR 1):** default
  **3**, a constructor parameter on `DownloadManager`; user-configurable via the
  Settings screen, read from `ISettingsService` at app launch.
- ~~History storage: stay JSON vs. move to SQLite~~ — **resolved (M5 spec):** **JSON**
  (`history.json`, per §10); revisit only if it grows large enough to warrant SQLite.
- ~~Pause/resume of in-flight downloads~~ — **resolved (M3): deferred.** M3 ships
  cancel-only (per-job + cancel-all); pause/resume remains a stretch goal, revisit
  post-v1 if there's demand.
- ~~Which yt-dlp/ffmpeg versions to pin for v1~~ — **resolved (M6 PR 2):** `yt-dlp`
  **2026.07.04**, `ffmpeg` BtbN **autobuild-2026-07-07-13-44** (asset
  `ffmpeg-n8.1.2-22-g94138f6973-win64-gpl-8.1.zip`); both SHA-256-verified in
  `build/fetch-binaries.ps1`. See §9 for the hashes.
- **Re-download deferred (M5 PR 2).** The History screen's "re-download" row-action
  (§13) was **not** implemented this PR. It needs (a) a cross-page seeding seam — the
  Downloader screen's `DownloaderViewModel` is DI-**transient**, so there's no existing
  channel for the History page to hand it a config and navigate over — and (b) a
  richer `HistoryEntry` that stores the **serialized `DownloadConfig`** (via
  `PresetMapping`-style (de)serialization), not just the human-readable `Format`
  label it carries today. Revisit alongside a broader look at cross-page
  navigation/state-passing, rather than a one-off hack for this single action.
- **M7 deferrals (recorded, not open):** **transitions/crossfades** (force the
  `filter_complex xfade` path, eliminate the stream-copy fast path, and break
  `outputDuration = Σ inputDurations`); **per-clip trim** (needs a preview scrubber to
  be usable, not blind timecode entry); **per-clip fit mode** (one `FitMode` per merge
  for now); **background music / audio replacement** (arguably its own tool); and a
  **merge queue** (`IMergeManager`) — one merge already saturates the CPU, since the
  concurrency lives inside the normalize phase. Revisit any of these on demand.
- **Known follow-up:** the App-layer `HistoryViewModel` subscribes to
  `IHistoryService.Changed` in its constructor with no matching unsubscribe, and the
  VM is registered DI-**transient** (a fresh instance per navigation) — so repeated
  visits to the History page accumulate handlers (a minor leak + redundant
  `Refresh()` calls per external change), mirroring the existing pattern on other
  App-layer VMs. Candidate fixes: register `HistoryViewModel` as a singleton, or
  detach the subscription on the page's `Unloaded` event. Not blocking for M5; flagged
  for a future pass.

---

## 20. Glossary

- **yt-dlp** — actively maintained fork of youtube-dl; performs extraction/download.
- **ffmpeg** — media transcode/mux/trim engine.
- **Tool / module** — a self-contained FFMedia feature hosted by the shell.
- **OptionSet** — YoutubeDLSharp's structured representation of yt-dlp CLI options.
- **Velopack** — installer + auto-update framework (Squirrel successor).

---

## Changelog

| Date | Version | Change |
|---|---|---|
| 2026-07-14 | 0.27 | **M9 implemented — Video Preview & Frame Capture — M9 complete.** Pause a video, click **Set Start / Set End**, and the paused frame's timestamp lands in the box. **Blind timecode entry is over** for the GIF Maker; M10 rolls it out to the Merger and Downloader. New **`FFMedia.Ui`** layer (§5) — the preview is a *shared* capability, so it is built shared from the start and M10 **adopts** it instead of running a promotion task. **The design's whole shape comes from one verified fact:** WPF's `MediaElement` renders through Windows Media Foundation, so it **cannot play VP9/WebM** — **a format our own downloader produces** — hence *fast path + ffmpeg proxy fallback*. **The proxy rescales but NEVER re-times**, because the captured timestamp is read from the *player's* position: a proxy whose timeline drifted would make **every captured time a lie**, and the GIF would be cut somewhere other than where the user saw. **The per-task review is the gate, not the green suite** — it caught a **UI-thread hang** (a shared pending-open field meant loading a second video left the first caller's task *never completing*), a readout **frozen at `0:00` forever** (WPF only refreshes a binding whose exact path was notified, so the control merely *looked* broken and told the user nothing — while capture still read the right value), and, in the branch's only never-reviewed task, a **composition bug no single task could see**: `MediaElementPlayer` is a **singleton** while `VideoPreview` is **transient**, so navigating away and back re-attached the player to a **fresh, source-less `MediaElement`** — the preview went black while the VM still reported `IsReady`, leaving capture **armed over a dead player** and writing **`0:00`** for a frame the user never saw. Every one of those suites was green. **Two lessons re-earned.** (1) *A test only pins an invariant if the fixture varies along the axis the invariant is about* — the plan's own integration fixture was **640×360** against a **640** cap, so `Width <= 640` passed **even with the scale filter deleted**; and the keystone no-retiming assertion compared **durations only**, which — measured against real ffmpeg — is **structurally blind** to an `fps=12` filter that **throws away half the frames while leaving the duration bit-identical**. The frame rate must now survive the transcode unchanged. (2) *A green gate that skipped the thing it was gating is not a gate* — every "0 warnings" claim on this branch came from an **incremental** build that never recompiled the test project, which a clean build showed had **3** warnings all along. **The final whole-branch review found a Critical for the third milestone running — in the FORMATTER, not the video code.** `TrimParsing.Format` built the whole part by **truncation** and the fraction by **rounding**, so any fraction ≥ 0.9995 rounded to a bare `1` **glued onto the truncated seconds**: **`Format(1.9996s)` emitted `"0:011"` — eleven seconds.** Pause at 1.9996 s, click Set Start, and the GIF is cut from **11 s**, silently, with Create still enabled; neighbouring values are **unparseable**, greying Create out on a perfectly good freshly-loaded video. **Both** values M9 newly feeds through `Format` (the player's **live position**, ffprobe's **probed duration**) are arbitrary *machine* values — **Task 1 tested it only against values a HUMAN types**, and its round-trip theory stopped at `0.999`, **one step short of the carry boundary**. Three more from the same review: a **killed proxy transcode poisoned that video's preview for seven days** (it wrote straight to its permanent cache key, and a half-written file is a **cache hit forever** — this was the one place on the branch not applying the project's own rule that `GifService`/`MergeService` already follow: **write to a sibling, verify, then move**); a preview left **playing kept playing after you navigated away**; and reaching the end left `IsPlaying` **true forever**. **Real ffmpeg caught a trap inside one of those fixes that no fake could have:** the proxy's sibling, first named `…mp4.part`, made ffmpeg fail with *"Unable to choose an output format"* — **ffmpeg picks its muxer from the output extension**, the exact constraint the other two services already state. **Verified:** clean (`--no-incremental`) Release build **0 warnings / 0 errors**; **822/822** unit tests; **12/12** integration tests against real ffmpeg — including a real **VP9/WebM** source yielding a playable `h264` proxy at exactly 640×360 whose **duration and frame rate both match the source**. **NOT verified — a human has not clicked through the preview.** This environment is headless: a real playing `MediaElement` cannot be driven, so *"does the readout visibly advance"*, *"does the restored frame come back after a page revisit"*, and *"does a preview left playing actually stop when you navigate away"* are all unproven. `MediaElement.Close()` has **no observable public effect** without a Media Foundation session (it does **not** reset `Source` — I assumed it did; it does not), so no test was written for it rather than one that could not fail. |
| 2026-07-13 | 0.26 | **M9 designed — Video Preview & Frame Capture** (spec: `docs/superpowers/specs/2026-07-13-m9-video-preview-design.md`). Every tool that asks for a **moment in a video** asks for it as **blind timecode text** — you type `1:23` and hope. §19 already recorded this as the reason the merger's **per-clip trim was deferred** (*"needs a preview scrubber to be usable, not blind timecode entry"*), so M9 builds that scrubber as a **shared** capability in a new **`FFMedia.Ui`** layer (a tool must never reference another tool, and it cannot reference the WinExe either — so a shared *UI* layer is the only correct home, and building it shared now means M10 **adopts** it rather than performing a promotion task, as `Resolution`/`TrimParsing` had to in M8). **Two facts verified rather than assumed, both of which would have shaped the design wrongly:** (1) WPF's `MediaElement` renders through **Windows Media Foundation**, so its codec support is Windows', not ours — tested against real files, MP4/H.264 and MKV/H.264 open, but **WebM/VP9 FAILS**. **WebM is a format our own downloader ships** (M2), so pointing `MediaElement` at the source and calling it done would show a **blank preview for videos FFMedia itself made**. Hence a **fast path + proxy fallback**: play the source directly; if it fails, transcode a small H.264 proxy and play that — the merger's `ConformanceCheck` discipline reused, costing nothing for the videos that already play and correct for everything ffmpeg can read. The proxy **rescales only and never re-times**, because the captured timestamp is read from the *player's* position: a proxy whose timeline drifted would make **every captured time a lie**, and the GIF would be cut somewhere other than where the user saw. (2) **`TrimParsing.TryParse` rejects `1:23.45`** — the colon form parses each part with `int.TryParse`, so only the *bare-seconds* form takes a fraction. A capture button writing `1:23.45` would therefore produce an **unparseable** value: the range goes invalid and **Create greys out**. The feature would have been **broken on arrival**; `TryParse` gains fractional support in the colon form, plus a round-trippable `Format`. Capture is **frozen while a GIF renders** (the render holds a snapshot — the bug M8 shipped **twice**), and gated **both** by `CanExecute` and inside the command, since a gesture that is not a command bypasses `CanExecute` entirely. **Deferred to M10:** the draggable range band, and rollout to the Merger (per-clip trim) + Downloader. **Also:** §17's **M6 row said "🚧 in progress"** through four shipped releases (v1.0.0 → v1.1.1) — corrected. *A roadmap that lags reality is not a stale note; it is a false claim.* Design only — no code in this change. |
| 2026-07-13 | 0.25 | **The M8 whole-branch review caught a data-loss bug — the merger's, un-relearned.** Every one of the 8 tasks had passed its own independent review; the *composition* was still wrong, which is exactly what this review exists to find. **`GifService` wrote the render straight to `request.OutputPath` and deleted that path on cancel / render-failure / verify-failure** — and `FfmpegRunner` prepends **`-y`**, so the render truncated whatever was already there the instant it opened it. This tool's whole workflow is **iterative**: load once, tune, **re-render to the same filename**, look, tune again. So *"make a GIF → it's 8 MB, too big → shrink it → re-render → hit Cancel because it's slow"* **deleted the good GIF from a minute ago, leaving the user with neither.** Cancelling during the *palette* pass destroyed a file ffmpeg had never even opened. **No task could have seen it alone** — Task 5 built the service against fakes, Tasks 6–7 built the UI that makes same-filename re-rendering the default gesture — and **the test suite pinned the destructive behaviour as correct** (it asserted the destination was *gone* after a cancel, and no test ever placed a pre-existing file there). Fixed by mirroring `MergeService` exactly: render to a **sibling** (same directory, so `File.Move` is a rename; same `.gif` extension, since ffmpeg picks its muxer from it), verify *that*, and only move a proven-whole GIF into place — **nothing the user already had is touched until there is something worth replacing it with** (§13). The three inverted tests now place a real GIF at the destination and assert it **survives byte-for-byte**. Three more, also invisible to any single task: (a) **`VerifyAsync` never checked the output's duration**, so a GIF ffmpeg wrote *short* (a filtergraph that dies after the first frame, a truncated write) probed clean as "a readable video with a video stream" and was reported a **success** — the re-probe proved *"a file came out"*, not *"the file I asked for came out"*, which is the precise failure mode this project already shipped once via the concat demuxer; the tolerance is **proportional** (15 %, floor 0.5 s, clamped to half the request), because a false positive here **deletes a healthy GIF** — the same trap the merger's flat 1 s tolerance hit. (b) **`SelectedSize` was a non-nullable `Resolution`** — a record, hence a *reference type* — two-way bound to a `ComboBox.SelectedItem`; **WPF writes `null` through that binding while `ItemsSource` rebuilds**, so loading a *second* video threw a `NullReferenceException` inside the estimator on the UI thread, **silently swallowed by the binding engine**, invisible only because no test ever loaded two videos. This is verbatim the M7 lesson (`0.18`: *"the null a ComboBox pushes while its ItemsSource is being rebuilt"*), and the fix is the same nullable projection. (c) A **missing output folder** surfaced as ffmpeg's *"No such file or directory"*, which `GifErrors` matched on its **first** rule and reported as ***"The video could not be found"*** — blaming the user's perfectly good `.mp4` for a typo'd *destination*, which is word-for-word the M7 mistake. Also: the final `File.Move` was **unguarded**, so a destination held open by another process (the user is very likely *looking at* the GIF they just made) orphaned the pending render **in the user's own output folder**. Release build **0 warnings / 0 errors**; **730/730** unit tests; **11/11** integration tests. |
| 2026-07-13 | 0.24 | **M8 GIF Maker implemented and proven against real ffmpeg — M8 complete.** The seven prior tasks on this branch built the module (promoted `Resolution`/`TrimParsing`, `GifBounds`, `GifArgsBuilder`'s two passes, the calibrated size estimate, `GifService`, `GifMakerViewModel`, the page + `ITool` + DI + tooltips), all proven with fakes; this task adds the first **real** proof. A new `GifIntegrationTests.CreateAsync_MakesARealGif_OfTheRightSizeAndLength` synthesizes a real 1280×720/30fps/6s clip with ffmpeg's own `testsrc`, makes a GIF through the real `GifService` (both real ffmpeg passes) at `-ss 2 -to 5`/480×270/15fps, and — because ffmpeg's exit code is exactly what cannot be trusted — **probes the output** with a real `FfprobeMediaAnalyzer` rather than trusting the `Result`. It confirms the height (270) is genuinely derived from the source aspect by `scale=W:-2`, and — the whole point of the test — confirms the duration is **3.0 s, not 5.0 s**: `-to` is absolute on the source timeline, not a duration from the seek point, and this is the only test that checks that claim against real ffmpeg rather than a fake that would happily accept either reading. Also confirms the temp palette PNG is gone afterward. **What the review rounds on this branch found, worth naming because each will recur:** (a) the re-probe — the rule the entire service exists to enforce — was, for a while, pinned by **nothing**: the "ffmpeg lies" unit test used a fake analyzer that failed on *every* path, so it failed at `PreflightAsync`'s probe of the source and never reached `VerifyAsync` at all; deleting the whole re-probe left every test green. Fixed by making the fake path-aware (succeed on the source, fail only on the output) so the test actually exercises the verification step it claims to. (b) The merger's shipped bug pattern **repeated twice** in `GifMakerViewModel`: `LoadVideoAsync` had no freeze guard, so swapping the source video mid-render silently overwrote the running job (and a drop gesture bypasses `CanExecute` entirely, which is why the fix — matching the merger's own lesson — guards in both the command *and* the mutator); and the history row's `Title` read the *live* filename property instead of a snapshot, so renaming the output mid-render produced a history entry describing a file it doesn't point at. (c) `gif-size.json` is user-visible and hand-editable, and `JsonStore` only quarantines **malformed JSON** — a syntactically valid `"SampleCount": -1` deserialized cleanly, divided by zero downstream, and **persisted a `NaN`** that would have poisoned every future estimate; now guarded. **Verified:** Release build **0 warnings / 0 errors**; **722/722** unit tests (`Category!=Integration`); **11/11** integration tests (the 4 pre-existing merge tests unaffected, the 1 new gif test, 6 pre-existing queue/yt-dlp tests unaffected). **Not verified:** a human has not clicked through the GIF Maker page — this environment is headless, so the XAML is checked by build + the page-load test only; worth flagging for the click-through specifically, the page's two `DynamicResource` lookups fail silently on a typo (unlike `StaticResource`, which throws at load), so no test would catch one. §5/§17 updated; Changelog gains this row. |
| 2026-07-12 | 0.23 | **M8 designed — GIF Maker, the third tool** (spec: `docs/superpowers/specs/2026-07-12-gif-maker-design.md`). One video → one GIF: **start / end / size / frame rate**, and nothing else in v1. A **single-item editor with a live estimated size**, not a queue — making a GIF is iterative, so the feedback loop beats throughput. **Always two-pass `palettegen` + `paletteuse`:** the obvious `ffmpeg -i in.mp4 out.gif` quantizes to a *generic* 256-colour palette and looks visibly banded, while the two-pass route builds a palette from the clip's own colours — **verified present in the bundled ffmpeg 8.1**, and costing ~3× of a fraction of a second (0.47 s vs 0.15 s on a 3 s clip), so the bad route is simply not offered. Also **measured and worth stating: two passes is not automatically smaller** (0.57 MB vs 0.38 MB) — the dithering that buys smooth gradients compresses worse, so quality vs size is a genuine trade-off, and a dither preset is the first deferred follow-up. **`-ss`/`-to` semantics verified rather than assumed** (both are widely mis-stated): `-to` is **absolute** on the source timeline, not a duration from the seek point (`-ss 2 -to 5` → exactly 3.0 s), and input seeking is **frame-accurate**, not keyframe-snapped (30 frames at 10 fps with keyframes 5 s apart) — so no `-accurate_seek` and no output-side `-ss` are needed. **Bounds:** size and frame rate are **capped at the source** (the §13 rule — a GIF wider or faster than its source contains no new information), and **height is derived from the source aspect** rather than being an independent box, which is the `1920 × 102` hole the merger shipped. **Size estimate is a RANGE**, calibrated from the user's own past GIFs (`gif-size.json`) — content makes GIF size genuinely unpredictable and a single number would be false precision; the `SpeedProfile` pattern, reused. The finished GIF is **re-probed before being reported a success**, and deleted if it is not whole (§6). Two promotions, because **a tool must never reference another tool** (§5): `Resolution` → `FFMedia.Media`, `TrimParsing` → `FFMedia.Core`. `IconGlyph = "Gif24"` and `SortOrder = 30` (ascending; downloader 10, merger 20) — the icon **verified to exist** in `SymbolRegular`, since the shell degrades an unparseable name to `Apps24` *silently*. §17 gains M8. Design only — no code in this change. |
| 2026-07-12 | 0.22 | **Delta updates were never actually being built — fixed.** SDD has claimed "installer + **delta** auto-update" since v0.9 (§3, §15), and the machinery was real, but **no release has ever shipped a delta**. `vpk pack` builds one by diffing against the **previous release's `.nupkg`**, and if that file is not in the output directory it emits a **full package only — with no error and no warning**. A CI runner starts from an empty checkout, so there was nothing to diff against: **v1.0.0 through v1.1.1 all shipped full-only**, and every update was a **~190 MB download** even when the release changed nothing but a few tooltip strings. Found by *reading the published manifest* after tagging v1.1.1 (`"Type":"Full"`, and nothing else) rather than by trusting the green workflow — pack succeeds either way, which is exactly why it went unnoticed through four releases. **Fix:** `release.yml` runs **`vpk download github` before `vpk pack`** (Velopack's documented flow), plus a step that **asserts a `*-delta.nupkg` exists** and raises a CI warning if not — because "the workflow passed" is not evidence a delta was built, and that assumption is what caused this. `continue-on-error` on the download, since the first release on a channel legitimately has nothing to diff against. **Proven end-to-end locally before merging** (vpk is installable locally, so this did not have to wait for a real tag): downloaded v1.1.1, packed a hypothetical 1.1.2, and Velopack logged `Building delta 1.1.1 -> 1.1.2` — **190.8 MB full → 18.6 MB delta, ≈90 % smaller**, with the manifest now advertising both a `Full` and a `Delta` asset. §15 records the trap. |
| 2026-07-12 | 0.21 | **Plain-English tooltips on every parameter (Downloader, Merger, Settings).** The app's settings are the vocabulary of video encoding — container, CRF, bitrate, fit mode, sample rate, and the raw names `yt-dlp` and `ffmpeg` — and to someone who just wants to save a video they mean nothing. The Downloader had **one** tooltip; the Merger's existing thirteen were written for an engineer (*"ffmpeg picks its muxer from that"*); Settings had **none**. Every user-settable control now explains itself, and — the actual rule, §13 — **names the trade-off rather than the definition**: *"Lower is better quality and a bigger file. 20 is a good default; above 28 it starts to look blocky."* A setting you cannot weigh is a setting you cannot choose. Three details that matter: tooltips are attached to the **label + control row**, because a user who does not know what "Container" means points at the *word*, and a tooltip on the ComboBox alone says nothing there; `ToolTipService.ShowDuration` is set on each page root, because **WPF hides a tooltip after 5 seconds** — it would vanish mid-sentence; and the jargon the app leaks (`yt-dlp`, `ffmpeg`) is explained where it appears. Enforced by the new **`TooltipCoverageTests`**, which walks the real pages and fails if any input has no tooltip — mutation-proven, and deliberately **not** filtering on `IsVisible`, since half the Downloader's parameters sit in rows collapsed until the matching output kind is picked, and skipping hidden controls would let exactly those ship bare with the test green. **Also fixed, uncovered by adding a second WPF test class:** every such class was building its own STA thread and `Application`, which works only while there is exactly one — the new class made them race (*"Cannot create more than one Application instance"*, plus a `XamlParseException` far from its cause). A shared **`WpfHost`** now owns the one Application and STA dispatcher, with `ShutdownMode = OnExplicitShutdown` (the default tears the Application down when the first test closes its window). §14 records it. **`SettingsPage` is not covered by the test** — it lives in the WinExe, which Tests does not reference; verified by build and eye only. Release build **0/0**; **642/642** unit tests (was 640); **4/4** merge integration tests. |
| 2026-07-12 | 0.20 | **Update feed repointed to the repo's canonical owner — a supply-chain fix, found while preparing the v1.1.0 release.** The repo was renamed `ChamHC-dev/ff-media` → **`CharmHC/ff-media`**, but `VelopackUpdateService.RepoUrl` and `release.yml`'s `--repoUrl` still named the old owner. It *looked* fine: GitHub's rename redirect answers the old path, and an anonymous `GET /repos/ChamHC-dev/ff-media/releases` still returns **200** (verified) — which is precisely why this is easy to miss. **But the redirect is dropped the moment anyone creates a repository at the abandoned name.** This app *downloads and installs executables* from that URL, so a stale feed is not a broken link — it is a hole an attacker could squat, serving the update to every installed client. Both URLs now name the canonical owner; §15 records the rule. Existing v1.0.1 installs are unaffected (they ship the old URL and still resolve through the redirect, so they will find v1.1.0). Release build **0/0**; **640/640** unit tests. |
| 2026-07-12 | 0.19 | **Merger clip list: column headers, and the checkbox that lied.** Reported by the user after a click-through. (1) **The per-clip control was a `CheckBox`** — and a checkbox in a list means *"include this row"* in every UI anyone has ever used, so the user reasonably believed it chose **which clips get merged**. It never did: it only exempts a row from **Shuffle**, and every clip in the list is merged whether ticked or not. *The affordance was making a promise the code did not keep.* It is now a **pin `ToggleButton`** (`PinOff24` → `Pin24`, both verified to exist in WPF-UI 4.3 before use), with a `PinTooltip` that states the one thing it affects and names the pinned position 1-based. The Shuffle tooltip's "locked" became "pinned" to match. **Code-level names (`IsLocked`/`LockedIndex`/`SetLock`) are unchanged** — internal, and renaming them would churn ~30 tests without changing anything the user sees. §13 gains the rule. (2) **The clip list had no column headers**, so the conformance badge in particular read as an unexplained chip. Added **Pin · Clip · Status · Actions**, aligned to the rows with **`Grid.IsSharedSizeScope` + `SharedSizeGroup`** — the load-bearing detail being that `Auto` columns in *separate* Grids size **independently**, so a header Grid without a shared scope drifts out of alignment with the rows as file names change. (3) The glyph swap is a `DataTemplate.Trigger` on a named element, deliberately **not** a `Style` — a `Style` with a `TargetType` and no `BasedOn` silently discards WPF-UI's implicit one, and a `BasedOn` on an unregistered key throws `XamlParseException` at page **load**; both have already shipped from this very page. It is pinned by a new test in `MergerPageLoadTests` that **realizes the row in a real visual tree and reads the glyph back** — mutation-proven (deleting the trigger fails it), because a dead trigger is invisible to both the compiler and the parse-only load test. Release build **0 warnings / 0 errors**; **640/640** unit tests; **4/4** merge integration tests against the real bundled ffmpeg. **Not verified:** a human has not seen the header alignment or the pin on screen. |
| 2026-07-12 | 0.18 | **`TargetBounds` (0.17's design) implemented — the override UI can no longer offer an out-of-range value.** New pure `TargetBounds` (`FFMedia.Tools.VideoMerger.Models`) built **from `MergeTargetDerivation`'s own maxima**, exposing four allowed-value lists (resolutions, frame rates, sample rates, channel counts); `MergeTarget.ClampTo(TargetBounds)` snaps an out-of-range override **down** to the largest still-allowed value (never up — that would silently reintroduce upscaling), falling back to the smallest allowed value only when every option exceeds the current one. `MergerViewModel` gained `Bounds`/`SelectedResolution`/`ShowOpusInMp4Warning`/`HasClips`, recomputing `Bounds` and re-clamping `Target` whenever the clip list changes; the free-text width/height boxes are replaced by a `Resolution`-bound ComboBox. **The keystone invariant: the derived target is always the first entry of every `TargetBounds` list**, because the lists are built from derivation's own maxima rather than recomputed independently — the offered options and the derived target cannot drift, mirroring the discipline `ConformanceCheck` already enforces for the fast path. **Codec × container stays deliberately unrestricted:** all 8 combinations (2 containers × 2 video codecs × 2 audio codecs) were confirmed to mux cleanly against the real bundled ffmpeg 8.1 — MP4 + Opus is a *playability* problem (silent on QuickTime/some TVs, fine on VLC/Chrome), not a validity one, so it warns via `ShowOpusInMp4Warning` rather than being blocked. **Proven against real ffmpeg, not just mocked:** a new integration test (`MergeAsync_ClampedTo720pTarget_ProducesAReal720pFile`) merges two synthesized 1080p `testsrc` clips with a target clamped-then-overridden to 720p, and — because ffmpeg's concat demuxer exits 0 even on a silently truncated merge — **probes the actual output file** rather than trusting the exit code, asserting a real 1280×720 stream and ~4 s duration (both clips present). **The whole-branch review then caught the composition gap** (every task had already passed its own review): **the Output panel stayed editable during a merge.** The merge runs against a *snapshot* of the target taken when Merge was clicked, so flipping Container to MKV mid-merge rewrote `OutputFileName` to `merged.mkv` while the encode still wrote `merged.mp4` — and the **history row then named a file that does not exist, in a format that was never produced**. Fixed with `CanEditTarget => HasClips && !IsMerging`: the `CanEditClips` precedent, one level up — *the page must never describe a merge that is not the one running.* Also fixed: the two audio ComboBoxes bound `SelectedItem` to non-nullable `int`s, so the `null` a ComboBox pushes while its `ItemsSource` is being rebuilt was a **silently-swallowed binding error** (now nullable projections, matching `SelectedResolution`/`SelectedFrameRate`); and the "no clips" gate had also frozen **File name / Folder / Browse**, which are now always live — choosing where a merge will land is meaningful before a single clip is added. Release build **0 warnings / 0 errors**; **637/637** unit tests pass (`Category!=Integration`); **4/4** merge integration tests pass against the real bundled ffmpeg/ffprobe (3 existing + the new 720p one). **Not verified:** a human has not clicked through the new bounded ComboBoxes in the running app — this environment is headless, so the UI is verified by `MergerViewModel` unit tests + the `MergerPageLoadTests` XAML gate + build only, per the existing M7 pattern (§13/§14). |
| 2026-07-12 | 0.17 | **Merger output options bounded by the source (`TargetBounds`) — design only, no code** (spec: `docs/superpowers/specs/2026-07-12-merger-target-bounds-design.md`). The override UI let the user pick settings that are **strictly worse than doing nothing**: 60 fps output from all-30 fps clips (ffmpeg duplicates every frame), 4K from 1080p sources (invented pixels), 5.1 from stereo (four silent channels), and `1920 × 102` (width and height are *independent* free-text boxes). `MergeTargetDerivation` already takes the **maximum** across the clips; the override UI simply ignored that ceiling. (**CRF and odd dimensions are already guarded** — `TargetCrf` ignores anything outside 0–51 and `TargetWidth`/`TargetHeight` round even; an earlier draft of the spec claimed otherwise and was corrected against the code. The gap is the *ceiling*, and the aspect-ratio hole two independent boxes leave open.) **Fix: a new pure `TargetBounds`, built from the derivation's own maxima**, turns each maximum into a *list of allowed values* — and **the derived target is always the first entry of each list**, so the offered options and the derived target cannot drift (the same keystone discipline `ConformanceCheck` enforces for the fast path). Resolution becomes a **dropdown of standard steps at the source's aspect ratio**, replacing two free text boxes, which makes upscaling, **odd dimensions** (libx264 rejects them outright) and absurd aspect ratios *unrepresentable* rather than validated-after-the-fact. The ceiling **moves** as clips are added/removed, so one rule covers it: **snap down to the largest allowed value ≤ the current one** — silently, keeping the override (the user's intent to go *smaller* is preserved). **Codec × container is deliberately NOT restricted:** all 8 combinations (2 containers × 2 video × 2 audio) were tested against the real bundled ffmpeg 8.1 and **every one muxes cleanly**, so there is no invalid combination to block — MP4 + Opus is a *playability* problem (VLC/Chrome yes, QuickTime/TVs no), not a validity one, and gets a **warning**. This keeps the promise sharp: **a blocked option means "provably pointless", never "we would rather you didn't".** §13 updated. Design only in this change — **implemented in 0.18, below.** |
| 2026-07-12 | 0.16 | **M7 PR 2 follow-ups — the six defects the user's headed click-through found.** PR 2 shipped with 597 green tests and a "pending a user visual check" caveat; the check found six real bugs, four of them invisible to the whole suite because **nothing ever instantiated the page**. (1) **Shuffle could pin a row forever.** `MergerViewModel.ShuffleSeed` was seeded once at construction and **never re-seeded** — despite a comment claiming the UI re-seeded it on every click. Every click rebuilt `new Random(sameSeed)` and replayed the **identical permutation**, so any index that permutation maps to itself is a row the user can *never* move (reproduced: ~63% of seeds pin ≥1 row; 17–33% pin the 2nd row specifically; the list cycles through only ~3 orders). `Ordering.Shuffle` was correct all along — an unbiased Fisher–Yates fed a constant seed. Now re-seeded after each use. **The suite could not have caught this:** every shuffle test assigned `ShuffleSeed` immediately before each `Shuffle()` call, simulating a re-seeding UI that did not exist (§14). (2) **The clip list was destroyed by navigation** — `MergerViewModel` was registered `AddTransient`, and unlike the downloader (whose queue lives in a singleton `DownloadManager`) the merger's clips live *in the ViewModel*. Now a **singleton**: the list survives navigation and resets only when the app closes. This deliberately reverses PR 2's tested decision ("a fresh page must not open holding the last merge's clip list"). (3) **`ClearClips`** + a **Clear all** button beside Shuffle; it is a list mutator, so it is frozen during a merge like the others. (4) **The clip box ignored dark mode** — the page used the plain WPF `ListView`, and **WPF-UI ships no style for it at all**: `ControlsDictionary` keys its implicit styles to its own subclasses. Now `ui:ListView`, plus an explicit `ControlElevationBorderBrush` outline (WPF-UI's ListView has no border, leaving the drop target with no visible edge). **A first attempt at this fix crashed the app** — `BasedOn="{StaticResource {x:Type ListViewItem}}"` asked for a key that does not exist, which compiles clean and throws `XamlParseException` at *page-load*, in front of the user. (5) **The mouse wheel did nothing outside the clip list.** `NavigationViewContentPresenter` **already wraps every page in a `ScrollViewer`** — which is why no other page has one. MergerPage added a second: the outer scroller handed it unbounded height so it could never scroll (`Scrollable=0`), yet a WPF `ScrollViewer` **marks wheel events handled even when it cannot move**, swallowing every tick while the shell's scroller (which had 185 px to give) never saw them. Removed. (6) Both new §13 rules recorded. **The durable fix is §14's new `MergerPageLoadTests`**: it builds the real page on an STA thread against the real resource dictionaries inside the real presenter, so a bad resource key or a nested scroller now fails in `dotnet test` rather than in front of the user — verified by mutation (re-introducing each bug fails exactly its own test and nothing else). **(7) A missing binary was reported as a corrupt file.** The user hit *"Not a video: x.mp4 could not be read as a video"* on a perfectly good mp4. **`ffprobe.exe` was simply absent from `assets/binaries`** — the folder is git-ignored, M7 PR 1 added ffprobe as a **new** required binary, and the fetch script had only been re-run inside the merger worktree, so every test passed there while the main checkout could not probe a single file (§9 now warns about exactly this). **The defect, though, is the message:** `MergerViewModel` collapsed *"the probe failed"* and *"the probe succeeded, but the file has no video track"* into one notification and **discarded `probe.Error`** — the analyzer was already saying `"Could not run ffprobe: The system cannot find the file specified."` and the ViewModel threw it away, blaming the user's file for a missing binary. Now two distinct notifications, with the failure path surfacing the analyzer's own reason (§11: *never diagnose an environment fault as a user-data fault*). Release build **0/0**; **606/606** unit tests pass (597 → 606, 9 new); **3/3** merge integration tests pass against the real bundled ffmpeg **in the main checkout** — which they could not have done before. |
| 2026-07-12 | 0.15 | **M7 PR 2 (the module UI) delivered — M7 complete.** `MergerViewModel` (headless, fully unit-tested with fakes) + `MergeClipViewModel` + `MergerPage` + `VideoMergerTool : ITool` + `AddVideoMerger()`. **The shell was not modified** — the tool registers `ITool`/`IToolPage` and is discovered, which is the whole point of the modular seam (§4/§5). Four engine changes were deliberately reopened: (1) **`MergeProgress` gained `ClipPercents`** (per-clip bars, user-chosen) — a *conforming* clip reads **100 from the first report**, because it has no encoding work and showing it as pending would be a lie; (2) **`HistoryEntry` gained `Source`** (`Download`/`Merge`, user-chosen) so a merge is a first-class history row rather than a download with a blank URL — old `history.json` files still load via a **tolerant converter** (unknown name / null / number all degrade to `Download`: *degrade the field, never destroy the file*); (3) **`MergeService` now verifies its own output** — ffmpeg's concat demuxer does **not** fail on a segment it cannot open, it **drops that segment and every one after it and exits 0**, so the engine as shipped in PR 1 could hand the user a silently truncated video and call it a success (trivially reachable on the fast path, where the list holds the user's own paths). Fixed with an open-every-segment preflight **+** a post-merge duration check **+** deletion of the misleading partial output; `-xerror` is *not* the fix (it fails healthy merges). (4) **The output container and the file extension are now kept in lockstep** — `ConcatArgsBuilder` emits no `-f`, so **ffmpeg picks its muxer from the file's extension** while `Target.Container` only gated `-movflags +faststart`: a derived **MKV** target therefore wrote a real **MP4**. **`MergeErrors`** (a static per-module mapper, matching `YtDlpErrors`) replaces the spec's `IErrorMapper`, which has never existed in this codebase. `VideoMergerTool.SortOrder` is **20, not the spec's 2** — ordering is *ascending* and the downloader is 10, so 2 would sort the merger *above* it, inverting the spec's own intent. Release build **0/0**; **597/597** unit tests pass; and the merger is finally proven end-to-end by **3 trait-gated integration tests** that shell out to the real bundled ffmpeg — three mismatched `testsrc` clips normalized and merged (**probing the output**, not just trusting the exit code), the fast path proven to *never enter the normalize phase*, and a cancelled merge proven to strand no temp debris and no half-written output. **The final whole-branch review then caught the composition defect** (each task was individually correct): the **clip list stayed editable during a merge**. Only Merge/Cancel were gated on `IsMerging` — Task 5 built the list commands, Task 7's threading argument *assumed* the list was frozen, and Task 8 added a drag gesture that bypasses commands entirely. Reordering mid-merge misattributes `ClipPercents` (indexed by the request snapshot) to the wrong rows, a removed clip is still in the output, and `OnMergeProgress` — which runs on **ffmpeg's stdout callback thread** — indexes `Clips` while the UI thread mutates it, throwing inside a `Process.OutputDataReceived` handler that has **no catch anywhere up the stack**. The list is now frozen while merging, via `CanExecute` **and** an explicit guard in every mutator (the drop/drag gestures never reach a command). It also found that **a failed merge could destroy the previous one**: the default output name is a constant, so merging twice targets the same path, and ffmpeg's `-y` overwrote the first merge's good video before the truncation guard deleted the wreckage — **leaving the user with neither file**. The concat now writes a **sibling** (same directory, so `File.Move` is a free rename; same extension, since ffmpeg picks its muxer from it), verifies *that*, and only moves a proven-whole merge into place — nothing the user already had is touched until there is something worth replacing it with. Plus: the override UI accepted **odd dimensions** (libx264 rejects them outright — `ToEven` now applies to overrides as it does to derivation), and the duration tolerance, a **flat 1 s**, could fail a healthy many-clip merge and *delete it* — it now scales with clip count, clamped to half the shortest clip so a dropped clip is still caught. **Not verified:** the page's layout, drag gestures and dark-mode rendering — this environment is headless and `FFMedia.Tests` does not reference the WinExe, so the XAML needs a user visual check (§13). |
| 2026-07-11 | 0.14 | **M7 PR 1 (engine) delivered** — no UI. **`FFMedia.Media` is realized** (§8): `MediaInfo`/`FrameRate` models, `IMediaAnalyzer`/`FfprobeMediaAnalyzer` (ffprobe JSON over `IProcessRunner`), `IFfmpegRunner`/`FfmpegRunner` (`-progress pipe:1`, stderr tail on failure), and the pure `FfprobeParsing` + `FfmpegProgressAccumulator` (§8 row **renamed** from `FfmpegProgressParsing` — it carries state across lines, emitting one snapshot per `progress=` terminator). Analyzer runs `-v error`, **not** `-v quiet`, which would suppress the very stderr the failure path reports. New module **`FFMedia.Tools.VideoMerger`** holds the pure engine — `MergeTargetDerivation` (dimensions rounded **down to even**: yuv420p's 2×2 chroma subsampling makes libx264 reject an odd width/height outright), `ConformanceCheck` (the keystone: drives the fast path, the badge and the ETA — `MergeEstimator` and `MergeService` both **call** it rather than re-implement it, so the estimate can never describe a different plan than the one that runs; it also rejects a clip carrying **extra streams**, e.g. embedded subtitles, which would otherwise be stream-copied and silently mute the later clips), `NormalizeArgsBuilder` (Fit/Fill/Stretch + `anullsrc` for silent clips), `ConcatArgsBuilder` (list-file `'\''` escaping — the naive `\'` makes ffmpeg exit −2), `Ordering.Shuffle` (seeded Fisher–Yates honoring locked indices), `MergeEstimator`, `SpeedProfile`/`SpeedProfileStore` (`encode-speed.json`, §10), `DiskSpaceGuard` (reserves temp **+ output**, since temp alone is 0 on the fast path), `TempDirectorySweeper` (run from the merge preflight, so a previous run’s orphans do not count against this run’s disk check) — plus `IMergeService`/`MergeService` (preflight → bounded-concurrency normalize → stream-copy concat; temp cleanup on **every** exit path; cancel returns `Canceled`, not `Failed`). `ExternalBinary.Ffprobe` added; `fetch-binaries.ps1` extracts `ffprobe.exe` from the *same* pinned, SHA-256-verified BtbN zip. Core gains a non-generic `Result`. `AddVideoMergerEngine` registers the engine; **no `ITool`/page registration** — the shell must not navigate to a page that does not exist yet (PR 2). Release build 0/0; **425/425** unit tests pass. |
| 2026-07-10 | 0.13 | **M7 designed** — second tool module, `FFMedia.Tools.VideoMerger` (full spec: `docs/superpowers/specs/2026-07-10-m7-video-merger-design.md`). Ingest local clips → **auto-derived, user-overridable** standardization target (resolution/FPS/codecs/audio layout) → **normalize only non-conforming clips** to temp intermediates under a `SemaphoreSlim` cap (the §12 pattern) → **stream-copy `concat`**. When every clip already conforms, normalization is skipped entirely and the merge is a ~1 s copy (**fast path**). Aspect mismatch defaults to **letterbox/pillarbox**, with a per-merge `FitMode` (Fit / Fill+Crop / Stretch); clips lacking an audio track get a synthesized `anullsrc` silent track so `concat`'s identical-stream-layout requirement holds. Ordering is manual (drag/move), random, or **random with clips locked to specific indices** (seeded Fisher–Yates ⇒ deterministic tests). Pre-merge the UI shows **exact output duration** (Σ clip durations — no transitions) plus an **estimated merge time as a range**, derived from a `SpeedProfile` rolling average of the user's own measured encode throughput (new `encode-speed.json`, §10) and **replaced by ffmpeg's real ETA** once merging starts; a **disk-space guard** fails fast before any encoding if the temp volume can't hold the intermediates. Live weighted progress (encode ≈ 95 %, concat ≈ 5 %) and cancel; temp cleanup on every exit path. **§3/§8: FFMpegCore dropped** — never referenced, and it manages its own child processes, bypassing the `IProcessRunner` seam; `FFMedia.Media` is instead realized as `IMediaAnalyzer`/`IFfmpegRunner` over `IProcessRunner` with pure `FfprobeParsing`/`FfmpegProgressParsing`. **§9: `ffprobe.exe` added**, extracted from the *same* pinned, SHA-256-verified BtbN zip (no new download, no new hash). §4/§5/§10/§17/§19 updated; M7 deferrals recorded (transitions, per-clip trim, per-clip fit, background music, merge queue). Delivery: **PR 1** engine (no UI), **PR 2** module VM + page + nav. Design only — no code in this change. |
| 2026-07-10 | 0.12.2 | Post-v1 UI fixes (round 2), both reported after installing v1. (1) **Dark-mode text still black on pages:** the v0.12 `FluentWindow.Foreground` themes only the chrome (title bar / nav pane) — WPF's `Frame` (which `NavigationView` hosts pages in) **isolates property-value inheritance**, so every plain `TextBlock` inside a page kept WPF's default **black** foreground (WPF-UI 4.3.0 ships no implicit keyless `TextBlock` style). Fixed by setting `Foreground="{DynamicResource TextFillColorPrimaryBrush}"` on **each `Page` root** (`WelcomePage`, `DownloaderPage`, `HistoryPage`, `SettingsPage`); page-local inheritance now themes all plain text and updates live on theme switch, while control templates (buttons/combos) keep overriding their own foreground. (2) **Blank content on launch:** `NavigationView` selects nothing by default, so the frame was empty until the user clicked a pane item; the shell now navigates to the (previously-unwired) `WelcomePage` once `RootNavigation` is `Loaded` (`WelcomePage` registered in DI). Release build 0/0, 189/189 unit tests pass; GUI appearance/landing pending a user visual check (headless dev env). |
| 2026-07-08 | 0.12.1 | Docs: added a **"personal project" scope note** — FFMedia is built primarily for the author's personal use and published as-is, with no maintenance/support commitment. Surfaced in the README (a `> [!NOTE]` callout under the intro + a Legal bullet) and SDD §1. No code change. |
| 2026-07-08 | 0.12 | Post-v1 UI fixes (shell). (1) **Dark-mode text was black:** page `TextBlock`s inherited WPF's default black `Foreground`; the `FluentWindow` now sets `Foreground="{DynamicResource TextFillColorPrimaryBrush}"` so text follows the theme and updates live on theme switch. (2) **Missing footer icons:** History/Settings nav items switched from raw-glyph `FontIcon` to WPF-UI `SymbolIcon` (`SymbolRegular.History24`/`Settings24`, bundled font — no OS-font dependency). (3) **Title bar:** added the logo via `ui:TitleBar.Icon` + "FFMedia" title at top-left; **removed the title-bar theme toggle** (theme already lives in Settings), dropping `MainWindowViewModel`'s now-unused `ToggleThemeCommand` + `ISettingsService`/`ThemeService` deps. (4) **Tool nav icon:** `ITool.IconGlyph` reinterpreted as a **`SymbolRegular` name** (e.g. `ArrowDownload24`), resolved by the shell to a `SymbolIcon` (`Enum.TryParse`, fallback `Apps24`) so the YouTube Downloader icon renders reliably — Core still exposes only a string. (5) **Settings auto-save:** removed the Save button — each setting persists on change via `On<Property>Changed` hooks, theme applies immediately, and **max concurrency** shows a red "takes effect after you restart" reminder (it's read once at construction, §12). (6) **History feedback:** `HistoryViewModel` now takes `INotificationService`; open-file/open-folder raise a `Warning` notification when the file/folder is missing (opening the parent folder if only the file is gone) and an `Error` if the shell launch throws. Release build 0/0, 189/189 unit tests pass; GUI appearance pending user visual check (headless dev env). §4.1/§13 updated. |
| 2026-07-08 | 0.11 | Public-repo licensing & disclaimers (audit after making the repo public). Added **`LICENSE`** (MIT — covers FFMedia's own source only) and **`THIRD-PARTY-NOTICES.md`** (yt-dlp = Unlicense; bundled **FFmpeg** = GPL-3.0 `win64-gpl` build, with corresponding-source links + trademark/non-affiliation notes; NuGet deps + their licenses). Expanded README with a **License** section and a fuller **Legal & disclaimer** (responsible use, no DRM circumvention, non-affiliation with YouTube/Google/FFmpeg/yt-dlp, no-warranty) and corrected the tech-stack list (FFMpegCore is planned, not yet referenced). §16 documents the MIT + GPL split and the FFmpeg process-isolation (mere aggregation) reasoning. Security/PII audit found **no secrets, credentials, or machine-specific paths** in tracked files. |
| 2026-07-08 | 0.10 | M6 ship v1 (PR 2): yt-dlp self-update + pinned binaries + app logo. Core gains `IProcessRunner`/`ProcessRunner` (`FFMedia.Core.Processes`, the process seam) and `IBinaryUpdateService`/`BinaryUpdateService` (`FFMedia.Core.Binaries`; installed versions via `--version`/`-version`, `yt-dlp -U` self-update, and a GitHub `releases/latest` check for yt-dlp). A singleton `BinaryUpdateViewModel` drives a Settings **Binaries** section (yt-dlp + ffmpeg versions, "Update yt-dlp", "check yt-dlp on startup" toggle) and a fire-and-forget startup check that notifies (never auto-applies). `AppSettings.Version` moves to **3** (`CheckYtDlpForUpdatesOnStartup`, default true). `fetch-binaries.ps1` pins `yt-dlp` **2026.07.04** and ffmpeg BtbN **autobuild-2026-07-07-13-44**, verifying both against a known SHA-256 (throws on mismatch); a Velopack app update re-bundles the pinned yt-dlp, expectedly reverting a prior in-app self-update. `assets/branding/logo.png` → a committed multi-res `app.ico` (`build/make-icon.ps1`), wired as the exe/window/taskbar/installer icon plus in-app (title bar, left of the theme toggle, + welcome page). Post-review fixes (whole-branch review before PR): the GitHub check now surfaces the remote tag only when **strictly newer** than installed (`YtDlpVersion.IsNewer`, pure + unit-tested) rather than on any inequality, the Core `HttpClient` gets an explicit 10 s timeout, and the latest-version failure paths (HTTP error, malformed JSON, installed-is-newer) are now covered by tests. Verified: Release build 0 warnings/0 errors, 189/189 unit tests pass (`Category!=Integration`), pinned `fetch-binaries.ps1` ran and verified clean. **Not yet verified:** a headed GUI smoke of the Binaries section, the real `yt-dlp -U`, and the logo surfaces — pending a user dry-run (headless dev environment). §6/§9/§10/§13/§16/§17/§19 updated; M6 PR 2 delivered, public v1.0.0 tag remains user-initiated. |
| 2026-07-07 | 0.9 | M6 ship v1 (PR 1): Velopack packaging + app delta auto-update. Explicit `Program.Main` runs `VelopackApp.Build().Run()` before WPF startup. Core `IUpdateService`/`AppUpdateInfo` realized in App by `VelopackUpdateService` (Velopack `UpdateManager` + GitHub `GithubSource`, stable channel, pinned Velopack **1.2.0**; safe no-op when uninstalled/dev). Singleton `UpdateViewModel` drives a dismissible shell update banner (Update & restart / Later) and a Settings "Check for updates now" action + current-version display; `AppSettings.CheckForUpdatesOnStartup` (schema **v2**) gates a fire-and-forget startup check. `build/pack.ps1` (publish self-contained + `vpk pack`, unsigned) + tag-gated `.github/workflows/release.yml` (`vpk upload github`). Verified: solution builds Release 0/0, all 152 unit tests pass, `pack.ps1` produced a real installer + nupkg + `RELEASES` locally (pack machinery proven). **Not yet verified:** the interactive install → update → relaunch loop and a GUI smoke of the banner/Settings controls — pending a user dry-run (headless dev environment). §3/§6/§9/§10/§13/§15/§17 updated; M6 marked in progress (PR 2 — binary updates — pending). |
| 2026-07-06 | 0.8 | M5 experience (PR 2): `IPresetService`/`IHistoryService`/`INotificationService` realized. JSON-backed `PresetService`/`HistoryService` (`presets.json`/`history.json`, `Changed` events); module `PresetMapping` (de)serializes `DownloadConfig` to an opaque payload string (tolerant on malformed/blank input); `DownloaderViewModel` gains save/apply/delete preset commands + an inline Presets section on the Downloader page. `DownloadManager` gains optional `IHistoryService?`/`INotificationService?` ctor params and appends history + notifies on `Completed`, notifies only on `Failed`, does neither on `Canceled` — dispatched inside `RunAndTrackAsync` before the idle signal, swallowed on failure so a broken sink can't break the queue. App gains `SnackbarNotificationService` (WPF-UI `SnackbarPresenter`) and a **History** screen (footer nav item: filter, open file/folder, clear). Re-download from history explicitly deferred (needs cross-page seeding seam + a config-carrying `HistoryEntry`). §6/§7.2/§13/§17/§19 updated; M5 marked complete. |
| 2026-07-06 | 0.7 | M5 foundation (PR 1): generic `JsonStore<T>` (atomic write, corrupt-file quarantine) + `AppSettings`/`ISettingsService` (JSON at %AppData%\FFMedia\settings.json). `AddFFMediaCore` gains a `dataDirectory` param and registers `ISettingsService`. App gains a `ThemeService` (dark/light/system via WPF-UI), a Settings screen (default folder, max concurrency, theme) as a footer nav item, a title-bar theme toggle, and applies the persisted theme at startup. Settings wired into behavior: downloader output folder seeded from settings; `DownloadManager` concurrency cap read from settings. §6/§10/§12/§13/§17/§19 updated. |
| 2026-07-05 | 0.6 | M4 processing: `ProcessingOptions` (`TrimRange?`/`PreciseCut`/`EmbedSubtitles`/`SubtitleLanguage`/`EmbedMetadata`/`EmbedThumbnail`, default metadata+thumbnail on) added to `DownloadConfig.Processing`; pure `OptionSetBuilder.ApplyProcessing` emits `--download-sections` (+ `--force-keyframes-at-cuts` when precise), video-only `--write-subs --write-auto-subs --embed-subs --sub-langs`, and `--embed-metadata`/`--embed-thumbnail`; pure `TrimParsing` (HH:MM:SS/MM:SS/seconds → `TimeSpan`, range only when valid). ViewModel gained processing selections + live trim-hint validation; page gained a Processing section. §7.3/§8/§17 updated to match. |
| 2026-07-05 | 0.5 | M3 queue: `IDownloadManager`/`DownloadJob` (module-owned, not Core) run a bounded-concurrency (`SemaphoreSlim` cap 3) download queue with auto-start on add, per-job + cancel-all cancellation, and clear-completed; `RetryPolicy` retries transient network failures with exponential backoff (3 attempts/1s base) while permanent errors fail fast; `IPlaylistProbe`/`PlaylistMapping` expand a playlist/channel URL into one job per entry at add-time. ViewModel restructured to add-to-queue with a bound `Jobs` list; page shows per-job progress/cancel + cancel-all/clear-completed. §6/§7.2/§12/§19 updated to match the realized design; §19 concurrency + pause/resume resolved. |
| 2026-07-05 | 0.4 | M2 formats: full matrix via pure `OptionSetBuilder` — video (MP4/MKV/WebM) at a resolution cap + audio-only (MP3/WAV/M4A/Opus/FLAC) with bitrate; `DownloadConfig` model; ViewModel selections + page dropdowns; §7.3 flags finalized (mux over recode, `--audio-quality` via custom option). |
| 2026-07-05 | 0.3 | M1 vertical slice delivered: YouTube Downloader tool (probe + single-MP4 download w/ live progress + cancel) via YoutubeDLSharp; module + tests retargeted to `net9.0-windows` (UseWPF); `IMediaProbe`/`IDownloadService` seam with a unit-tested `DownloaderViewModel` (fakes) + trait-gated yt-dlp integration test; shell nav wiring joins `ITool` + `IToolPage` (WPF-UI navigation); added `Result<T>` and `IToolPage` to Core. |
| 2026-07-04 | 0.2 | M0 foundation delivered: solution skeleton, Core (`ITool`/`IToolRegistry`, `IBinaryProvider`, `AddFFMediaCore`), WPF-UI shell w/ Host+Serilog, fetch-binaries script, CI. `ITool.Icon` is now a string glyph (Core stays UI-agnostic); assertion library deferred (FluentAssertions v8 is paid); M0 uses plain xUnit `Assert`. WPF-UI resolved to 4.3.0. |
| 2026-07-04 | 0.1 | Initial SDD from brainstorming: stack (WPF+WPF-UI/.NET 9), modular shell architecture, downloader design, milestones M0–M7. |
