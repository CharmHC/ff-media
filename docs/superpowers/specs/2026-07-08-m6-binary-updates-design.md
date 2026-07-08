# M6 PR 2 — yt-dlp Self-Update, Binary Versions, Pinned Binaries & App Logo

> **Status:** Approved design · **Date:** 2026-07-08 · **Milestone:** M6 (Ship v1), PR 2
> **Branch:** `feat/m6-binary-updates`

This spec defers to [`SDD.md`](../../../SDD.md) as the single source of truth. It realizes
the M6 PR 2 scope left pending after PR 1 (Velopack packaging + app auto-update): the
**yt-dlp/ffmpeg binary update flow**, plus the app-logo wiring that was never done. On
merge, the SDD moves to **v0.10** and the relevant sections are updated to match.

---

## 1. Goals

1. **`IProcessRunner`** — a Core-level, testable seam for launching child processes
   (SDD §6, specced since M0 but never built). Streams stdout/stderr, honors
   `CancellationToken`.
2. **`IBinaryUpdateService`** — report bundled binary versions and perform an in-app
   **yt-dlp self-update** (`yt-dlp -U`). ffmpeg has no self-update (rides app releases).
3. **Settings UI** — show yt-dlp + ffmpeg versions, an **"Update yt-dlp"** action, and a
   **"Check yt-dlp for updates on startup"** toggle that notifies (does not silently update).
4. **Pinned `fetch-binaries.ps1`** — pin exact versions for both binaries and verify
   SHA-256 after download (resolves the SDD §19 open question; satisfies §16).
5. **App logo** — move the repo-root `logo.png` to a proper home and use it as the app
   icon (exe/window/taskbar/installer) and in-app branding.

### Non-goals (this PR)

- Silent/automatic yt-dlp updates (startup path only *notifies*; applying is manual).
- An ffmpeg self-update path (deliberately absent — ffmpeg rides Velopack app releases).
- A scheduled/periodic update cadence beyond the single check-on-startup toggle.
- Code-signing the installer or the `.ico` (unsigned for v1, unchanged from PR 1).

---

## 2. `IProcessRunner` (FFMedia.Core)

The seam that makes binary orchestration testable without real exes (SDD §6).

```csharp
namespace FFMedia.Core.Processes;

public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with the given arguments, capturing
    /// stdout/stderr. Optionally streams stdout lines as they arrive. Honors cancellation
    /// by killing the process (entire tree). Never throws for a non-zero exit — the exit
    /// code is returned in the result. Throws only for launch failure (e.g. file missing).</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<string>? onOutputLine = null,
        CancellationToken ct = default);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
```

**Concrete `ProcessRunner`:**
- `ProcessStartInfo` with `RedirectStandardOutput/Error`, `UseShellExecute = false`,
  `CreateNoWindow = true`. Arguments added via `ArgumentList` (no manual quoting).
- Async output pumping (`OutputDataReceived`/`ErrorDataReceived` + `WaitForExitAsync(ct)`),
  accumulating both streams; each stdout line also forwarded to `onOutputLine`.
- On cancellation: `Kill(entireProcessTree: true)`, then rethrow `OperationCanceledException`.
- Registered in `AddFFMediaCore` as a singleton (stateless).

**Tests (`ProcessRunnerTests`, non-integration, Windows-hosted):**
- `cmd /c exit 3` → `ExitCode == 3`.
- `cmd /c echo hello` → stdout contains `hello`; `onOutputLine` receives the line.
- A cancelled long-running `ping` (or `timeout`) throws `OperationCanceledException`.
These spawn only OS built-ins — fast, hermetic, no network, so they stay out of the
`Integration` category.

---

## 3. `IBinaryUpdateService` (FFMedia.Core)

```csharp
namespace FFMedia.Core.Binaries;

public interface IBinaryUpdateService
{
    /// <summary>Installed version string of the bundled binary, or null if it can't be read.</summary>
    Task<string?> GetInstalledVersionAsync(ExternalBinary binary, CancellationToken ct = default);

    /// <summary>Latest published yt-dlp version if newer than installed; null if up to date
    /// or the check fails. Used by the startup "notify" path.</summary>
    Task<string?> GetLatestYtDlpVersionAsync(CancellationToken ct = default);

    /// <summary>Runs the yt-dlp self-update (<c>yt-dlp -U</c>) and reports the outcome.</summary>
    Task<BinaryUpdateResult> UpdateYtDlpAsync(CancellationToken ct = default);
}

public sealed record BinaryUpdateResult(bool Updated, string? FromVersion, string? ToVersion, string Message);
```

**Concrete `BinaryUpdateService` (Core):** depends on `IProcessRunner`, `IBinaryProvider`,
an injected `HttpClient`, and `ILogger`.

