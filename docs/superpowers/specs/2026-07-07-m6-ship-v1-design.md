# M6 "Ship v1" — Design Spec

> **Milestone:** M6 (SDD §17) · **Date:** 2026-07-07 · **Status:** Approved, ready for planning
>
> Delivers the **release story**: a Velopack installer with delta **auto-update**, an
> in-app **yt-dlp self-update** flow, bundled-binary **version display**, and pinned,
> reproducible binaries — the machinery needed to ship **v1**. Defers to
> [`SDD.md`](../../../SDD.md) as the single source of truth; this spec elaborates M6 and the
> SDD is updated to match as each PR lands.

---

## 1. Scope & decisions (locked)

| Decision | Choice | Rationale |
|---|---|---|
| Update feed host | **GitHub Releases** (`ChamHC-dev/ff-media`) | Free, fits the existing repo + CI, public downloads, first-class Velopack support (`GithubSource` / `vpk upload github`). |
| App update UX | **Check on startup + notify**, plus a manual **"Check for updates"** in Settings | Background check on launch; a dismissible banner offers **Update & restart**. User stays in control; no silent installs. |
| Binary updates | **yt-dlp self-update in-app; ffmpeg via app release** | yt-dlp breaks often against YouTube and must update independently (`yt-dlp -U`); ffmpeg changes rarely and rides Velopack app releases. Matches SDD §9. |
| Code signing | **Ship unsigned for v1** | No cert cost/setup for a solo dev; accept the SmartScreen "unknown publisher" prompt. Build leaves a clear signing seam for later. |
| Delivery | **Two PRs** (packaging + app auto-update, then binary-update flow) | Each PR is coherent and reviewable per standing Rule 3; a single build+App+Core diff is hard to review. |
| v1 release | **Machinery + dry-run only** | Build & verify the pack/publish loop end-to-end against a test tag/local feed; tagging the real public **v1.0.0** stays the user's call. |
| Binary pinning | **Pin exact versions** in `fetch-binaries.ps1` + record in SDD §9, with hash checks | Reproducible release builds; deliberate bumps. Resolves the SDD §19 open question. |
| Update-logic architecture | **Core abstractions realized in App** (`IUpdateService`→`VelopackUpdateService`; `IBinaryUpdateService` over a Core `IProcessRunner`) | Matches the established seam pattern (`INotificationService`/`ISettingsService`); keeps ViewModels testable; keeps Velopack + process-spawning out of Core. |

**Non-goals for M6:** code signing/notarization, an MSIX/Store package, native Windows toast
(still deferred; may revisit once the app has a stable AppUserModelID from install),
auto-download-and-apply without a prompt, live self-updating of ffmpeg, an update-cadence
scheduler beyond "check on startup" + manual, delta-update *authoring* tuning beyond Velopack
defaults.

---

## 2. Layering (respects SDD §5 dependency rules)

Dependencies point **inward** to Core. Core references no UI framework and no packaging
framework; Velopack lives only in `FFMedia.App`.

### 2.1 `FFMedia.Core` (new, UI/packaging-agnostic, unit-tested)

```
FFMedia.Core/
├─ Updates/
│  ├─ AppUpdateInfo.cs        — record: TargetVersion (string), optional ReleaseNotes/size
│  └─ IUpdateService.cs       — CheckForUpdatesAsync() → AppUpdateInfo?;  DownloadAndApplyAndRestartAsync(IProgress<int>?, CT)
├─ Process/
│  ├─ ProcessResult.cs        — record: ExitCode, StdOut, StdErr
│  ├─ IProcessRunner.cs       — RunAsync(exePath, args, CT) → ProcessResult   (realizes SDD §6 seam)
│  └─ ProcessRunner.cs        — System.Diagnostics.Process impl (stream capture, honors CT)
└─ Binaries/
   ├─ IBinaryUpdateService.cs — GetVersionAsync(ExternalBinary) → string?;  UpdateYtDlpAsync(IProgress<string>?, CT) → Result
   ├─ BinaryUpdateService.cs  — over IProcessRunner + IBinaryProvider
   └─ BinaryVersionParsing.cs — pure: raw `--version` / `-version` output → version string
```

