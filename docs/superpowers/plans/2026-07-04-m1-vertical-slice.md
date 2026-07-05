# M1 — Vertical Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the first working feature end-to-end: paste a YouTube URL → probe metadata → download a single MP4 with live progress and cancel, driven from the YouTube Downloader tool page that now appears in the shell's navigation pane.

**Architecture:** A new self-contained YouTube Downloader tool module (`FFMedia.Tools.YouTubeDownloader`) exposes an `ITool`, a `DownloaderViewModel`, a `DownloaderPage`, and yt-dlp-backed services behind interfaces (`IMediaProbe`, `IDownloadService`) so the ViewModel is unit-testable with fakes. The module self-registers via `AddYouTubeDownloader`. The App shell wires WPF-UI's `INavigationService`/`IPageService`, builds nav items from `IToolRegistry` joined with per-tool page registrations, and Core stays UI-agnostic.

**Tech Stack:** C# / .NET 9 · WPF + WPF-UI 4.3.0 · CommunityToolkit.Mvvm · YoutubeDLSharp (→ yt-dlp) · xUnit.

## Global Constraints

- `FFMedia.Core` references **NO UI framework** (WPF/WPF-UI). Core may use only BCL + DI Abstractions.
- **`FFMedia.Tools.YouTubeDownloader` retargets to `net9.0-windows` with `UseWPF=true`** (it now holds Views + ViewModels). **`FFMedia.Tests` retargets to `net9.0-windows`** so it can reference the module and unit-test ViewModels headlessly. (SDD §5 updated in Task 8.)
- **Nullable enabled** everywhere; **`TreatWarningsAsErrors=true`** in `FFMedia.Core`.
- **One public type per file**; file name matches the type.
- ViewModels use **CommunityToolkit.Mvvm** source generators; **no logic in code-behind** beyond view wiring.
- **async/await** for all I/O and process work; never block on `.Result`/`.Wait()`; every long op takes a `CancellationToken`.
- Bundled binaries resolved via **`IBinaryProvider`** (never PATH). yt-dlp/ffmpeg paths come from `IBinaryProvider`.
- yt-dlp/ffmpeg-backed service impls are **thin wrappers** verified by a trait-gated integration test; **pure logic** (option building, progress/metadata mapping, ViewModel state) is unit-tested with fakes — no network in unit tests.
- Unit tests use **plain xUnit `Assert`**.
- **CI must stay green without network:** integration tests are tagged `[Trait("Category","Integration")]` and excluded in CI (Task 7 updates `ci.yml` to `--filter Category!=Integration`).
- Workflow: whole plan on ONE branch off `main` (`feat/m1-vertical-slice`), delivered as one **PR for review** (CLAUDE.md Rule 3). Keep **`SDD.md`** current (Rule 1) + progress-log entry (Rule 2) — Task 8.

---

## File Structure

```
src/
├─ FFMedia.Core/
│  └─ Results/Result.cs                         (Task 1) generic operation result
│  └─ Tools/IToolPage.cs                        (Task 6) tool-id → page-type contract (System.Type only)
├─ FFMedia.Tools.YouTubeDownloader/             (retargeted net9.0-windows/UseWPF — Task 2)
│  ├─ Infrastructure/IYoutubeDlFactory.cs       (Task 2)
│  ├─ Infrastructure/YoutubeDlFactory.cs        (Task 2)
│  ├─ Models/MediaInfo.cs                        (Task 3)
│  ├─ Models/DownloadRequest.cs                  (Task 3)
│  ├─ Models/DownloadUpdate.cs                   (Task 3)
│  ├─ Services/IMediaProbe.cs                    (Task 3)
│  ├─ Services/IDownloadService.cs               (Task 3)
│  ├─ Services/DownloadOptions.cs                (Task 3) MP4 OptionSet builder (pure)
│  ├─ Services/ProgressMapping.cs                (Task 4) DownloadProgress → DownloadUpdate (pure)
│  ├─ Services/YtDlpMediaProbe.cs                (Task 4) thin yt-dlp wrapper
│  ├─ Services/YtDlpDownloadService.cs           (Task 4) thin yt-dlp wrapper
│  ├─ ViewModels/DownloaderViewModel.cs          (Task 5)
│  ├─ Views/DownloaderPage.xaml(.cs)             (Task 6)
│  ├─ YouTubeDownloaderTool.cs                   (Task 6) ITool
│  └─ ServiceCollectionExtensions.cs             (Task 6) AddYouTubeDownloader
├─ FFMedia.App/
│  ├─ ViewModels/MainWindowViewModel.cs          (Task 7, modified) builds nav items
│  ├─ MainWindow.xaml(.cs)                        (Task 7, modified) MenuItemsSource + nav services
│  ├─ Navigation/ToolPage.cs                      (Task 6) IToolPage impl (or in module)
│  └─ App.xaml.cs                                 (Task 7, modified) register nav services + AddYouTubeDownloader
└─ FFMedia.Tests/                                (retargeted net9.0-windows — Task 2)
   ├─ Results/ResultTests.cs                      (Task 1)
   ├─ YouTubeDownloader/YoutubeDlFactoryTests.cs  (Task 2)
   ├─ YouTubeDownloader/DownloadOptionsTests.cs   (Task 3)
   ├─ YouTubeDownloader/ProgressMappingTests.cs   (Task 4)
   ├─ YouTubeDownloader/DownloaderViewModelTests.cs (Task 5)
   └─ Integration/YtDlpIntegrationTests.cs        (Task 7, trait-gated)
```

---

### Task 1: `Result<T>` in Core (operation result)

