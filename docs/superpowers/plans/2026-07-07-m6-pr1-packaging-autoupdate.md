# M6 PR 1 — Packaging + Installer + App Auto-Update — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Package FFMedia with Velopack and give it a check-on-startup + manual auto-update flow served from GitHub Releases, unsigned, with the public v1.0.0 release left to the user (machinery + dry-run only).

**Architecture:** An explicit `Program.Main` runs `VelopackApp.Build().Run()` before WPF starts. A Core `IUpdateService` abstraction (returning a UI-agnostic `AppUpdateInfo` DTO) is realized in `FFMedia.App` by `VelopackUpdateService` over Velopack's `UpdateManager` + `GithubSource`. A singleton `UpdateViewModel` drives a dismissible shell banner and a "check now" action in Settings; a new `CheckForUpdatesOnStartup` setting gates the background startup check. `build/pack.ps1` + a tag-gated `release.yml` produce and publish the Velopack installer + deltas.

**Tech Stack:** C# / .NET 9, WPF + WPF-UI 4.3.0, CommunityToolkit.Mvvm, Velopack (NuGet + `vpk` global tool), GitHub Actions, GitHub Releases.

## Global Constraints

- **Dependency direction (SDD §5):** `FFMedia.Core` references no UI/packaging framework. Velopack lives only in `FFMedia.App`. Core exposes `IUpdateService` + `AppUpdateInfo`; the App realizes it.
- **App-layer test policy (M5 precedent):** `FFMedia.Tests` references `FFMedia.Core` and `FFMedia.Tools.YouTubeDownloader` only — **not** the `FFMedia.App` WinExe. App-layer ViewModels/services (`UpdateViewModel`, `VelopackUpdateService`, `SettingsViewModel`, `Program`) are verified by **build + manual run / dry-run**, not xUnit. Only Core logic (`AppSettings`) gets unit tests here.
- **Nullable reference types on**; one public type per file; file name matches type (SDD §18).
- **No silent crashes (SDD §11):** the startup update check must never block launch or crash the app — it runs on a background task and swallows+logs its own exceptions.
- **Unsigned for v1:** `vpk pack` runs without a signing cert; leave a commented `--signParams` seam. Accept SmartScreen "unknown publisher."
- **Repo / feed:** GitHub Releases on `https://github.com/ChamHC-dev/ff-media`. Stable channel only (`prerelease: false`).
- **Branch:** all work on `feat/m6-packaging-autoupdate` off latest `main`. Deliver via PR; do not merge (standing Rule 3).
- **Velopack API (verified 2026-07-07):**
  - `Velopack.VelopackApp.Build().Run()` — call first in `Main`.
  - `new Velopack.UpdateManager(Velopack.Sources.IUpdateSource source, UpdateOptions? = null, IVelopackLocator? = null)`.
  - `new Velopack.Sources.GithubSource(string repoUrl, string? accessToken, bool prerelease, IFileDownloader? downloader = null)`.
  - `UpdateManager.IsInstalled` → `bool`; `UpdateManager.CurrentVersion` → `SemanticVersion?`.
  - `Task<UpdateInfo?> CheckForUpdatesAsync()` (null = up to date).
  - `Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken = default)`.
  - `void ApplyUpdatesAndRestart(VelopackAsset? toApply, string[]? restartArgs = null)`.
  - `UpdateInfo.TargetFullRelease` is a `VelopackAsset`; `.Version` is a `SemanticVersion`.

---

## File map (PR 1)

**Create**
- `src/FFMedia.App/Program.cs` — explicit entry point (Velopack init before WPF).
- `src/FFMedia.Core/Updates/AppUpdateInfo.cs` — UI-agnostic update DTO.
- `src/FFMedia.Core/Updates/IUpdateService.cs` — update abstraction.
- `src/FFMedia.App/Services/VelopackUpdateService.cs` — Velopack realization.
- `src/FFMedia.App/ViewModels/UpdateViewModel.cs` — banner + check-now state.
- `build/pack.ps1` — publish + `vpk pack` (local/dry-run + CI).
- `.github/workflows/release.yml` — tag-gated pack + `vpk upload github`.
- `src/FFMedia.Tests/Settings/AppSettingsUpdateFlagTests.cs` — migration test.

**Modify**
- `src/FFMedia.App/FFMedia.App.csproj` — Velopack pkg; App.xaml → `Page`; `<StartupObject>`; `<Version>`.
- `src/FFMedia.App/App.xaml.cs` — DI registrations; startup update check.
- `src/FFMedia.App/MainWindow.xaml` — update banner + converter resource.
- `src/FFMedia.App/ViewModels/MainWindowViewModel.cs` — expose `Updates`.
- `src/FFMedia.App/ViewModels/SettingsViewModel.cs` — `CheckForUpdatesOnStartup` + expose `Updates`.
- `src/FFMedia.App/Views/SettingsPage.xaml` — toggle + "Check for updates now".
- `src/FFMedia.Core/Settings/AppSettings.cs` — `CheckForUpdatesOnStartup`; `Version` → 2.
- `SDD.md` — → v0.9. `CLAUDE.md` — Progress Log entry.

