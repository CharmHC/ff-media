# M5 "Experience" — Design Spec

> **Milestone:** M5 (SDD §17) · **Date:** 2026-07-06 · **Status:** Approved, ready for planning
>
> Delivers persistent **settings**, download **presets**, **history**, in-app
> **notifications**, and dark/light **theming**. Defers to [`SDD.md`](../../../SDD.md)
> as the single source of truth; this spec elaborates M5 and the SDD is updated to match
> as each PR lands.

---

## 1. Scope & decisions (locked)

| Decision | Choice | Rationale |
|---|---|---|
| Delivery | **Two PRs** (foundation, then presets/history/notifications) | Each PR is coherent and reviewable per standing Rule 3; a single 5-subsystem diff is hard to review. |
| History storage | **JSON now** (`history.json` via `JsonStore<T>`) | Simplest, consistent with settings/presets, zero new deps. `version` field keeps a future SQLite migration clean. Resolves SDD §19. |
| Notifications reach | **In-app only** (WPF-UI Snackbar) | No OS integration needed pre-install; native Windows toast needs an AppUserModelID that belongs with Velopack packaging (M6). Documented deferral. |
| Completion hook | **Approach A** — `DownloadManager` invokes `IHistoryService` + `INotificationService` at the terminal chokepoint | Reliable regardless of UI lifetime; testable with fakes. (Alternative B — an event App subscribes to — rejected for indirection.) |
| Presets UX | **Inline on the Downloader page** (dropdown + save/delete), no separate screen | SDD §13 never asked for a presets screen; smaller surface. |
| Concurrency setting | Applied **at `DownloadManager` construction** (next launch); live re-tuning deferred | Avoids fiddly runtime `SemaphoreSlim` resizing; UI notes "applies on restart". |

**Non-goals for M5:** native Windows toast, live concurrency re-tuning, per-preset output
folder, SQLite history, yt-dlp/ffmpeg update preferences UI (update *flow* is M6 — settings
may reserve the field but the flow is not built here).

---

## 2. Layering (respects SDD §5 dependency rules)

Dependencies point **inward** to Core. Core references no UI framework; the WPF/WPF-UI
implementations live in `FFMedia.App`.

### 2.1 `FFMedia.Core` (new, UI-agnostic, unit-tested)

```
FFMedia.Core/
├─ Persistence/
│  └─ JsonStore<T>.cs           — atomic JSON load/save under %AppData%\FFMedia
├─ Settings/
│  ├─ AppSettings.cs            — record: DefaultOutputFolder, MaxConcurrency, Theme, Version
│  ├─ AppTheme.cs               — enum: System | Light | Dark
│  ├─ ISettingsService.cs       — Current snapshot + Save + Changed event
│  └─ SettingsService.cs
├─ Presets/
│  ├─ Preset.cs                 — Name + opaque serialized payload (Core stays config-agnostic)
│  ├─ IPresetService.cs         — List / Save / Delete
│  └─ PresetService.cs
├─ History/
│  ├─ HistoryEntry.cs           — Title, Url, OutputPath, Format, Timestamp, Status
│  ├─ IHistoryService.cs        — Append / Query / Clear
│  └─ HistoryService.cs
└─ Notifications/
   ├─ Notification.cs           — Message, Title?, Severity (Info|Success|Warning|Error)
   └─ INotificationService.cs   — Notify(Notification)   [interface only, no WPF]
```

