# M0 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the FFMedia solution skeleton — a modular WPF-UI app shell with DI/Generic Host, Serilog logging, the `ITool` module-discovery seam, an `IBinaryProvider` for bundled yt-dlp/ffmpeg, a binary-fetch script, and CI — so later milestones drop tools into a working chassis.

**Architecture:** An app shell (`FFMedia.App`, WPF + WPF-UI) composes services via `Microsoft.Extensions.Hosting` and renders a `NavigationView` whose entries come from a UI-agnostic `IToolRegistry` in `FFMedia.Core`. All testable logic (tool registry, binary resolution, DI wiring) lives in `FFMedia.Core` with zero UI dependencies. M0 registers no tools yet — the shell shows a welcome page — proving the seam without any feature code.

**Tech Stack:** C# / .NET 9 (`net9.0-windows` for UI, `net9.0` for Core/Tests) · WPF + WPF-UI · CommunityToolkit.Mvvm · Microsoft.Extensions.Hosting (DI) · Serilog · xUnit · GitHub Actions · PowerShell (binary fetch).

## Global Constraints

- Target framework: **`net9.0-windows`** for `FFMedia.App`; **`net9.0`** for `FFMedia.Core`, `FFMedia.Media`, `FFMedia.Tools.*`, and `FFMedia.Tests`.
- **`FFMedia.Core` references NO UI framework** (no WPF, no WPF-UI). Dependencies point inward toward Core.
- **`Nullable` enabled** everywhere; **`TreatWarningsAsErrors` = true** in `FFMedia.Core`.
- **One public type per file**; file name matches the type.
- ViewModels use **CommunityToolkit.Mvvm** source generators; no logic in code-behind.
- **async/await** for all I/O and process work; never block on `.Result`/`.Wait()`.
- Bundled binaries live in **`assets/binaries/`** (git-ignored), resolved via `IBinaryProvider` — never assume system PATH.
- **No telemetry**; all data stays local.
- Tests use **plain xUnit `Assert`** (no FluentAssertions — v8 is paid). Assertion-library choice deferred; noted in SDD.
- Workflow: this whole plan executes on ONE branch off `main` (`feat/m0-foundation`) and is delivered as a single **PR for review** (CLAUDE.md Rule 3). Keep **`SDD.md`** current (Rule 1) and append a **progress-log** entry (Rule 2) — handled in Task 8.

---

## File Structure

```
ff-media/
├─ FFMedia.sln
├─ .github/workflows/ci.yml            (Task 7)
├─ build/fetch-binaries.ps1            (Task 6)
├─ assets/binaries/.gitkeep           (Task 1)
└─ src/
   ├─ FFMedia.Core/
   │  ├─ FFMedia.Core.csproj           (Task 1)
   │  ├─ Tools/ITool.cs                (Task 2)
   │  ├─ Tools/IToolRegistry.cs        (Task 2)
   │  ├─ Tools/ToolRegistry.cs         (Task 2)
   │  ├─ Binaries/ExternalBinary.cs    (Task 3)
   │  ├─ Binaries/IBinaryProvider.cs   (Task 3)
   │  ├─ Binaries/BundledBinaryProvider.cs (Task 3)
   │  └─ CoreServiceCollectionExtensions.cs (Task 4)
   ├─ FFMedia.Media/FFMedia.Media.csproj   (Task 1, placeholder)
   ├─ FFMedia.Tools.YouTubeDownloader/...csproj (Task 1, placeholder)
   ├─ FFMedia.App/
   │  ├─ FFMedia.App.csproj            (Task 1)
   │  ├─ App.xaml + App.xaml.cs        (Task 1, then Task 5)
   │  ├─ MainWindow.xaml + .cs         (Task 5)
   │  ├─ ViewModels/MainWindowViewModel.cs (Task 5)
   │  └─ Views/WelcomePage.xaml + .cs  (Task 5)
   └─ FFMedia.Tests/
      ├─ FFMedia.Tests.csproj          (Task 1)
      ├─ Tools/ToolRegistryTests.cs    (Task 2)
      ├─ Binaries/BundledBinaryProviderTests.cs (Task 3)
      └─ CoreServiceCollectionExtensionsTests.cs (Task 4)
```