---

## Task 1: Velopack package + explicit entry point

**Files:**
- Create: `src/FFMedia.App/Program.cs`
- Modify: `src/FFMedia.App/FFMedia.App.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `FFMedia.App.Program.Main(string[])` — the app entry point (Velopack-initialized before WPF).

- [ ] **Step 1: Branch off latest main**

```bash
git checkout main && git pull --ff-only
git checkout -b feat/m6-packaging-autoupdate
```

- [ ] **Step 2: Add the Velopack package + entry-point wiring to the csproj**

Edit `src/FFMedia.App/FFMedia.App.csproj`. Add the Velopack `PackageReference` into the existing package `ItemGroup`:

```xml
    <PackageReference Include="Velopack" Version="0.0.1298" />
```

> The engineer must confirm the latest stable `Velopack` version on nuget.org at implementation time and pin it here; `0.0.1298` is a known-good baseline. Record the pinned version in SDD §3 in Task 9.

In the `<PropertyGroup>`, add a version and the explicit startup object (WPF otherwise generates its own `Main` from `App.xaml`, which would collide with `Program.Main`):

```xml
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <Version>0.9.0</Version>
    <StartupObject>FFMedia.App.Program</StartupObject>
  </PropertyGroup>
```

Then stop WPF from emitting its own `Main` by switching `App.xaml` from an `ApplicationDefinition` to a `Page` (it still generates `InitializeComponent`, just no `Main`). Add this new `ItemGroup`:

```xml
  <ItemGroup>
    <ApplicationDefinition Remove="App.xaml" />
    <Page Include="App.xaml" SubType="Designer" Generator="MSBuild:Compile" />
  </ItemGroup>
```

- [ ] **Step 3: Create the explicit entry point**

Create `src/FFMedia.App/Program.cs`:

```csharp
using System;
using Velopack;

namespace FFMedia.App;

/// <summary>
/// Explicit entry point. Velopack MUST run before WPF starts so it can service its
/// install/update/uninstall hooks (this returns immediately in normal runs).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
```

- [ ] **Step 4: Build to verify the entry-point switch compiles**

Run: `dotnet build src/FFMedia.App/FFMedia.App.csproj -c Debug`
Expected: **Build succeeded**, no "CS5001 does not contain a static 'Main'" and no "multiple entry points" (CS0017) error.

- [ ] **Step 5: Manual run — app still launches normally**

Run: `dotnet run --project src/FFMedia.App`
Expected: the FFMedia shell window opens exactly as before (nav pane, tools, no Fatal dialog). Close it.

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.App/FFMedia.App.csproj src/FFMedia.App/Program.cs
git commit -m "feat(app): add Velopack + explicit Program.Main entry point"
```

---

## Task 2: Core update contracts (`IUpdateService` + `AppUpdateInfo`)

**Files:**
- Create: `src/FFMedia.Core/Updates/AppUpdateInfo.cs`
- Create: `src/FFMedia.Core/Updates/IUpdateService.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `FFMedia.Core.Updates.AppUpdateInfo(string TargetVersion)` — record.
  - `FFMedia.Core.Updates.IUpdateService` with:
    - `Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)`
    - `Task DownloadAndApplyAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default)`

- [ ] **Step 1: Create the DTO**

Create `src/FFMedia.Core/Updates/AppUpdateInfo.cs`:

```csharp
namespace FFMedia.Core.Updates;

/// <summary>UI-agnostic description of an available app update (Velopack details stay in the App layer).</summary>
public sealed record AppUpdateInfo(string TargetVersion);
```

- [ ] **Step 2: Create the interface**

Create `src/FFMedia.Core/Updates/IUpdateService.cs`:

```csharp
namespace FFMedia.Core.Updates;

/// <summary>Checks for and applies application updates. Realized in the App layer over Velopack.</summary>
public interface IUpdateService
{
    /// <summary>Returns the available update, or <c>null</c> if the app is up to date or updates
    /// are not applicable (e.g. running uninstalled in dev). Never throws for "no update".</summary>
    Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>Downloads the pending update and restarts into it. No-op if nothing to apply.</summary>
    Task DownloadAndApplyAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}
