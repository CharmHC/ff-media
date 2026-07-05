# M3 — Queue: Design

> **Status:** Approved · **Date:** 2026-07-05 · **Milestone:** M3 (see [SDD.md](../../../SDD.md) §17)
>
> Adds a **bounded-concurrency download queue** with per-job progress/cancel,
> **transient-failure retry**, and **playlist/channel expansion** to the YouTube
> Downloader. Builds on M2's `DownloadConfig`/`OptionSetBuilder`/`IDownloadService`.
> Design defers to [SDD.md](../../../SDD.md); §6 placement is updated by this work.

---

## 1. Goal & Scope

**Goal (SDD §17, M3):** Turn the single-download flow into a **queue**: add one or
more URLs, each becomes a job that downloads under a **bounded concurrency** cap with
live per-job progress and cancel. Playlist/channel URLs **expand** into one job per
entry. Transient network failures **auto-retry**.

**Confirmed decisions (brainstorming):**
- **Auto-start on add** — enqueuing a job runs it immediately, up to the concurrency cap.
- **Auto-retry with backoff** — transient (network) failures retried; permanent ones fail fast.
- **Playlist/channel → per-entry jobs** — each entry is its own job with its own progress/cancel.
- **Cancel only, no pause/resume** — SDD §19 keeps pause/resume a stretch goal.

**In scope**
- `JobStatus` + `DownloadJob` observable model.
- `RetryPolicy` (pure transient classification + backoff).
- `IDownloadManager`/`DownloadManager`: observable job collection, bounded concurrency,
  auto-start, per-job cancel, cancel-all, clear-completed, failure isolation, retry.
- Playlist/channel expansion (probe enumerates entries → one job per entry).
- ViewModel restructure to "add to queue" + a bound job list; page queue-list UI.

**Out of scope (deferred)**
- **User-configurable** concurrency, and **settings/presets/history/notifications/theming** — M5.
- **Pause/resume** of in-flight downloads — stretch (SDD §19).
- **Trim/subtitles/metadata embedding** — M4.
- History persistence of completed jobs — M5.

---

## 2. Key Design Decisions

1. **Queue lives in the module, not Core.** SDD §6 originally sketched
   `IDownloadManager`/`IJobQueue` in `FFMedia.Core`, but the queue orchestrates the
   module's `IMediaProbe`/`IDownloadService`, so it belongs in
   `FFMedia.Tools.YouTubeDownloader`. The generic bounded-concurrency pattern can be
   lifted to Core if a second tool needs it (YAGNI now). **SDD §6 is updated to match.**
2. **Probe/expand at add-time; the manager only downloads.** Resolving titles and
   expanding a playlist happens when the user adds a URL (the "fetching" step), so jobs
   reach the manager already knowing their `Url`/`Title`/`Config`. This keeps
   `DownloadManager` a focused download engine (Downloading→Processing→terminal) and lets
   titles appear in the queue immediately.
3. **Retry is driven by `Result`, not exceptions.** `IDownloadService.DownloadAsync`
   already returns `Result<string>` for expected failures (M1). The manager inspects
   `Result.Error`; `RetryPolicy.IsTransient` decides whether to retry. Cancellation still
   surfaces as `OperationCanceledException` → `Canceled` (never retried).
4. **Bounded concurrency via `SemaphoreSlim`.** A `SemaphoreSlim(maxConcurrency)` gates
   the actual download; each enqueued job awaits a slot (honoring its cancel token while
   queued), runs, then releases. Default `maxConcurrency = 3` (SDD §12/§19), a constant in
   M3 (configurable in M5).
5. **Failure isolation.** Each job runs in its own tracked task inside a try/catch; a
   failed or canceled job sets its own terminal status and never affects siblings.

---

## 3. Components

All in `FFMedia.Tools.YouTubeDownloader` unless noted.

### 3.1 `JobStatus` — `Models/JobStatus.cs`
```csharp
public enum JobStatus { Queued, Downloading, Processing, Completed, Canceled, Failed }
```
(SDD §7.2's `Fetching` happens at add-time before a job exists, so it is not a manager
state in M3.)

### 3.2 `DownloadJob` — `Models/DownloadJob.cs`
An `ObservableObject` (CommunityToolkit.Mvvm) shown directly in the queue list:
```csharp
public partial class DownloadJob : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Url { get; }
    public string Title { get; }
    public DownloadConfig Config { get; }
    public string OutputFolder { get; }
    internal CancellationTokenSource Cts { get; } = new();

    [ObservableProperty] private JobStatus _status = JobStatus.Queued;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _outputPath;

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Canceled or JobStatus.Failed;
    // ctor sets Url/Title/Config/OutputFolder
}
```