`IBinaryProvider` stays a **pure path resolver** (`GetPath`/`Exists`) — version reporting and
self-update are a **separate** `IBinaryUpdateService` (single responsibility, SDD §18), even
though SDD §6 originally sketched them on `IBinaryProvider`; the SDD §6 row is updated to
reflect this split.

### 2.2 `FFMedia.App` (Velopack + WPF/WPF-UI implementations)

```
FFMedia.App/
├─ Program.cs                       — explicit entry point: VelopackApp.Build().Run() BEFORE WPF
├─ Services/
│  └─ VelopackUpdateService.cs      — IUpdateService via Velopack UpdateManager + GithubSource
├─ ViewModels/  UpdateViewModel.cs   — startup check, banner state, Update&restart / manual check
└─ Views/       (update banner hosted in MainWindow; version rows added to SettingsPage)
```

### 2.3 The Velopack boundary (deliberate)

Velopack's `UpdateManager`/`UpdateInfo` types never cross into Core. `VelopackUpdateService`
maps a Velopack `UpdateInfo` to Core's `AppUpdateInfo` record on the way out, and drives
`DownloadUpdatesAsync` + `ApplyUpdatesAndRestart` on the way in. `UpdateViewModel` depends only
on Core's `IUpdateService`, so it is unit-testable with a fake.

---

## 3. PR 1 — Packaging + installer + app auto-update (`feat/m6-packaging-autoupdate`)

### 3.1 Velopack entry point

WPF auto-generates `Main` from `App.xaml` (`ApplicationDefinition`). Velopack requires
`VelopackApp.Build().Run()` to execute **first**, to service its install/update/uninstall
hooks. Resolution: add an explicit `Program.Main` (`[STAThread]`), set
`<StartupObject>FFMedia.App.Program</StartupObject>`, and change `App.xaml`'s build action from
`ApplicationDefinition` to `Page` so WPF stops emitting its own `Main`. `Program.Main` calls
`VelopackApp.Build().Run()`, then constructs and runs `App` exactly as today. All existing
startup logic in `App.OnStartup` (host, Serilog, theme, global exception handlers) is unchanged.

### 3.2 Release machinery

- `build/pack.ps1` — `dotnet publish` self-contained (`-r win-x64 --self-contained`, per SDD
  §15) into a staging dir that **includes `assets/binaries/*.exe`**, then `vpk pack`
  (`--packId FFMedia --packVersion <v> --mainExe FFMedia.App.exe`). Version comes from the App
  csproj `<Version>` (single source; passed to `vpk`).
- `.github/workflows/release.yml` — triggered on a `v*` tag: runs `fetch-binaries.ps1` (pinned),
  `pack.ps1`, then `vpk upload github --repoUrl https://github.com/ChamHC-dev/ff-media` to push
  the release + delta artifacts. **Unsigned**: no cert step; a commented signing seam
  (`--signParams`) documents where a cert drops in later.
- The existing CI build workflow is untouched (still builds/tests every push,
  `Category!=Integration`).

### 3.3 App update flow

- Core `IUpdateService`: `CheckForUpdatesAsync()` → `AppUpdateInfo?` (null = up to date);
  `DownloadAndApplyAndRestartAsync(IProgress<int>?, CT)`.
- App `VelopackUpdateService`: `new UpdateManager(new GithubSource("https://github.com/ChamHC-dev/ff-media", null, false))`;
  `CheckForUpdatesAsync` → map to `AppUpdateInfo`; download + `ApplyUpdatesAndRestart`. When not
  installed via Velopack (e.g. `dotnet run` in dev), `UpdateManager.IsInstalled` is false — the
  service returns "no update / not applicable" and never throws.
- **Startup check:** in `App.OnStartup` (after the window shows), if
  `Settings.CheckForUpdatesOnStartup` is true, `UpdateViewModel` runs `CheckForUpdatesAsync` on a
  background task (non-blocking, failures swallowed + logged — a dead feed must never block
  launch). If an update exists, the shell shows a **dismissible update banner**.
