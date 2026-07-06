# M5 Foundation (PR 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the JSON persistence foundation and wire the first real app settings — default output folder, download concurrency, and dark/light theme — into a Settings screen and into runtime behavior.

**Architecture:** A generic `JsonStore<T>` in `FFMedia.Core` gives atomic, corruption-tolerant JSON persistence under `%AppData%\FFMedia`. `SettingsService` (Core, UI-agnostic) exposes an `AppSettings` snapshot + `Save` + `Changed`. `FFMedia.App` adds a `ThemeService` over WPF-UI's `ApplicationThemeManager`, a `SettingsPage`/`SettingsViewModel`, and a footer nav entry, and applies the persisted theme at startup. Settings flow into behavior: the downloader seeds its output folder from settings, and the download manager reads its concurrency cap from settings at construction.

**Tech Stack:** C# / .NET 9, WPF + WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection/Hosting, System.Text.Json, Microsoft.Extensions.Logging.Abstractions, xUnit.

This is **PR 1 of the two-PR M5 plan** (spec: `docs/superpowers/specs/2026-07-06-m5-experience-design.md`). Presets, history, and notifications are PR 2 and get their own plan after this lands.

## Global Constraints

- Branch: **`feat/m5-foundation`** (already created off `main`; the design spec is its first commit). Commit per task; **do not merge** — push and open a PR for the user to review (standing Rule 3).
- `FFMedia.Core` targets `net9.0`, is **UI-framework-free**, and has `TreatWarningsAsErrors=true` — Core code must be warning-clean.
- Nullable reference types **on**; **one public type per file**, filename matches the type (SDD §18).
- Tests use **plain xUnit `Assert`** (no FluentAssertions — it's paid). TDD is the default for Core logic.
- Data lives under `%AppData%\FFMedia\` (SDD §10): this PR adds `settings.json`.
- Dependencies point inward to Core; App provides WPF/WPF-UI implementations of Core abstractions.
- Run unit tests with `dotnet test --filter "Category!=Integration"` (integration tests are trait-gated and need real binaries).
- Keep [`SDD.md`](../../../SDD.md) and the CLAUDE.md Progress Log in sync in the final task (SDD → v0.7).

---

## File Structure

**Create (Core):**
- `src/FFMedia.Core/Persistence/JsonStore.cs` — generic atomic JSON load/save for one value at a fixed path.
- `src/FFMedia.Core/Settings/AppTheme.cs` — enum `System | Light | Dark`.
- `src/FFMedia.Core/Settings/AppSettings.cs` — record: `Version`, `DefaultOutputFolder`, `MaxConcurrency`, `Theme`.
- `src/FFMedia.Core/Settings/ISettingsService.cs` — `Current` + `Save` + `Changed`.
- `src/FFMedia.Core/Settings/SettingsService.cs` — `JsonStore`-backed implementation.

**Create (App):**
- `src/FFMedia.App/Services/ThemeService.cs` — maps `AppTheme` → WPF-UI theme.
- `src/FFMedia.App/ViewModels/SettingsViewModel.cs` — settings form state + Save/Browse commands.
- `src/FFMedia.App/Views/SettingsPage.xaml` (+ `.xaml.cs`) — the Settings screen.

**Create (Tests):**
- `src/FFMedia.Tests/Persistence/JsonStoreTests.cs`
- `src/FFMedia.Tests/Settings/AppSettingsTests.cs`
- `src/FFMedia.Tests/Settings/SettingsServiceTests.cs`

**Modify:**
- `src/FFMedia.Core/FFMedia.Core.csproj` — add `Microsoft.Extensions.Logging.Abstractions`.
- `src/FFMedia.Core/CoreServiceCollectionExtensions.cs` — add `dataDirectory` param; register `ISettingsService`.
- `src/FFMedia.Tests/CoreServiceCollectionExtensionsTests.cs` — pass `dataDirectory` at the two call sites.
- `src/FFMedia.App/App.xaml.cs` — pass data dir to `AddFFMediaCore`; register `ThemeService`/`SettingsViewModel`/`SettingsPage`; apply theme + register services at startup.
- `src/FFMedia.App/ViewModels/MainWindowViewModel.cs` — add `FooterMenuItems`; add `ToggleTheme` command.
- `src/FFMedia.App/MainWindow.xaml` — bind `FooterMenuItemsSource`; add title-bar theme toggle.
- `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs` — seed `OutputFolder` from `ISettingsService`.
- `src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs` — add a fake `ISettingsService`.
- `src/FFMedia.Tools.YouTubeDownloader/ServiceCollectionExtensions.cs` — construct `DownloadManager` with the settings concurrency cap.

---

## Task 1: `JsonStore<T>` — atomic, corruption-tolerant JSON persistence

**Files:**
- Modify: `src/FFMedia.Core/FFMedia.Core.csproj`
- Create: `src/FFMedia.Core/Persistence/JsonStore.cs`
- Test: `src/FFMedia.Tests/Persistence/JsonStoreTests.cs`

**Interfaces:**
- Produces: `FFMedia.Core.Persistence.JsonStore<T>` with ctor `(string filePath, ILogger logger)`, `T Load(Func<T> defaultFactory)`, `void Save(T value)`. On corrupt/unreadable file, `Load` logs a warning, renames the bad file to `<filePath>.bak` (overwriting any prior `.bak`), and returns `defaultFactory()`. `Save` writes to `<filePath>.tmp` then atomically moves it over `<filePath>`, creating the directory if needed. Enums serialize as strings; JSON is indented.

- [ ] **Step 1: Add the logging-abstractions package to Core**

Edit `src/FFMedia.Core/FFMedia.Core.csproj` — add to the existing `<ItemGroup>` that has `Microsoft.Extensions.DependencyInjection.Abstractions`:

```xml
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
```

- [ ] **Step 2: Write the failing tests**

Create `src/FFMedia.Tests/Persistence/JsonStoreTests.cs`:

```csharp
using System.IO;
using FFMedia.Core.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Persistence;

public class JsonStoreTests
{
    private sealed record Sample(int N, string Name);

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "data.json");

    private static JsonStore<Sample> Store(string path) =>
        new(path, NullLogger.Instance);

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = Store(TempFile());
        var value = store.Load(() => new Sample(42, "fallback"));
        Assert.Equal(new Sample(42, "fallback"), value);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = TempFile();
        var store = Store(path);
        store.Save(new Sample(7, "seven"));
        var reloaded = Store(path).Load(() => new Sample(0, "x"));
        Assert.Equal(new Sample(7, "seven"), reloaded);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var path = TempFile(); // parent dir does not exist yet
        Store(path).Save(new Sample(1, "a"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_CorruptFile_QuarantinesToBak_AndReturnsDefault()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json ");

        var value = Store(path).Load(() => new Sample(99, "default"));

        Assert.Equal(new Sample(99, "default"), value);
        Assert.True(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path)); // moved aside
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: FAIL — `JsonStore<>` does not exist (compile error).

- [ ] **Step 4: Implement `JsonStore<T>`**

Create `src/FFMedia.Core/Persistence/JsonStore.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FFMedia.Core.Persistence;

/// <summary>
/// Atomic JSON persistence for a single value at a fixed file path. <see cref="Save"/> writes to a
/// temp file then moves it into place; <see cref="Load"/> returns a caller-supplied default
/// (quarantining a corrupt file to "&lt;path&gt;.bak") rather than throwing.
/// </summary>
public sealed class JsonStore<T>
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ILogger _logger;

    public JsonStore(string filePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);
        _filePath = filePath;
        _logger = logger;
    }

    public T Load(Func<T> defaultFactory)
    {
        ArgumentNullException.ThrowIfNull(defaultFactory);
        if (!File.Exists(_filePath))
        {
            return defaultFactory();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<T>(json, Options) ?? defaultFactory();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Corrupt or unreadable store at {Path}; quarantining and using default.", _filePath);
            TryQuarantine();
            return defaultFactory();
        }
    }

    public void Save(T value)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, _filePath, overwrite: true);
    }

    private void TryQuarantine()
    {
        try
        {
            File.Move(_filePath, _filePath + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to quarantine corrupt store at {Path}.", _filePath);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: PASS (all four `JsonStoreTests` green, existing tests still green).

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Core/FFMedia.Core.csproj src/FFMedia.Core/Persistence/JsonStore.cs src/FFMedia.Tests/Persistence/JsonStoreTests.cs
git commit -m "feat(core): add atomic corruption-tolerant JsonStore<T>"
```

---

## Task 2: `AppTheme` + `AppSettings`

**Files:**
- Create: `src/FFMedia.Core/Settings/AppTheme.cs`
- Create: `src/FFMedia.Core/Settings/AppSettings.cs`
- Test: `src/FFMedia.Tests/Settings/AppSettingsTests.cs`

**Interfaces:**
- Produces: `FFMedia.Core.Settings.AppTheme { System, Light, Dark }`. `FFMedia.Core.Settings.AppSettings` — a record with init-only `int Version` (=1), `string DefaultOutputFolder` (=`%MyVideos%\FFMedia`), `int MaxConcurrency` (=3), `AppTheme Theme` (=`System`); static `AppSettings Default => new()`. Round-trips through `JsonStore<AppSettings>` with `Theme` as a string.

- [ ] **Step 1: Write the failing tests**

Create `src/FFMedia.Tests/Settings/AppSettingsTests.cs`:

```csharp
using System.IO;
using FFMedia.Core.Persistence;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var d = AppSettings.Default;
        Assert.Equal(1, d.Version);
        Assert.Equal(3, d.MaxConcurrency);
        Assert.Equal(AppTheme.System, d.Theme);
        Assert.EndsWith(Path.Combine("Videos", "FFMedia"), d.DefaultOutputFolder);
    }

    [Fact]
    public void RoundTripsThroughJsonStore_WithThemeAsString()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var original = AppSettings.Default with { MaxConcurrency = 5, Theme = AppTheme.Dark, DefaultOutputFolder = @"C:\videos" };

        store.Save(original);

        Assert.Contains("\"Dark\"", File.ReadAllText(path)); // enum persisted as string
        Assert.Equal(original, store.Load(() => AppSettings.Default));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: FAIL — `AppSettings`/`AppTheme` do not exist.

- [ ] **Step 3: Implement the enum and record**

Create `src/FFMedia.Core/Settings/AppTheme.cs`:

```csharp
namespace FFMedia.Core.Settings;

/// <summary>The user's theme preference. <see cref="System"/> follows the OS setting.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}
```

Create `src/FFMedia.Core/Settings/AppSettings.cs`:

```csharp
using System.IO;

namespace FFMedia.Core.Settings;

/// <summary>Persisted application settings (JSON at %AppData%\FFMedia\settings.json). The
/// <see cref="Version"/> field supports forward migration.</summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string DefaultOutputFolder { get; init; } = DefaultFolder();
    public int MaxConcurrency { get; init; } = 3;
    public AppTheme Theme { get; init; } = AppTheme.System;

    public static AppSettings Default => new();

    private static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFMedia");
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Core/Settings/AppTheme.cs src/FFMedia.Core/Settings/AppSettings.cs src/FFMedia.Tests/Settings/AppSettingsTests.cs
git commit -m "feat(core): add AppSettings + AppTheme model"
```

---

## Task 3: `ISettingsService` + `SettingsService`

**Files:**
- Create: `src/FFMedia.Core/Settings/ISettingsService.cs`
- Create: `src/FFMedia.Core/Settings/SettingsService.cs`
- Test: `src/FFMedia.Tests/Settings/SettingsServiceTests.cs`

**Interfaces:**
- Consumes: `JsonStore<AppSettings>`, `AppSettings`.
- Produces: `FFMedia.Core.Settings.ISettingsService` — `AppSettings Current { get; }`, `void Save(AppSettings settings)`, `event EventHandler<AppSettings>? Changed`. `SettingsService` ctor `(string dataDirectory, ILogger<SettingsService> logger)` loads `settings.json` from `dataDirectory` at construction (default when missing); `Save` persists, updates `Current`, and raises `Changed`.

- [ ] **Step 1: Write the failing tests**

Create `src/FFMedia.Tests/Settings/SettingsServiceTests.cs`:

```csharp
using System.IO;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class SettingsServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Current_WhenNoFile_IsDefault()
    {
        var svc = new SettingsService(TempDir(), NullLogger<SettingsService>.Instance);
        Assert.Equal(AppSettings.Default, svc.Current);
    }

    [Fact]
    public void Save_UpdatesCurrent_RaisesChanged_AndPersists()
    {
        var dir = TempDir();
        var svc = new SettingsService(dir, NullLogger<SettingsService>.Instance);
        AppSettings? observed = null;
        svc.Changed += (_, s) => observed = s;

        var updated = AppSettings.Default with { MaxConcurrency = 6, Theme = AppTheme.Light };
        svc.Save(updated);

        Assert.Equal(updated, svc.Current);
        Assert.Equal(updated, observed);
        // A fresh service over the same directory reloads the saved value.
        Assert.Equal(updated, new SettingsService(dir, NullLogger<SettingsService>.Instance).Current);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: FAIL — `ISettingsService`/`SettingsService` do not exist.

- [ ] **Step 3: Implement the interface and service**

Create `src/FFMedia.Core/Settings/ISettingsService.cs`:

```csharp
namespace FFMedia.Core.Settings;

/// <summary>Loads and persists <see cref="AppSettings"/>; notifies listeners on change.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    void Save(AppSettings settings);

    event EventHandler<AppSettings>? Changed;
}
```

Create `src/FFMedia.Core/Settings/SettingsService.cs`:

```csharp
using System.IO;
using FFMedia.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace FFMedia.Core.Settings;

/// <summary>JSON-file-backed <see cref="ISettingsService"/> (settings.json under the data directory).</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly JsonStore<AppSettings> _store;

    public SettingsService(string dataDirectory, ILogger<SettingsService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = new JsonStore<AppSettings>(Path.Combine(dataDirectory, "settings.json"), logger);
        Current = _store.Load(() => AppSettings.Default);
    }

    public AppSettings Current { get; private set; }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.Save(settings);
        Current = settings;
        Changed?.Invoke(this, settings);
    }

    public event EventHandler<AppSettings>? Changed;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Core/Settings/ISettingsService.cs src/FFMedia.Core/Settings/SettingsService.cs src/FFMedia.Tests/Settings/SettingsServiceTests.cs
git commit -m "feat(core): add ISettingsService/SettingsService over JsonStore"
```

---

## Task 4: Register `ISettingsService` in `AddFFMediaCore`

**Files:**
- Modify: `src/FFMedia.Core/CoreServiceCollectionExtensions.cs`
- Modify: `src/FFMedia.Tests/CoreServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: `SettingsService`, `ISettingsService`.
- Produces: `AddFFMediaCore(this IServiceCollection, string binariesDirectory, string dataDirectory)` now also registers `ISettingsService` as a singleton, resolving an `ILogger<SettingsService>` when logging is present and falling back to `NullLogger<SettingsService>.Instance` otherwise.

- [ ] **Step 1: Update the registration test (failing) to the new signature**

In `src/FFMedia.Tests/CoreServiceCollectionExtensionsTests.cs`, add a `dataDirectory` argument to both `AddFFMediaCore` calls and add one new test. Replace the file body's two `.AddFFMediaCore(binariesDirectory: ...)` calls and append a settings test:

Change the first test's call to:

```csharp
            .AddFFMediaCore(binariesDirectory: Path.GetTempPath(), dataDirectory: Path.GetTempPath())
```

Change the second test's call to:

```csharp
            .AddFFMediaCore(binariesDirectory: dir, dataDirectory: Path.GetTempPath())
```

Add this test method to the class:

```csharp
    [Fact]
    public void AddFFMediaCore_ResolvesSettingsService()
    {
        var provider = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: Path.GetTempPath(), dataDirectory: Path.GetTempPath())
            .BuildServiceProvider();

        var settings = provider.GetRequiredService<FFMedia.Core.Settings.ISettingsService>();

        Assert.Equal(3, settings.Current.MaxConcurrency); // default when no file exists
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: FAIL — `AddFFMediaCore` has no `dataDirectory` parameter (compile error).

- [ ] **Step 3: Update `AddFFMediaCore`**

Replace `src/FFMedia.Core/CoreServiceCollectionExtensions.cs` with:

```csharp
using FFMedia.Core.Binaries;
using FFMedia.Core.Settings;
using FFMedia.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFMedia.Core;

/// <summary>Registers UI-agnostic FFMedia core services.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <param name="binariesDirectory">Directory holding bundled yt-dlp.exe / ffmpeg.exe.</param>
    /// <param name="dataDirectory">Directory for persisted JSON (settings/presets/history), e.g. %AppData%\FFMedia.</param>
    public static IServiceCollection AddFFMediaCore(
        this IServiceCollection services, string binariesDirectory, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binariesDirectory);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IBinaryProvider>(_ => new BundledBinaryProvider(binariesDirectory));
        services.AddSingleton<ISettingsService>(sp => new SettingsService(
            dataDirectory,
            sp.GetService<ILogger<SettingsService>>() ?? NullLogger<SettingsService>.Instance));
        return services;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: PASS (all Core + settings tests green).

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Core/CoreServiceCollectionExtensions.cs src/FFMedia.Tests/CoreServiceCollectionExtensionsTests.cs
git commit -m "feat(core): register ISettingsService in AddFFMediaCore (add dataDirectory)"
```

---

## Task 5: `ThemeService` + apply persisted theme at startup

> App-layer WPF glue over WPF-UI. No unit tests (Tests does not reference the WinExe App project; UI is thin by design per SDD §14). Verified by build + manual run.

**Files:**
- Create: `src/FFMedia.App/Services/ThemeService.cs`
- Modify: `src/FFMedia.App/App.xaml.cs`

**Interfaces:**
- Consumes: `AppTheme`, `ISettingsService`.
- Produces: `FFMedia.App.Services.ThemeService` with `void Apply(AppTheme theme)` mapping `Light`/`Dark`/`System` onto WPF-UI's `ApplicationThemeManager`. Registered as a singleton; `App.OnStartup` calls `Apply(settings.Current.Theme)` before showing the window.

- [ ] **Step 1: Implement `ThemeService`**

Create `src/FFMedia.App/Services/ThemeService.cs`:

```csharp
using FFMedia.Core.Settings;
using Wpf.Ui.Appearance;

namespace FFMedia.App.Services;

/// <summary>Applies the user's <see cref="AppTheme"/> preference via WPF-UI's theme manager.</summary>
public sealed class ThemeService
{
    public void Apply(AppTheme theme)
    {
        switch (theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }
}
```

- [ ] **Step 2: Wire data directory + ThemeService into the host, apply theme at startup**

In `src/FFMedia.App/App.xaml.cs`:

Change the `AddFFMediaCore` call inside `ConfigureServices` from:

```csharp
                services.AddFFMediaCore(binariesDir);
```

to:

```csharp
                services.AddFFMediaCore(binariesDir, appData);
```

Register `ThemeService` — add this line in the same `ConfigureServices` block (e.g. right after `services.AddYouTubeDownloader();`):

```csharp
                services.AddSingleton<FFMedia.App.Services.ThemeService>();
```

In `OnStartup`, apply the persisted theme just before showing the window. Replace:

```csharp
        await _host.StartAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
```

with:

```csharp
        await _host.StartAsync();

        var settings = _host.Services.GetRequiredService<FFMedia.Core.Settings.ISettingsService>();
        _host.Services.GetRequiredService<FFMedia.App.Services.ThemeService>().Apply(settings.Current.Theme);

        _host.Services.GetRequiredService<MainWindow>().Show();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/FFMedia.App`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/FFMedia.App/Services/ThemeService.cs src/FFMedia.App/App.xaml.cs
git commit -m "feat(app): apply persisted theme at startup via ThemeService"
```

---

## Task 6: Settings screen + footer nav + title-bar theme toggle

> App-layer WPF glue. Verified by build + manual run. The persistence logic it drives is already unit-tested in `SettingsService`.

**Files:**
- Create: `src/FFMedia.App/ViewModels/SettingsViewModel.cs`
- Create: `src/FFMedia.App/Views/SettingsPage.xaml`
- Create: `src/FFMedia.App/Views/SettingsPage.xaml.cs`
- Modify: `src/FFMedia.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/FFMedia.App/MainWindow.xaml`
- Modify: `src/FFMedia.App/App.xaml.cs`

**Interfaces:**
- Consumes: `ISettingsService`, `ThemeService`, `AppSettings`, `AppTheme`.
- Produces: `SettingsViewModel` (observable `DefaultOutputFolder`, `MaxConcurrency`, `SelectedTheme`, `StatusMessage`; `Themes` list; `SaveCommand`, `BrowseFolderCommand`). `MainWindowViewModel.FooterMenuItems` (a `NavigationViewItem` → `SettingsPage`) and `ToggleThemeCommand`. `SettingsPage(SettingsViewModel vm)`.

- [ ] **Step 1: Implement `SettingsViewModel`**

Create `src/FFMedia.App/ViewModels/SettingsViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.App.Services;
using FFMedia.Core.Settings;
using Microsoft.Win32;

namespace FFMedia.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;

    public SettingsViewModel(ISettingsService settings, ThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        _settings = settings;
        _theme = theme;

        var current = settings.Current;
        _defaultOutputFolder = current.DefaultOutputFolder;
        _maxConcurrency = current.MaxConcurrency;
        _selectedTheme = current.Theme;
    }

    [ObservableProperty] private string _defaultOutputFolder;
    [ObservableProperty] private int _maxConcurrency;
    [ObservableProperty] private AppTheme _selectedTheme;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { InitialDirectory = DefaultOutputFolder };
        if (dialog.ShowDialog() == true)
        {
            DefaultOutputFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var updated = _settings.Current with
        {
            DefaultOutputFolder = DefaultOutputFolder,
            MaxConcurrency = Math.Max(1, MaxConcurrency),
            Theme = SelectedTheme,
        };
        _settings.Save(updated);
        _theme.Apply(SelectedTheme);
        StatusMessage = "Settings saved. Concurrency changes take effect on next launch.";
    }
}
```

- [ ] **Step 2: Create the Settings page**

Create `src/FFMedia.App/Views/SettingsPage.xaml`:

```xml
<Page x:Class="FFMedia.App.Views.SettingsPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
    <StackPanel Margin="24" MaxWidth="640" HorizontalAlignment="Left">
        <TextBlock Text="Settings" FontSize="24" FontWeight="SemiBold" Margin="0,0,0,16" />

        <TextBlock Text="Default output folder" Margin="0,8,0,4" />
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <ui:TextBox Grid.Column="0" Text="{Binding DefaultOutputFolder, UpdateSourceTrigger=PropertyChanged}" />
            <ui:Button Grid.Column="1" Content="Browse…" Margin="8,0,0,0" Command="{Binding BrowseFolderCommand}" />
        </Grid>

        <TextBlock Text="Max concurrent downloads" Margin="0,16,0,4" />
        <ui:NumberBox Value="{Binding MaxConcurrency, Mode=TwoWay}" Minimum="1" Maximum="10" MaxWidth="160" HorizontalAlignment="Left" />

        <TextBlock Text="Theme" Margin="0,16,0,4" />
        <ComboBox ItemsSource="{Binding Themes}" SelectedItem="{Binding SelectedTheme, Mode=TwoWay}" MaxWidth="200" HorizontalAlignment="Left" />

        <ui:Button Content="Save" Appearance="Primary" Margin="0,20,0,0" HorizontalAlignment="Left" Command="{Binding SaveCommand}" />
        <TextBlock Text="{Binding StatusMessage}" Foreground="{DynamicResource TextFillColorSecondaryBrush}" Margin="0,10,0,0" />
    </StackPanel>
</Page>
```

Create `src/FFMedia.App/Views/SettingsPage.xaml.cs`:

```csharp
using System.Windows.Controls;
using FFMedia.App.ViewModels;

namespace FFMedia.App.Views;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Add footer nav item + theme toggle to `MainWindowViewModel`**

Replace `src/FFMedia.App/ViewModels/MainWindowViewModel.cs` with:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.App.Services;
using FFMedia.App.Views;
using FFMedia.Core.Settings;
using FFMedia.Core.Tools;
using Wpf.Ui.Controls;

namespace FFMedia.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;

    public MainWindowViewModel(
        IToolRegistry registry,
        IEnumerable<IToolPage> pages,
        ISettingsService settings,
        ThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        _settings = settings;
        _theme = theme;

        var pageById = pages.ToDictionary(p => p.ToolId, p => p.PageType);
        var items = new ObservableCollection<object>();
        foreach (var tool in registry.Tools)
        {
            if (!pageById.TryGetValue(tool.Id, out var pageType))
            {
                continue;
            }

            items.Add(new NavigationViewItem
            {
                Content = tool.DisplayName,
                Icon = new FontIcon { Glyph = tool.IconGlyph },
                TargetPageType = pageType,
            });
        }

        MenuItems = items;

        FooterMenuItems = new ObservableCollection<object>
        {
            new NavigationViewItem
            {
                Content = "Settings",
                Icon = new FontIcon { Glyph = "" }, // Segoe Fluent settings gear
                TargetPageType = typeof(SettingsPage),
            },
        };
    }

    /// <summary>Navigation-pane entries, one per registered tool with a mapped page.</summary>
    [ObservableProperty]
    private ObservableCollection<object> _menuItems = new();

    /// <summary>Footer navigation entries (app-level pages, not tools).</summary>
    [ObservableProperty]
    private ObservableCollection<object> _footerMenuItems = new();

    /// <summary>Quick title-bar toggle between Light and Dark; persists the choice.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _settings.Current.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _theme.Apply(next);
        _settings.Save(_settings.Current with { Theme = next });
    }
}
```

- [ ] **Step 4: Bind footer + add the toggle button in `MainWindow.xaml`**

In `src/FFMedia.App/MainWindow.xaml`:

Add a theme-toggle button to the title bar by giving `<ui:TitleBar>` a trailing content. Replace:

```xml
        <ui:TitleBar Grid.Row="0" Title="FFMedia" />