---

### Task 1: Solution & project skeleton

**Files:**
- Create: `FFMedia.sln`
- Create: `src/FFMedia.Core/FFMedia.Core.csproj`
- Create: `src/FFMedia.Media/FFMedia.Media.csproj`
- Create: `src/FFMedia.Tools.YouTubeDownloader/FFMedia.Tools.YouTubeDownloader.csproj`
- Create: `src/FFMedia.App/FFMedia.App.csproj`, `src/FFMedia.App/App.xaml`, `src/FFMedia.App/App.xaml.cs`
- Create: `src/FFMedia.Tests/FFMedia.Tests.csproj`
- Create: `assets/binaries/.gitkeep`

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable solution with correct project references. All later tasks add files into these projects.

- [ ] **Step 1: Create the solution and project directories via the SDK**

Run from repo root:
```bash
dotnet new sln -n FFMedia
dotnet new classlib -n FFMedia.Core -o src/FFMedia.Core -f net9.0
dotnet new classlib -n FFMedia.Media -o src/FFMedia.Media -f net9.0
dotnet new classlib -n FFMedia.Tools.YouTubeDownloader -o src/FFMedia.Tools.YouTubeDownloader -f net9.0
dotnet new wpf -n FFMedia.App -o src/FFMedia.App -f net9.0-windows
dotnet new xunit -n FFMedia.Tests -o src/FFMedia.Tests -f net9.0
```
Delete the default `Class1.cs` files the templates create:
```bash
rm -f src/FFMedia.Core/Class1.cs src/FFMedia.Media/Class1.cs src/FFMedia.Tools.YouTubeDownloader/Class1.cs
```

- [ ] **Step 2: Add all projects to the solution**

```bash
dotnet sln add src/FFMedia.Core src/FFMedia.Media src/FFMedia.Tools.YouTubeDownloader src/FFMedia.App src/FFMedia.Tests
```

- [ ] **Step 3: Wire project references (dependencies point inward toward Core)**

```bash
dotnet add src/FFMedia.Media reference src/FFMedia.Core
dotnet add src/FFMedia.Tools.YouTubeDownloader reference src/FFMedia.Core src/FFMedia.Media
dotnet add src/FFMedia.App reference src/FFMedia.Core src/FFMedia.Tools.YouTubeDownloader
dotnet add src/FFMedia.Tests reference src/FFMedia.Core
```

- [ ] **Step 4: Set common properties on FFMedia.Core.csproj**

Replace the `<PropertyGroup>` in `src/FFMedia.Core/FFMedia.Core.csproj` so it reads:
```xml
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
```

- [ ] **Step 5: Create the binaries placeholder so the folder is tracked**

Create `assets/binaries/.gitkeep` with a single line of content:
```
# Bundled yt-dlp.exe and ffmpeg.exe are fetched by build/fetch-binaries.ps1 (git-ignored).
```
Note: `.gitignore` ignores `assets/binaries/` but the `.gitkeep` is force-added in Step 7.

- [ ] **Step 6: Build the whole solution and run the (empty) test suite**

Run:
```bash
dotnet build FFMedia.sln
dotnet test FFMedia.sln
```
Expected: build succeeds; test run reports "Passed! - Failed: 0, Passed: 0" (or 1 passing template test if present — either is fine).

- [ ] **Step 7: Commit**

```bash
git add -A
git add -f assets/binaries/.gitkeep
git commit -m "chore: scaffold FFMedia solution skeleton (Core/Media/Tools/App/Tests)"
```

---

### Task 2: `ITool` + `IToolRegistry` (module-discovery seam)

**Files:**
- Create: `src/FFMedia.Core/Tools/ITool.cs`
- Create: `src/FFMedia.Core/Tools/IToolRegistry.cs`
- Create: `src/FFMedia.Core/Tools/ToolRegistry.cs`
- Test: `src/FFMedia.Tests/Tools/ToolRegistryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `ITool` — `{ string Id; string DisplayName; string Description; string IconGlyph; int SortOrder; }`
  - `IToolRegistry` — `{ IReadOnlyList<ITool> Tools { get; } }`
  - `ToolRegistry(IEnumerable<ITool> tools)` — internal, ordered by `SortOrder` then `DisplayName`.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/Tools/ToolRegistryTests.cs`:
```csharp
using FFMedia.Core.Tools;
using Xunit;

namespace FFMedia.Tests.Tools;

public class ToolRegistryTests
{
    private sealed record FakeTool(string Id, string DisplayName, int SortOrder) : ITool
    {
        public string Description => $"{DisplayName} description";
        public string IconGlyph => "";
    }

    [Fact]
    public void Tools_AreOrderedBySortOrderThenDisplayName()
    {
        var a = new FakeTool("b", "Beta", 10);
        var b = new FakeTool("a", "Alpha", 10);
        var c = new FakeTool("c", "Gamma", 5);

        var registry = new ToolRegistry(new ITool[] { a, b, c });

        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, registry.Tools.Select(t => t.DisplayName));
    }

    [Fact]
    public void Tools_IsEmpty_WhenNoToolsRegistered()
    {
        var registry = new ToolRegistry(Array.Empty<ITool>());
        Assert.Empty(registry.Tools);
    }
}
```
`ToolRegistry` is `internal`, so expose it to the test project. Add to `src/FFMedia.Core/FFMedia.Core.csproj` inside a new `<ItemGroup>`:
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="FFMedia.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~ToolRegistryTests`
Expected: FAIL — `ITool` / `ToolRegistry` do not exist (compile error).

- [ ] **Step 3: Create the interfaces and implementation**

Create `src/FFMedia.Core/Tools/ITool.cs`:
```csharp
namespace FFMedia.Core.Tools;

/// <summary>A self-contained FFMedia feature hosted by the app shell.</summary>
public interface ITool
{
    /// <summary>Stable identifier, e.g. "youtube-downloader".</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the navigation pane.</summary>
    string DisplayName { get; }

    /// <summary>Short description of what the tool does.</summary>
    string Description { get; }

    /// <summary>Segoe Fluent Icons glyph (kept as a string so Core stays UI-agnostic).</summary>
    string IconGlyph { get; }

    /// <summary>Relative ordering in the navigation pane (ascending).</summary>
    int SortOrder { get; }
}
```

Create `src/FFMedia.Core/Tools/IToolRegistry.cs`:
```csharp
namespace FFMedia.Core.Tools;

/// <summary>Aggregates all registered tools for the shell to render.</summary>
public interface IToolRegistry
{
    /// <summary>Registered tools, ordered for display.</summary>
    IReadOnlyList<ITool> Tools { get; }
}
```

Create `src/FFMedia.Core/Tools/ToolRegistry.cs`:
```csharp
namespace FFMedia.Core.Tools;

internal sealed class ToolRegistry : IToolRegistry
{
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Tools = tools
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<ITool> Tools { get; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~ToolRegistryTests`
Expected: PASS (2/2).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add ITool and IToolRegistry module-discovery seam"
```

---

### Task 3: `IBinaryProvider` (bundled yt-dlp/ffmpeg resolution)

**Files:**
- Create: `src/FFMedia.Core/Binaries/ExternalBinary.cs`
- Create: `src/FFMedia.Core/Binaries/IBinaryProvider.cs`
- Create: `src/FFMedia.Core/Binaries/BundledBinaryProvider.cs`
- Test: `src/FFMedia.Tests/Binaries/BundledBinaryProviderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum ExternalBinary { YtDlp, Ffmpeg }`
  - `IBinaryProvider` — `{ string GetPath(ExternalBinary); bool Exists(ExternalBinary); }`
  - `BundledBinaryProvider(string binariesDirectory)` — resolves `yt-dlp.exe` / `ffmpeg.exe` under the given directory. Directory is injected (not `AppContext.BaseDirectory`) for testability.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/Binaries/BundledBinaryProviderTests.cs`:
```csharp
using FFMedia.Core.Binaries;
using Xunit;

namespace FFMedia.Tests.Binaries;

public class BundledBinaryProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ffmedia-tests-" + Guid.NewGuid().ToString("N"));

    public BundledBinaryProviderTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData(ExternalBinary.YtDlp, "yt-dlp.exe")]
    [InlineData(ExternalBinary.Ffmpeg, "ffmpeg.exe")]
    public void GetPath_ReturnsExpectedFileUnderBinariesDirectory(ExternalBinary binary, string fileName)
    {
        var provider = new BundledBinaryProvider(_dir);
        Assert.Equal(Path.Combine(_dir, fileName), provider.GetPath(binary));
    }

    [Fact]
    public void Exists_IsFalse_WhenFileMissing()
    {
        var provider = new BundledBinaryProvider(_dir);
        Assert.False(provider.Exists(ExternalBinary.YtDlp));
    }

    [Fact]
    public void Exists_IsTrue_WhenFilePresent()
    {
        File.WriteAllText(Path.Combine(_dir, "ffmpeg.exe"), "stub");
        var provider = new BundledBinaryProvider(_dir);
        Assert.True(provider.Exists(ExternalBinary.Ffmpeg));
    }

    [Fact]
    public void Constructor_Throws_WhenDirectoryNull()
        => Assert.Throws<ArgumentNullException>(() => new BundledBinaryProvider(null!));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~BundledBinaryProviderTests`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3: Create the enum, interface, and implementation**

Create `src/FFMedia.Core/Binaries/ExternalBinary.cs`:
```csharp
namespace FFMedia.Core.Binaries;

/// <summary>External command-line binaries FFMedia orchestrates.</summary>
public enum ExternalBinary
{
    YtDlp,
    Ffmpeg
}
```

Create `src/FFMedia.Core/Binaries/IBinaryProvider.cs`:
```csharp
namespace FFMedia.Core.Binaries;

/// <summary>Resolves the on-disk location of bundled external binaries.</summary>
public interface IBinaryProvider
{
    /// <summary>Absolute path to the given binary (whether or not it exists yet).</summary>
    string GetPath(ExternalBinary binary);

    /// <summary>True if the binary file is present on disk.</summary>
    bool Exists(ExternalBinary binary);
}
```

Create `src/FFMedia.Core/Binaries/BundledBinaryProvider.cs`:
```csharp
namespace FFMedia.Core.Binaries;

/// <summary>Resolves binaries bundled under a given directory (e.g. app-relative assets/binaries).</summary>
public sealed class BundledBinaryProvider : IBinaryProvider
{
    private static readonly IReadOnlyDictionary<ExternalBinary, string> FileNames =
        new Dictionary<ExternalBinary, string>
        {
            [ExternalBinary.YtDlp] = "yt-dlp.exe",
            [ExternalBinary.Ffmpeg] = "ffmpeg.exe",
        };

    private readonly string _binariesDirectory;

    public BundledBinaryProvider(string binariesDirectory)
    {
        ArgumentNullException.ThrowIfNull(binariesDirectory);
        _binariesDirectory = binariesDirectory;
    }

    public string GetPath(ExternalBinary binary)
        => Path.Combine(_binariesDirectory, FileNames[binary]);

    public bool Exists(ExternalBinary binary)
        => File.Exists(GetPath(binary));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~BundledBinaryProviderTests`
Expected: PASS (5/5 including theory cases).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add IBinaryProvider for bundled yt-dlp/ffmpeg resolution"
```

---

### Task 4: Core DI registration extension

**Files:**
- Create: `src/FFMedia.Core/CoreServiceCollectionExtensions.cs`
- Modify: `src/FFMedia.Core/FFMedia.Core.csproj` (add DI Abstractions package)
- Test: `src/FFMedia.Tests/CoreServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: `ITool`, `IToolRegistry`, `ToolRegistry` (Task 2); `IBinaryProvider`, `BundledBinaryProvider` (Task 3).
- Produces: `IServiceCollection AddFFMediaCore(this IServiceCollection services, string binariesDirectory)` — registers `IToolRegistry`→`ToolRegistry` and `IBinaryProvider`→`BundledBinaryProvider(binariesDirectory)` as singletons.