```

- [ ] **Step 3: Build to verify the contracts compile**

Run: `dotnet build src/FFMedia.Core/FFMedia.Core.csproj -c Debug`
Expected: **Build succeeded** (Core has no new dependencies; still UI/packaging-free).

- [ ] **Step 4: Commit**

```bash
git add src/FFMedia.Core/Updates/
git commit -m "feat(core): add IUpdateService + AppUpdateInfo update contracts"
```

---

## Task 3: `VelopackUpdateService` + DI registration

**Files:**
- Create: `src/FFMedia.App/Services/VelopackUpdateService.cs`
- Modify: `src/FFMedia.App/App.xaml.cs`

**Interfaces:**
- Consumes: `IUpdateService`, `AppUpdateInfo` (Task 2); Velopack `UpdateManager`/`GithubSource`.
- Produces: `FFMedia.App.Services.VelopackUpdateService : IUpdateService` (DI singleton).

- [ ] **Step 1: Implement the service**

Create `src/FFMedia.App/Services/VelopackUpdateService.cs`:

```csharp
using FFMedia.Core.Updates;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace FFMedia.App.Services;

/// <summary>
/// <see cref="IUpdateService"/> backed by Velopack against GitHub Releases. Velopack's
/// UpdateInfo/UpdateManager types never leak into Core — only <see cref="AppUpdateInfo"/> crosses out.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/ChamHC-dev/ff-media";

    private readonly UpdateManager _manager;
    private readonly ILogger<VelopackUpdateService> _logger;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        // prerelease: false → stable channel only.
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    public async Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        // Not installed via Velopack (e.g. `dotnet run` in dev): nothing to update.
        if (!_manager.IsInstalled)
        {
            return null;
        }

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        _pending = info;
        return info is null ? null : new AppUpdateInfo(info.TargetFullRelease.Version.ToString());
    }

    public async Task DownloadAndApplyAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!_manager.IsInstalled)
        {
            return;
        }

        var info = _pending ?? await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
        {
            return;
        }

        await _manager.DownloadUpdatesAsync(info, p => progress?.Report(p), ct).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(info.TargetFullRelease); // exits the process
    }
}
```

- [ ] **Step 2: Register it in the composition root**

In `src/FFMedia.App/App.xaml.cs`, inside `ConfigureServices(services => { ... })`, add (near the other singletons, e.g. after the `INotificationService` registration):

```csharp
                services.AddSingleton<FFMedia.Core.Updates.IUpdateService,
                    FFMedia.App.Services.VelopackUpdateService>();
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build FFMedia.sln -c Debug`
Expected: **Build succeeded**.

- [ ] **Step 4: Manual run — dev (uninstalled) path is a safe no-op**

Run: `dotnet run --project src/FFMedia.App`
Expected: app launches; no update UI appears; no exception/Fatal dialog (because `IsInstalled` is false under `dotnet run`, the check short-circuits to `null`). Close it.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.App/Services/VelopackUpdateService.cs src/FFMedia.App/App.xaml.cs
git commit -m "feat(app): realize IUpdateService via Velopack GitHub source"
```

---

## Task 4: `AppSettings.CheckForUpdatesOnStartup` + version bump (Core, TDD)

**Files:**
- Modify: `src/FFMedia.Core/Settings/AppSettings.cs`
- Create: `src/FFMedia.Tests/Settings/AppSettingsUpdateFlagTests.cs`

**Interfaces:**
- Consumes: `JsonStore<AppSettings>` (existing).
- Produces: `AppSettings.CheckForUpdatesOnStartup` (`bool`, default `true`); `AppSettings.Default.Version == 2`.

- [ ] **Step 1: Write the failing tests**

Create `src/FFMedia.Tests/Settings/AppSettingsUpdateFlagTests.cs`:

```csharp
using System.IO;
using System.Text.Json;
using FFMedia.Core.Persistence;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class AppSettingsUpdateFlagTests
{
    [Fact]
    public void Default_EnablesStartupUpdateCheck_AndIsVersion2()
    {
        var d = AppSettings.Default;
        Assert.True(d.CheckForUpdatesOnStartup);
        Assert.Equal(2, d.Version);
    }

    [Fact]
    public void LoadingV1FileWithoutFlag_DefaultsFlagToTrue()
    {
        // Simulate an existing v1 settings.json written before the flag existed.
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Version": 1, "MaxConcurrency": 3, "Theme": "System" }""");

        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var loaded = store.Load(() => AppSettings.Default);

        Assert.True(loaded.CheckForUpdatesOnStartup); // missing field → default true
    }

    [Fact]
    public void FlagRoundTripsThroughJsonStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var original = AppSettings.Default with { CheckForUpdatesOnStartup = false };

        store.Save(original);

        Assert.Equal(original, store.Load(() => AppSettings.Default));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AppSettingsUpdateFlagTests"`
Expected: FAIL — `AppSettings` has no `CheckForUpdatesOnStartup` (compile error) and `Version` default is 1.

- [ ] **Step 3: Add the field and bump the version**

Edit `src/FFMedia.Core/Settings/AppSettings.cs`:

```csharp
public sealed record AppSettings
{
    public int Version { get; init; } = 2;
    public string DefaultOutputFolder { get; init; } = DefaultFolder();
    public int MaxConcurrency { get; init; } = 3;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public bool CheckForUpdatesOnStartup { get; init; } = true;

    public static AppSettings Default => new();

    private static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFMedia");
}
```