```

with:

```xml
        <ui:TitleBar Grid.Row="0" Title="FFMedia">
            <ui:TitleBar.Header>
                <ui:Button Icon="{ui:SymbolIcon WeatherMoon24}"
                           Appearance="Transparent"
                           HorizontalAlignment="Right" Margin="0,0,8,0"
                           ToolTip="Toggle light/dark theme"
                           Command="{Binding ToggleThemeCommand}" />
            </ui:TitleBar.Header>
        </ui:TitleBar>
```

Add the footer binding to the `NavigationView`. Replace:

```xml
        <ui:NavigationView Grid.Row="1" x:Name="RootNavigation"
                           MenuItemsSource="{Binding MenuItems}"
                           IsBackButtonVisible="Collapsed"
                           PaneDisplayMode="Left" />
```

with:

```xml
        <ui:NavigationView Grid.Row="1" x:Name="RootNavigation"
                           MenuItemsSource="{Binding MenuItems}"
                           FooterMenuItemsSource="{Binding FooterMenuItems}"
                           IsBackButtonVisible="Collapsed"
                           PaneDisplayMode="Left" />
```

- [ ] **Step 5: Register the settings VM + page in DI**

In `src/FFMedia.App/App.xaml.cs` `ConfigureServices`, add (after the `ThemeService` registration from Task 5):

```csharp
                services.AddTransient<FFMedia.App.ViewModels.SettingsViewModel>();
                services.AddTransient<FFMedia.App.Views.SettingsPage>();