- [ ] **Step 1: Add the DI Abstractions package to Core**

Run:
```bash
dotnet add src/FFMedia.Core package Microsoft.Extensions.DependencyInjection.Abstractions
```
Expected: package added (latest 9.x). Record the resolved version (it lands in the csproj).

- [ ] **Step 2: Write the failing test**

Create `src/FFMedia.Tests/CoreServiceCollectionExtensionsTests.cs`:
```csharp
using FFMedia.Core;
using FFMedia.Core.Binaries;
using FFMedia.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FFMedia.Tests;

public class CoreServiceCollectionExtensionsTests
{
    private sealed record FakeTool(string Id, string DisplayName, int SortOrder) : ITool
    {
        public string Description => "";
        public string IconGlyph => "";
    }

    [Fact]
    public void AddFFMediaCore_ResolvesRegistry_WithRegisteredToolsOrdered()
    {
        var provider = new ServiceCollection()
            .AddSingleton<ITool>(new FakeTool("z", "Zeta", 20))
            .AddSingleton<ITool>(new FakeTool("a", "Alpha", 10))
            .AddFFMediaCore(binariesDirectory: Path.GetTempPath())
            .BuildServiceProvider();

        var registry = provider.GetRequiredService<IToolRegistry>();

        Assert.Equal(new[] { "Alpha", "Zeta" }, registry.Tools.Select(t => t.DisplayName));
    }

    [Fact]
    public void AddFFMediaCore_ResolvesBinaryProvider_UsingGivenDirectory()
    {
        var dir = Path.GetTempPath();
        var provider = new ServiceCollection()
            .AddFFMediaCore(binariesDirectory: dir)
            .BuildServiceProvider();

        var binaries = provider.GetRequiredService<IBinaryProvider>();

        Assert.Equal(Path.Combine(dir, "yt-dlp.exe"), binaries.GetPath(ExternalBinary.YtDlp));
    }
}
```
Add `Microsoft.Extensions.DependencyInjection` package to the test project so `BuildServiceProvider()` is available:
```bash
dotnet add src/FFMedia.Tests package Microsoft.Extensions.DependencyInjection
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~CoreServiceCollectionExtensionsTests`
Expected: FAIL — `AddFFMediaCore` does not exist (compile error).

- [ ] **Step 4: Create the extension**

Create `src/FFMedia.Core/CoreServiceCollectionExtensions.cs`:
```csharp
using FFMedia.Core.Binaries;
using FFMedia.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace FFMedia.Core;

/// <summary>Registers UI-agnostic FFMedia core services.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <param name="binariesDirectory">Directory holding bundled yt-dlp.exe / ffmpeg.exe.</param>
    public static IServiceCollection AddFFMediaCore(this IServiceCollection services, string binariesDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binariesDirectory);

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IBinaryProvider>(_ => new BundledBinaryProvider(binariesDirectory));
        return services;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/FFMedia.Tests --filter FullyQualifiedName~CoreServiceCollectionExtensionsTests`
Expected: PASS (2/2).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): add AddFFMediaCore DI registration extension"
```

---

### Task 5: WPF-UI app shell (Host, Serilog, NavigationView)

**Files:**
- Modify: `src/FFMedia.App/FFMedia.App.csproj` (packages)
- Replace: `src/FFMedia.App/App.xaml`, `src/FFMedia.App/App.xaml.cs`
- Delete default `src/FFMedia.App/MainWindow.xaml` + `.cs`, recreate as the Fluent shell
- Create: `src/FFMedia.App/ViewModels/MainWindowViewModel.cs`
- Create: `src/FFMedia.App/Views/WelcomePage.xaml` + `.xaml.cs`

**Interfaces:**
- Consumes: `AddFFMediaCore` (Task 4), `IToolRegistry` (Task 2).
- Produces: a runnable shell. `MainWindowViewModel(IToolRegistry registry)` exposes `IReadOnlyList<ITool> Tools`. (Trivial pass-through — verified by running the app, not a unit test, to keep the test project WPF-free.)

- [ ] **Step 1: Add packages to FFMedia.App**

Run:
```bash
dotnet add src/FFMedia.App package WPF-UI
dotnet add src/FFMedia.App package CommunityToolkit.Mvvm
dotnet add src/FFMedia.App package Microsoft.Extensions.Hosting
dotnet add src/FFMedia.App package Serilog.Extensions.Hosting
dotnet add src/FFMedia.App package Serilog.Sinks.File
dotnet add src/FFMedia.App package Serilog.Sinks.Debug
```
Expected: all resolve (WPF-UI 3.x, CommunityToolkit.Mvvm 8.x, Hosting 9.x, Serilog sinks). Record resolved versions.

- [ ] **Step 2: Replace `App.xaml` to merge WPF-UI resource dictionaries**

Overwrite `src/FFMedia.App/App.xaml`:
```xml
<Application x:Class="FFMedia.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ui:ThemesDictionary Theme="Dark" />
                <ui:ControlsDictionary />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 3: Replace `App.xaml.cs` with Host + Serilog bootstrap**

