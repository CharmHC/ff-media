# M6 PR 2 — Binary Updates & App Logo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an in-app yt-dlp self-update flow (versions + update + startup notify), pin the bundled binaries with SHA-256 verification, and wire `logo.png` in as the app icon (exe/window/installer) and in-app branding.

**Architecture:** A new Core `IProcessRunner` seam launches child processes; a Core `IBinaryUpdateService` uses it to read versions and run `yt-dlp -U`, and queries the GitHub API for the latest yt-dlp version. A singleton App `BinaryUpdateViewModel` surfaces this in Settings and on startup. `fetch-binaries.ps1` is rewritten to pin exact versions and verify hashes. The logo is moved to `assets/branding/`, converted to a multi-res `.ico`, and referenced by the csproj, window, nav pane, welcome page, and `vpk pack`.

**Tech Stack:** C# / .NET 9, WPF + WPF-UI, CommunityToolkit.Mvvm, xUnit, PowerShell, Velopack `vpk` 1.2.0.

## Global Constraints

- Nullable reference types **on**; `Core` treats warnings as errors — build must be **0 warnings / 0 errors** (SDD §18).
- One public type per file; file name matches the type (SDD §18).
- `async`/`await` end-to-end for I/O and process work; no blocking `.Result` (SDD §18).
- `FFMedia.Core` references **no** UI framework (SDD §5). New Core code uses only the BCL + `Microsoft.Extensions.Logging.Abstractions`.
- Unit tests run under `Category!=Integration`; all **152** existing tests plus the new ones must pass.
- App-layer ViewModels are **not** unit-tested (the Tests project doesn't reference the WinExe) — verified by build + manual, per the M5/M6 precedent (SDD §14).
- Branch: `feat/m6-binary-updates`. Commit per task. **Do not merge** — deliver via PR for user review (Standing Rule 3).
- Pinned binaries (record verbatim in `fetch-binaries.ps1` and SDD §9):
  - yt-dlp **`2026.07.04`**, `yt-dlp.exe` SHA-256 `52fe3c26dcf71fbdc85b528589020bb0b8e383155cfa81b64dd447bbe35e24b8`.
  - ffmpeg BtbN tag **`autobuild-2026-07-07-13-44`**, asset `ffmpeg-n8.1.2-22-g94138f6973-win64-gpl-8.1.zip` (static gpl), zip SHA-256 **computed in Task 8**.

---

## File Structure

**Create (Core):**
- `src/FFMedia.Core/Processes/IProcessRunner.cs` — process-launch seam.
- `src/FFMedia.Core/Processes/ProcessResult.cs` — exit code + captured stdout/stderr.
- `src/FFMedia.Core/Processes/ProcessRunner.cs` — `System.Diagnostics.Process` impl.
- `src/FFMedia.Core/Binaries/IBinaryUpdateService.cs` — version/update contract.
- `src/FFMedia.Core/Binaries/BinaryUpdateResult.cs` — update outcome record.
- `src/FFMedia.Core/Binaries/BinaryUpdateService.cs` — impl over `IProcessRunner` + `HttpClient`.
- `src/FFMedia.Core/Binaries/FfmpegVersionParsing.cs` — pure first-line parser.

**Create (App):**
- `src/FFMedia.App/ViewModels/BinaryUpdateViewModel.cs` — singleton VM.

**Create (build/assets):**
- `assets/branding/logo.png` — moved from repo root.
- `assets/branding/app.ico` — generated multi-res icon (committed).
- `build/make-icon.ps1` — png → ico helper.

**Create (tests):**
- `src/FFMedia.Tests/Processes/ProcessRunnerTests.cs`
- `src/FFMedia.Tests/Binaries/FfmpegVersionParsingTests.cs`
- `src/FFMedia.Tests/Binaries/BinaryUpdateServiceTests.cs`
- `src/FFMedia.Tests/Settings/AppSettingsYtDlpFlagTests.cs`

**Modify:**
- `src/FFMedia.Core/CoreServiceCollectionExtensions.cs` — register the two new services.
- `src/FFMedia.Core/Settings/AppSettings.cs` — add flag, bump schema to v3.
- `src/FFMedia.App/App.xaml.cs` — DI registration + startup check.
- `src/FFMedia.App/ViewModels/SettingsViewModel.cs` — expose `Binaries`, persist the new flag.
- `src/FFMedia.App/Views/SettingsPage.xaml` — "Binaries" section.
- `src/FFMedia.App/Views/SettingsPage.xaml.cs` — load versions on page load.
- `src/FFMedia.App/MainWindow.xaml` — window icon + nav-pane header logo.
- `src/FFMedia.App/Views/WelcomePage.xaml` — logo image.
- `src/FFMedia.App/FFMedia.App.csproj` — `<ApplicationIcon>` + logo `<Resource>`.
- `build/fetch-binaries.ps1` — pinned versions + hash verification.
- `build/pack.ps1` — `vpk pack --icon`.
- `SDD.md`, `CLAUDE.md` — docs.

---

## Task 1: `IProcessRunner` + `ProcessRunner` (Core)

**Files:**
- Create: `src/FFMedia.Core/Processes/IProcessRunner.cs`
- Create: `src/FFMedia.Core/Processes/ProcessResult.cs`
- Create: `src/FFMedia.Core/Processes/ProcessRunner.cs`
- Modify: `src/FFMedia.Core/CoreServiceCollectionExtensions.cs`
- Test: `src/FFMedia.Tests/Processes/ProcessRunnerTests.cs`

**Interfaces:**
- Produces: `IProcessRunner.RunAsync(string fileName, IReadOnlyList<string> arguments, IProgress<string>? onOutputLine = null, CancellationToken ct = default) → Task<ProcessResult>`; `ProcessResult(int ExitCode, string StandardOutput, string StandardError)`.

- [ ] **Step 1: Write the failing test**

`src/FFMedia.Tests/Processes/ProcessRunnerTests.cs`:
```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Processes;
using Xunit;

namespace FFMedia.Tests.Processes;

public class ProcessRunnerTests
{
    private static readonly IProcessRunner Runner = new ProcessRunner();

    [Fact]
    public async Task RunAsync_ReturnsExitCode()
    {
        var result = await Runner.RunAsync("cmd.exe", new[] { "/c", "exit", "3" });
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CapturesStdout_AndStreamsLines()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(lines.Add);
        var result = await Runner.RunAsync("cmd.exe", new[] { "/c", "echo", "hello" }, progress);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
        // Progress<T> posts asynchronously; give the callback a beat to drain.
        await Task.Delay(100);
        Assert.Contains(lines, l => l.Contains("hello"));
    }

    [Fact]
    public async Task RunAsync_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner.RunAsync("cmd.exe", new[] { "/c", "ping", "-n", "30", "127.0.0.1" }, null, cts.Token));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~ProcessRunnerTests"`
Expected: FAIL — `ProcessRunner` / `IProcessRunner` do not exist (compile error).

- [ ] **Step 3: Write the interface and result record**

`src/FFMedia.Core/Processes/ProcessResult.cs`:
```csharp
namespace FFMedia.Core.Processes;

/// <summary>Outcome of a finished child process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
```

`src/FFMedia.Core/Processes/IProcessRunner.cs`:
```csharp
namespace FFMedia.Core.Processes;

/// <summary>Launches child processes, capturing output and honoring cancellation. The seam
/// that makes binary orchestration testable without real exes (SDD §6).</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/>, capturing
    /// stdout/stderr and optionally streaming stdout lines via <paramref name="onOutputLine"/>.
    /// A non-zero exit is returned in the result (not thrown). Cancellation kills the process
    /// tree and throws <see cref="OperationCanceledException"/>. A launch failure (e.g. missing
    /// file) throws.</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<string>? onOutputLine = null,
        CancellationToken ct = default);
}
```

- [ ] **Step 4: Write the implementation**

`src/FFMedia.Core/Processes/ProcessRunner.cs`:
```csharp
using System.Diagnostics;
using System.Text;

namespace FFMedia.Core.Processes;

/// <summary><see cref="IProcessRunner"/> over <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onOutputLine?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best-effort kill; the original cancellation is what matters.
            }
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
```

- [ ] **Step 5: Register in `AddFFMediaCore`**

In `src/FFMedia.Core/CoreServiceCollectionExtensions.cs`, add `using FFMedia.Core.Processes;` at the top, and register the runner right after the `IBinaryProvider` line (`services.AddSingleton<IBinaryProvider>(...)`):
```csharp
        services.AddSingleton<IProcessRunner, ProcessRunner>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~ProcessRunnerTests"`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Core/Processes src/FFMedia.Core/CoreServiceCollectionExtensions.cs src/FFMedia.Tests/Processes
git commit -m "feat(core): add IProcessRunner process-launch seam"
```

---

## Task 2: `FfmpegVersionParsing` pure helper (Core)

**Files:**
- Create: `src/FFMedia.Core/Binaries/FfmpegVersionParsing.cs`
- Test: `src/FFMedia.Tests/Binaries/FfmpegVersionParsingTests.cs`

**Interfaces:**
- Produces: `static string? FfmpegVersionParsing.Parse(string ffmpegVersionOutput)` — returns the version token from ffmpeg's first output line, or `null` if unparseable.

- [ ] **Step 1: Write the failing test**

`src/FFMedia.Tests/Binaries/FfmpegVersionParsingTests.cs`:
```csharp
using FFMedia.Core.Binaries;
using Xunit;

namespace FFMedia.Tests.Binaries;

public class FfmpegVersionParsingTests
{
    [Theory]
    [InlineData("ffmpeg version 8.1 Copyright (c) 2000-2025 the FFmpeg developers", "8.1")]
    [InlineData("ffmpeg version n8.1.2-22-g94138f6973-win64-gpl-8.1 Copyright", "n8.1.2-22-g94138f6973-win64-gpl-8.1")]
    [InlineData("ffmpeg version N-125485-ga41f543113 Copyright (c) 2000", "N-125485-ga41f543113")]
    public void Parse_ExtractsVersionToken(string firstLine, string expected)
    {
        Assert.Equal(expected, FfmpegVersionParsing.Parse(firstLine + "\nbuilt with gcc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not ffmpeg output at all")]
    public void Parse_ReturnsNull_OnUnparseable(string input)
    {
        Assert.Null(FfmpegVersionParsing.Parse(input));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~FfmpegVersionParsingTests"`
Expected: FAIL — `FfmpegVersionParsing` does not exist.

- [ ] **Step 3: Write the implementation**

`src/FFMedia.Core/Binaries/FfmpegVersionParsing.cs`:
```csharp
namespace FFMedia.Core.Binaries;

/// <summary>Pure parser for the version token in ffmpeg's <c>-version</c> first line.</summary>
public static class FfmpegVersionParsing
{
    public static string? Parse(string ffmpegVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(ffmpegVersionOutput))
        {
            return null;
        }

        var firstLine = ffmpegVersionOutput.Split('\n')[0].Trim();
        const string marker = "ffmpeg version ";
        var idx = firstLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var rest = firstLine[(idx + marker.Length)..].Trim();
        var token = rest.Split(' ')[0];
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~FfmpegVersionParsingTests"`
Expected: PASS (6 cases).

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Core/Binaries/FfmpegVersionParsing.cs src/FFMedia.Tests/Binaries/FfmpegVersionParsingTests.cs
git commit -m "feat(core): pure ffmpeg version-line parser"
```

---

## Task 3: `IBinaryUpdateService` + `BinaryUpdateService` (Core)

**Files:**
- Create: `src/FFMedia.Core/Binaries/IBinaryUpdateService.cs`
- Create: `src/FFMedia.Core/Binaries/BinaryUpdateResult.cs`
- Create: `src/FFMedia.Core/Binaries/BinaryUpdateService.cs`
- Modify: `src/FFMedia.Core/CoreServiceCollectionExtensions.cs`
- Test: `src/FFMedia.Tests/Binaries/BinaryUpdateServiceTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner.RunAsync(...)` (Task 1); `FfmpegVersionParsing.Parse(...)` (Task 2); `IBinaryProvider.GetPath(ExternalBinary)`; `ExternalBinary.{YtDlp,Ffmpeg}`.
- Produces:
  - `IBinaryUpdateService.GetInstalledVersionAsync(ExternalBinary, CancellationToken) → Task<string?>`
  - `IBinaryUpdateService.GetLatestYtDlpVersionAsync(CancellationToken) → Task<string?>`
  - `IBinaryUpdateService.UpdateYtDlpAsync(CancellationToken) → Task<BinaryUpdateResult>`
  - `BinaryUpdateResult(bool Updated, string? FromVersion, string? ToVersion, string Message)`

- [ ] **Step 1: Write the failing test (with in-file fakes)**

`src/FFMedia.Tests/Binaries/BinaryUpdateServiceTests.cs`:
```csharp
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Binaries;

public class BinaryUpdateServiceTests
{
    // Fake runner: returns queued ProcessResults in order, keyed by the first argument.
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Queue<ProcessResult> _results;
        public List<string> FirstArgs { get; } = new();
        public FakeProcessRunner(params ProcessResult[] results) => _results = new Queue<ProcessResult>(results);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IProgress<string>? onOutputLine = null, CancellationToken ct = default)
        {
            FirstArgs.Add(arguments.Count > 0 ? arguments[0] : "");
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class StubBinaryProvider : IBinaryProvider
    {
        public string GetPath(ExternalBinary binary) => $"{binary}.exe";
        public bool Exists(ExternalBinary binary) => true;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _code;
        public StubHandler(string json, HttpStatusCode code = HttpStatusCode.OK) { _json = json; _code = code; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_json) });
    }

    private static BinaryUpdateService Make(IProcessRunner runner, HttpClient http) =>
        new(runner, new StubBinaryProvider(), http, NullLogger<BinaryUpdateService>.Instance);

    [Fact]
    public async Task GetInstalledVersion_YtDlp_TrimsOutput()
    {
        var runner = new FakeProcessRunner(new ProcessResult(0, "2026.07.04\n", ""));
        var svc = Make(runner, new HttpClient(new StubHandler("{}")));
        Assert.Equal("2026.07.04", await svc.GetInstalledVersionAsync(ExternalBinary.YtDlp));
    }

    [Fact]
    public async Task GetInstalledVersion_Ffmpeg_ParsesFirstLine()
    {
        var runner = new FakeProcessRunner(new ProcessResult(0, "ffmpeg version 8.1 Copyright\nbuilt with", ""));
        var svc = Make(runner, new HttpClient(new StubHandler("{}")));
        Assert.Equal("8.1", await svc.GetInstalledVersionAsync(ExternalBinary.Ffmpeg));
    }

    [Fact]
    public async Task GetInstalledVersion_ReturnsNull_OnNonZeroExit()
    {
        var runner = new FakeProcessRunner(new ProcessResult(1, "", "boom"));
        var svc = Make(runner, new HttpClient(new StubHandler("{}")));
        Assert.Null(await svc.GetInstalledVersionAsync(ExternalBinary.YtDlp));
    }

    [Fact]
    public async Task GetLatestYtDlpVersion_ReturnsTag_WhenNewer()
    {
        // installed = 2026.07.01 (first runner call), remote tag = 2026.07.04
        var runner = new FakeProcessRunner(new ProcessResult(0, "2026.07.01", ""));
        var http = new HttpClient(new StubHandler("""{ "tag_name": "2026.07.04" }"""));
        var svc = Make(runner, http);
        Assert.Equal("2026.07.04", await svc.GetLatestYtDlpVersionAsync());
    }

    [Fact]
    public async Task GetLatestYtDlpVersion_ReturnsNull_WhenUpToDate()
    {
        var runner = new FakeProcessRunner(new ProcessResult(0, "2026.07.04", ""));
        var http = new HttpClient(new StubHandler("""{ "tag_name": "2026.07.04" }"""));
        var svc = Make(runner, http);
        Assert.Null(await svc.GetLatestYtDlpVersionAsync());
    }

    [Fact]
    public async Task UpdateYtDlp_ReportsUpdated_WhenVersionChanges()
    {
        // call order: version(before)=old, -U exit 0, version(after)=new
        var runner = new FakeProcessRunner(
            new ProcessResult(0, "2026.07.01", ""),
            new ProcessResult(0, "Updated yt-dlp to 2026.07.04", ""),
            new ProcessResult(0, "2026.07.04", ""));
        var svc = Make(runner, new HttpClient(new StubHandler("{}")));

        var result = await svc.UpdateYtDlpAsync();

        Assert.True(result.Updated);
        Assert.Equal("2026.07.01", result.FromVersion);
        Assert.Equal("2026.07.04", result.ToVersion);
        Assert.Contains("-U", runner.FirstArgs);
    }

    [Fact]
    public async Task UpdateYtDlp_ReportsUpToDate_WhenVersionUnchanged()
    {
        var runner = new FakeProcessRunner(
            new ProcessResult(0, "2026.07.04", ""),
            new ProcessResult(0, "yt-dlp is up to date", ""),
            new ProcessResult(0, "2026.07.04", ""));
        var svc = Make(runner, new HttpClient(new StubHandler("{}")));

        var result = await svc.UpdateYtDlpAsync();

        Assert.False(result.Updated);
        Assert.Equal("2026.07.04", result.ToVersion);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~BinaryUpdateServiceTests"`
Expected: FAIL — `IBinaryUpdateService` / `BinaryUpdateService` / `BinaryUpdateResult` do not exist.

- [ ] **Step 3: Write the contract + result record**

`src/FFMedia.Core/Binaries/BinaryUpdateResult.cs`:
```csharp
namespace FFMedia.Core.Binaries;

/// <summary>Outcome of a yt-dlp self-update attempt.</summary>
public sealed record BinaryUpdateResult(bool Updated, string? FromVersion, string? ToVersion, string Message);
```

`src/FFMedia.Core/Binaries/IBinaryUpdateService.cs`:
```csharp
namespace FFMedia.Core.Binaries;

/// <summary>Reports bundled-binary versions and performs the yt-dlp self-update (SDD §9).</summary>
public interface IBinaryUpdateService
{
    /// <summary>Installed version of the bundled binary, or null if it can't be read.</summary>
    Task<string?> GetInstalledVersionAsync(ExternalBinary binary, CancellationToken ct = default);

    /// <summary>Latest published yt-dlp version if newer than installed; null if up to date or
    /// the check fails. yt-dlp has no offline check-only mode, so this queries GitHub.</summary>
    Task<string?> GetLatestYtDlpVersionAsync(CancellationToken ct = default);

    /// <summary>Runs <c>yt-dlp -U</c> and reports the outcome.</summary>
    Task<BinaryUpdateResult> UpdateYtDlpAsync(CancellationToken ct = default);
}
```

- [ ] **Step 4: Write the implementation**

`src/FFMedia.Core/Binaries/BinaryUpdateService.cs`:
```csharp
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FFMedia.Core.Processes;
using Microsoft.Extensions.Logging;

namespace FFMedia.Core.Binaries;

/// <summary><see cref="IBinaryUpdateService"/> over <see cref="IProcessRunner"/> (versions +
/// <c>yt-dlp -U</c>) and the GitHub API (latest-version check).</summary>
public sealed class BinaryUpdateService : IBinaryUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    private readonly IProcessRunner _runner;
    private readonly IBinaryProvider _binaries;
    private readonly HttpClient _http;
    private readonly ILogger<BinaryUpdateService> _logger;

    public BinaryUpdateService(
        IProcessRunner runner, IBinaryProvider binaries, HttpClient http, ILogger<BinaryUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(binaries);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _binaries = binaries;
        _http = http;
        _logger = logger;
    }

    public async Task<string?> GetInstalledVersionAsync(ExternalBinary binary, CancellationToken ct = default)
    {
        try
        {
            var path = _binaries.GetPath(binary);
            var args = binary == ExternalBinary.YtDlp ? new[] { "--version" } : new[] { "-version" };
            var result = await _runner.RunAsync(path, args, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return null;
            }

            return binary == ExternalBinary.YtDlp
                ? result.StandardOutput.Trim()
                : FfmpegVersionParsing.Parse(result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Binary} version", binary);
            return null;
        }
    }

    public async Task<string?> GetLatestYtDlpVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            req.Headers.UserAgent.ParseAdd("FFMedia");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var payload = await resp.Content.ReadFromJsonAsync<GithubRelease>(ct).ConfigureAwait(false);
            var latest = payload?.TagName?.Trim();
            if (string.IsNullOrEmpty(latest))
            {
                return null;
            }

            var installed = await GetInstalledVersionAsync(ExternalBinary.YtDlp, ct).ConfigureAwait(false);
            return string.Equals(latest, installed, StringComparison.OrdinalIgnoreCase) ? null : latest;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp latest-version check failed");
            return null;
        }
    }

    public async Task<BinaryUpdateResult> UpdateYtDlpAsync(CancellationToken ct = default)
    {
        var from = await GetInstalledVersionAsync(ExternalBinary.YtDlp, ct).ConfigureAwait(false);
        var path = _binaries.GetPath(ExternalBinary.YtDlp);
        var result = await _runner.RunAsync(path, new[] { "-U" }, null, ct).ConfigureAwait(false);
        var to = await GetInstalledVersionAsync(ExternalBinary.YtDlp, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return new BinaryUpdateResult(false, from, to, "yt-dlp update failed. See logs.");
        }

        var updated = !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        var message = updated
            ? $"Updated yt-dlp {from} → {to}."
            : $"yt-dlp is already up to date ({to}).";
        return new BinaryUpdateResult(updated, from, to, message);
    }

    private sealed record GithubRelease([property: JsonPropertyName("tag_name")] string? TagName);
}
```

- [ ] **Step 5: Register in `AddFFMediaCore`**

In `src/FFMedia.Core/CoreServiceCollectionExtensions.cs`, add `using System.Net.Http;` at the top, and register after the `IProcessRunner` line from Task 1:
```csharp
        services.AddSingleton<IBinaryUpdateService>(sp => new BinaryUpdateService(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IBinaryProvider>(),
            new HttpClient(),
            sp.GetService<ILogger<BinaryUpdateService>>() ?? NullLogger<BinaryUpdateService>.Instance));
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~BinaryUpdateServiceTests"`
Expected: PASS (7 tests).

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Core/Binaries src/FFMedia.Core/CoreServiceCollectionExtensions.cs src/FFMedia.Tests/Binaries/BinaryUpdateServiceTests.cs
git commit -m "feat(core): IBinaryUpdateService (versions + yt-dlp self-update)"
```

---

## Task 4: `AppSettings` v3 flag

**Files:**
- Modify: `src/FFMedia.Core/Settings/AppSettings.cs`
- Test: `src/FFMedia.Tests/Settings/AppSettingsYtDlpFlagTests.cs`

**Interfaces:**
- Produces: `AppSettings.CheckYtDlpForUpdatesOnStartup` (bool, default `true`); `AppSettings.Version == 3`.

- [ ] **Step 1: Write the failing test**

`src/FFMedia.Tests/Settings/AppSettingsYtDlpFlagTests.cs`:
```csharp
using System;
using System.IO;
using FFMedia.Core.Persistence;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class AppSettingsYtDlpFlagTests
{
    [Fact]
    public void Default_EnablesYtDlpStartupCheck_AndIsVersion3()
    {
        var d = AppSettings.Default;
        Assert.True(d.CheckYtDlpForUpdatesOnStartup);
        Assert.Equal(3, d.Version);
    }

    [Fact]
    public void LoadingV2FileWithoutFlag_DefaultsFlagToTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Version": 2, "MaxConcurrency": 3, "Theme": "System", "CheckForUpdatesOnStartup": true }""");

        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var loaded = store.Load(() => AppSettings.Default);

        Assert.True(loaded.CheckYtDlpForUpdatesOnStartup); // missing field → default true
    }

    [Fact]
    public void FlagRoundTripsThroughJsonStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var original = AppSettings.Default with { CheckYtDlpForUpdatesOnStartup = false };

        store.Save(original);

        Assert.Equal(original, store.Load(() => AppSettings.Default));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~AppSettingsYtDlpFlagTests"`
Expected: FAIL — `CheckYtDlpForUpdatesOnStartup` does not exist / `Version` is 2.

- [ ] **Step 3: Update `AppSettings`**

In `src/FFMedia.Core/Settings/AppSettings.cs`, change the `Version` default to `3` and add the flag after `CheckForUpdatesOnStartup`:
```csharp
    public int Version { get; init; } = 3;
    public string DefaultOutputFolder { get; init; } = DefaultFolder();
    public int MaxConcurrency { get; init; } = 3;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public bool CheckForUpdatesOnStartup { get; init; } = true;
    public bool CheckYtDlpForUpdatesOnStartup { get; init; } = true;
```

- [ ] **Step 4: Verify the existing v2 test still holds**

The older `AppSettingsUpdateFlagTests.Default_EnablesStartupUpdateCheck_AndIsVersion2` asserts `Version == 2`. Update that single assertion to `3`:

In `src/FFMedia.Tests/Settings/AppSettingsUpdateFlagTests.cs`, rename the test and fix the version assertion:
```csharp
    [Fact]
    public void Default_EnablesStartupUpdateCheck_AndIsVersion3()
    {
        var d = AppSettings.Default;
        Assert.True(d.CheckForUpdatesOnStartup);
        Assert.Equal(3, d.Version);
    }
```

- [ ] **Step 5: Run both settings test classes**

Run: `dotnet test src/FFMedia.Tests --filter "Category!=Integration&FullyQualifiedName~AppSettings"`
Expected: PASS (all AppSettings tests, old + new).

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Core/Settings/AppSettings.cs src/FFMedia.Tests/Settings/AppSettingsYtDlpFlagTests.cs src/FFMedia.Tests/Settings/AppSettingsUpdateFlagTests.cs
git commit -m "feat(core): AppSettings v3 — CheckYtDlpForUpdatesOnStartup flag"
```

---

## Task 5: `BinaryUpdateViewModel` + DI wiring (App)

**Files:**
- Create: `src/FFMedia.App/ViewModels/BinaryUpdateViewModel.cs`
- Modify: `src/FFMedia.App/App.xaml.cs`

**Interfaces:**
- Consumes: `IBinaryUpdateService` (Task 3); `INotificationService.Notify(Notification)`; `Notification(string Title, string Message, NotificationSeverity Severity)`; `NotificationSeverity.Info`; `ExternalBinary`.
- Produces: `BinaryUpdateViewModel` with `YtDlpVersion`, `FfmpegVersion`, `IsYtDlpUpdateAvailable`, `StatusMessage`, `IsBusy`; commands `RefreshVersionsCommand`, `UpdateYtDlpCommand`; method `CheckOnStartupAsync()`.

- [ ] **Step 1: Write the ViewModel**

`src/FFMedia.App/ViewModels/BinaryUpdateViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.Binaries;
using FFMedia.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace FFMedia.App.ViewModels;

/// <summary>Drives the Settings "Binaries" section and the startup yt-dlp check. Singleton so
/// the startup check and Settings share one instance (mirrors <see cref="UpdateViewModel"/>).</summary>
public partial class BinaryUpdateViewModel : ObservableObject
{
    private readonly IBinaryUpdateService _binaries;
    private readonly INotificationService _notifications;
    private readonly ILogger<BinaryUpdateViewModel> _logger;

    public BinaryUpdateViewModel(
        IBinaryUpdateService binaries, INotificationService notifications, ILogger<BinaryUpdateViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(logger);
        _binaries = binaries;
        _notifications = notifications;
        _logger = logger;
    }

    [ObservableProperty] private string _ytDlpVersion = "…";
    [ObservableProperty] private string _ffmpegVersion = "…";
    [ObservableProperty] private bool _isYtDlpUpdateAvailable;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        YtDlpVersion = await _binaries.GetInstalledVersionAsync(ExternalBinary.YtDlp) ?? "unknown";
        FfmpegVersion = await _binaries.GetInstalledVersionAsync(ExternalBinary.Ffmpeg) ?? "unknown";
    }

    [RelayCommand]
    private async Task UpdateYtDlpAsync()
    {
        IsBusy = true;
        StatusMessage = "Updating yt-dlp…";
        try
        {
            var result = await _binaries.UpdateYtDlpAsync();
            StatusMessage = result.Message;
            YtDlpVersion = result.ToVersion ?? YtDlpVersion;
            IsYtDlpUpdateAvailable = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp update failed");
            StatusMessage = "Update failed. See logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Background check invoked once at startup. Never throws; a dead feed is logged.</summary>
    public async Task CheckOnStartupAsync()
    {
        try
        {
            var latest = await _binaries.GetLatestYtDlpVersionAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(latest))
            {
                IsYtDlpUpdateAvailable = true;
                _notifications.Notify(new Notification(
                    "yt-dlp update available",
                    "A newer yt-dlp is available — update it in Settings.",
                    NotificationSeverity.Info));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup yt-dlp check failed");
        }
    }
}
```

- [ ] **Step 2: Register in DI**

In `src/FFMedia.App/App.xaml.cs`, in `ConfigureServices`, add after the `UpdateViewModel` registration:
```csharp
                services.AddSingleton<FFMedia.App.ViewModels.BinaryUpdateViewModel>();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/FFMedia.App -c Debug`
Expected: Build succeeded, 0 warnings / 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/FFMedia.App/ViewModels/BinaryUpdateViewModel.cs src/FFMedia.App/App.xaml.cs
git commit -m "feat(app): BinaryUpdateViewModel for versions + yt-dlp update"
```

---

## Task 6: Settings page — Binaries section

**Files:**
- Modify: `src/FFMedia.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/FFMedia.App/Views/SettingsPage.xaml`
- Modify: `src/FFMedia.App/Views/SettingsPage.xaml.cs`

**Interfaces:**
- Consumes: `BinaryUpdateViewModel` (Task 5); `AppSettings.CheckYtDlpForUpdatesOnStartup` (Task 4).
- Produces: `SettingsViewModel.Binaries` property; `SettingsViewModel.CheckYtDlpForUpdatesOnStartup` bound property.

- [ ] **Step 1: Extend `SettingsViewModel`**

In `src/FFMedia.App/ViewModels/SettingsViewModel.cs`:

Change the constructor signature and body to take and expose the binaries VM, and read the new flag. Replace the constructor and add the property + observable field:
```csharp
    public SettingsViewModel(
        ISettingsService settings, ThemeService theme, UpdateViewModel updates, BinaryUpdateViewModel binaries)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(binaries);
        _settings = settings;
        _theme = theme;
        Updates = updates;
        Binaries = binaries;

        var current = settings.Current;
        _defaultOutputFolder = current.DefaultOutputFolder;
        _maxConcurrency = current.MaxConcurrency;
        _selectedTheme = current.Theme;
        _checkForUpdatesOnStartup = current.CheckForUpdatesOnStartup;
        _checkYtDlpForUpdatesOnStartup = current.CheckYtDlpForUpdatesOnStartup;
    }
```
Add the observable field next to `_checkForUpdatesOnStartup`:
```csharp
    [ObservableProperty] private bool _checkYtDlpForUpdatesOnStartup;
```
Add the property next to `Updates`:
```csharp
    /// <summary>Shared binary-update state (also drives the startup yt-dlp check).</summary>
    public BinaryUpdateViewModel Binaries { get; }
```
In `Save()`, add the new field to the `with` expression:
```csharp
        var updated = _settings.Current with
        {
            DefaultOutputFolder = DefaultOutputFolder,
            MaxConcurrency = Math.Max(1, MaxConcurrency),
            Theme = SelectedTheme,
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
            CheckYtDlpForUpdatesOnStartup = CheckYtDlpForUpdatesOnStartup,
        };
```

- [ ] **Step 2: Add the "Binaries" section to `SettingsPage.xaml`**

In `src/FFMedia.App/Views/SettingsPage.xaml`, insert this block after the `Updates` section (after the `TextBlock` bound to `Updates.StatusMessage`, before the `Save` button):
```xml
        <TextBlock Text="Binaries" FontSize="18" FontWeight="SemiBold" Margin="0,24,0,4" />
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="yt-dlp" VerticalAlignment="Center" MinWidth="64" />
            <TextBlock Grid.Column="1" VerticalAlignment="Center" Margin="8,0,0,0"
                       Text="{Binding Binaries.YtDlpVersion}"
                       Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
            <ui:Button Grid.Column="2" Content="Update yt-dlp"
                       Command="{Binding Binaries.UpdateYtDlpCommand}"
                       IsEnabled="{Binding Binaries.CanUpdate}" />
        </Grid>
        <Grid Margin="0,8,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="ffmpeg" VerticalAlignment="Center" MinWidth="64" />
            <TextBlock Grid.Column="1" VerticalAlignment="Center" Margin="8,0,0,0"
                       Text="{Binding Binaries.FfmpegVersion, StringFormat='{}{0}  (updates with the app)'}"
                       Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
        </Grid>
        <ui:ToggleSwitch Content="Check yt-dlp for updates on startup" Margin="0,10,0,0"
                         IsChecked="{Binding CheckYtDlpForUpdatesOnStartup, Mode=TwoWay}" />
        <TextBlock Text="{Binding Binaries.StatusMessage}"
                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" Margin="0,6,0,0" />
```

The `IsEnabled` binding uses a `CanUpdate` computed property (WPF-UI ships no plain inverse-bool→bool converter, so we expose the negation on the VM). Go back to `src/FFMedia.App/ViewModels/BinaryUpdateViewModel.cs` (Task 5) and replace `[ObservableProperty] private bool _isBusy;` with:
```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool _isBusy;

    public bool CanUpdate => !IsBusy;
```
(`CanUpdate` re-raises whenever `IsBusy` changes, so the button re-enables when the update finishes.)

- [ ] **Step 3: Load versions when the page opens**

In `src/FFMedia.App/Views/SettingsPage.xaml.cs`, wire the `Loaded` event to refresh versions. Read the current file first; then ensure the constructor subscribes:
```csharp
        Loaded += (_, _) =>
        {
            if (DataContext is FFMedia.App.ViewModels.SettingsViewModel vm)
            {
                vm.Binaries.RefreshVersionsCommand.Execute(null);
            }
        };
```
Place this at the end of the existing constructor (after `InitializeComponent();` and whatever DataContext assignment exists).

- [ ] **Step 4: Build and smoke-run**

Run: `dotnet build src/FFMedia.App -c Debug`
Expected: Build succeeded, 0 warnings / 0 errors.

Then manually: `dotnet run --project src/FFMedia.App` → open Settings → the Binaries section shows yt-dlp + ffmpeg versions (requires `build/fetch-binaries.ps1` to have populated `assets/binaries/`), the toggle reflects the setting, and "Update yt-dlp" is clickable. (Manual per SDD §14.)

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.App/ViewModels/SettingsViewModel.cs src/FFMedia.App/Views/SettingsPage.xaml src/FFMedia.App/Views/SettingsPage.xaml.cs
git commit -m "feat(app): Settings Binaries section (versions + update yt-dlp)"
```

---

## Task 7: Startup yt-dlp check

**Files:**
- Modify: `src/FFMedia.App/App.xaml.cs`

**Interfaces:**
- Consumes: `BinaryUpdateViewModel.CheckOnStartupAsync()` (Task 5); `AppSettings.CheckYtDlpForUpdatesOnStartup` (Task 4).

- [ ] **Step 1: Add the fire-and-forget startup check**

In `src/FFMedia.App/App.xaml.cs`, in `OnStartup`, right after the existing app-update block:
```csharp
        if (settings.Current.CheckForUpdatesOnStartup)
        {
            var updates = _host.Services.GetRequiredService<FFMedia.App.ViewModels.UpdateViewModel>();
            _ = updates.CheckOnStartupAsync(); // fire-and-forget; swallows+logs its own errors
        }
```
add:
```csharp
        if (settings.Current.CheckYtDlpForUpdatesOnStartup)
        {
            var binaries = _host.Services.GetRequiredService<FFMedia.App.ViewModels.BinaryUpdateViewModel>();
            _ = binaries.CheckOnStartupAsync(); // fire-and-forget; swallows+logs its own errors
        }
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FFMedia.App -c Debug`
Expected: Build succeeded, 0 warnings / 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/FFMedia.App/App.xaml.cs
git commit -m "feat(app): notify on startup when a yt-dlp update is available"
```

---

## Task 8: Pin `fetch-binaries.ps1` with hash verification

**Files:**
- Modify: `build/fetch-binaries.ps1`

- [ ] **Step 1: Compute the ffmpeg zip hash**

Download the pinned ffmpeg zip once and record its SHA-256 (needed for the script constant):
```bash
curl -sL -o /tmp/ffmpeg-pinned.zip \
  "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-07-07-13-44/ffmpeg-n8.1.2-22-g94138f6973-win64-gpl-8.1.zip"
sha256sum /tmp/ffmpeg-pinned.zip
```
Record the printed hash — it replaces `PUT_FFMPEG_ZIP_SHA256_HERE` in Step 2.

- [ ] **Step 2: Rewrite the script**

Replace the entire contents of `build/fetch-binaries.ps1` with:
```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  Downloads PINNED yt-dlp.exe and ffmpeg.exe into assets/binaries/ and verifies SHA-256.
.NOTES
  Versions/hashes are pinned for reproducible builds (SDD §9, §16). yt-dlp's hash is
  cross-checked against its official SHA2-256SUMS; ffmpeg's is computed from the pinned
  BtbN zip (BtbN publishes no sums). Bump these deliberately, not automatically.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\assets\binaries')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# --- Pinned versions + hashes ---
$YtDlpVersion    = '2026.07.04'
$YtDlpSha256     = '52fe3c26dcf71fbdc85b528589020bb0b8e383155cfa81b64dd447bbe35e24b8'
$FfmpegTag       = 'autobuild-2026-07-07-13-44'
$FfmpegAsset     = 'ffmpeg-n8.1.2-22-g94138f6973-win64-gpl-8.1.zip'
$FfmpegZipSha256 = 'PUT_FFMPEG_ZIP_SHA256_HERE'

function Assert-Hash([string]$Path, [string]$Expected, [string]$Name) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "$Name SHA-256 mismatch.`n  expected: $Expected`n  actual:   $actual"
    }
    Write-Host "  verified $Name ($actual)"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# --- yt-dlp (pinned single exe) ---
$ytdlp = Join-Path $OutDir 'yt-dlp.exe'
Write-Host "Downloading yt-dlp $YtDlpVersion -> $ytdlp"
Invoke-WebRequest -Uri "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpVersion/yt-dlp.exe" -OutFile $ytdlp
Assert-Hash -Path $ytdlp -Expected $YtDlpSha256 -Name 'yt-dlp.exe'

# --- ffmpeg (pinned BtbN gpl build; verify the zip, then extract ffmpeg.exe) ---
$ffmpegExe = Join-Path $OutDir 'ffmpeg.exe'
$tmpZip = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N') + '.zip')
$tmpDir = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Host "Downloading ffmpeg $FfmpegTag..."
    Invoke-WebRequest -Uri "https://github.com/BtbN/FFmpeg-Builds/releases/download/$FfmpegTag/$FfmpegAsset" -OutFile $tmpZip
    Assert-Hash -Path $tmpZip -Expected $FfmpegZipSha256 -Name 'ffmpeg zip'
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

- [ ] **Step 3: Run the pinned script to prove it verifies clean**

Run (PowerShell): `powershell -ExecutionPolicy Bypass -File build/fetch-binaries.ps1`
Expected: prints `verified yt-dlp.exe (...)` and `verified ffmpeg zip (...)`, then the resolved versions (`2026.07.04` and an `8.1` ffmpeg line). No hash-mismatch throw.

- [ ] **Step 4: Commit**

```bash
git add build/fetch-binaries.ps1
git commit -m "build: pin yt-dlp/ffmpeg versions + verify SHA-256 (SDD §16, §19)"
```

---

## Task 9: App logo — move, convert, wire up

**Files:**
- Move: `logo.png` → `assets/branding/logo.png`
- Create: `build/make-icon.ps1`
- Create: `assets/branding/app.ico` (generated)
- Modify: `src/FFMedia.App/FFMedia.App.csproj`
- Modify: `src/FFMedia.App/MainWindow.xaml`
- Modify: `src/FFMedia.App/Views/WelcomePage.xaml`
- Modify: `build/pack.ps1`

- [ ] **Step 1: Move the logo**

```bash
mkdir -p assets/branding
git mv logo.png assets/branding/logo.png
```

- [ ] **Step 2: Write the icon generator**

`build/make-icon.ps1`:
```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  Generates a multi-resolution app.ico from assets/branding/logo.png (16..256, PNG-compressed).
#>
[CmdletBinding()]
param(
    [string]$Png = (Join-Path $PSScriptRoot '..\assets\branding\logo.png'),
    [string]$Ico = (Join-Path $PSScriptRoot '..\assets\branding\app.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 32, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Png).Path)
try {
    $pngStreams = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($src, 0, 0, $s, $s)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngStreams += ,($ms.ToArray())
        $ms.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    # ICONDIR
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $bytes = $pngStreams[$i]
        $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # width  (0 = 256)
        $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # height (0 = 256)
        $bw.Write([Byte]0); $bw.Write([Byte]0)                   # colors, reserved
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)              # planes, bpp
        $bw.Write([UInt32]$bytes.Length)                         # size
        $bw.Write([UInt32]$offset)                               # offset
        $offset += $bytes.Length
    }
    foreach ($bytes in $pngStreams) { $bw.Write($bytes) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path (Split-Path $Ico) (Split-Path $Ico -Leaf)), $out.ToArray())
    $bw.Dispose(); $out.Dispose()
    Write-Host "Wrote $Ico"
}
finally {
    $src.Dispose()
}
```

- [ ] **Step 3: Generate the ico**

Run (PowerShell): `powershell -ExecutionPolicy Bypass -File build/make-icon.ps1`
Expected: `Wrote ...assets/branding/app.ico`. Verify with: `file assets/branding/app.ico` (or open it) → an `MS Windows icon resource` with multiple images.

- [ ] **Step 4: Wire the icon into the csproj**

In `src/FFMedia.App/FFMedia.App.csproj`, add `<ApplicationIcon>` inside the existing `<PropertyGroup>` (the one with `<OutputType>`):
```xml
    <ApplicationIcon>..\..\assets\branding\app.ico</ApplicationIcon>
```
And add a new `<ItemGroup>` linking the png as a WPF `Resource` for in-app pack-URI use:
```xml
  <ItemGroup>
    <Resource Include="..\..\assets\branding\logo.png" Link="Assets\logo.png" />
  </ItemGroup>
```

- [ ] **Step 5: Window icon + nav-pane header**

In `src/FFMedia.App/MainWindow.xaml`, add `Icon` to the `FluentWindow` opening tag (after `Title="FFMedia"`):
```xml
                 Icon="pack://application:,,,/Assets/logo.png"
```
And give the `NavigationView` a pane header logo — replace the `<ui:NavigationView Grid.Row="2" ... />` self-closing element with one that carries a header:
```xml
        <ui:NavigationView Grid.Row="2" x:Name="RootNavigation"
                           MenuItemsSource="{Binding MenuItems}"
                           FooterMenuItemsSource="{Binding FooterMenuItems}"
                           IsBackButtonVisible="Collapsed"
                           PaneDisplayMode="Left">
            <ui:NavigationView.Header>
                <Image Source="pack://application:,,,/Assets/logo.png"
                       Height="28" HorizontalAlignment="Left" Margin="16,8" />
            </ui:NavigationView.Header>
        </ui:NavigationView>
```

- [ ] **Step 6: Welcome page logo**

In `src/FFMedia.App/Views/WelcomePage.xaml`, replace the `<ui:SymbolIcon Symbol="Play24" .../>` line with:
```xml
        <Image Source="pack://application:,,,/Assets/logo.png" Width="96" Height="96" HorizontalAlignment="Center" />
```

- [ ] **Step 7: Installer icon in pack.ps1**

In `build/pack.ps1`, add an `--icon` argument to the `vpk pack` invocation (after `--packTitle 'FFMedia'`):
```powershell
    --icon (Join-Path $root 'assets/branding/app.ico') `
```

- [ ] **Step 8: Build and smoke-run**

Run: `dotnet build src/FFMedia.App -c Debug`
Expected: Build succeeded, 0 warnings / 0 errors.

Then: `dotnet run --project src/FFMedia.App` → the window/taskbar shows the logo, the nav pane shows the logo header, and the welcome page shows the logo. (Manual per SDD §14.)

- [ ] **Step 9: Commit**

```bash
git add assets/branding build/make-icon.ps1 build/pack.ps1 src/FFMedia.App/FFMedia.App.csproj src/FFMedia.App/MainWindow.xaml src/FFMedia.App/Views/WelcomePage.xaml
git commit -m "feat(app): use logo for app icon (exe/window/installer) + in-app branding"
```

---

## Task 10: Docs — SDD v0.10 + progress log

**Files:**
- Modify: `SDD.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update SDD**

In `SDD.md`:
- Header: bump **Version** to `0.10`, update **Last updated** to `2026-07-08`.
- §6: add a "M6 PR 2 note" that `IProcessRunner` and `IBinaryUpdateService` are now **realized** in `FFMedia.Core` (process seam + version/self-update; the latter also queries the GitHub API for the latest yt-dlp version).
- §9: record the **pinned versions** (yt-dlp `2026.07.04`; ffmpeg BtbN `autobuild-2026-07-07-13-44`, `ffmpeg-n8.1.2-...-win64-gpl-8.1.zip`) and their SHA-256 verification; note the **yt-dlp self-update** is realized (`yt-dlp -U`) and that a Velopack app update re-bundles the pinned yt-dlp (reverting a self-update — expected).
- §10: note `AppSettings.Version` moves to **3** with `CheckYtDlpForUpdatesOnStartup` (default true).
- §13: note the Settings **Binaries** section (yt-dlp/ffmpeg versions, "Update yt-dlp", startup-check toggle) and the **app logo** (window/taskbar/nav/welcome).
- §16: note binaries are pinned + SHA-256-verified in `fetch-binaries.ps1`.
- §17: mark **M6 PR 2 delivered** in the M6 row; note the public v1 tag remains user-initiated.
- §19: resolve "Which yt-dlp/ffmpeg versions to pin for v1" — record the pinned values.
- Changelog: add a `0.10` row dated `2026-07-08` summarizing this PR.

- [ ] **Step 2: Update CLAUDE.md progress log**

In `CLAUDE.md`, add a new entry at the **top** of the Progress Log:
```markdown
### 2026-07-08 — M6 Ship v1 (PR 2: binary updates + app logo)

- **Done:** yt-dlp self-update + pinned binaries + app logo. Core gained `IProcessRunner`/
  `ProcessRunner` (the process seam, SDD §6) and `IBinaryUpdateService`/`BinaryUpdateService`
  (installed versions via `--version`/`-version`, `yt-dlp -U` self-update, and a GitHub
  latest-version check). A singleton `BinaryUpdateViewModel` drives a Settings **Binaries**
  section (yt-dlp + ffmpeg versions, "Update yt-dlp", "check yt-dlp on startup" toggle) and a
  fire-and-forget startup check that notifies (never auto-applies). `AppSettings` → schema
  **v3** (`CheckYtDlpForUpdatesOnStartup`, default true). `fetch-binaries.ps1` now pins
  yt-dlp **2026.07.04** and ffmpeg BtbN **autobuild-2026-07-07-13-44** and **verifies SHA-256**
  (throws on mismatch). `logo.png` moved to `assets/branding/`, converted to a committed
  multi-res `app.ico` (via `build/make-icon.ps1`), and wired as the exe/window/taskbar/
  installer icon + in-app branding (nav header + welcome page). **Verified:** Release build
  0/0, all unit tests pass (`Category!=Integration`), pinned `fetch-binaries.ps1` runs and
  verifies clean. **Not verified (pending user dry-run):** headed GUI smoke of the Binaries
  section, the real `yt-dlp -U`, and the logo surfaces. SDD → v0.10.
- **Decisions:** yt-dlp self-update via `yt-dlp -U`; ffmpeg has no self-update (rides app
  releases); startup check notifies only; both binaries pinned + hash-verified (ffmpeg hash
  computed once from the pinned zip); logo used everywhere. App-layer VMs verified by build +
  manual per the M5/M6 precedent.
- **Next:** user performs the headed dry-run; the public **v1.0.0** tag (machinery proven in
  PR 1) is user-initiated.
```

- [ ] **Step 3: Commit**

```bash
git add SDD.md CLAUDE.md
git commit -m "docs: SDD v0.10 + progress log for M6 PR 2 (binary updates + logo)"
```

---

## Final verification (before opening the PR)

- [ ] **Full build:** `dotnet build -c Release` → 0 warnings / 0 errors.
- [ ] **Full unit suite:** `dotnet test src/FFMedia.Tests --filter "Category!=Integration"` → all pass (152 existing + new).
- [ ] **Pinned fetch proven:** `build/fetch-binaries.ps1` ran clean (Task 8, Step 3).
- [ ] **Whole-branch review:** run `superpowers:requesting-code-review` (or `/code-review`) over the branch diff; address findings.
- [ ] **Push + PR:** push `feat/m6-binary-updates`, open a PR for the user to review. **Do not merge.**

---

## Self-Review notes

- **Spec coverage:** §2 IProcessRunner→Task 1; §3 IBinaryUpdateService (+ ffmpeg parse)→Tasks 2–3; §4 Settings UI + startup + v3 flag→Tasks 4–7; §5 pinned fetch→Task 8; §6 logo→Task 9; §7 docs/testing→Task 10 + Final verification. All covered.
- **Placeholder note:** the only intentional literal is `PUT_FFMPEG_ZIP_SHA256_HERE` in Task 8, replaced in Step 1 of that task by computing the real hash — a data-gathering step with an exact command, not a code placeholder.
- **Type consistency:** `RefreshVersionsCommand`/`UpdateYtDlpCommand`/`CheckOnStartupAsync`/`CanUpdate`/`Binaries` names are used consistently across Tasks 5–7; `BinaryUpdateResult(Updated, FromVersion, ToVersion, Message)` matches its usages; `ExternalBinary.{YtDlp,Ffmpeg}` and `Notification(Title, Message, Severity)` match the existing Core types.