- [ ] **Step 4: Fix the existing default-version assertion**

The existing `AppSettingsTests.Default_HasExpectedValues` asserts `Version == 1`. Update that line in `src/FFMedia.Tests/Settings/AppSettingsTests.cs`:

```csharp
        Assert.Equal(2, d.Version);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AppSettings"`
Expected: PASS (both `AppSettingsTests` and `AppSettingsUpdateFlagTests`).

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Core/Settings/AppSettings.cs src/FFMedia.Tests/Settings/
git commit -m "feat(core): add CheckForUpdatesOnStartup setting (schema v2)"
```

---

## Task 5: `UpdateViewModel` (banner + check-now state)

**Files:**
- Create: `src/FFMedia.App/ViewModels/UpdateViewModel.cs`
- Modify: `src/FFMedia.App/App.xaml.cs`

**Interfaces:**
- Consumes: `IUpdateService` (Task 2).
- Produces: `FFMedia.App.ViewModels.UpdateViewModel` (DI singleton) with public members:
  - `bool IsUpdateAvailable`, `string? TargetVersion`, `string CurrentVersion`, `string StatusMessage`, `bool IsBusy`
  - `Task CheckOnStartupAsync()`
  - `IAsyncRelayCommand CheckNowCommand`, `IAsyncRelayCommand UpdateAndRestartCommand`, `IRelayCommand DismissCommand`

- [ ] **Step 1: Implement the ViewModel**

Create `src/FFMedia.App/ViewModels/UpdateViewModel.cs`:

```csharp
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.Updates;
using Microsoft.Extensions.Logging;

namespace FFMedia.App.ViewModels;