```

- [ ] **Step 6: Build and run to verify**

Run: `dotnet build src/FFMedia.App`
Expected: Build succeeded, 0 errors.

Manual check (run the app): `dotnet run --project src/FFMedia.App`
- A **Settings** item appears at the bottom of the nav pane; clicking it shows the form.
- Changing **Theme** to Light/Dark and clicking **Save** repaints the app immediately; the status line confirms the save.
- Restarting the app preserves the chosen theme and folder (proves persistence).
- The title-bar moon button flips light/dark.

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.App/ViewModels/SettingsViewModel.cs src/FFMedia.App/Views/SettingsPage.xaml src/FFMedia.App/Views/SettingsPage.xaml.cs src/FFMedia.App/ViewModels/MainWindowViewModel.cs src/FFMedia.App/MainWindow.xaml src/FFMedia.App/App.xaml.cs
git commit -m "feat(app): add Settings screen, footer nav, and title-bar theme toggle"
```

---

## Task 7: Seed downloader output folder from settings

**Files:**
- Modify: `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`
- Modify: `src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs`

**Interfaces:**
- Consumes: `ISettingsService`.
- Produces: `DownloaderViewModel(IPlaylistProbe, IDownloadManager, ISettingsService)` — `OutputFolder` initialized from `settings.Current.DefaultOutputFolder`.