**Files:**
- Create: `src/FFMedia.Core/Results/Result.cs`
- Test: `src/FFMedia.Tests/Results/ResultTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Result<T>` with `bool IsSuccess`, `T? Value`, `string? Error`, factory methods `Result<T>.Success(T)` / `Result<T>.Failure(string)`. Used by `IMediaProbe`/`IDownloadService` to convey expected failures (unavailable/private video) without exceptions.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/Results/ResultTests.cs`:
```csharp
using FFMedia.Core.Results;
using Xunit;

namespace FFMedia.Tests.Results;

public class ResultTests
{
    [Fact]
    public void Success_SetsValueAndIsSuccess()
    {
        var r = Result<int>.Success(42);
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Failure_SetsErrorAndNotSuccess()
    {
        var r = Result<string>.Failure("boom");
        Assert.False(r.IsSuccess);
        Assert.Equal("boom", r.Error);
        Assert.Null(r.Value);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~ResultTests`
Expected: FAIL — `Result<T>` does not exist.

- [ ] **Step 3: Implement**

Create `src/FFMedia.Core/Results/Result.cs`:
```csharp
namespace FFMedia.Core.Results;

/// <summary>Outcome of an operation that may fail with a user-facing reason.</summary>
public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, string? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }

    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(string error) => new(false, default, error);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~ResultTests`
Expected: PASS (2/2).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add Result<T> for expected-failure outcomes"
```

---

### Task 2: Retarget module + tests to WPF; add YoutubeDLSharp; `YoutubeDlFactory`

**Files:**
- Modify: `src/FFMedia.Tools.YouTubeDownloader/FFMedia.Tools.YouTubeDownloader.csproj` (TFM + UseWPF + packages)
- Modify: `src/FFMedia.Tests/FFMedia.Tests.csproj` (TFM + reference module)
- Create: `src/FFMedia.Tools.YouTubeDownloader/Infrastructure/IYoutubeDlFactory.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Infrastructure/YoutubeDlFactory.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/YoutubeDlFactoryTests.cs`

**Interfaces:**
- Consumes: `IBinaryProvider`, `ExternalBinary` (Core, from M0).
- Produces: `IYoutubeDlFactory { YoutubeDL Create(); }`; `YoutubeDlFactory(IBinaryProvider)` builds a `YoutubeDL` with `YoutubeDLPath` = yt-dlp path and `FFmpegPath` = ffmpeg path from `IBinaryProvider`.

- [ ] **Step 1: Retarget the module csproj**

In `src/FFMedia.Tools.YouTubeDownloader/FFMedia.Tools.YouTubeDownloader.csproj`, set the `<PropertyGroup>` target/props to:
```xml
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
```
Keep the existing `<ProjectReference>`s to `FFMedia.Core` and `FFMedia.Media`.

- [ ] **Step 2: Add packages to the module**

```bash
dotnet add src/FFMedia.Tools.YouTubeDownloader package WPF-UI
dotnet add src/FFMedia.Tools.YouTubeDownloader package CommunityToolkit.Mvvm
dotnet add src/FFMedia.Tools.YouTubeDownloader package YoutubeDLSharp
```
Record resolved versions. (WPF-UI must match the App's 4.3.0 — verify the resolved version equals what `FFMedia.App` uses; if `dotnet add` picks a different one, pin it to the App's version.)

- [ ] **Step 3: Retarget the test project and reference the module**

In `src/FFMedia.Tests/FFMedia.Tests.csproj`, change `<TargetFramework>net9.0</TargetFramework>` to `<TargetFramework>net9.0-windows</TargetFramework>`. Then:
```bash
dotnet add src/FFMedia.Tests reference src/FFMedia.Tools.YouTubeDownloader
```

- [ ] **Step 4: Write the failing test**

Create `src/FFMedia.Tests/YouTubeDownloader/YoutubeDlFactoryTests.cs`:
```csharp
using FFMedia.Core.Binaries;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class YoutubeDlFactoryTests
{
    private sealed class FakeBinaryProvider : IBinaryProvider
    {
        public string GetPath(ExternalBinary binary) =>
            binary == ExternalBinary.YtDlp ? @"C:\bin\yt-dlp.exe" : @"C:\bin\ffmpeg.exe";
        public bool Exists(ExternalBinary binary) => true;
    }

    [Fact]
    public void Create_SetsYtDlpAndFfmpegPathsFromBinaryProvider()
    {
        var factory = new YoutubeDlFactory(new FakeBinaryProvider());

        var ytdl = factory.Create();

        Assert.Equal(@"C:\bin\yt-dlp.exe", ytdl.YoutubeDLPath);
        Assert.Equal(@"C:\bin\ffmpeg.exe", ytdl.FFmpegPath);
    }
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~YoutubeDlFactoryTests`
Expected: FAIL — `IYoutubeDlFactory`/`YoutubeDlFactory` do not exist.

- [ ] **Step 6: Implement**

Create `src/FFMedia.Tools.YouTubeDownloader/Infrastructure/IYoutubeDlFactory.cs`:
```csharp
using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Infrastructure;

/// <summary>Creates a YoutubeDL configured with the bundled yt-dlp/ffmpeg paths.</summary>
public interface IYoutubeDlFactory
{
    YoutubeDL Create();
}
```

Create `src/FFMedia.Tools.YouTubeDownloader/Infrastructure/YoutubeDlFactory.cs`:
```csharp
using FFMedia.Core.Binaries;
using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Infrastructure;

public sealed class YoutubeDlFactory : IYoutubeDlFactory
{
    private readonly IBinaryProvider _binaries;

    public YoutubeDlFactory(IBinaryProvider binaries)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        _binaries = binaries;
    }

    public YoutubeDL Create() => new()
    {
        YoutubeDLPath = _binaries.GetPath(ExternalBinary.YtDlp),
        FFmpegPath = _binaries.GetPath(ExternalBinary.Ffmpeg),
    };
}
```

- [ ] **Step 7: Verify the factory test passes and the whole solution still builds/tests**

Run:
```bash
dotnet test src/FFMedia.Tests --filter FullyQualifiedName~YoutubeDlFactoryTests
dotnet build FFMedia.sln
dotnet test FFMedia.sln
```
Expected: factory test PASS; solution builds; all prior tests still green (Result + M0 tests).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(youtube): retarget module to WPF, add YoutubeDLSharp + YoutubeDlFactory"
```

---

### Task 3: Domain models, service interfaces, MP4 option builder

**Files:**
- Create: `src/FFMedia.Tools.YouTubeDownloader/Models/MediaInfo.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Models/DownloadRequest.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Models/DownloadUpdate.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Services/IMediaProbe.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Services/IDownloadService.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Services/DownloadOptions.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/DownloadOptionsTests.cs`

**Interfaces:**
- Consumes: `Result<T>` (Task 1).
- Produces:
  - `record MediaInfo(string Title, TimeSpan? Duration, string? ThumbnailUrl, string? Uploader)`
  - `record DownloadRequest(string Url, string OutputFolder)`
  - `record DownloadUpdate(double Percent, string? Speed, string? Eta, string Stage)`
  - `IMediaProbe { Task<Result<MediaInfo>> ProbeAsync(string url, CancellationToken ct); }`
  - `IDownloadService { Task<Result<string>> DownloadAsync(DownloadRequest request, IProgress<DownloadUpdate> progress, CancellationToken ct); }`
  - `DownloadOptions.Mp4(string outputFolder)` → `OptionSet` with `RecodeVideo = VideoRecodeFormat.Mp4`, `NoPlaylist = true`, and `Output` set to `<folder>/%(title)s.%(ext)s`.

- [ ] **Step 1: Write the failing test (option builder is the pure, testable piece)**

Create `src/FFMedia.Tests/YouTubeDownloader/DownloadOptionsTests.cs`:
```csharp
using FFMedia.Tools.YouTubeDownloader.Services;
using YoutubeDLSharp.Options;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloadOptionsTests
{
    [Fact]
    public void Mp4_SetsRecodeMp4_NoPlaylist_AndOutputTemplateUnderFolder()
    {
        var options = DownloadOptions.Mp4(@"C:\out");

        Assert.Equal(VideoRecodeFormat.Mp4, options.RecodeVideo);
        Assert.True(options.NoPlaylist);
        Assert.Contains(@"C:\out", options.Output);
        Assert.Contains("%(title)s.%(ext)s", options.Output);
    }
}
```
Note: confirm the `YoutubeDLSharp.Options` namespace for `OptionSet`/`VideoRecodeFormat` against the installed package; adjust the `using` if the package exposes them elsewhere.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloadOptionsTests`
Expected: FAIL — `DownloadOptions` does not exist.

- [ ] **Step 3: Create the models, interfaces, and option builder**

Create `src/FFMedia.Tools.YouTubeDownloader/Models/MediaInfo.cs`:
```csharp
namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>Metadata about a media item probed from a URL.</summary>
public sealed record MediaInfo(string Title, TimeSpan? Duration, string? ThumbnailUrl, string? Uploader);
```

Create `src/FFMedia.Tools.YouTubeDownloader/Models/DownloadRequest.cs`:
```csharp
namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>A request to download a single media item as MP4 (M1 scope).</summary>
public sealed record DownloadRequest(string Url, string OutputFolder);
```

Create `src/FFMedia.Tools.YouTubeDownloader/Models/DownloadUpdate.cs`:
```csharp
namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>A progress update during a download.</summary>
public sealed record DownloadUpdate(double Percent, string? Speed, string? Eta, string Stage);
```

Create `src/FFMedia.Tools.YouTubeDownloader/Services/IMediaProbe.cs`:
```csharp
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Fetches media metadata for a URL without downloading.</summary>
public interface IMediaProbe
{
    Task<Result<MediaInfo>> ProbeAsync(string url, CancellationToken ct);
}
```

Create `src/FFMedia.Tools.YouTubeDownloader/Services/IDownloadService.cs`:
```csharp
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Downloads a media item, reporting progress; returns the output file path.</summary>
public interface IDownloadService
{
    Task<Result<string>> DownloadAsync(DownloadRequest request, IProgress<DownloadUpdate> progress, CancellationToken ct);
}
```

Create `src/FFMedia.Tools.YouTubeDownloader/Services/DownloadOptions.cs`:
```csharp
using System.IO;
using YoutubeDLSharp.Options;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Builds yt-dlp option sets. M1: single-video MP4.</summary>
public static class DownloadOptions
{
    public static OptionSet Mp4(string outputFolder) => new()
    {
        RecodeVideo = VideoRecodeFormat.Mp4,
        NoPlaylist = true,
        Output = Path.Combine(outputFolder, "%(title)s.%(ext)s"),
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloadOptionsTests`
Expected: PASS. If `OptionSet.Output` normalizes separators, keep the `Assert.Contains` checks tolerant (they check substrings, not exact equality).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(youtube): add media models, service interfaces, MP4 option builder"
```

---

### Task 4: yt-dlp-backed services + progress mapping

**Files:**
- Create: `src/FFMedia.Tools.YouTubeDownloader/Services/ProgressMapping.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Services/YtDlpMediaProbe.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Services/YtDlpDownloadService.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/ProgressMappingTests.cs`

**Interfaces:**
- Consumes: `IYoutubeDlFactory` (Task 2), models + `IMediaProbe`/`IDownloadService` (Task 3), `Result<T>` (Task 1).
- Produces: `ProgressMapping.ToUpdate(DownloadProgress)` (pure, tested); `YtDlpMediaProbe : IMediaProbe` and `YtDlpDownloadService : IDownloadService` (thin wrappers, verified by the Task 7 integration test).

- [ ] **Step 1: Write the failing test for the pure mapping**

Create `src/FFMedia.Tests/YouTubeDownloader/ProgressMappingTests.cs`:
```csharp
using FFMedia.Tools.YouTubeDownloader.Services;
using YoutubeDLSharp;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class ProgressMappingTests
{
    [Fact]
    public void ToUpdate_MapsProgressFractionToPercent_AndCarriesFields()
    {
        // DownloadProgress(state, progress, downloadSpeed, eta, ...) — confirm ctor against installed YoutubeDLSharp.
        var p = new DownloadProgress(DownloadState.Downloading, progress: 0.5f, totalDownloadSize: "10MiB", downloadSpeed: "1MiB/s", eta: "00:10");

        var update = ProgressMapping.ToUpdate(p);

        Assert.Equal(50.0, update.Percent, precision: 3);
        Assert.Equal("1MiB/s", update.Speed);
        Assert.Equal("00:10", update.Eta);
        Assert.Equal(DownloadState.Downloading.ToString(), update.Stage);
    }
}
```
Note: `DownloadProgress`'s constructor parameter names/order may differ in the installed version. If so, adapt the test's constructor call (and the mapping) to the actual public constructor/properties — the assertions on the mapped `DownloadUpdate` stay the same.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~ProgressMappingTests`
Expected: FAIL — `ProgressMapping` does not exist.

- [ ] **Step 3: Implement the mapping**

Create `src/FFMedia.Tools.YouTubeDownloader/Services/ProgressMapping.cs`:
```csharp
using FFMedia.Tools.YouTubeDownloader.Models;
using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Maps YoutubeDLSharp progress to the module's DownloadUpdate.</summary>
public static class ProgressMapping
{
    public static DownloadUpdate ToUpdate(DownloadProgress p) => new(
        Percent: p.Progress * 100.0,
        Speed: p.DownloadSpeed,
        Eta: p.ETA,
        Stage: p.State.ToString());
}
```
Note: confirm the property names on `DownloadProgress` (`Progress`, `DownloadSpeed`, `ETA`, `State`) against the installed package; adjust if they differ.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~ProgressMappingTests`
Expected: PASS.

- [ ] **Step 5: Implement the thin service wrappers (no unit test — covered by Task 7 integration test)**

Create `src/FFMedia.Tools.YouTubeDownloader/Services/YtDlpMediaProbe.cs`:
```csharp
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

public sealed class YtDlpMediaProbe : IMediaProbe
{
    private readonly IYoutubeDlFactory _factory;

    public YtDlpMediaProbe(IYoutubeDlFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<Result<MediaInfo>> ProbeAsync(string url, CancellationToken ct)
    {
        var ytdl = _factory.Create();
        var res = await ytdl.RunVideoDataFetch(url, ct: ct);
        if (!res.Success)
            return Result<MediaInfo>.Failure(string.Join(Environment.NewLine, res.ErrorOutput));

        var v = res.Data;
        var duration = v.Duration is > 0 ? TimeSpan.FromSeconds(v.Duration.Value) : (TimeSpan?)null;
        return Result<MediaInfo>.Success(new MediaInfo(v.Title, duration, v.Thumbnail, v.Uploader));
    }
}
```
Note: `VideoData` property names/types (`Duration` as `float?` seconds, `Thumbnail`, `Uploader`, `Title`) may differ slightly; adapt the mapping to the installed `VideoData` shape. The method contract (returns `Result<MediaInfo>`) must not change.

Create `src/FFMedia.Tools.YouTubeDownloader/Services/YtDlpDownloadService.cs`:
```csharp
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;
using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Services;

public sealed class YtDlpDownloadService : IDownloadService
{
    private readonly IYoutubeDlFactory _factory;

    public YtDlpDownloadService(IYoutubeDlFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<Result<string>> DownloadAsync(
        DownloadRequest request, IProgress<DownloadUpdate> progress, CancellationToken ct)
    {
        var ytdl = _factory.Create();
        ytdl.OutputFolder = request.OutputFolder;

        var innerProgress = new Progress<DownloadProgress>(p => progress.Report(ProgressMapping.ToUpdate(p)));
        var options = DownloadOptions.Mp4(request.OutputFolder);

        var res = await ytdl.RunVideoDownload(request.Url, progress: innerProgress, ct: ct, overrideOptions: options);
        return res.Success
            ? Result<string>.Success(res.Data)
            : Result<string>.Failure(string.Join(Environment.NewLine, res.ErrorOutput));
    }
}
```
Note: confirm `RunVideoDownload` parameter names (`progress`, `ct`, `overrideOptions`) against the installed package; adapt if needed. Contract must not change.

- [ ] **Step 6: Build and run the full suite**

Run:
```bash
dotnet build FFMedia.sln
dotnet test FFMedia.sln
```
Expected: builds; all unit tests green (integration test not added yet).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(youtube): add yt-dlp probe/download services and progress mapping"
```

---

### Task 5: `DownloaderViewModel` (TDD with fakes)

**Files:**
- Create: `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`
- Test: `src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs`

**Interfaces:**
- Consumes: `IMediaProbe`, `IDownloadService`, models (Task 3), `Result<T>` (Task 1).
- Produces: `DownloaderViewModel(IMediaProbe, IDownloadService)` with observable `Url`, `StatusMessage`, `MediaTitle`, `Progress` (double 0–100), `IsBusy`, and `ProbeCommand`/`DownloadCommand`/`CancelCommand`. `OutputFolder` defaults to `%USERPROFILE%\Videos\FFMedia`.

Behavior to implement and test:
- `ProbeAsync`: sets `IsBusy` while running; on `Result.Success` sets `MediaTitle` and a "Ready" status; on `Failure` sets `StatusMessage` to the error and leaves `MediaTitle` empty.
- `DownloadAsync`: requires a probed URL; sets `IsBusy`, forwards progress into `Progress`; on success sets status "Saved to {path}"; on failure sets the error; supports cancellation via `CancelCommand` (a `CancellationTokenSource`), on cancel sets status "Canceled".

- [ ] **Step 1: Write the failing tests**

Create `src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs`:
```csharp
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using FFMedia.Tools.YouTubeDownloader.ViewModels;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloaderViewModelTests
{
    private sealed class FakeProbe : IMediaProbe
    {
        public Result<MediaInfo> Next = Result<MediaInfo>.Success(new MediaInfo("Test Video", null, null, null));
        public Task<Result<MediaInfo>> ProbeAsync(string url, CancellationToken ct) => Task.FromResult(Next);
    }

    private sealed class FakeDownload : IDownloadService
    {
        public Result<string> Next = Result<string>.Success(@"C:\out\Test Video.mp4");
        public IReadOnlyList<DownloadUpdate> Updates = new[] { new DownloadUpdate(50, "1MiB/s", "00:05", "Downloading") };
        public Task<Result<string>> DownloadAsync(DownloadRequest request, IProgress<DownloadUpdate> progress, CancellationToken ct)
        {
            foreach (var u in Updates) progress.Report(u);
            return Task.FromResult(Next);
        }
    }

    [Fact]
    public async Task Probe_Success_SetsMediaTitle()
    {
        var vm = new DownloaderViewModel(new FakeProbe(), new FakeDownload()) { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        Assert.Equal("Test Video", vm.MediaTitle);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Probe_Failure_SetsStatusAndNoTitle()
    {
        var probe = new FakeProbe { Next = Result<MediaInfo>.Failure("Video unavailable") };
        var vm = new DownloaderViewModel(probe, new FakeDownload()) { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.MediaTitle);
        Assert.Contains("unavailable", vm.StatusMessage);
    }

    [Fact]
    public async Task Download_Success_ReportsProgressAndSavedPath()
    {
        var vm = new DownloaderViewModel(new FakeProbe(), new FakeDownload()) { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.Progress);
        Assert.Contains("Test Video.mp4", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Download_Failure_SetsErrorStatus()
    {
        var dl = new FakeDownload { Next = Result<string>.Failure("network error"), Updates = Array.Empty<DownloadUpdate>() };
        var vm = new DownloaderViewModel(new FakeProbe(), dl) { Url = "https://x" };
        await vm.ProbeCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Contains("network error", vm.StatusMessage);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloaderViewModelTests`
Expected: FAIL — `DownloaderViewModel` does not exist.

- [ ] **Step 3: Implement the ViewModel**

Create `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`:
```csharp
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;

namespace FFMedia.Tools.YouTubeDownloader.ViewModels;

public partial class DownloaderViewModel : ObservableObject
{
    private readonly IMediaProbe _probe;
    private readonly IDownloadService _download;
    private CancellationTokenSource? _cts;

    public DownloaderViewModel(IMediaProbe probe, IDownloadService download)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(download);
        _probe = probe;
        _download = download;
        OutputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFMedia");
    }

    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _mediaTitle = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _outputFolder;

    [RelayCommand]
    private async Task ProbeAsync()
    {
        IsBusy = true;
        MediaTitle = string.Empty;
        StatusMessage = "Probing…";
        try
        {
            var result = await _probe.ProbeAsync(Url, CancellationToken.None);
            if (result.IsSuccess)
            {
                MediaTitle = result.Value!.Title;
                StatusMessage = "Ready to download";
            }
            else
            {
                StatusMessage = result.Error ?? "Probe failed";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        IsBusy = true;
        Progress = 0;
        StatusMessage = "Downloading…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<DownloadUpdate>(u =>
        {
            Progress = u.Percent;
            StatusMessage = $"{u.Stage} {u.Percent:0}%  {u.Speed}  ETA {u.Eta}";
        });
        try
        {
            var result = await _download.DownloadAsync(
                new DownloadRequest(Url, OutputFolder), progress, _cts.Token);
            StatusMessage = result.IsSuccess
                ? $"Saved to {result.Value}"
                : result.Error ?? "Download failed";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Canceled";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~DownloaderViewModelTests`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(youtube): add DownloaderViewModel with probe/download/cancel"
```

---

### Task 6: Tool page view, `ITool`, and `AddYouTubeDownloader`

**Files:**
- Create: `src/FFMedia.Core/Tools/IToolPage.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/Navigation/ToolPage.cs` (implements `IToolPage`)
- Create: `src/FFMedia.Tools.YouTubeDownloader/Views/DownloaderPage.xaml` + `.xaml.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/YouTubeDownloaderTool.cs`
- Create: `src/FFMedia.Tools.YouTubeDownloader/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `ITool` (Core), `DownloaderViewModel` + services (earlier tasks).
- Produces:
  - `IToolPage { string ToolId { get; } Type PageType { get; } }` in Core (uses only `System.Type`, keeps Core UI-agnostic).
  - `YouTubeDownloaderTool : ITool` (Id `"youtube-downloader"`, DisplayName `"YouTube Downloader"`, an `IconGlyph`, SortOrder `10`).
  - `AddYouTubeDownloader(this IServiceCollection)` registering the tool, its `IToolPage`, services, ViewModel, and page.

- [ ] **Step 1: Add the `IToolPage` contract to Core**

Create `src/FFMedia.Core/Tools/IToolPage.cs`:
```csharp
namespace FFMedia.Core.Tools;

/// <summary>Associates a tool with the root view (page) type the shell should navigate to.
/// Uses only System.Type so Core stays UI-framework-agnostic.</summary>
public interface IToolPage
{
    string ToolId { get; }
    Type PageType { get; }
}
```

- [ ] **Step 2: Create the `IToolPage` implementation**

Create `src/FFMedia.Tools.YouTubeDownloader/Navigation/ToolPage.cs`:
```csharp
using FFMedia.Core.Tools;

namespace FFMedia.Tools.YouTubeDownloader.Navigation;

public sealed class ToolPage : IToolPage
{
    public ToolPage(string toolId, Type pageType)
    {
        ToolId = toolId;
        PageType = pageType;
    }

    public string ToolId { get; }
    public Type PageType { get; }
}
```

- [ ] **Step 3: Create the tool page view**

Create `src/FFMedia.Tools.YouTubeDownloader/Views/DownloaderPage.xaml`:
```xml
<Page x:Class="FFMedia.Tools.YouTubeDownloader.Views.DownloaderPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
    <StackPanel Margin="24" MaxWidth="720">
        <TextBlock Text="YouTube Downloader" FontSize="24" FontWeight="SemiBold" />
        <TextBlock Text="Paste a URL, probe it, then download as MP4." Opacity="0.8" Margin="0,4,0,16" />

        <ui:TextBox PlaceholderText="https://www.youtube.com/watch?v=…"
                    Text="{Binding Url, UpdateSourceTrigger=PropertyChanged}" />

        <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
            <ui:Button Content="Probe" Command="{Binding ProbeCommand}" />
            <ui:Button Content="Download" Command="{Binding DownloadCommand}" Margin="8,0,0,0" />
            <ui:Button Content="Cancel" Command="{Binding CancelCommand}" Margin="8,0,0,0" />
        </StackPanel>

        <TextBlock Text="{Binding MediaTitle}" FontWeight="SemiBold" Margin="0,16,0,0" />
        <ProgressBar Value="{Binding Progress}" Maximum="100" Height="6" Margin="0,8,0,0" />
        <TextBlock Text="{Binding StatusMessage}" Opacity="0.8" Margin="0,8,0,0" TextWrapping="Wrap" />
    </StackPanel>
</Page>
```
Note: if you don't have an `InverseBoolConverter` resource wired, simplify the `IsEnabled` binding (e.g., bind buttons' enabled state to command `CanExecute` or drop the converter for M1). Adapt WPF-UI control names/namespaces to the installed 4.3.0 API as needed; the goal is a functional page bound to `DownloaderViewModel`.

Create `src/FFMedia.Tools.YouTubeDownloader/Views/DownloaderPage.xaml.cs`:
```csharp
using System.Windows.Controls;
using FFMedia.Tools.YouTubeDownloader.ViewModels;

namespace FFMedia.Tools.YouTubeDownloader.Views;

public partial class DownloaderPage : Page
{
    public DownloaderPage(DownloaderViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
```

- [ ] **Step 4: Create the `ITool`**

Create `src/FFMedia.Tools.YouTubeDownloader/YouTubeDownloaderTool.cs`:
```csharp
using FFMedia.Core.Tools;

namespace FFMedia.Tools.YouTubeDownloader;

public sealed class YouTubeDownloaderTool : ITool
{
    public string Id => "youtube-downloader";
    public string DisplayName => "YouTube Downloader";
    public string Description => "Download YouTube videos and audio.";
    public string IconGlyph => ""; // Segoe Fluent Icons "Download" glyph
    public int SortOrder => 10;
}
```

- [ ] **Step 5: Create the DI extension**

Create `src/FFMedia.Tools.YouTubeDownloader/ServiceCollectionExtensions.cs`:
```csharp
using FFMedia.Core.Tools;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Navigation;
using FFMedia.Tools.YouTubeDownloader.Services;
using FFMedia.Tools.YouTubeDownloader.ViewModels;
using FFMedia.Tools.YouTubeDownloader.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FFMedia.Tools.YouTubeDownloader;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddYouTubeDownloader(this IServiceCollection services)
    {
        services.AddSingleton<ITool, YouTubeDownloaderTool>();
        services.AddSingleton<IToolPage>(new ToolPage("youtube-downloader", typeof(DownloaderPage)));
        services.AddSingleton<IYoutubeDlFactory, YoutubeDlFactory>();
        services.AddSingleton<IMediaProbe, YtDlpMediaProbe>();
        services.AddSingleton<IDownloadService, YtDlpDownloadService>();
        services.AddTransient<DownloaderViewModel>();
        services.AddTransient<DownloaderPage>();
        return services;
    }
}
```
This requires `Microsoft.Extensions.DependencyInjection.Abstractions` in the module:
```bash
dotnet add src/FFMedia.Tools.YouTubeDownloader package Microsoft.Extensions.DependencyInjection.Abstractions
```

- [ ] **Step 6: Build**

Run: `dotnet build FFMedia.sln`
Expected: builds (no new unit tests this task; page/tool are UI/wiring, exercised in Task 7).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(youtube): add tool page, ITool, and AddYouTubeDownloader registration"
```

---

### Task 7: Shell navigation wiring + integration test + run verification

**Files:**
- Modify: `src/FFMedia.App/App.xaml.cs` (register nav services + `AddYouTubeDownloader`)
- Modify: `src/FFMedia.App/ViewModels/MainWindowViewModel.cs` (build nav items)
- Modify: `src/FFMedia.App/MainWindow.xaml` + `.xaml.cs` (MenuItemsSource + nav/page services)
- Modify: `.github/workflows/ci.yml` (exclude integration tests)
- Create: `src/FFMedia.Tests/Integration/YtDlpIntegrationTests.cs` (trait-gated)

**Interfaces:**
- Consumes: `IToolRegistry`, `IToolPage` (Core), `AddYouTubeDownloader` (module), WPF-UI `INavigationService`/`IPageService`.
- Produces: a running shell where selecting "YouTube Downloader" in the nav pane shows `DownloaderPage`, and a trait-gated integration test proving real probe+download.

- [ ] **Step 1: Register navigation services + the tool in the composition root**

In `src/FFMedia.App/App.xaml.cs`, inside `ConfigureServices`, add (alongside the existing `AddFFMediaCore` + MainWindow/VM registrations):
```csharp
services.AddNavigationViewPageProvider();          // WPF-UI DI page provider
services.AddSingleton<Wpf.Ui.INavigationService, Wpf.Ui.NavigationService>();
services.AddYouTubeDownloader();
```
Note: **WPF-UI 4.x navigation API is the highest-risk part of this plan — verify against the installed 4.3.0 assemblies before writing, don't trust these names blind.** 4.x commonly renamed the page-resolution seam from `IPageService`/`SetPageService` to `INavigationViewPageProvider` / `SetPageProviderService` (via `services.AddNavigationViewPageProvider()`), and `INavigationService`/`NavigationService` live in the `Wpf.Ui` namespace. Inspect the actual `Wpf.Ui` public API (e.g., a throwaway reflection probe or the package's IntelliSense) and use whatever the installed version exposes to achieve: DI-resolved pages + a `NavigationView` driven by code-behind service wiring. Keep the ViewModel/Core seams exactly as specified; only the WPF-UI glue adapts. Record the actual API used in the task report.

- [ ] **Step 2: Build nav items in `MainWindowViewModel`**

Replace `src/FFMedia.App/ViewModels/MainWindowViewModel.cs` with a version that joins tools with their pages into WPF-UI `NavigationViewItem`s:
```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FFMedia.Core.Tools;
using Wpf.Ui.Controls;

namespace FFMedia.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(IToolRegistry registry, IEnumerable<IToolPage> pages)
    {
        var pageById = pages.ToDictionary(p => p.ToolId, p => p.PageType);
        var items = new ObservableCollection<object>();
        foreach (var tool in registry.Tools)
        {
            if (!pageById.TryGetValue(tool.Id, out var pageType)) continue;
            items.Add(new NavigationViewItem
            {
                Content = tool.DisplayName,
                Icon = new FontIcon { Glyph = tool.IconGlyph },
                TargetPageType = pageType,
            });
        }
        MenuItems = items;
    }

    [ObservableProperty]
    private ObservableCollection<object> _menuItems = new();
}
```
Note: `NavigationViewItem`/`FontIcon` property names (`Content`, `Icon`, `TargetPageType`, `Glyph`) are per WPF-UI 4.3.0; adapt if the installed API differs (e.g., icon via `SymbolIcon` or `Glyph` on a different type). Outcome: one nav entry per registered tool, showing its DisplayName + glyph, targeting its page.

- [ ] **Step 3: Wire `MenuItemsSource` + nav/page services in `MainWindow`**

In `src/FFMedia.App/MainWindow.xaml`, set the NavigationView's `MenuItemsSource="{Binding MenuItems}"` (keep the existing empty-pane structure otherwise). In `src/FFMedia.App/MainWindow.xaml.cs`, inject and attach the services:
```csharp
using FFMedia.App.ViewModels;
using Wpf.Ui;                 // INavigationService
using Wpf.Ui.Controls;        // FluentWindow

namespace FFMedia.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel, INavigationService navigationService, IPageService pageService)
    {
        DataContext = viewModel;
        InitializeComponent();
        navigationService.SetNavigationControl(RootNavigation);
        RootNavigation.SetPageService(pageService);
    }
}
```
Note: `IPageService`/`INavigationService` types + `SetPageService`/`SetNavigationControl` are the WPF-UI 4.3.0 MVVM navigation entry points; adapt names to the installed API. Remove the M0 `ContentFrame.Navigate(new WelcomePage())` code-behind now that the NavigationView drives content; you may keep `WelcomePage` as the default landing or navigate to the first tool on load.

- [ ] **Step 4: Exclude integration tests in CI**

In `.github/workflows/ci.yml`, change the Test step to:
```yaml
      - name: Test
        run: dotnet test FFMedia.sln --configuration Release --no-build --verbosity normal --filter "Category!=Integration"