### 3.3 `RetryPolicy` — `Services/RetryPolicy.cs` (pure, tested)
```csharp
public sealed class RetryPolicy
{
    public RetryPolicy(int maxAttempts, TimeSpan baseDelay);
    public int MaxAttempts { get; }
    public TimeSpan DelayFor(int attempt);          // exponential: baseDelay * 2^(attempt-1)
    public static bool IsTransient(string? error);  // network signatures → true; permanent → false
    public static RetryPolicy Default { get; }       // 3 attempts, 1s base
}
```
- **Transient signatures** (case-insensitive substring match on the error text): "timed out",
  "timeout", "connection reset", "temporary failure", "network is unreachable",
  "unable to download", "HTTP Error 5" (5xx), "getaddrinfo", "read timed out".
- **Permanent** (not retried): "video unavailable", "private", "removed", "geo",
  "sign in", "members-only", "copyright" — i.e. anything not matching a transient signature
  is treated as permanent (fail fast). The classification is a pure function → unit-tested.

### 3.4 `IDownloadManager` / `DownloadManager` — `Services/…`
```csharp
public interface IDownloadManager
{
    ReadOnlyObservableCollection<DownloadJob> Jobs { get; }
    DownloadJob Enqueue(DownloadJob job);   // adds (Queued) and auto-starts
    void Cancel(DownloadJob job);           // cancels one job's token
    void CancelAll();                       // cancels every non-terminal job
    void ClearCompleted();                  // removes terminal jobs from the collection
    Task IdleAsync();                       // completes when no jobs are running or queued
}
```
`DownloadManager(IDownloadService download, RetryPolicy policy, int maxConcurrency = 3)`.

**Run loop (per job, fire-and-forget task tracked internally):**
1. `await _slots.WaitAsync(job.Cts.Token)` — waits for a slot; a cancel while queued
   throws `OperationCanceledException` → job `Canceled` (slot never acquired, so not released).
2. For `attempt = 1..MaxAttempts`:
   - `job.Status = Downloading`; run `download.DownloadAsync(new DownloadRequest(job.Url,
     job.OutputFolder, job.Config), progress, job.Cts.Token)`.
   - `progress` maps `DownloadUpdate` → `job.Progress`/`job.ProgressText` and reflects the
     stage (post-processing ⇒ `job.Status = Processing`).
   - On `Result.IsSuccess`: `job.Status = Completed`, `job.OutputPath = result.Value`; stop.
   - On failure: if `RetryPolicy.IsTransient(error)` and `attempt < MaxAttempts` →
     `await Task.Delay(policy.DelayFor(attempt), job.Cts.Token)` and retry; else
     `job.Status = Failed`, `job.ErrorMessage = error`; stop.
3. `OperationCanceledException` anywhere → `job.Status = Canceled`.
4. `finally`: release the slot if acquired; remove the job's task from the in-flight set.

`IdleAsync()` completes when the in-flight set is empty — a real API (usable for "all done"
UX later) and the deterministic seam tests await instead of sleeping.

`CancelAll()` cancels each non-terminal job's `Cts`. `ClearCompleted()` removes
`IsTerminal` jobs from `Jobs`.

### 3.5 Playlist/channel expansion — `Services/…`
Extend probing to enumerate entries: a plain video yields **1** entry, a playlist/channel
yields **N**. The add-flow maps each entry → a `DownloadJob` (title + url + the chosen
config) and enqueues it. The exact YoutubeDLSharp enumeration API (`RunVideoDataFetch`
flat/`Entries`) is finalized in the Task-4 plan against the installed package (as prior
milestones finalized yt-dlp flags). *(Built in Task 4 — after the engine.)*

### 3.6 ViewModel / Page
`DownloaderViewModel` restructures from single Probe/Download to **URL + config →
"Add to queue"**: it resolves/expands the URL, builds `DownloadJob`s from the M2 format
selections, and enqueues them into the injected `IDownloadManager`. The page binds
`Manager.Jobs` to a list (per-row title, progress bar, status, speed/ETA, a cancel button)
with a footer (**Cancel all**, **Clear completed**). *(Built in Tasks 5–6.)*

---

## 4. Concurrency & Cancellation Model