- **Installed versions** — `IProcessRunner.RunAsync(ytDlpPath, ["--version"])` returns the
  bare version (trim). `IProcessRunner.RunAsync(ffmpegPath, ["-version"])` → first line
  parsed by a **pure, tested** helper `FfmpegVersionParsing.Parse` (`"ffmpeg version 7.1-…"`
  → `"7.1"`). Any launch failure / non-zero exit → `null` (caller shows "unknown").
- **Latest yt-dlp** — `GET https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest`
  with a `User-Agent` header; read `tag_name`. Returns it only when it differs from the
  installed version; returns `null` on up-to-date or any error (best-effort). yt-dlp has
  no offline check-only mode, so the remote tag lookup is the pragmatic check.
- **Update** — capture `GetInstalledVersionAsync` before, run
  `IProcessRunner.RunAsync(ytDlpPath, ["-U"], onOutputLine)`, capture the version after,
  and build a `BinaryUpdateResult`. Exit 0 with a changed version → `Updated = true`.
- Registered in `AddFFMediaCore`. A named/typed `HttpClient` is registered by the App
  composition root (Core stays free of `IHttpClientFactory` wiring; it just takes an
  `HttpClient`).

> **Pinning caveat (recorded in SDD §9):** a Velopack **app** update re-bundles the
> *pinned* yt-dlp, reverting a prior self-update. This is expected — the user can re-run
> "Update yt-dlp". Pinning governs reproducible builds; the self-update deliberately moves
> forward between app releases.

**Tests (`BinaryUpdateServiceTests` + `FfmpegVersionParsingTests`, non-integration):**
- Fake `IProcessRunner` returns canned `--version` / `-version` output → assert parsed
  versions.
- Fake `HttpMessageHandler` returns a canned `releases/latest` JSON → assert
  `GetLatestYtDlpVersionAsync` returns the tag when newer, `null` when equal.
- Fake runner simulates `-U` (version bumps) → assert `BinaryUpdateResult.Updated` +
  from/to. `FfmpegVersionParsing` covered with several real first-line samples + junk.

---

## 4. Settings UI: versions, update, startup check

**Singleton `BinaryUpdateViewModel` (App layer)** — mirrors the existing singleton
`UpdateViewModel`, so it is shared by both the Settings page and the startup check:

- Observable: `YtDlpVersion`, `FfmpegVersion`, `IsYtDlpUpdateAvailable`, `StatusMessage`, `IsBusy`.
- Commands:
  - `RefreshVersionsCommand` — loads both installed versions (called on Settings-page load).
  - `UpdateYtDlpCommand` — runs `UpdateYtDlpAsync`, then refreshes the displayed version and
    sets a status message; clears `IsYtDlpUpdateAvailable`.
  - `CheckOnStartupAsync()` — background, never throws: if `GetLatestYtDlpVersionAsync`
    returns non-null, sets `IsYtDlpUpdateAvailable` and raises an **in-app notification**
    (`INotificationService`, Info: "A yt-dlp update is available — update it in Settings").

**`AppSettings`** gains `CheckYtDlpForUpdatesOnStartup` (bool, default `true`) → **schema
v3**. Covered by a unit test mirroring `AppSettingsUpdateFlagTests`.

**`SettingsPage.xaml`** gains a "Binaries" section below "Updates":
- yt-dlp: version text + `[Update yt-dlp]` button (bound to `Binaries.UpdateYtDlpCommand`,
  disabled while `IsBusy`).
- ffmpeg: version text (read-only; note "updates with the app").
- `[x] Check yt-dlp for updates on startup` toggle (bound to `CheckYtDlpForUpdatesOnStartup`,
  persisted by the existing `Save`).
- A status line bound to `Binaries.StatusMessage`.

`SettingsViewModel` takes the singleton `BinaryUpdateViewModel` (exposed as a `Binaries`
property, exactly like `Updates`) and includes the new toggle in its `Save`.

**`App.OnStartup`** — after the existing app-update check, if
`settings.Current.CheckYtDlpForUpdatesOnStartup`, fire-and-forget
`binaries.CheckOnStartupAsync()` (swallows/logs its own errors; never blocks or crashes
launch — same shape as the app-update startup check).