```

- [ ] **Step 5: Add the trait-gated integration test**

Create `src/FFMedia.Tests/Integration/YtDlpIntegrationTests.cs`:
```csharp
using FFMedia.Core.Binaries;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.Integration;

[Trait("Category", "Integration")]
public class YtDlpIntegrationTests
{
    // Resolves the real bundled binaries relative to the test output's app base.
    private static IBinaryProvider RealBinaries()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "binaries");
        return new BundledBinaryProvider(dir);
    }

    // A short, stable, license-permissive video. "Me at the zoo" — the first YouTube video (~19s).
    private const string TestUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    [Fact]
    public async Task Probe_ReturnsTitle()
    {
        var binaries = RealBinaries();
        Assert.True(binaries.Exists(ExternalBinary.YtDlp), "Run build/fetch-binaries.ps1 first.");
        var probe = new YtDlpMediaProbe(new YoutubeDlFactory(binaries));

        var result = await probe.ProbeAsync(TestUrl, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.Title));
    }

    [Fact]
    public async Task Download_ProducesMp4File()
    {
        var binaries = RealBinaries();
        Assert.True(binaries.Exists(ExternalBinary.Ffmpeg), "Run build/fetch-binaries.ps1 first.");
        var outDir = Path.Combine(Path.GetTempPath(), "ffmedia-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var svc = new YtDlpDownloadService(new YoutubeDlFactory(binaries));
            var updates = new List<DownloadUpdate>();
            var progress = new Progress<DownloadUpdate>(updates.Add);

            var result = await svc.DownloadAsync(new DownloadRequest(TestUrl, outDir), progress, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error);
            Assert.NotEmpty(Directory.GetFiles(outDir, "*.mp4"));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }
}
```
Note: the test needs the bundled binaries present in the test's output `assets/binaries/`. Ensure the module/test output includes them — if `BundledBinaryProvider` can't find them relative to the test base dir, point `RealBinaries()` at the repo `assets/binaries/` absolute path resolved from the test working directory, or copy them into the test output. Since binaries are git-ignored, run `build/fetch-binaries.ps1` before running integration tests.

- [ ] **Step 6: Verify — unit tests (no network) still green, then run the app**

Run:
```bash
dotnet build FFMedia.sln
dotnet test FFMedia.sln --filter "Category!=Integration"
```
Expected: builds; all unit tests green; integration tests skipped.

Then run the app and confirm the shell shows a "YouTube Downloader" entry in the nav pane and clicking it shows the downloader page:
```bash
MSYS_NO_PATHCONV=1 timeout 20 dotnet run --project src/FFMedia.App; echo "exit=$?"
```
Expected: launches, stays alive (exit 124 on timeout-kill), no Fatal entries in `%AppData%\FFMedia\logs`. (Full click-through + a real download is the human's acceptance check; optionally run the integration tests locally: `dotnet test FFMedia.sln --filter "Category=Integration"` after `build/fetch-binaries.ps1`.)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(app): wire tool navigation, add gated yt-dlp integration test"
```