/// <summary>Drives the shell update banner and the Settings "check for updates" action.</summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ILogger<UpdateViewModel> _logger;

    public UpdateViewModel(IUpdateService updates, ILogger<UpdateViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(logger);
        _updates = updates;
        _logger = logger;
        CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
    }

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string? _targetVersion;
    [ObservableProperty] private string _currentVersion;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Background check invoked once at startup. Never throws; a dead feed is logged, not fatal.</summary>
    public async Task CheckOnStartupAsync()
    {
        try
        {
            var info = await _updates.CheckForUpdatesAsync().ConfigureAwait(true);
            if (info is not null)
            {
                TargetVersion = info.TargetVersion;
                IsUpdateAvailable = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup update check failed");
        }
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        IsBusy = true;
        StatusMessage = "Checking for updates…";
        try
        {
            var info = await _updates.CheckForUpdatesAsync().ConfigureAwait(true);
            if (info is null)
            {
                IsUpdateAvailable = false;
                StatusMessage = $"You're up to date (v{CurrentVersion}).";
            }
            else
            {
                TargetVersion = info.TargetVersion;
                IsUpdateAvailable = true;
                StatusMessage = $"Update available: v{info.TargetVersion}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual update check failed");
            StatusMessage = "Update check failed. See logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAndRestartAsync()
    {
        IsBusy = true;
        StatusMessage = "Downloading update…";
        try
        {
            await _updates.DownloadAndApplyAndRestartAsync().ConfigureAwait(true);
            // On success the process is replaced/restarted and does not return here.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying update failed");
            StatusMessage = "Update failed. See logs.";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => IsUpdateAvailable = false;
}
```

- [ ] **Step 2: Register it in the composition root**

In `src/FFMedia.App/App.xaml.cs` `ConfigureServices`, add after the `IUpdateService` registration:

```csharp
                services.AddSingleton<FFMedia.App.ViewModels.UpdateViewModel>();
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build FFMedia.sln -c Debug`
Expected: **Build succeeded**.

- [ ] **Step 4: Commit**

```bash
git add src/FFMedia.App/ViewModels/UpdateViewModel.cs src/FFMedia.App/App.xaml.cs
git commit -m "feat(app): add UpdateViewModel for banner + check-now state"
```

---

## Task 6: Shell update banner + startup check wiring

**Files:**
- Modify: `src/FFMedia.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/FFMedia.App/MainWindow.xaml`
- Modify: `src/FFMedia.App/App.xaml.cs`

**Interfaces:**
- Consumes: `UpdateViewModel` (Task 5), `ISettingsService` (existing).
- Produces: `MainWindowViewModel.Updates` (`UpdateViewModel`), bound by the shell banner; a fire-and-forget startup check in `App.OnStartup`.

- [ ] **Step 1: Expose `Updates` on `MainWindowViewModel`**

In `src/FFMedia.App/ViewModels/MainWindowViewModel.cs`, add the `UpdateViewModel` dependency. Change the constructor signature and body:

```csharp
    public MainWindowViewModel(
        IToolRegistry registry,
        IEnumerable<IToolPage> pages,
        ISettingsService settings,
        ThemeService theme,
        UpdateViewModel updates)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(updates);
        _settings = settings;
        _theme = theme;
        Updates = updates;
```

And add the property (near the other public members, e.g. above `ToggleTheme`):

```csharp
    /// <summary>Shared update state; the shell banner and Settings "check now" bind to this instance.</summary>
    public UpdateViewModel Updates { get; }
```

- [ ] **Step 2: Add the banner to the shell**

Edit `src/FFMedia.App/MainWindow.xaml`. Add a window resource for the converter, insert a banner row, and shift the nav + snackbar down a row. Replace the whole `<Grid>…</Grid>` body with:

```xml
    <ui:FluentWindow.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </ui:FluentWindow.Resources>
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <ui:TitleBar Grid.Row="0" Title="FFMedia">
            <ui:TitleBar.Header>
                <ui:Button Icon="{ui:SymbolIcon WeatherMoon24}"
                           Appearance="Transparent"
                           HorizontalAlignment="Right" Margin="0,0,8,0"
                           ToolTip="Toggle light/dark theme"
                           Command="{Binding ToggleThemeCommand}" />
            </ui:TitleBar.Header>
        </ui:TitleBar>

        <!-- Update banner: shown only when a newer version is available. -->
        <Border Grid.Row="1"
                Visibility="{Binding Updates.IsUpdateAvailable, Converter={StaticResource BoolToVis}}"
                Background="{DynamicResource ControlFillColorSecondaryBrush}"
                BorderBrush="{DynamicResource AccentControlElevationBorderBrush}"
                BorderThickness="0,0,0,1" Padding="16,10">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0" VerticalAlignment="Center">
                    <TextBlock FontWeight="SemiBold"
                               Text="{Binding Updates.TargetVersion, StringFormat='Version {0} is available'}" />
                    <TextBlock Text="{Binding Updates.StatusMessage}"
                               Foreground="{DynamicResource TextFillColorSecondaryBrush}"
                               Visibility="{Binding Updates.IsBusy, Converter={StaticResource BoolToVis}}" />
                </StackPanel>
                <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                    <ui:Button Content="Update &amp; restart" Appearance="Primary" Margin="0,0,8,0"
                               Command="{Binding Updates.UpdateAndRestartCommand}" />
                    <ui:Button Content="Later" Appearance="Secondary"
                               Command="{Binding Updates.DismissCommand}" />
                </StackPanel>
            </Grid>
        </Border>

        <ui:NavigationView Grid.Row="2" x:Name="RootNavigation"
                           MenuItemsSource="{Binding MenuItems}"
                           FooterMenuItemsSource="{Binding FooterMenuItems}"
                           IsBackButtonVisible="Collapsed"
                           PaneDisplayMode="Left" />

        <ui:SnackbarPresenter Grid.Row="2" x:Name="RootSnackbar"
                              VerticalAlignment="Bottom" Margin="0,0,0,16" />
    </Grid>
```

> Only WPF's built-in `BooleanToVisibilityConverter` is used — no custom converter needed. The
> banner collapses entirely when `Updates.IsUpdateAvailable` is false.

- [ ] **Step 3: Kick off the startup check in `App.OnStartup`**

In `src/FFMedia.App/App.xaml.cs`, inside `OnStartup`, after the window is shown, add the gated background check. Replace the final `Show()` line region with:

```csharp
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        if (settings.Current.CheckForUpdatesOnStartup)
        {
            var updates = _host.Services.GetRequiredService<FFMedia.App.ViewModels.UpdateViewModel>();
            _ = updates.CheckOnStartupAsync(); // fire-and-forget; swallows+logs its own errors
        }
```

(`settings` is the `ISettingsService` already resolved a few lines above for theming.)

- [ ] **Step 4: Build the solution**

Run: `dotnet build FFMedia.sln -c Debug`
Expected: **Build succeeded**.

- [ ] **Step 5: Manual run — banner stays hidden in dev, app is healthy**

Run: `dotnet run --project src/FFMedia.App`
Expected: shell opens; **no** update banner (dev build is uninstalled → check returns null); nav + snackbar unaffected by the new row; theme toggle still works. Close it.

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.App/ViewModels/MainWindowViewModel.cs src/FFMedia.App/MainWindow.xaml src/FFMedia.App/App.xaml.cs
git commit -m "feat(app): shell update banner + gated startup update check"
```

---

## Task 7: Settings — startup-check toggle + "Check for updates now"

**Files:**
- Modify: `src/FFMedia.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/FFMedia.App/Views/SettingsPage.xaml`

**Interfaces:**
- Consumes: `UpdateViewModel` (Task 5), `ISettingsService` (existing).
- Produces: `SettingsViewModel.CheckForUpdatesOnStartup` (`bool`, saved), `SettingsViewModel.Updates` (`UpdateViewModel`).

- [ ] **Step 1: Extend `SettingsViewModel`**

Edit `src/FFMedia.App/ViewModels/SettingsViewModel.cs`. Add the `UpdateViewModel` dependency, initialize the flag from settings, expose `Updates`, and persist the flag in `Save`:

```csharp
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;

    public SettingsViewModel(ISettingsService settings, ThemeService theme, UpdateViewModel updates)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(updates);
        _settings = settings;
        _theme = theme;
        Updates = updates;

        var current = settings.Current;
        _defaultOutputFolder = current.DefaultOutputFolder;
        _maxConcurrency = current.MaxConcurrency;
        _selectedTheme = current.Theme;
        _checkForUpdatesOnStartup = current.CheckForUpdatesOnStartup;
    }

    [ObservableProperty] private string _defaultOutputFolder;
    [ObservableProperty] private int _maxConcurrency;
    [ObservableProperty] private AppTheme _selectedTheme;
    [ObservableProperty] private bool _checkForUpdatesOnStartup;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Shared update state (also drives the shell banner). Bound by the Settings "check now" UI.</summary>
    public UpdateViewModel Updates { get; }
```

And add the new field to the `Save` command's record update:

```csharp
        var updated = _settings.Current with
        {
            DefaultOutputFolder = DefaultOutputFolder,
            MaxConcurrency = Math.Max(1, MaxConcurrency),
            Theme = SelectedTheme,
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
        };
```

- [ ] **Step 2: Add the UI to `SettingsPage.xaml`**

Edit `src/FFMedia.App/Views/SettingsPage.xaml`. Insert an "Updates" block between the Theme `ComboBox` and the `Save` button:

```xml
        <TextBlock Text="Updates" FontSize="18" FontWeight="SemiBold" Margin="0,24,0,4" />
        <ui:ToggleSwitch Content="Check for updates on startup"
                         IsChecked="{Binding CheckForUpdatesOnStartup, Mode=TwoWay}" />
        <StackPanel Orientation="Horizontal" Margin="0,10,0,0" VerticalAlignment="Center">
            <ui:Button Content="Check for updates now"
                       Command="{Binding Updates.CheckNowCommand}" />
            <TextBlock Text="{Binding Updates.CurrentVersion, StringFormat='Current: v{0}'}"
                       VerticalAlignment="Center" Margin="12,0,0,0"
                       Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
        </StackPanel>
        <TextBlock Text="{Binding Updates.StatusMessage}"
                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" Margin="0,6,0,0" />
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build FFMedia.sln -c Debug`
Expected: **Build succeeded**.

- [ ] **Step 4: Manual run — Settings shows the new controls and persists the toggle**

Run: `dotnet run --project src/FFMedia.App`
Steps to verify:
1. Open **Settings** (footer nav).
2. Confirm the **Updates** section: a "Check for updates on startup" toggle (on by default), a "Check for updates now" button, and "Current: v0.9.0.0".
3. Click **Check for updates now** → status shows "You're up to date (v…)." (dev build is uninstalled, so the check returns null — this exercises the wiring).
4. Toggle "Check for updates on startup" **off**, click **Save**, close the app, relaunch, reopen Settings → the toggle is still **off**. Re-enable it and Save.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.App/ViewModels/SettingsViewModel.cs src/FFMedia.App/Views/SettingsPage.xaml
git commit -m "feat(app): Settings update toggle + check-for-updates-now"
```

---

## Task 8: Release machinery — `pack.ps1` + `release.yml` (dry-run)

**Files:**
- Create: `build/pack.ps1`
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `build/fetch-binaries.ps1` (existing), the `vpk` global tool, App `<Version>` (Task 1).
- Produces: a local Velopack release set under `artifacts/releases/`; a tag-gated CI publish job.

- [ ] **Step 1: Write the pack script**

Create `build/pack.ps1`:

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes FFMedia self-contained and packs a Velopack release (installer + deltas).
  Local dry-run: run this, then install the produced Setup.exe to smoke-test updates.
.NOTES
  Prerequisite: dotnet tool install -g vpk   (Velopack CLI)
#>
[CmdletBinding()]
param(
    [string]$Version = '0.9.0',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$proj = Join-Path $root 'src/FFMedia.App/FFMedia.App.csproj'
$publishDir = Join-Path $root 'artifacts/publish'
$releaseDir = Join-Path $root 'artifacts/releases'

# 1. Ensure the bundled binaries are present (yt-dlp.exe / ffmpeg.exe).
& (Join-Path $PSScriptRoot 'fetch-binaries.ps1')

# 2. Publish self-contained (SDD §15). The csproj Content rule copies assets/binaries/*.exe.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj -c Release -r $Runtime --self-contained true -o $publishDir

# 3. Pack a Velopack release. UNSIGNED for v1 — add --signParams "..." here when a cert exists.
vpk pack `
    --packId FFMedia `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe FFMedia.App.exe `
    --packTitle 'FFMedia' `
    --outputDir $releaseDir

Write-Host "`nVelopack release created in $releaseDir" -ForegroundColor Green
Write-Host "Dry-run install: run the Setup.exe there, then bump -Version and re-pack to test the update loop."
```

- [ ] **Step 2: Write the tag-gated release workflow**

Create `.github/workflows/release.yml`:

```yaml
name: release

# Fires only when the maintainer pushes a version tag (e.g. v1.0.0). Until then this is
# dormant — the real public v1.0.0 release stays a deliberate, user-initiated action.
on:
  push:
    tags: [ 'v*' ]

jobs:
  release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Install Velopack CLI
        run: dotnet tool install -g vpk

      - name: Derive version from tag
        shell: pwsh
        run: |
          $v = $env:GITHUB_REF_NAME.TrimStart('v')
          echo "PACK_VERSION=$v" >> $env:GITHUB_ENV

      - name: Pack (publish + vpk pack)
        shell: pwsh
        run: ./build/pack.ps1 -Version $env:PACK_VERSION

      - name: Publish to GitHub Release
        shell: pwsh
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          vpk upload github `
            --repoUrl https://github.com/ChamHC-dev/ff-media `
            --publish `
            --releaseName "FFMedia $env:PACK_VERSION" `
            --tag $env:GITHUB_REF_NAME `
            --token $env:GITHUB_TOKEN `
            --outputDir artifacts/releases
```

- [ ] **Step 3: Add build artifacts to `.gitignore`**

Append to `.gitignore` (if `artifacts/` isn't already ignored):

```
# Velopack pack output
artifacts/
```

- [ ] **Step 4: Dry-run the pack locally and verify the update loop**

Prerequisite (once): `dotnet tool install -g vpk`

Run:
```bash
pwsh ./build/pack.ps1 -Version 0.9.0
```
Expected: `artifacts/releases/` contains `FFMedia-win-Setup.exe`, a `.nupkg`, and `RELEASES`/`assets.*` metadata; the script prints the green success line.

Then verify the delta/update loop end-to-end (local dry-run — no GitHub publish):
1. Run the produced `Setup.exe` → FFMedia installs to `%LocalAppData%\FFMedia` and launches (this is now an **installed** build, so `IsInstalled` is true).
2. `pwsh ./build/pack.ps1 -Version 0.9.1` to produce a newer release in the same `artifacts/releases/`.
3. Point a throwaway `UpdateManager` at the local folder to confirm deltas resolve — simplest path: temporarily change `VelopackUpdateService`'s source to `new UpdateManager(releaseDirPath)` in a local-only build, relaunch the installed app, and confirm the **banner appears** for 0.9.1, **Update & restart** downloads + relaunches onto 0.9.1 (check "Current: v0.9.1.0" in Settings). Revert the temporary source change afterward.

Record the observed result (installed → banner → update → relaunch) for the Progress Log in Task 9.

- [ ] **Step 5: Commit**

```bash
git add build/pack.ps1 .github/workflows/release.yml .gitignore
git commit -m "build: Velopack pack script + tag-gated GitHub release workflow"
```

---

## Task 9: SDD → v0.9, Progress Log, and green build/test

**Files:**
- Modify: `SDD.md`
- Modify: `CLAUDE.md`

**Interfaces:** none (documentation + final verification).

- [ ] **Step 1: Full solution build + test**

Run: `dotnet build FFMedia.sln -c Release`
Expected: **Build succeeded**, no warnings-as-errors in Core.

Run: `dotnet test --filter "Category!=Integration"`
Expected: **all tests pass** (existing suite + the three new `AppSettingsUpdateFlagTests`).

- [ ] **Step 2: Update the SDD to v0.9**

Edit `SDD.md`:
- Header: `Version: 0.9`, `Last updated: 2026-07-07`.
- §3 Technology Stack: record the pinned `Velopack` NuGet version + `vpk` tool.
- §6: add an `IUpdateService` row / M6 note (realized in App as `VelopackUpdateService` over Velopack `UpdateManager` + `GithubSource`; Core stays packaging-free).
- §9 Binary Management → Updating: note the app now performs a Velopack check-on-startup + manual "Check for updates" against GitHub Releases; ffmpeg rides these app releases (yt-dlp self-update lands in M6 PR 2).
- §10: nothing new (settings.json gains `CheckForUpdatesOnStartup`; note schema `Version` → 2).
- §13: add the update banner + Settings "check for updates on startup" toggle + "check now".
- §15 Packaging: describe `build/pack.ps1` (publish + `vpk pack`, unsigned) and the tag-gated `release.yml` (`vpk upload github`); note the real v1.0.0 tag is user-initiated.
- §17: mark M6 **in progress** — PR 1 (packaging + app auto-update) delivered; PR 2 (binary updates) pending.
- Changelog: add a `0.9` row summarizing PR 1.

- [ ] **Step 3: Append the Progress Log entry to CLAUDE.md**

Add a new dated entry at the **top** of the `## 📓 Progress Log` section:

```markdown
### 2026-07-07 — M6 Ship v1 (PR 1: packaging + app auto-update)

- **Done:** Velopack packaging + delta auto-update. Explicit `Program.Main` runs
  `VelopackApp.Build().Run()` before WPF (App.xaml switched to a `Page`, `<StartupObject>`
  set). Core `IUpdateService`/`AppUpdateInfo` realized in App by `VelopackUpdateService`
  (Velopack `UpdateManager` + GitHub `GithubSource`, stable channel; safe no-op when
  uninstalled/dev). Singleton `UpdateViewModel` drives a dismissible shell **update banner**
  (Update & restart / Later) and a Settings **"Check for updates now"** action + current-version
  display; a new `AppSettings.CheckForUpdatesOnStartup` (schema **v2**) gates a fire-and-forget
  startup check that never blocks/crashes launch. `build/pack.ps1` (publish self-contained +
  `vpk pack`, unsigned) + tag-gated `.github/workflows/release.yml` (`vpk upload github`).
  Verified: solution builds Release, all unit tests pass, local dry-run install → 0.9.1 pack →
  banner → Update & restart → relaunch onto 0.9.1. SDD → v0.9.
- **Decisions:** update feed = GitHub Releases; UX = check-on-startup + manual (no silent
  installs); unsigned for v1 (SmartScreen accepted; `--signParams` seam left in `pack.ps1`);
  the real public **v1.0.0** tag is left to the user (machinery + dry-run only). App-layer VMs
  (`UpdateViewModel`/`SettingsViewModel`) verified by build + manual per the M5 precedent
  (Tests doesn't reference the WinExe); only `AppSettings` migration is unit-tested.
- **Next:** M6 PR 2 — yt-dlp self-update (`IProcessRunner` + `IBinaryUpdateService`), binary
  version display in Settings, pinned `fetch-binaries.ps1` with hash checks.
```

- [ ] **Step 4: Commit**

```bash
git add SDD.md CLAUDE.md
git commit -m "docs: sync SDD to v0.9 and log M6 PR1 progress"
```

- [ ] **Step 5: Push and open the PR**

```bash
git push -u origin feat/m6-packaging-autoupdate
gh pr create --base main --title "M6 PR1: Velopack packaging + app auto-update" \
  --body "Delivers M6 PR 1 per docs/superpowers/plans/2026-07-07-m6-pr1-packaging-autoupdate.md: Velopack entry point, IUpdateService + VelopackUpdateService (GitHub feed), shell update banner + startup check, Settings toggle + check-now, pack.ps1 + tag-gated release.yml (unsigned, dry-run). Public v1.0.0 tag left to the maintainer.

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

Stop here for user review — do **not** merge (standing Rule 3).

---

## Self-Review

**Spec coverage (spec §3 = PR 1):**
- Velopack entry point → Task 1. ✅
- Release machinery (`pack.ps1`, `release.yml`, GitHub feed, unsigned, dry-run) → Task 8. ✅
- Core `IUpdateService`/`AppUpdateInfo` → Task 2; App `VelopackUpdateService` (+ IsInstalled dev guard) → Task 3. ✅
- Startup background check + dismissible banner → Task 6. ✅
- Settings "check on startup" toggle (+ schema v2 migration) → Tasks 4, 7. ✅
- Settings "Check for updates now" + current-version display → Task 7. ✅
- SDD → v0.9 + Progress Log → Task 9. ✅

**Deviation from spec, recorded:** spec §5 listed headless `UpdateViewModel`/`SettingsViewModel` tests. `FFMedia.Tests` does not reference the `FFMedia.App` WinExe (M5 precedent), so App-layer VMs are verified by build + manual run instead; only `AppSettings` (Core) is unit-tested. Called out in Global Constraints and Task 9 Progress Log.

**Placeholder scan:** two items require an implementer lookup, both explicitly flagged, not silent gaps — the exact latest `Velopack` NuGet version (Task 1 Step 2) and the exact `vpk`/`vpk upload github` flags against the installed CLI (Task 8). Both have concrete known-good values to start from.

**Type consistency:** `IUpdateService` (`CheckForUpdatesAsync`/`DownloadAndApplyAndRestartAsync`) and `AppUpdateInfo(TargetVersion)` are defined in Task 2 and consumed identically in Tasks 3, 5, 7. `UpdateViewModel` members (`IsUpdateAvailable`, `TargetVersion`, `CurrentVersion`, `StatusMessage`, `CheckNowCommand`, `UpdateAndRestartCommand`, `DismissCommand`, `CheckOnStartupAsync`) are defined in Task 5 and bound consistently in Tasks 6 (XAML) and 7 (XAML). `AppSettings.CheckForUpdatesOnStartup` defined in Task 4, consumed in Tasks 6 and 7.