- [ ] **Step 1: Update the test fixture (failing) to inject settings and assert seeding**

In `src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs`:

Add a fake settings service inside the test class (next to `FakeManager`):

```csharp
    private sealed class FakeSettings : FFMedia.Core.Settings.ISettingsService
    {
        public FFMedia.Core.Settings.AppSettings Current { get; private set; } =
            FFMedia.Core.Settings.AppSettings.Default with { DefaultOutputFolder = @"C:\seeded" };
        public void Save(FFMedia.Core.Settings.AppSettings settings) => Current = settings;
        public event EventHandler<FFMedia.Core.Settings.AppSettings>? Changed;
    }
```

Change the `Vm` helper from:

```csharp
    private static DownloaderViewModel Vm(FakePlaylistProbe probe, FakeManager mgr) => new(probe, mgr);
```

to:

```csharp
    private static DownloaderViewModel Vm(FakePlaylistProbe probe, FakeManager mgr) =>
        new(probe, mgr, new FakeSettings());
```

Add a new test asserting the seed:

```csharp
    [Fact]
    public void OutputFolder_SeededFromSettings()
    {
        var vm = Vm(new FakePlaylistProbe(), new FakeManager());
        Assert.Equal(@"C:\seeded", vm.OutputFolder);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: FAIL — `DownloaderViewModel` has no 3-arg constructor (compile error).

- [ ] **Step 3: Inject settings into `DownloaderViewModel`**

In `src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs`, add the using and update the constructor. Add near the top:

```csharp
using FFMedia.Core.Settings;
```

Replace the fields + constructor:

```csharp
    private readonly IPlaylistProbe _playlistProbe;
    private readonly IDownloadManager _manager;

    public DownloaderViewModel(IPlaylistProbe playlistProbe, IDownloadManager manager)
    {
        ArgumentNullException.ThrowIfNull(playlistProbe);
        ArgumentNullException.ThrowIfNull(manager);
        _playlistProbe = playlistProbe;
        _manager = manager;
        OutputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFMedia");
    }
