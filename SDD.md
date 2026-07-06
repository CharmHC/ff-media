# FFMedia — Software Design Document (SDD)

> **Status:** Living document · **Version:** 0.7 · **Last updated:** 2026-07-06
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
| Media processing | **[FFMpegCore](https://github.com/rosenbjerg/FFMpegCore)** (MIT) | Fluent ffmpeg wrapper for trim + future tools. MIT license (commercial-safe). |
| Logging | **Serilog** (file + in-app sink) | Diagnose yt-dlp/ffmpeg failures from user logs. |
| Persistence | **System.Text.Json** (settings/presets/history) | Simple; migrate history to SQLite only if it grows. |
| Packaging / update | **[Velopack](https://velopack.io/)** | Installer + delta auto-update, no UAC prompt; can update bundled yt-dlp. |
| Testing | **xUnit** | Tests use xUnit. Assertion library deferred — FluentAssertions v8+ is a paid commercial license; evaluate **Shouldly** / **AwesomeAssertions** (both free) when richer assertions are needed. M0 uses plain `Assert`. |

> **Rejected alternatives:** WinUI 3 (rougher windowing/packaging for a solo dev),
> Xabe.FFmpeg (CC BY-NC-SA / non-commercial), Electron/Tauri (heavier, non-native),
> Python/PyQt (weaker native Windows packaging story).

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
│ YouTube      │   │ (future) Video   │   │ (future) more     │
│ Downloader   │   │ Standardize/Merge│   │ tools…            │
│ (v1 module)  │   │                  │   │                   │
└──────┬───────┘   └──────────────────┘   └───────────────────┘
       │ uses
       ▼
┌──────────────────────────────────────────────────────────┐
│ FFMedia.Core  (UI-agnostic services & abstractions)      │
│  ITool · IBinaryProvider · ISettingsService ·            │
│  IHistoryService · INotificationService ·                │
│  IProcessRunner                                          │
├──────────────────────────────────────────────────────────┤
│ FFMedia.Media — FFMpegCore wrappers (shared)             │
├──────────────────────────────────────────────────────────┤
│ Bundled binaries:  assets/binaries/yt-dlp.exe            │
│                    assets/binaries/ffmpeg.exe            │
└──────────────────────────────────────────────────────────┘
```

### 4.1 The module contract (`ITool`)

```csharp
public interface ITool
{
    string Id { get; }              // stable, e.g. "youtube-downloader"
    string DisplayName { get; }     // "YouTube Downloader"
    string Description { get; }
    string IconGlyph { get; }       // Segoe Fluent Icons glyph; kept a string so Core stays UI-agnostic
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
│  └─ binaries/                  ← bundled yt-dlp.exe, ffmpeg.exe (git-ignored; fetched by build script)
├─ build/                        ← packaging scripts (Velopack), binary-fetch script
├─ docs/
│  └─ superpowers/specs/         ← brainstorming spec record (points here)
└─ src/
   ├─ FFMedia.App/               ← WPF shell (composition root, shell views, theming)
   ├─ FFMedia.Core/              ← abstractions + services, NO WPF references
   ├─ FFMedia.Media/             ← FFMpegCore wrappers (shared media ops)
   ├─ FFMedia.Tools.YouTubeDownloader/  ← v1 tool module (VMs, Views, orchestration)
   └─ FFMedia.Tests/             ← xUnit tests (targets Core + module logic)
```

**Target frameworks:** `FFMedia.Core` and `FFMedia.Media` target `net9.0` and stay
UI-framework-free. **Tool modules that hold WPF Views/ViewModels**
(e.g. `FFMedia.Tools.YouTubeDownloader`) target **`net9.0-windows` with `UseWPF=true`**.
**`FFMedia.Tests` targets `net9.0-windows`** so it can reference the module and
unit-test ViewModels headlessly (no window is shown).

**Dependency rules (enforced by project references):**

- `FFMedia.Core` references **no** UI framework. It is the testable heart.
- `FFMedia.Media` references `FFMedia.Core` (+ FFMpegCore).
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
   (`--force-keyframes-at-cuts` for precise cuts); the `FFMedia.Media` FFMpegCore trim
   wrapper is reserved for future tools (see §8).
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

Thin, testable wrappers over **FFMpegCore** for operations FFMedia performs
directly (as opposed to delegating to yt-dlp):

- Frame-accurate **trim/clip** (with or without re-encode).
- Probe media info (duration, streams) when needed independent of yt-dlp.
- **Foundation for future tools** (standardize resolution/FPS/format, concat/merge).

`FFMedia.Media` locates `ffmpeg.exe` through `IBinaryProvider` (no PATH assumption).

> **M4 note:** the YouTube Downloader's trim/clip feature (§7.3) is realized via yt-dlp's
> own `--download-sections` (+ `--force-keyframes-at-cuts` for a precise cut) rather than a
> post-download `FFMedia.Media` pass — it's simpler and avoids a redundant re-encode. The
> `FFMpegCore`-backed trim wrapper described above stays a reserved foundation for future
> tools that need frame-accurate cutting independent of yt-dlp.

---

## 9. Binary Management

- **Bundling:** `yt-dlp.exe` and `ffmpeg.exe` ship in the installer under
  `assets/binaries/`. They are **git-ignored**; a `build/fetch-binaries` script
  downloads pinned versions for local dev and CI.
- **Resolution:** `IBinaryProvider` resolves the app-relative binary path at
  runtime (`AppContext.BaseDirectory/assets/binaries`); never relies on the system
  PATH. The **`FFMedia.App` and `FFMedia.Tests` builds copy `assets/binaries/*.exe`
  into their output** so `dotnet run` and the integration tests find the binaries
  (no-op when the folder is empty — run `build/fetch-binaries.ps1` first).
- **Updating:**
  - **App + ffmpeg** update via **Velopack** releases.
  - **yt-dlp** additionally supports in-app self-update (`yt-dlp -U`) because it
    breaks frequently against YouTube changes and must update independently of app
    releases. Update checks are user-initiated or on a configurable schedule.

---

## 10. Data & Persistence

All under `%AppData%\FFMedia\`:

| File | Content | Format |
|---|---|---|
| `settings.json` | Default output folder, concurrency, theme, update prefs | JSON |
| `presets.json` | Named download presets | JSON |
| `history.json` | Completed downloads (title, url, path, format, timestamp) | JSON → SQLite if it grows |
| `logs/ffmedia-*.log` | Rolling Serilog logs | text |

Schema changes carry a `version` field for forward migration.

> **M5 PR 1 note:** `settings.json` now exists, written by the generic `JsonStore<T>`
> (atomic temp-file write + corrupt-file quarantine to `.bak`, defaulting on read
> failure). `AppSettings` carries a `Version` field for forward migration, per the
> convention above.

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
  title-bar theme toggle; Mica backdrop.
- **Downloader screen:**
  - URL input + "Add" (accepts multiple / paste-list).
  - Preview cards (thumbnail, title, duration) after probe.
  - Format/quality selector + options (trim, subs, embed) + output folder.
  - **Queue list** with per-item progress bar, speed/ETA, pause? (stretch), cancel.
  - Footer: global actions (start all, clear completed, open folder).
- **Settings screen:** default folder, concurrency, theme, update cadence, binary
  versions + "Update yt-dlp".
- **History screen:** searchable list with "open file/folder" and "re-download".
- Accessibility: keyboard navigation, sufficient contrast in both themes.

> **M5 PR 1 note:** the **Settings screen** now exists (footer nav item) with default
> output folder, max concurrency, and theme controls, backed by `ISettingsService`.
> A **title-bar theme toggle** (light/dark/system, via WPF-UI `ApplicationThemeManager`)
> also now exists and applies the persisted theme at startup. Update cadence and binary
> version display remain planned (M5 PR 2 / M6).

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
- Self-contained .NET publish (no framework prerequisite for end users).
- CI builds on every push; release workflow tags → Velopack pack + publish.

---

## 16. Security, Legal & Privacy

- **No telemetry**; all data stays local.
- App displays a **disclaimer**: users are responsible for complying with content
  owners' rights and YouTube's Terms of Service; FFMedia is a general-purpose tool.
- No DRM circumvention, paywall bypass, or credential harvesting.
- External binaries are pinned to known versions and fetched over HTTPS with
  integrity checks in the build script.

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
| **M5** | Experience | 🚧 in progress (branch `feat/m5-foundation`) — **PR 1 delivered:** settings persistence + theming foundation (`JsonStore<T>`, `ISettingsService`, Settings screen, dark/light/system theming). **PR 2 (planned):** presets, history, notifications. |
| **M6** | Ship v1 | **Velopack** installer + delta auto-update, yt-dlp/ffmpeg update flow, **v1 release**. |
| **M7** | *(future)* | Second tool module (video **standardize/merge**) — validates the modular seam. |

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
- Which yt-dlp/ffmpeg versions to pin for v1 — set during M2, record in §9.

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
| 2026-07-06 | 0.7 | M5 foundation (PR 1): generic `JsonStore<T>` (atomic write, corrupt-file quarantine) + `AppSettings`/`ISettingsService` (JSON at %AppData%\FFMedia\settings.json). `AddFFMediaCore` gains a `dataDirectory` param and registers `ISettingsService`. App gains a `ThemeService` (dark/light/system via WPF-UI), a Settings screen (default folder, max concurrency, theme) as a footer nav item, a title-bar theme toggle, and applies the persisted theme at startup. Settings wired into behavior: downloader output folder seeded from settings; `DownloadManager` concurrency cap read from settings. §6/§10/§12/§13/§17/§19 updated. |
| 2026-07-05 | 0.6 | M4 processing: `ProcessingOptions` (`TrimRange?`/`PreciseCut`/`EmbedSubtitles`/`SubtitleLanguage`/`EmbedMetadata`/`EmbedThumbnail`, default metadata+thumbnail on) added to `DownloadConfig.Processing`; pure `OptionSetBuilder.ApplyProcessing` emits `--download-sections` (+ `--force-keyframes-at-cuts` when precise), video-only `--write-subs --write-auto-subs --embed-subs --sub-langs`, and `--embed-metadata`/`--embed-thumbnail`; pure `TrimParsing` (HH:MM:SS/MM:SS/seconds → `TimeSpan`, range only when valid). ViewModel gained processing selections + live trim-hint validation; page gained a Processing section. §7.3/§8/§17 updated to match. |
| 2026-07-05 | 0.5 | M3 queue: `IDownloadManager`/`DownloadJob` (module-owned, not Core) run a bounded-concurrency (`SemaphoreSlim` cap 3) download queue with auto-start on add, per-job + cancel-all cancellation, and clear-completed; `RetryPolicy` retries transient network failures with exponential backoff (3 attempts/1s base) while permanent errors fail fast; `IPlaylistProbe`/`PlaylistMapping` expand a playlist/channel URL into one job per entry at add-time. ViewModel restructured to add-to-queue with a bound `Jobs` list; page shows per-job progress/cancel + cancel-all/clear-completed. §6/§7.2/§12/§19 updated to match the realized design; §19 concurrency + pause/resume resolved. |
| 2026-07-05 | 0.4 | M2 formats: full matrix via pure `OptionSetBuilder` — video (MP4/MKV/WebM) at a resolution cap + audio-only (MP3/WAV/M4A/Opus/FLAC) with bitrate; `DownloadConfig` model; ViewModel selections + page dropdowns; §7.3 flags finalized (mux over recode, `--audio-quality` via custom option). |
| 2026-07-05 | 0.3 | M1 vertical slice delivered: YouTube Downloader tool (probe + single-MP4 download w/ live progress + cancel) via YoutubeDLSharp; module + tests retargeted to `net9.0-windows` (UseWPF); `IMediaProbe`/`IDownloadService` seam with a unit-tested `DownloaderViewModel` (fakes) + trait-gated yt-dlp integration test; shell nav wiring joins `ITool` + `IToolPage` (WPF-UI navigation); added `Result<T>` and `IToolPage` to Core. |
| 2026-07-04 | 0.2 | M0 foundation delivered: solution skeleton, Core (`ITool`/`IToolRegistry`, `IBinaryProvider`, `AddFFMediaCore`), WPF-UI shell w/ Host+Serilog, fetch-binaries script, CI. `ITool.Icon` is now a string glyph (Core stays UI-agnostic); assertion library deferred (FluentAssertions v8 is paid); M0 uses plain xUnit `Assert`. WPF-UI resolved to 4.3.0. |
| 2026-07-04 | 0.1 | Initial SDD from brainstorming: stack (WPF+WPF-UI/.NET 9), modular shell architecture, downloader design, milestones M0–M7. |