Overwrite `src/FFMedia.App/App.xaml.cs`:
```csharp
using System.IO;
using System.Windows;
using FFMedia.App.ViewModels;
using FFMedia.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace FFMedia.App;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FFMedia");
        var binariesDir = Path.Combine(AppContext.BaseDirectory, "assets", "binaries");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(Path.Combine(appData, "logs", "ffmedia-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddFFMediaCore(binariesDir);
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        Log.CloseAndFlush();
        _host.Dispose();
        base.OnExit(e);
    }
}
```
Note: the WPF template sets `StartupUri` in `App.xaml`; Step 2 removed it, so startup is driven by `OnStartup` here.

- [ ] **Step 4: Create the ViewModel**

Create `src/FFMedia.App/ViewModels/MainWindowViewModel.cs`:
```csharp
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using FFMedia.Core.Tools;

namespace FFMedia.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Tools = registry.Tools;
    }

    /// <summary>Tools shown in the navigation pane (empty in M0).</summary>
    public IReadOnlyList<ITool> Tools { get; }
}
```

- [ ] **Step 5: Create the welcome page**

Create `src/FFMedia.App/Views/WelcomePage.xaml`:
```xml
<Page x:Class="FFMedia.App.Views.WelcomePage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" MaxWidth="480">
        <ui:SymbolIcon Symbol="Play24" FontSize="48" HorizontalAlignment="Center" />
        <TextBlock Text="FFMedia" FontSize="28" FontWeight="SemiBold" HorizontalAlignment="Center" Margin="0,12,0,0" />
        <TextBlock TextWrapping="Wrap" TextAlignment="Center" Margin="0,8,0,0"
                   Text="Your all-in-one media toolbox. Tools will appear in the navigation pane." />
    </StackPanel>
</Page>
```
Create `src/FFMedia.App/Views/WelcomePage.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace FFMedia.App.Views;

public partial class WelcomePage : Page
{
    public WelcomePage() => InitializeComponent();
}
```

- [ ] **Step 6: Recreate `MainWindow` as a Fluent shell**

Overwrite `src/FFMedia.App/MainWindow.xaml`:
```xml
<ui:FluentWindow x:Class="FFMedia.App.MainWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 xmlns:views="clr-namespace:FFMedia.App.Views"
                 Title="FFMedia" Width="1000" Height="640"
                 WindowStartupLocation="CenterScreen"
                 ExtendsContentIntoTitleBar="True"
                 WindowBackdropType="Mica">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <ui:TitleBar Grid.Row="0" Title="FFMedia" />

        <!-- M0: navigation pane is intentionally empty (no tools yet). Tool binding
             (MenuItemsSource) is wired in M1 once the first ITool exists and the exact
             WPF-UI items API is confirmed against the installed WPF-UI version. -->
        <ui:NavigationView Grid.Row="1" x:Name="RootNavigation"
                           IsBackButtonVisible="Collapsed"
                           PaneDisplayMode="Left">
            <ui:NavigationView.Content>
                <Frame x:Name="ContentFrame" />
            </ui:NavigationView.Content>
        </ui:NavigationView>
    </Grid>
</ui:FluentWindow>
```
Overwrite `src/FFMedia.App/MainWindow.xaml.cs`:
```csharp
using FFMedia.App.ViewModels;
using FFMedia.App.Views;
using Wpf.Ui.Controls;

namespace FFMedia.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        ContentFrame.Navigate(new WelcomePage());
    }
}
```