```

with:

```csharp
    private readonly IPlaylistProbe _playlistProbe;
    private readonly IDownloadManager _manager;

    public DownloaderViewModel(IPlaylistProbe playlistProbe, IDownloadManager manager, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(playlistProbe);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        _playlistProbe = playlistProbe;
        _manager = manager;
        OutputFolder = settings.Current.DefaultOutputFolder;
    }
```

(The `System.IO` using may now be unused; remove it only if the compiler warns.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "Category!=Integration" src/FFMedia.Tests`
Expected: PASS (new seeding test green; all existing DownloaderViewModel tests still green via the updated `Vm` helper).

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Tools.YouTubeDownloader/ViewModels/DownloaderViewModel.cs src/FFMedia.Tests/YouTubeDownloader/DownloaderViewModelTests.cs
git commit -m "feat(youtube): seed downloader output folder from settings"
```

---

## Task 8: Read download concurrency from settings

**Files:**
- Modify: `src/FFMedia.Tools.YouTubeDownloader/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `ISettingsService` (from Core, registered by `AddFFMediaCore` before `AddYouTubeDownloader`), `IDownloadService`, `RetryPolicy`.
- Produces: `IDownloadManager` constructed with `maxConcurrency = max(1, settings.Current.MaxConcurrency)`.