---

### Task 8: Update SDD + progress log

**Files:**
- Modify: `SDD.md` (§5 module/test TFMs; §7 note M1 delivered; §17 M1 row; Changelog; Version→0.3)
- Modify: `CLAUDE.md` (Progress Log)

**Interfaces:**
- Consumes: everything from Tasks 1–7.
- Produces: SDD matching reality (Rule 1) + progress entry (Rule 2).

- [ ] **Step 1: Update `SDD.md` §5 — module/test target frameworks**

Note that tool modules containing WPF Views (e.g., `FFMedia.Tools.YouTubeDownloader`) target **`net9.0-windows` (UseWPF)**, and `FFMedia.Tests` targets **`net9.0-windows`** to unit-test ViewModels; `FFMedia.Core` remains `net9.0` and UI-free. Add `IToolPage` (Core, System.Type only) to the abstractions list.

- [ ] **Step 2: Update `SDD.md` §17 — mark M1 delivered; bump version/changelog**

Annotate the M1 row "✅ delivered (branch `feat/m1-vertical-slice`)". Bump header `Version:` to `0.3`, `Last updated:` to `2026-07-04`. Add a Changelog row:
```
| 2026-07-04 | 0.3 | M1 vertical slice: YouTube Downloader tool (probe + single-MP4 download w/ progress + cancel) via YoutubeDLSharp; module retargeted to net9.0-windows; IMediaProbe/IDownloadService seam (unit-tested VM) + trait-gated integration test; shell nav wiring (WPF-UI INavigationService/IPageService); added Result<T> and IToolPage to Core. |
```