- [ ] **Step 7: Build, then run the app to verify the shell renders**

Run:
```bash
dotnet build src/FFMedia.App
```
Expected: build succeeds. Then run the app and confirm a Fluent (Mica, dark) window titled "FFMedia" opens showing the welcome page with an empty navigation pane:
```bash
dotnet run --project src/FFMedia.App
```
Close the window to end the run. (This is the M0 acceptance check — an empty but working shell.)

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(app): WPF-UI Fluent shell with Host, Serilog, and NavigationView seam"
```

---

### Task 6: `fetch-binaries.ps1` (download bundled yt-dlp + ffmpeg)

**Files:**
- Create: `build/fetch-binaries.ps1`

**Interfaces:**
- Consumes: nothing.
- Produces: populates `assets/binaries/yt-dlp.exe` and `assets/binaries/ffmpeg.exe`, which `BundledBinaryProvider` resolves at runtime.

- [ ] **Step 1: Create the script**

Create `build/fetch-binaries.ps1`:
```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads yt-dlp.exe and ffmpeg.exe into assets/binaries/ for local dev and packaging.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\assets\binaries')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # faster Invoke-WebRequest

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# --- yt-dlp (single exe, latest release) ---
$ytdlp = Join-Path $OutDir 'yt-dlp.exe'
Write-Host "Downloading yt-dlp -> $ytdlp"
Invoke-WebRequest -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $ytdlp