- One `SemaphoreSlim(maxConcurrency)` bounds simultaneous downloads; queued jobs await a slot.
- Each `DownloadJob` owns a `CancellationTokenSource`. `Cancel(job)` cancels one;
  `CancelAll()` cancels all non-terminal jobs. yt-dlp receives the token via
  `IDownloadService` (M1 wiring).
- `Jobs` (an `ObservableCollection`) is mutated only on the UI thread (Enqueue/Clear are
  user actions). Per-job property updates from worker threads rely on WPF data binding's
  cross-thread `PropertyChanged` marshaling; verified at run-time in Task 7. If a
  cross-thread update misbehaves, the manager takes an injected "post to UI" delegate
  (same pattern as M2's `progressFactory`) — noted as the fallback, not built pre-emptively.
- Tests run with no dispatcher and use `IdleAsync()` to await completion deterministically
  (no `Task.Delay`-based waits; retry backoff is injected as a tiny/zero delay in tests).

---

## 5. Testing Strategy

**Unit (no network):**
- **`DownloadJob`:** initial `Status == Queued`; `IsTerminal` true only for Completed/Canceled/Failed.
- **`RetryPolicy`:** transient signatures → `IsTransient` true; permanent/unknown → false;
  `DelayFor` grows exponentially; `Default` is 3 attempts / 1s base.
- **`DownloadManager`** (with a controllable fake `IDownloadService`):
  - **Bounded concurrency** — enqueue N > cap jobs against a fake that blocks until released,
    and assert the observed maximum in-flight count never exceeds the cap.
  - **Auto-start** — an enqueued job reaches `Downloading` without an explicit start call.
  - **Success/failure/cancel** terminal statuses set correctly.
  - **Failure isolation** — one job fails; the others still `Completed`.
  - **Retry** — a fake that fails transiently then succeeds ends `Completed` after the
    expected attempt count; a permanent failure ends `Failed` with no retry.
  - **`CancelAll`** / **`ClearCompleted`** behave as specified.
  - Tests await `IdleAsync()` and inject a zero/short-delay `RetryPolicy`.
- **Playlist expansion mapping** (Task 4) — entries → jobs, with a fake probe.
- **ViewModel** (Tasks 5) — "add" builds the right jobs from selections and enqueues them.

**Integration (trait-gated, off in CI, Task 7):** queue two real URLs (and/or a tiny
playlist) and assert both complete with bounded concurrency.

All unit tests use plain xUnit `Assert`; no network; no wall-clock sleeps for control flow.

---

## 6. SDD Updates (Rule 1)

- **§6** — note the download queue (`IDownloadManager`/`DownloadJob`) lives in the
  YouTube Downloader module (orchestrates module services); the generic concurrency
  pattern may move to Core if a second tool needs it.
- **§7.2** — annotate that M3 implements the state machine as
  Queued→Downloading→Processing→Completed/Canceled/Failed (fetching happens at add-time),
  with transient-only retry.
- **§12** — confirm the realized concurrency model (SemaphoreSlim cap = 3; per-job CTS;
  cancel-all via each job's token).
- **§17** — mark **M3 ✅ delivered**; **§19** — resolve "default concurrency = 3 (M3)" and
  "pause/resume deferred".
- **Header version → 0.5**, `Last updated`, Changelog row. **CLAUDE.md** progress entry.

*(Docs land in Task 8.)*

---

## 7. Definition of Done (M3)

- `dotnet build FFMedia.sln` clean; `dotnet test --filter "Category!=Integration"` all green.
- App: add one or more URLs (incl. a playlist) → jobs appear and download under a 3-at-a-time
  cap with live per-job progress; cancel one, cancel all, and clear completed all work;
  a transient failure retries, a permanent one fails fast without stalling the queue.
- `RetryPolicy` and `DownloadManager` concurrency/retry/isolation are unit-tested with a fake
  service (no network, no sleeps); `FFMedia.Core` stays UI-free.
- SDD updated to v0.5; CLAUDE.md progress logged.
- Delivered as a single PR (`feat/m3-queue` → `main`).

---

## 8. Incremental Delivery Note

This session executes **Tasks 1–3** (the fully unit-tested queue engine: `DownloadJob`,
`RetryPolicy`, `DownloadManager`) and stops. Tasks 4–8 (playlist expansion, ViewModel/page
UI, integration, docs) follow in a later session. Tasks 1–3 are self-contained and
reviewable on their own — no UI or network dependency.
