# FFMedia — Software Design Document (SDD)

> **Status:** Living document · **Version:** 0.2 · **Last updated:** 2026-07-04
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
│  ITool · IBinaryProvider · IJobQueue/DownloadManager ·   │
│  ISettingsService · IHistoryService ·                    │
│  INotificationService · IProcessRunner                   │
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
| `IDownloadManager` / `IJobQueue` | Enqueue jobs, enforce bounded concurrency, expose observable job collection + per-job state. |
| `ISettingsService` | Load/save app settings (JSON in `%AppData%\FFMedia`). |
| `IPresetService` | CRUD saved download presets. |
| `IHistoryService` | Append/query completed-download history. |
| `INotificationService` | In-app snackbar/toast + optional Windows toast. |
| `IErrorMapper` | Map raw yt-dlp/ffmpeg stderr to friendly, actionable messages. |

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
6. **Post-process** — yt-dlp performs recode / audio-extract / embed. Standalone
   trim (if the user asked for a clip without re-encode) uses `FFMedia.Media`.
7. **Complete** — notify, write to history, expose "Open folder" / "Open file".

### 7.2 Job state machine

```
Queued ─▶ Fetching ─▶ Downloading ─▶ Processing ─▶ Completed
   │          │            │              │
   └──────────┴────────────┴──────────────┴────▶ Canceled
                            │
                            └──────────────────▶ Failed  (+ retry on transient network)
```

- **Failure isolation:** a failed/canceled job never stalls the queue.
- **Retry policy:** transient network errors retried with backoff (configurable
  max attempts); non-transient errors (private/removed/geo-blocked) fail fast.

### 7.3 Output format matrix

The `OptionSet` builder is a **pure function** `DownloadConfig → yt-dlp args`
(heavily unit-tested). Representative mappings:

| User choice | yt-dlp behavior (conceptual) |
|---|---|
| MP4 (best ≤1080p) | `-f "bv*[height<=1080][ext=mp4]+ba[ext=m4a]/b[ext=mp4]" --merge-output-format mp4` |
| MKV (best) | `-f "bv*+ba/b" --merge-output-format mkv` |
| Audio → MP3 | `-x --audio-format mp3 --audio-quality 0` |
| Audio → WAV | `-x --audio-format wav` |
| Audio → FLAC/M4A/Opus | `-x --audio-format <fmt>` |
| Embed thumbnail | `--embed-thumbnail` |
| Embed metadata | `--embed-metadata` |
| Subtitles | `--write-subs --embed-subs --sub-langs <langs>` |
| Trim/clip | `--download-sections "*START-END"` (or ffmpeg re-encode via FFMedia.Media) |
| Output template | `-o "<folder>/%(title)s.%(ext)s"` |

> Exact flags are finalized against the bundled yt-dlp version during M2 and
> recorded here.

---

## 8. Media Processing (`FFMedia.Media`)

Thin, testable wrappers over **FFMpegCore** for operations FFMedia performs
directly (as opposed to delegating to yt-dlp):

- Frame-accurate **trim/clip** (with or without re-encode).
- Probe media info (duration, streams) when needed independent of yt-dlp.
- **Foundation for future tools** (standardize resolution/FPS/format, concat/merge).

`FFMedia.Media` locates `ffmpeg.exe` through `IBinaryProvider` (no PATH assumption).

---

## 9. Binary Management

- **Bundling:** `yt-dlp.exe` and `ffmpeg.exe` ship in the installer under
  `assets/binaries/`. They are **git-ignored**; a `build/fetch-binaries` script
  downloads pinned versions for local dev and CI.
- **Resolution:** `IBinaryProvider` resolves the app-relative binary path at
  runtime; never relies on the system PATH.
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

- `IDownloadManager` uses a bounded `System.Threading.Channels.Channel` +
  `SemaphoreSlim` to cap simultaneous downloads (default configurable, e.g. 3).
- Each job owns a `CancellationTokenSource`; "Cancel all" cancels the linked
  parent token.
- UI updates marshal to the dispatcher; long work runs off the UI thread.
- `IProgress<T>` provides thread-safe progress reporting into ViewModels.

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
| **M1** | Vertical slice | Paste URL → probe → download single **MP4** with **live progress + cancel**. End-to-end through all layers. |
| **M2** | Formats | Full format matrix: video containers + audio-only (**wav/mp3**/m4a/opus/flac) + quality/resolution. `OptionSet` builder fully tested. |
| **M3** | Queue | Download **queue**, bounded **concurrency**, **playlist/channel** support. |
| **M4** | Processing | **Trim/clip**, **subtitles**, **metadata + thumbnail** embedding. |
| **M5** | Experience | **Settings**, **presets**, **history**, **notifications**, dark/light **theming**. |
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

- Final default concurrency value (start at 3, tune during M3).
- History storage: stay JSON vs. move to SQLite — decide when history UX lands (M5).
- Pause/resume of in-flight downloads: stretch goal, evaluate in M3.
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
| 2026-07-04 | 0.2 | M0 foundation delivered: solution skeleton, Core (`ITool`/`IToolRegistry`, `IBinaryProvider`, `AddFFMediaCore`), WPF-UI shell w/ Host+Serilog, fetch-binaries script, CI. `ITool.Icon` is now a string glyph (Core stays UI-agnostic); assertion library deferred (FluentAssertions v8 is paid); M0 uses plain xUnit `Assert`. WPF-UI resolved to 4.3.0. |
| 2026-07-04 | 0.1 | Initial SDD from brainstorming: stack (WPF+WPF-UI/.NET 9), modular shell architecture, downloader design, milestones M0–M7. |