# --- ffmpeg (extract ffmpeg.exe from BtbN gpl build) ---
$ffmpegExe = Join-Path $OutDir 'ffmpeg.exe'
$tmpZip = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N') + '.zip')
$tmpDir = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Host "Downloading ffmpeg build..."
    Invoke-WebRequest -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip' -OutFile $tmpZip
    Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force
    $found = Get-ChildItem -Path $tmpDir -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $found) { throw "ffmpeg.exe not found in downloaded archive." }
    Copy-Item -Path $found.FullName -Destination $ffmpegExe -Force
    Write-Host "Extracted ffmpeg -> $ffmpegExe"
}
finally {
    Remove-Item -Path $tmpZip -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`nResolved versions:"
& $ytdlp --version
& $ffmpegExe -version | Select-Object -First 1
```

- [ ] **Step 2: Run the script and verify both binaries land**

Run (PowerShell):
```powershell
powershell -ExecutionPolicy Bypass -File build/fetch-binaries.ps1
```
Expected: `assets/binaries/yt-dlp.exe` and `assets/binaries/ffmpeg.exe` exist; the script prints a yt-dlp version string and an `ffmpeg version ...` line. Verify presence:
```bash
ls -la assets/binaries/
```
Expected: both `.exe` files listed. (They stay git-ignored — this is a local-dev fetch.)

- [ ] **Step 3: Commit (script only — binaries remain ignored)**

```bash
git add build/fetch-binaries.ps1
git commit -m "build: add fetch-binaries.ps1 to download bundled yt-dlp/ffmpeg"
```

---

### Task 7: CI workflow (build + test on Windows)

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: the solution (Task 1) and tests (Tasks 2–4).
- Produces: a GitHub Actions workflow that builds and tests on every push and PR.

- [ ] **Step 1: Create the workflow**

Create `.github/workflows/ci.yml`:
```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build-and-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Restore
        run: dotnet restore FFMedia.sln

      - name: Build
        run: dotnet build FFMedia.sln --configuration Release --no-restore

      - name: Test
        run: dotnet test FFMedia.sln --configuration Release --no-build --verbosity normal
```
Note: CI does not run `fetch-binaries.ps1` — binaries are not needed to build or to run the M0 unit tests.

- [ ] **Step 2: Validate the YAML builds the same commands locally**

Run:
```bash
dotnet restore FFMedia.sln && dotnet build FFMedia.sln -c Release --no-restore && dotnet test FFMedia.sln -c Release --no-build
```
Expected: all succeed (tests green). This mirrors what CI will run. (The workflow itself runs for real when the PR is opened.)

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: build and test on windows-latest for push and PR"
```

---

### Task 8: Update SDD + progress log (project rules)

**Files:**
- Modify: `SDD.md` (§3 testing note, §4.1 `ITool` icon type, §17 M0 status, Changelog)
- Modify: `CLAUDE.md` (Progress Log)

**Interfaces:**
- Consumes: everything built in Tasks 1–7.
- Produces: an SDD that matches reality (Rule 1) and a progress-log entry (Rule 2).

- [ ] **Step 1: Update `SDD.md` §4.1 — `ITool.Icon` type**

In `SDD.md`, replace the `SymbolRegular Icon` line in the `ITool` code block with:
```csharp
    string IconGlyph { get; }       // Segoe Fluent Icons glyph; kept a string so Core stays UI-agnostic
```
Remove the `Type ViewModelType` / `SortOrder`/`Icon` mismatch if present so the block matches the implemented interface (`Id`, `DisplayName`, `Description`, `IconGlyph`, `SortOrder`).

- [ ] **Step 2: Update `SDD.md` §3 — testing/assertions note**

In the Testing row (or a note beneath the stack table), replace the FluentAssertions mention with:
> Tests use **xUnit**. Assertion library deferred — FluentAssertions v8+ is a paid commercial license; evaluate **Shouldly** / **AwesomeAssertions** (both free) when richer assertions are needed. M0 uses plain `Assert`.

- [ ] **Step 3: Update `SDD.md` §17 — mark M0 delivered & bump version/changelog**

In §17, annotate the M0 row with "✅ delivered (PR #N)". Bump the header `Version:` to `0.2` and `Last updated:` to `2026-07-04`. Add a Changelog row:
```
| 2026-07-04 | 0.2 | M0 foundation delivered: solution skeleton, Core (ITool/IToolRegistry, IBinaryProvider, AddFFMediaCore), WPF-UI shell w/ Host+Serilog, fetch-binaries script, CI. ITool icon is a string glyph (Core stays UI-agnostic). Assertion lib deferred (FluentAssertions now paid). |
```

- [ ] **Step 4: Append a `CLAUDE.md` progress-log entry (newest first)**

Add above the most recent entry:
```markdown
### 2026-07-04 — M0 Foundation

- **Done:** Solution skeleton (Core/Media/Tools/App/Tests); Core `ITool`/`IToolRegistry`,
  `IBinaryProvider`, `AddFFMediaCore` (all unit-tested); WPF-UI Fluent shell with Generic
  Host + Serilog + `NavigationView` seam; `build/fetch-binaries.ps1`; GitHub Actions CI.
- **Changed:** `ITool.Icon` → `string IconGlyph` (keeps Core UI-agnostic); assertions use
  plain xUnit `Assert` (FluentAssertions v8 is paid) — SDD updated to v0.2.
- **Next:** M1 — vertical slice: URL → probe → download single MP4 with progress + cancel.
```

- [ ] **Step 5: Verify docs build/render and commit**

Run:
```bash
dotnet build FFMedia.sln
```
Expected: still builds (docs-only change). Commit:
```bash
git add SDD.md CLAUDE.md
git commit -m "docs: sync SDD to v0.2 and log M0 progress"
```

---

## Definition of Done (M0)

- `dotnet build FFMedia.sln` and `dotnet test FFMedia.sln` both succeed; all Core tests green.
- `dotnet run --project src/FFMedia.App` opens a Fluent (Mica/dark) shell titled "FFMedia" with a welcome page and empty navigation pane.
- `build/fetch-binaries.ps1` populates `assets/binaries/` with `yt-dlp.exe` + `ffmpeg.exe`.
- CI workflow present and green on the PR.
- `FFMedia.Core` has no UI-framework reference.
- SDD updated to v0.2; CLAUDE.md progress logged.
- Delivered as a single PR (`feat/m0-foundation` → `main`) for review.