- **Update banner:** a WPF-UI `InfoBar` (or equivalent) hosted in `MainWindow`, bound to
  `UpdateViewModel` (`IsUpdateAvailable`, `TargetVersion`, `UpdateAndRestartCommand`, download
  progress, `DismissCommand`). This is a **dedicated banner**, not the action-less snackbar
  `INotificationService` — an update needs an actionable button. (Confirmed with user.)
- **Settings:** a **"Check for updates on startup"** toggle (new `AppSettings.CheckForUpdatesOnStartup`,
  default `true`) and a **"Check for updates now"** button showing current app version vs. result
  (up-to-date / vX.Y.Z available → same Update&restart path).

### 3.4 Settings migration

`AppSettings.Version` bumps **1 → 2** to add `CheckForUpdatesOnStartup` (default `true`).
`JsonStore<T>`/`SettingsService` already default missing fields on load, so an existing v1
`settings.json` reads cleanly with the new field defaulted; the `Version` field is stamped to 2
on next save. (No destructive migration needed.)

---

## 4. PR 2 — Binary update flow + version display (`feat/m6-binary-updates`)

### 4.1 Process seam + binary update service (Core)

- `IProcessRunner`/`ProcessRunner` — minimal `RunAsync(exePath, args, CT) → ProcessResult`
  (captures stdout/stderr, honors cancellation). Realizes the long-planned SDD §6 seam and makes
  binary version/update logic testable with a fake runner.