- [ ] **Step 1: Construct `DownloadManager` from settings**

In `src/FFMedia.Tools.YouTubeDownloader/ServiceCollectionExtensions.cs`, add the using:

```csharp
using FFMedia.Core.Settings;
```

Replace:

```csharp
        services.AddSingleton<IDownloadManager, DownloadManager>();
```

with:

```csharp
        services.AddSingleton<IDownloadManager>(sp => new DownloadManager(
            sp.GetRequiredService<IDownloadService>(),
            sp.GetRequiredService<RetryPolicy>(),
            Math.Max(1, sp.GetRequiredService<ISettingsService>().Current.MaxConcurrency)));
```

Ensure `using Microsoft.Extensions.DependencyInjection;` is present (it is — `GetRequiredService`).

- [ ] **Step 2: Build the whole solution to verify wiring**

Run: `dotnet build`
Expected: Build succeeded, 0 errors (App composition still resolves `IDownloadManager`, now honoring the settings cap).

- [ ] **Step 3: Commit**

```bash
git add src/FFMedia.Tools.YouTubeDownloader/ServiceCollectionExtensions.cs
git commit -m "feat(youtube): read download concurrency cap from settings"
```

---

## Task 9: Docs sync, full verification, push, open PR

**Files:**
- Modify: `SDD.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Sync the SDD to v0.7**

In `SDD.md`:
- Bump the header: `**Version:** 0.7` and `**Last updated:** 2026-07-06`.
- §6 services table: note `ISettingsService` is now realized (JSON-backed, `%AppData%\FFMedia\settings.json`); `IPresetService`/`IHistoryService`/`INotificationService` remain planned for M5 PR 2.
- §10: note `settings.json` now exists with a `Version` field (via `JsonStore<T>`).
- §12: replace the "user-configurable is deferred to M5" note — concurrency is now read from `ISettingsService` at `DownloadManager` construction (applied at launch).
- §13: note the Settings screen (default folder, concurrency, theme) and title-bar theme toggle now exist.
- §17: mark M5 as **in progress** — PR 1 delivers settings persistence + theming foundation.
- §19: mark the "History storage: JSON vs SQLite" question resolved to **JSON** (decided in the M5 spec).
- Add a Changelog row:

```markdown
| 2026-07-06 | 0.7 | M5 foundation (PR 1): generic `JsonStore<T>` (atomic write, corrupt-file quarantine) + `AppSettings`/`ISettingsService` (JSON at %AppData%\FFMedia\settings.json). `AddFFMediaCore` gains a `dataDirectory` param and registers `ISettingsService`. App gains a `ThemeService` (dark/light/system via WPF-UI), a Settings screen (default folder, max concurrency, theme) as a footer nav item, a title-bar theme toggle, and applies the persisted theme at startup. Settings wired into behavior: downloader output folder seeded from settings; `DownloadManager` concurrency cap read from settings. §6/§10/§12/§13/§17/§19 updated. |
```

- [ ] **Step 2: Add a Progress Log entry to CLAUDE.md**

At the top of the `## 📓 Progress Log` list in `CLAUDE.md`, insert:

```markdown
### 2026-07-06 — M5 Experience (PR 1: foundation)

- **Done:** Persistence foundation + settings + theming. `JsonStore<T>` (Core) does
  atomic temp-file writes and quarantines a corrupt file to `.bak` before returning a
  default. `AppSettings` (`Version`/`DefaultOutputFolder`/`MaxConcurrency`/`Theme`) +
  `ISettingsService`/`SettingsService` persist to `%AppData%\FFMedia\settings.json`;
  `AddFFMediaCore` gained a `dataDirectory` param and registers the service. App gained
  `ThemeService` (light/dark/system via WPF-UI `ApplicationThemeManager`), a **Settings**
  screen (footer nav) with folder/concurrency/theme, a title-bar theme toggle, and
  startup theme application. Wired into behavior: downloader output folder seeded from
  settings; `DownloadManager` concurrency cap read from settings at construction. SDD → v0.7.
- **Decisions:** history stored as JSON (resolves §19); notifications in-app only
  (Windows toast deferred to M6); concurrency applied at launch (live re-tuning deferred);
  App-layer VMs verified by build + manual run (Tests doesn't reference the WinExe; UI is
  thin per §14). Presets/history/notifications land in PR 2 (`feat/m5-presets-history`).
- **Next:** M5 PR 2 — presets (inline), history + screen, in-app notifications, and the
  `DownloadManager` completion hook.
```