App-layer VMs are verified by **build + manual run** per the M5/M6 precedent (the Tests
project doesn't reference the WinExe; UI is thin per SDD §14). Only `AppSettings` v3 gets a
unit test.

---

## 5. Pinned `fetch-binaries.ps1` with hash verification

Replaces the current `/latest/` fetches with **tag-pinned** URLs + SHA-256 verification
(resolves SDD §19; satisfies §16). Shape:

```powershell
# --- Pinned versions (record chosen values + hashes here) ---
$YtDlpVersion   = '<pinned>'          # e.g. 2025.06.30
$YtDlpSha256    = '<hex>'             # cross-checked vs the release's official SHA2-256SUMS
$FfmpegTag      = '<pinned>'          # BtbN release tag, e.g. autobuild-YYYY-MM-DD-...
$FfmpegZipSha256 = '<hex>'            # computed once from the pinned zip (BtbN publishes none)

function Assert-Hash($path, $expected, $name) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $path).Hash
    if ($actual -ne $expected.ToUpperInvariant()) {
        throw "$name hash mismatch. expected=$expected actual=$actual"
    }
}
```

- yt-dlp: download `.../download/$YtDlpVersion/yt-dlp.exe`, `Assert-Hash` before use.
- ffmpeg: download `.../download/$FfmpegTag/ffmpeg-...-win64-gpl.zip`, `Assert-Hash` the
  **zip**, then extract `ffmpeg.exe`.
- On any mismatch the script **throws** and the (git-ignored) exes are not left in a
  half-verified state.
- The actual pinned versions + hashes are chosen during implementation by fetching the
  real files (yt-dlp cross-checked against its `SHA2-256SUMS`; ffmpeg zip hashed once) and
  are recorded in the script **and** in SDD §9. Proof of correctness = running the pinned
  script clean end-to-end.

---

## 6. App logo — everywhere

- **Move** repo-root `logo.png` → **`assets/branding/logo.png`** (canonical brand asset;
  sits alongside the git-ignored `assets/binaries/`, but branding is committed).
- **Generate** `assets/branding/app.ico` (multi-resolution 16/32/48/64/128/256,
  PNG-compressed entries) via a committed helper **`build/make-icon.ps1`** (resizes with
  `System.Drawing`, writes a real multi-image ICO container). The `.ico` is **committed**
  so the csproj and `vpk` don't depend on running the helper.
- **Wire up:**
  - `FFMedia.App.csproj` — `<ApplicationIcon>..\..\assets\branding\app.ico</ApplicationIcon>`
    (exe icon in Explorer/taskbar); link `logo.png` as a WPF `<Resource>` (Link
    `Assets\logo.png`) for in-app pack-URI use.
  - `MainWindow.xaml` — `Icon="pack://application:,,,/Assets/logo.png"` on the
    `FluentWindow` (title-bar + taskbar) and a small logo `Image` in the `NavigationView`
    pane header.
  - `WelcomePage.xaml` — replace the generic `Play24` `SymbolIcon` with the logo `Image`.
  - `build/pack.ps1` — add `--icon "$root/assets/branding/app.ico"` to `vpk pack`
    (installer + Start-menu shortcut icon).

If a linked-resource pack URI proves fiddly at build time, the fallback is to place a real
copy under `src/FFMedia.App/Assets/logo.png` (a normal project `Resource`); the canonical
source stays `assets/branding/logo.png`.

---

## 7. Testing & delivery

- **Build:** solution builds **Release 0 warnings / 0 errors**.
- **Unit (`Category!=Integration`):** all existing **152** pass, plus new
  `ProcessRunnerTests`, `BinaryUpdateServiceTests`, `FfmpegVersionParsingTests`, and the
  `AppSettings` v3 flag test.
- **Pinned fetch:** run `build/fetch-binaries.ps1` for real to prove the pinned
  versions + hashes verify clean.
- **Manual (headed):** left to the user per precedent — Settings shows versions, "Update
  yt-dlp" runs, startup toggle notifies; logo appears on exe/window/nav/welcome.
- **Docs:** SDD → **v0.10** (§6 `IProcessRunner`/`IBinaryUpdateService` realized, §9
  pinned versions + self-update + revert caveat, §13 Settings binaries section, §16
  integrity note, §17 M6 PR 2 row, §19 pin question resolved, Changelog). CLAUDE.md
  progress-log entry (newest first).
- **Delivery:** commit on `feat/m6-binary-updates`, push, open a PR for review. **Not**
  merged by me (Standing Rule 3). A whole-branch review runs before the PR is opened.

---

## 8. Design decisions (locked)

| Decision | Choice | Rationale |
|---|---|---|
| Process seam location | `IProcessRunner` in `FFMedia.Core` | SDD §6; keeps orchestration testable with a fake runner. |
| yt-dlp update mechanism | `yt-dlp -U` (self-replace) | Built-in, matches SDD §9; per-user install dir is writable (no UAC). |
| ffmpeg update | none (rides app releases) | SDD §9; avoids a second update channel for v1. |
| yt-dlp "check only" | GitHub `releases/latest` tag | yt-dlp has no offline check-only mode. |
| Startup behavior | notify, never auto-apply | User keeps control; mirrors the app-update toggle. |
| Hash pinning | pin + SHA-256 verify **both** | Reproducible builds; satisfies SDD §16, resolves §19. |
| ffmpeg hash source | computed once from pinned BtbN zip | BtbN publishes no official sums. |
| Logo scope | exe/window/taskbar/installer **and** in-app | Full brand presence (user choice). |
| `.ico` | generated once, committed | csproj/`vpk` don't depend on running the helper. |