`JsonStore<T>` is the single tested persistence primitive backing settings, presets, and
history. It writes to a temp file then atomically moves it into place, tolerates a missing
file (returns a caller-supplied default), and recovers from a corrupt file (logs + returns
default rather than throwing). All stores live under
`%AppData%\FFMedia\` (SDD §10): `settings.json`, `presets.json`, `history.json`.

### 2.2 `FFMedia.App` (WPF/WPF-UI implementations + shell)

```
FFMedia.App/
├─ Services/
│  ├─ SnackbarNotificationService.cs  — INotificationService via WPF-UI SnackbarPresenter
│  └─ ThemeService.cs                  — wraps WPF-UI ApplicationThemeManager
├─ Views/  SettingsPage.xaml(.cs), HistoryPage.xaml(.cs)
└─ ViewModels/  SettingsViewModel.cs, HistoryViewModel.cs
```

### 2.3 The preset-payload boundary (deliberate)

A preset's payload is a **`DownloadConfig`, which lives in the tool module** — but the module
depends on Core, never the reverse, so Core cannot reference `DownloadConfig`. Resolution:

- Core's `Preset` stores an **opaque, already-serialized payload** (`string` JSON) plus its
  `Name`. `IPresetService` is agnostic to what the payload means.
- The **YouTube Downloader module owns (de)serialization** of `DownloadConfig` ↔ payload
  string (a thin `PresetMapping` helper alongside `OptionSetBuilder`), and calls
  `IPresetService` with the serialized form.

This keeps Core ignorant of module types while giving the module full control of its config
schema (including forward-compat via `DownloadConfig`'s own shape). If a second tool later
needs presets, the same opaque-payload service serves it unchanged.

---

## 3. Shell / navigation for app-level pages

Settings and History are **not `ITool`s** and must not enter `IToolRegistry`/`IToolPage`.

- The shell's `NavigationView` gains a **`FooterMenuItemsSource`** carrying **Settings** and
  **History** items (Segoe Fluent glyphs), built by `MainWindowViewModel` from App-registered
  page descriptors. The `ITool` contract and existing tool nav are untouched.
- The title bar gains a **theme toggle** control (SDD §13) bound to `ThemeService`.
- App-level pages are registered in `App.xaml.cs` composition root and resolved through the
  same DI-backed `INavigationViewPageProvider` already wired for tool pages.

---

## 4. Settings → behavior seams

`AppSettings` (record, with `Version` for migration):

| Field | Type | Wired into |
|---|---|---|
| `DefaultOutputFolder` | `string` | `DownloaderViewModel` seeds `OutputFolder` from settings instead of the hardcoded `MyVideos\FFMedia`. |
| `MaxConcurrency` | `int` (default 3) | `DownloadManager` cap read from settings at construction (replaces the `= 3` constant; resolves SDD §19). Applied on next launch. |
| `Theme` | `AppTheme` | `ThemeService` applies at startup and on toggle; persisted on change. |

`ISettingsService` exposes a `Current` snapshot, a `Save(AppSettings)` that persists and
raises `Changed`, and loads once at startup. The `SettingsViewModel` edits a working copy and
commits via `Save`.

---

## 5. Presets (inline on Downloader page)

- Downloader page gains: a **preset dropdown**, **"Save current as preset…"** (prompts for a
  name), and **"Delete preset"**.
- Applying a preset **seeds** the format/quality/processing selections in `DownloaderViewModel`
  (it deserializes the payload into the current `SelectedKind/Container/Resolution/AudioFormat/
  Bitrate` + processing selections). Output folder is **not** part of a preset (it's a global
  setting).
- `DownloaderViewModel` gains an `IPresetService` dependency + `PresetMapping` for
  serialize/deserialize; commands: `SaveAsPresetCommand`, `ApplyPresetCommand`,
  `DeletePresetCommand`; an observable `Presets` collection.

---

## 6. History + notifications — the completion hook (Approach A)

`DownloadManager.RunAndTrackAsync`'s `finally` block is the single point where a job is known
terminal, exactly once, regardless of whether the Downloader page is still alive.

- `DownloadManager` gains **optional** `IHistoryService?` + `INotificationService?`
  constructor deps (nullable so existing tests and the pure-engine story degrade gracefully).
- On terminal transition:
  - `Completed` → append a `HistoryEntry` (title/url/output path/format/timestamp/status) **and**
    raise a success `Notification`.
  - `Failed` → raise an error `Notification` (no history row, or a `Failed`-status row — see
    open item 8.1). `Canceled` → no notification, no history.
- The SDD §7.2 "pure download engine" note gets a small honest amendment: the manager now
  performs terminal-transition side effects through Core abstractions (still no direct UI
  dependency).

`SnackbarNotificationService` (App) renders notifications on a shell-owned
`SnackbarPresenter`. Because notifications arrive off the UI thread (manager worker), the
service marshals to the dispatcher before showing.

### 6.1 History page

`HistoryPage` + `HistoryViewModel`:

- Searchable/filterable list (title, url, format, timestamp, status).
- Per-row actions: **Open file**, **Open folder** (`explorer /select,`), **Re-download**
  (seeds the Downloader page's URL + config from the entry).
- **Clear history** action.

---

## 7. Testing strategy (SDD §14)

**Unit (Core, no UI, fast):**
- `JsonStore<T>`: round-trip; missing file → default; corrupt file → default + logged;
  atomic write (no partial file on failure).
- `SettingsService` / `PresetService` / `HistoryService`: CRUD, persistence, `Changed` event.
- `DownloadManager` completion hook: fake `IHistoryService`/`INotificationService` assert a
  `Completed` job appends history + notifies success; `Failed` notifies error; `Canceled` does
  neither. Existing manager tests (nullable deps) keep passing.
- `PresetMapping`: `DownloadConfig` ↔ payload round-trip (module test).

**ViewModel (headless):** `SettingsViewModel` commit/cancel; `HistoryViewModel` filter +
re-download seeding; `DownloaderViewModel` preset save/apply/delete.

**Manual smoke:** theme toggle persists across restart; snackbar on completion/failure;
preset apply changes selections; history re-download; settings folder/concurrency take effect.

---

## 8. Open items to settle during planning

1. **Failed-job history:** record `Failed` downloads as history rows (status `Failed`) or only
   `Completed`? Leaning **only `Completed`** for a clean "your downloads" list; failures are
   surfaced live + logged. Confirm in plan.
2. **Preset payload versioning:** `PresetMapping` should tolerate an older payload shape
   gracefully (unknown/missing fields fall back to defaults) rather than throw.
3. **Theme = System:** resolve `System` against the OS setting at startup and (nice-to-have)
   react to OS theme changes while running; the latter may be deferred if WPF-UI makes it
   costly.

---

## 9. PR breakdown & SDD sync

### PR 1 — `feat/m5-foundation`
`JsonStore<T>`, `AppSettings`/`AppTheme`/`ISettingsService`+impl, `ThemeService` + title-bar
toggle, `SettingsPage`/`SettingsViewModel`, footer-nav plumbing, and settings wired into
`DownloaderViewModel` output folder + `DownloadManager` concurrency. Core services registered
in `AddFFMediaCore`; App services registered in composition root. **SDD → v0.7** (§6 services
realized, §10 files, §12 concurrency-from-settings, §13 settings/theming, §17 M5 partial, §19
concurrency resolved).

### PR 2 — `feat/m5-presets-history`
`Preset`/`IPresetService`+impl + module `PresetMapping` + inline preset UX;
`HistoryEntry`/`IHistoryService`+impl + `HistoryPage`/`HistoryViewModel`;
`Notification`/`INotificationService` + `SnackbarNotificationService`; the `DownloadManager`
completion hook. **SDD → v0.8** (§6 remaining services, §7.2 completion-hook amendment, §13
history screen, §17 M5 complete).

---

## 10. Definition of done

- Both PRs merged (user-reviewed); build clean; all unit + ViewModel tests pass; integration
  tests unaffected.
- Settings, presets, history persist across restarts under `%AppData%\FFMedia`.
- Theme toggle works and persists; snackbars fire on job completion/failure.
- SDD synced to v0.8 with M5 marked delivered and its Changelog entries added.
- CLAUDE.md Progress Log entries appended (one per PR).