- [ ] **Step 3: Full build + unit test run**

Run: `dotnet build`
Expected: Build succeeded, 0 errors/warnings.

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS — all unit tests green (existing suite + new JsonStore/AppSettings/SettingsService/registration/seeding tests).

- [ ] **Step 4: Commit docs**

```bash
git add SDD.md CLAUDE.md
git commit -m "docs: sync SDD to v0.7 and log M5 PR1 progress"
```

- [ ] **Step 5: Push and open the PR**

```bash
git push -u origin feat/m5-foundation
```

Then open a PR against `main` for the user to review (via `gh pr create` if available, otherwise provide the compare URL). Title: `M5 PR 1 — settings persistence + theming foundation`. Body: summarize the JsonStore/AppSettings/ISettingsService additions, the Settings screen + theming, and the folder/concurrency wiring; note tests pass and that PR 2 (presets/history/notifications) follows. **Do not merge** — the user reviews and merges (standing Rule 3).

---

## Self-Review

**Spec coverage (against `2026-07-06-m5-experience-design.md`, PR 1 scope in §9):**
- `JsonStore<T>` (§2.1) → Task 1. ✅
- `AppSettings`/`AppTheme`/`ISettingsService`+impl (§2.1, §4) → Tasks 2–4. ✅
- `ThemeService` + title-bar toggle + startup apply (§2.2, §3) → Tasks 5–6. ✅
- Settings screen + footer nav (§3, §4) → Task 6. ✅
- Output folder seeded from settings (§4) → Task 7. ✅
- `DownloadManager` concurrency from settings (§4, resolves SDD §19 concurrency) → Task 8. ✅
- SDD → v0.7, progress log (§9) → Task 9. ✅
- **Deliberate deviation:** spec §7 lists a headless `SettingsViewModel` test; the App project is a WinExe not referenced by `FFMedia.Tests`, and the existing codebase leaves App VMs (`MainWindowViewModel`) untested. The *logic* (`SettingsService`) is fully unit-tested; the VM is thin glue verified by manual run (SDD §14: "UI is thin by design"). Noted, not a gap.
- `System` theme live OS-change reaction (spec §8.3) — deferred as the spec allows; `ApplySystemTheme()` resolves against the OS at apply-time (startup + save).

**Placeholder scan:** none — every code step contains complete code; no TBD/TODO.

**Type consistency:** `AddFFMediaCore(binariesDirectory, dataDirectory)` used consistently (Tasks 4, 5, and both test call sites). `ISettingsService` members (`Current`/`Save`/`Changed`) match across service, fakes, and consumers. `ThemeService.Apply(AppTheme)` signature consistent (Tasks 5, 6). `DownloaderViewModel` 3-arg ctor consistent (Tasks 7 impl + test helper). `FooterMenuItems`/`ToggleThemeCommand` names match XAML bindings (Task 6).
