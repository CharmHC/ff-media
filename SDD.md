# FFMedia — Software Design Document (SDD)

> **Status:** Living document · **Version:** 0.13 · **Last updated:** 2026-07-10
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
   ├─ FFMedia.Tools.YouTubeDownloader/  ← v1 tool module (VMs, Views, orchestration)
   ├─ FFMedia.Tools.VideoMerger/ ← M7 tool module (standardize + merge)
   └─ FFMedia.Tests/             ← xUnit tests (targets Core + module logic)
```

**Target frameworks:** `FFMedia.Core` and `FFMedia.Media` target `net9.0` and stay
UI-framework-free. **Tool modules that hold WPF Views/ViewModels**
(e.g. `FFMedia.Tools.YouTubeDownloader`) target **`net9.0-windows` with `UseWPF=true`**.
**`FFMedia.Tests` targets `net9.0-windows`** so it can reference the module and
unit-test ViewModels headlessly (no window is shown).

**Dependency rules (enforced by project references):**

- `FFMedia.Core` references **no** UI framework. It is the testable heart.
- `FFMedia.Media` references `FFMedia.Core` only (no third-party media library).
- Tool modules reference `FFMedia.Core` (+ `FFMedia.Media`, WPF-UI). They **do not**
  reference `FFMedia.App`.
- `FFMedia.App` references `FFMedia.Core` + each tool module (composition root only).
- Dependencies point **inward** toward `Core`; `Core` depends on nothing app-specific.

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
| `MediaInfo` | record | `Duration`, `ContainerFormat`, `VideoStreamInfo?` (w/h, frame rate, codec, pixel format, rotation), `AudioStreamInfo?` (codec, sample rate, channels) |
| `IMediaAnalyzer` → `FfprobeMediaAnalyzer` | service | `ffprobe -v quiet -print_format json -show_format -show_streams` → `Result<MediaInfo>` |
| `FfprobeParsing` | **pure** | ffprobe JSON → `MediaInfo` |
| `IFfmpegRunner` → `FfmpegRunner` | service | Runs ffmpeg with an arg list + `-progress pipe:1 -nostats`; streams progress; honors `CancellationToken`; captures the stderr tail on failure |
| `FfmpegProgressParsing` | **pure** | `out_time_us=…` / `speed=…` lines → `FfmpegProgress(Position, Speed)` |

Both services locate their binary through `IBinaryProvider` (no PATH assumption).

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
- **Video Merger screen (M7):** clip list (**drag-to-reorder** + move up/down, per-clip lock
  toggle showing the pinned index, conformance badge, per-clip progress, remove) · Shuffle
  (honors locks) · output target (auto-derived, every field overridable, + `FitMode`, folder,
  filename) · a summary line (**exact output duration**, **estimated merge time as a range**,
  count of clips needing re-encode, temp space) · Merge / Cancel + overall progress.
- Accessibility: keyboard navigation, sufficient contrast in both themes.

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
- **Integration (opt-in, trait-gated, off in CI):** hit one stable known video to
  smoke-test the real yt-dlp/ffmpeg pipeline.
- **Coverage priority:** the orchestration/argument-building logic is the highest
  risk and gets the most tests; UI is thin by design.
- TDD is the default workflow for Core logic.

---

## 15. Packaging & Distribution

- **Velopack** produces the installer and delta auto-updates.
- Bundled `yt-dlp.exe` + `ffmpeg.exe` are included in the release package.
- Release channel + update feed configured in `build/`.
- **The GitHub repo must be public.** The in-app update check uses
  `GithubSource(..., accessToken: null, ...)` (anonymous), and GitHub returns **404** to
  anonymous callers on a *private* repo — surfacing as "Update check failed." A distributed
  desktop app can't ship a token safely (extractable from the `.exe`), so public is the
  distribution model. `ChamHC-dev/ff-media` was made public on 2026-07-08 for this reason.
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
| **M6** | Ship v1 | 🚧 **in progress** — **PR 1 delivered** (branch `feat/m6-packaging-autoupdate`): Velopack packaging (`build/pack.ps1`, tag-gated `release.yml`) + app delta auto-update (`IUpdateService`/`VelopackUpdateService`, shell update banner, Settings toggle + check-now). **PR 2 delivered** (branch `feat/m6-binary-updates`): yt-dlp self-update (`IProcessRunner`/`IBinaryUpdateService`), Settings **Binaries** section, pinned + SHA-256-verified `fetch-binaries.ps1`, and the app logo. **Remaining:** the public **v1.0.0** release tag stays **user-initiated**. |
| **M7** | Video Merger | 📐 **designed** ([spec](docs/superpowers/specs/2026-07-10-m7-video-merger-design.md)) — second tool module (`FFMedia.Tools.VideoMerger`), validating the modular seam. Ingest local clips → auto-derived, user-overridable standardization target → normalize non-conforming clips (bounded concurrency) → stream-copy concat. Ordering manual / random / random-with-locks; pre-merge output duration + estimated merge time; live progress; cancel. Realizes `FFMedia.Media` (§8) and adds `ffprobe.exe` (§9). **PR 1** = engine (no UI); **PR 2** = module VM + page + nav. |

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