- [ ] **Step 3: Append a `CLAUDE.md` progress-log entry (newest first)**

Add above the most recent entry:
```markdown
### 2026-07-04 — M1 Vertical Slice

- **Done:** YouTube Downloader tool end-to-end — paste URL → probe (`IMediaProbe`) → download single MP4 with live progress + cancel (`IDownloadService`) via YoutubeDLSharp; `DownloaderViewModel` (unit-tested with fakes); tool page + nav wiring so it appears in the shell; trait-gated yt-dlp integration test; `Result<T>` + `IToolPage` added to Core.
- **Changed:** `FFMedia.Tools.YouTubeDownloader` + `FFMedia.Tests` retargeted to `net9.0-windows`; CI excludes `Category=Integration`.
- **Next:** M2 — full format matrix (video containers + audio-only wav/mp3/m4a/opus/flac + quality/resolution).
```

- [ ] **Step 4: Verify build and commit**

```bash
dotnet build FFMedia.sln
git add SDD.md CLAUDE.md
git commit -m "docs: sync SDD to v0.3 and log M1 progress"
```

---

## Definition of Done (M1)

- `dotnet build FFMedia.sln` succeeds; `dotnet test FFMedia.sln --filter "Category!=Integration"` all green.
- App launches; the nav pane shows "YouTube Downloader"; its page probes a URL and downloads a single MP4 with live progress and a working cancel.
- yt-dlp/ffmpeg invoked via `IBinaryProvider` bundled paths (never PATH).
- `FFMedia.Core` still references no UI framework; `Result<T>` + `IToolPage` use only BCL.
- Trait-gated integration test exists and passes locally (after `fetch-binaries.ps1`); CI excludes it and stays green.
- SDD updated to v0.3; CLAUDE.md progress logged.
- Delivered as a single PR (`feat/m1-vertical-slice` → `main`).