- `IBinaryUpdateService`/`BinaryUpdateService`:
  - `GetVersionAsync(ExternalBinary)` → runs `yt-dlp --version` / `ffmpeg -version`, parses via
    pure `BinaryVersionParsing`; returns `null` if the binary is missing or the call fails.
  - `UpdateYtDlpAsync(IProgress<string>?, CT)` → runs `yt-dlp -U` against the bundled exe,
    streams output, returns a `Result` (success message or friendly failure). ffmpeg has **no**
    update method (rides app releases).
  - *Considered & rejected:* routing yt-dlp update through the module's existing
    `IYoutubeDlFactory`/YoutubeDLSharp `RunUpdate()`. Binary version/update is an **app-level**
    (Settings) concern, not the Downloader tool's — keeping it in Core over a uniform
    `IProcessRunner` avoids coupling a shared concern into one tool module and handles ffmpeg
    (which YoutubeDLSharp doesn't wrap) the same way.

### 4.2 Settings — binary section

`SettingsPage`/`SettingsViewModel` gain a **Binaries** section:

- **yt-dlp** version (from `GetVersionAsync`) + **"Update yt-dlp"** button → `UpdateYtDlpAsync`
  with progress text and a success/failure result (snackbar via existing `INotificationService`).
- **ffmpeg** version — **display only** (a note that it updates with the app).
- Versions load lazily/async when the Settings page opens (never block the UI thread on a process
  spawn).

### 4.3 Pinned binaries

- `fetch-binaries.ps1` — replace `latest` with **pinned** yt-dlp + ffmpeg versions (parameterized
  at the top of the script), download from the version-pinned release URLs, and **verify SHA-256**
  against recorded hashes before use (fail hard on mismatch, per SDD §16 integrity checks).
- Record the pinned versions + hashes in **SDD §9** (resolves the §19 open question). `release.yml`
  uses the same pinned fetch so release artifacts are reproducible.

---

## 5. Testing strategy (SDD §14)

**Unit (Core, no UI/network, fast):**
- `BinaryVersionParsing`: representative `yt-dlp --version` / `ffmpeg -version` outputs → version
  string; junk/empty → null.
- `BinaryUpdateService`: fake `IProcessRunner` — `GetVersionAsync` parses; missing binary → null;
  `UpdateYtDlpAsync` maps exit code/output to `Result` (success + failure); cancellation
  propagates.
- `ProcessRunner`: a tiny real-process smoke (e.g. run `cmd /c echo`) asserting stdout capture +
  exit code + cancellation — trait-gated if it proves flaky in CI.

**ViewModel (headless, fake `IUpdateService`/`IBinaryUpdateService`):**
- `UpdateViewModel`: no update → banner hidden; update present → banner shown with target version;
  manual check reflects up-to-date vs available; a throwing/failed check leaves the app usable
  (no crash) and hides the banner.
- `SettingsViewModel`: `CheckForUpdatesOnStartup` toggle persists; binary versions populate;
  "Update yt-dlp" surfaces success/failure.

**Not unit-tested (verified by dry-run, per SDD §14 "UI/packaging is thin/manual"):**
- Velopack pack/publish + the actual install→update→restart loop — verified manually against a
  **test tag / local file feed**: install vN, publish vN+1, confirm the running app detects it,
  shows the banner, downloads the delta, and restarts onto vN+1.
- yt-dlp self-update writing the bundled exe on a real machine.

---

## 6. Open items to settle during planning

1. **Version single-source:** App `<Version>` drives both the assembly version and `vpk
   --packVersion`. Confirm the exact property (`<Version>`) and that `Program`/Settings read it via
   `Assembly.GetEntryAssembly().GetName().Version` (or informational version) for the "current
   version" display.
2. **Velopack package name/id:** confirm the current Velopack NuGet package + `vpk` tool name and
   pin them (`Velopack` package + `vpk` global tool) during planning; note the version in SDD §3.
3. **`App.xaml` build-action switch:** verify the `ApplicationDefinition`→`Page` +
   `<StartupObject>` approach builds cleanly with WPF-UI's resource dictionaries loaded from
   `App.xaml` (they must still be merged; `App` ctor already does host setup).
4. **Delta baseline for dry-run:** decide the smallest realistic two-version sequence to exercise
   deltas (e.g. 0.9.0 → 0.9.1) without publishing publicly — a local `--outputDir` feed is enough.
5. **yt-dlp `-U` in an installed layout:** confirm `yt-dlp -U` can write the bundled exe under the
   Velopack install dir (`%LocalAppData%\FFMedia\current\...`) at runtime; if the location is
   read-only in practice, fall back to re-downloading the pinned-channel latest into a writable
   binaries dir. Resolve during PR 2.

---

## 7. PR breakdown & SDD sync

### PR 1 — `feat/m6-packaging-autoupdate`
`Program.cs` + Velopack entry wiring; `build/pack.ps1`; `.github/workflows/release.yml`
(dry-run/tag-gated, unsigned); Core `IUpdateService`/`AppUpdateInfo`; App
`VelopackUpdateService`; `UpdateViewModel` + shell update banner; startup check;
`AppSettings.CheckForUpdatesOnStartup` (Version→2) + Settings toggle + "Check for updates now".
**SDD → v0.9** (§3 Velopack version pinned, §6 `IUpdateService`, §9 app-update flow, §13 update
UI, §15 pack/publish workflow, §17 M6 partial).

### PR 2 — `feat/m6-binary-updates`
Core `IProcessRunner`/`ProcessRunner`; `IBinaryUpdateService`/`BinaryUpdateService` +
`BinaryVersionParsing`; Settings Binaries section (versions + "Update yt-dlp"); pinned
`fetch-binaries.ps1` with SHA-256 checks; pinned versions/hashes recorded in SDD §9.
**SDD → v1.0** (§6 `IProcessRunner`/`IBinaryUpdateService` realized, §9 binary versions pinned +
yt-dlp self-update, §13 binary version display, §17 **M6 complete**, §19 pinning resolved).

---

## 8. Definition of done

- Both PRs merged (user-reviewed); build clean; all unit + ViewModel tests pass; integration
  tests unaffected.
- `build/pack.ps1` produces a Velopack installer locally; the install→publish→auto-update→restart
  loop is verified end-to-end against a **test/local feed** (dry-run), with evidence recorded in
  the Progress Log.
- yt-dlp self-update works from the Settings screen; yt-dlp + ffmpeg versions display.
- `settings.json` migrates v1→v2 cleanly (new toggle defaulted, no data loss).
- `fetch-binaries.ps1` pins exact versions with passing hash checks; SDD §9 records them.
- SDD synced to **v1.0** with **M6 marked delivered** and Changelog entries added (one per PR).
- CLAUDE.md Progress Log entries appended (one per PR).
- The real public **v1.0.0** tag/release is **left to the user** (machinery + dry-run only).
```
