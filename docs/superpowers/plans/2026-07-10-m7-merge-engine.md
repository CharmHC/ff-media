# M7 PR 1 — Video Merger Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the headless engine for FFMedia's Video Merger tool — media probing, ffmpeg execution, the pure decision functions, and `MergeService` — with no UI.

**Architecture:** `FFMedia.Media` (net9.0, UI-free) gains `IMediaAnalyzer` (ffprobe → `MediaInfo`) and `IFfmpegRunner` (ffmpeg + progress), both driven through Core's existing `IProcessRunner` seam with pure parsers. `FFMedia.Tools.VideoMerger` (net9.0-windows) holds the pure engine (target derivation, conformance, arg builders, ordering, estimator) and `MergeService`, which normalizes only non-conforming clips under a `SemaphoreSlim` cap, then stream-copy concats. Every consequential decision is a pure, unit-tested function; every process call goes through a fakeable interface.

**Tech Stack:** C# / .NET 9 · xUnit (plain `Assert`, no FluentAssertions) · `System.Text.Json` · `Microsoft.Extensions.Logging.Abstractions` · bundled `ffmpeg.exe`/`ffprobe.exe`.

**Spec:** `docs/superpowers/specs/2026-07-10-m7-video-merger-design.md` (read it before starting).

## Global Constraints

- Branch: `feat/m7-merge-engine`. Never commit to `main`. Deliver via PR (CLAUDE.md Rule 3).
- `FFMedia.Core` and `FFMedia.Media` target `net9.0`, reference **no** UI framework. `FFMedia.Core` has `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`; `FFMedia.Media` must too.
- `FFMedia.Tools.VideoMerger` targets `net9.0-windows` with `<UseWPF>true</UseWPF>` (matches `FFMedia.Tools.YouTubeDownloader`).
- Nullable reference types **on** everywhere. One public type per file; file name matches type.
- **No new NuGet dependencies.** FFMpegCore is explicitly rejected (spec D6).
- Tests use plain xUnit `Assert` (FluentAssertions v8+ is paid). `FFMedia.Tests` targets `net9.0-windows` and has `<Using Include="Xunit" />` already — do not re-import `Xunit` in test files unless the file also needs `using Xunit;` for attributes not covered (existing tests do `using Xunit;` explicitly; follow suit).
- Integration tests are trait-gated with `[Trait("Category", "Integration")]`; CI runs `--filter "Category!=Integration"`.
- No UI in this PR. No ViewModels, no XAML, no `ITool` registration (those are PR 2).
- `async`/`await` end-to-end; no blocking `.Result`.
- Verification command for every task: `dotnet test FFMedia.sln --filter "Category!=Integration"` (from repo root). Baseline before you start: **189 passing**.

---

### Task 1: Non-generic `Result` in Core

The spec's `DiskSpaceGuard` and `IFfmpegRunner` return "succeeded, or failed with a reason" and carry no value. Core today has only `Result<T>` (`src/FFMedia.Core/Results/Result.cs`). Add the non-generic sibling.

**Files:**
- Create: `src/FFMedia.Core/Results/UnitResult.cs` — holds the non-generic `Result`. Note `src/FFMedia.Core/Results/Result.cs` already exists and holds `Result<T>`, so the filename cannot match the type name here. This is a deliberate, one-off exception to the "file name matches type" convention, called out in the new file's header comment.
- Test: `src/FFMedia.Tests/Results/ResultTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `FFMedia.Core.Results.Result` with `bool IsSuccess`, `string? Error`, `static Result Success()`, `static Result Failure(string error)`.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/Results/ResultTests.cs`:

```csharp
using FFMedia.Core.Results;
using Xunit;

namespace FFMedia.Tests.Results;

public class ResultTests
{
    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_CarriesError()
    {
        var result = Result.Failure("boom");
        Assert.False(result.IsSuccess);
        Assert.Equal("boom", result.Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~ResultTests"`
Expected: FAIL — compile error, `Result` does not contain a definition for `Success()` taking zero arguments.

- [ ] **Step 3: Write minimal implementation**

Create `src/FFMedia.Core/Results/UnitResult.cs`:

```csharp
namespace FFMedia.Core.Results;

/// <summary>Outcome of an operation that carries no value but may fail with a user-facing reason.
/// Lives in UnitResult.cs because Result.cs holds the generic <see cref="Result{T}"/>.</summary>
public sealed class Result
{
    private Result(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public string? Error { get; }

    public static Result Success() => new(true, null);
    public static Result Failure(string error) => new(false, error);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~ResultTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Core/Results/UnitResult.cs src/FFMedia.Tests/Results/ResultTests.cs
git commit -m "feat(core): add non-generic Result for value-less outcomes"
```

---

### Task 2: `ffprobe.exe` binary plumbing

`ffprobe.exe` is inside the **same** pinned, SHA-256-verified BtbN zip already downloaded for `ffmpeg.exe`. No new download, no new hash.

**Files:**
- Modify: `src/FFMedia.Core/Binaries/ExternalBinary.cs`
- Modify: `src/FFMedia.Core/Binaries/BundledBinaryProvider.cs:6-11` (the `FileNames` dictionary)
- Modify: `build/fetch-binaries.ps1:48-69`
- Test: `src/FFMedia.Tests/Binaries/BundledBinaryProviderTests.cs` (append)

**Interfaces:**
- Consumes: `ExternalBinary`, `IBinaryProvider.GetPath`.
- Produces: `ExternalBinary.Ffprobe`; `BundledBinaryProvider.GetPath(ExternalBinary.Ffprobe)` → `<dir>/ffprobe.exe`.

- [ ] **Step 1: Write the failing test**

Append to `src/FFMedia.Tests/Binaries/BundledBinaryProviderTests.cs` (inside the existing class):

```csharp
    [Fact]
    public void GetPath_ResolvesFfprobe()
    {
        var provider = new BundledBinaryProvider(@"C:\bin");
        Assert.Equal(Path.Combine(@"C:\bin", "ffprobe.exe"), provider.GetPath(ExternalBinary.Ffprobe));
    }
```

If `System.IO` is not already imported in that file, add `using System.IO;` at the top.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~BundledBinaryProviderTests"`
Expected: FAIL — compile error, `ExternalBinary` does not contain a definition for `Ffprobe`.

- [ ] **Step 3: Write minimal implementation**

`src/FFMedia.Core/Binaries/ExternalBinary.cs`:

```csharp
namespace FFMedia.Core.Binaries;

/// <summary>External command-line binaries FFMedia orchestrates.</summary>
public enum ExternalBinary
{
    YtDlp,
    Ffmpeg,
    Ffprobe
}
```

`src/FFMedia.Core/Binaries/BundledBinaryProvider.cs` — extend the dictionary:

```csharp
    private static readonly IReadOnlyDictionary<ExternalBinary, string> FileNames =
        new Dictionary<ExternalBinary, string>
        {
            [ExternalBinary.YtDlp] = "yt-dlp.exe",
            [ExternalBinary.Ffmpeg] = "ffmpeg.exe",
            [ExternalBinary.Ffprobe] = "ffprobe.exe",
        };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~BundledBinaryProviderTests"`
Expected: PASS.

- [ ] **Step 5: Extract ffprobe.exe in the fetch script**

In `build/fetch-binaries.ps1`, replace the ffmpeg block (currently lines 48-65) with:

```powershell
# --- ffmpeg + ffprobe (pinned BtbN gpl build; verify the zip once, then extract both exes) ---
$ffmpegExe  = Join-Path $OutDir 'ffmpeg.exe'
$ffprobeExe = Join-Path $OutDir 'ffprobe.exe'
$tmpZip = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N') + '.zip')
$tmpDir = Join-Path $env:TEMP ("ffmpeg-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Host "Downloading ffmpeg $FfmpegTag..."
    Invoke-WebRequest -Uri "https://github.com/BtbN/FFmpeg-Builds/releases/download/$FfmpegTag/$FfmpegAsset" -OutFile $tmpZip
    Assert-Hash -Path $tmpZip -Expected $FfmpegZipSha256 -Name 'ffmpeg zip'
    Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force
    foreach ($pair in @(@('ffmpeg.exe', $ffmpegExe), @('ffprobe.exe', $ffprobeExe))) {
        $found = Get-ChildItem -Path $tmpDir -Recurse -Filter $pair[0] | Select-Object -First 1
        if (-not $found) { throw "$($pair[0]) not found in downloaded archive." }
        Copy-Item -Path $found.FullName -Destination $pair[1] -Force
        Write-Host "Extracted $($pair[0]) -> $($pair[1])"
    }
}
finally {
    Remove-Item -Path $tmpZip -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}
```

And update the synopsis on line 4 to `Downloads PINNED yt-dlp.exe, ffmpeg.exe and ffprobe.exe into assets/binaries/ and verifies SHA-256.`, and append after the existing `& $ffmpegExe -version | Select-Object -First 1`:

```powershell
& $ffprobeExe -version | Select-Object -First 1
```

- [ ] **Step 6: Run the fetch script for real**

Run: `powershell -ExecutionPolicy Bypass -File build/fetch-binaries.ps1`
Expected: prints `verified yt-dlp.exe`, `verified ffmpeg zip`, `Extracted ffmpeg.exe -> …`, `Extracted ffprobe.exe -> …`, then three version lines. Confirm `assets/binaries/ffprobe.exe` exists.

If the machine is offline, skip this step and note it — the unit test above still gates the code change.

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Core/Binaries/ExternalBinary.cs src/FFMedia.Core/Binaries/BundledBinaryProvider.cs build/fetch-binaries.ps1 src/FFMedia.Tests/Binaries/BundledBinaryProviderTests.cs
git commit -m "feat(core): bundle ffprobe.exe from the pinned ffmpeg archive"
```

---

### Task 3: `FFMedia.Media` project setup + `MediaInfo` models + `FfprobeParsing`

**Files:**
- Modify: `src/FFMedia.Media/FFMedia.Media.csproj` (add `TreatWarningsAsErrors`)
- Modify: `src/FFMedia.Tests/FFMedia.Tests.csproj` (add a `ProjectReference` to `FFMedia.Media`)
- Create: `src/FFMedia.Media/FrameRate.cs`
- Create: `src/FFMedia.Media/VideoStreamInfo.cs`
- Create: `src/FFMedia.Media/AudioStreamInfo.cs`
- Create: `src/FFMedia.Media/MediaInfo.cs`
- Create: `src/FFMedia.Media/FfprobeParsing.cs`
- Test: `src/FFMedia.Tests/Media/FfprobeParsingTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `readonly record struct FrameRate(int Numerator, int Denominator)` with `double Value`, `string ToFfmpegString()`, `static bool TryParse(string, out FrameRate)`.
  - `sealed record VideoStreamInfo(int Width, int Height, FrameRate FrameRate, string CodecName, string PixelFormat, int Rotation)`
  - `sealed record AudioStreamInfo(string CodecName, int SampleRate, int Channels)`
  - `sealed record MediaInfo(TimeSpan Duration, string ContainerFormat, VideoStreamInfo? Video, AudioStreamInfo? Audio)` with `bool HasAudio => Audio is not null`.
  - `static class FfprobeParsing { public static MediaInfo? Parse(string json); }`

- [ ] **Step 1: Wire the projects**

`src/FFMedia.Media/FFMedia.Media.csproj` — add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to the existing `PropertyGroup`.

`src/FFMedia.Tests/FFMedia.Tests.csproj` — add to the existing `ProjectReference` `ItemGroup`:

```xml
    <ProjectReference Include="..\FFMedia.Media\FFMedia.Media.csproj" />
```

Run: `dotnet build FFMedia.sln`
Expected: build succeeds, 0 errors.

- [ ] **Step 2: Write the failing test**

Create `src/FFMedia.Tests/Media/FfprobeParsingTests.cs`:

```csharp
using FFMedia.Media;
using Xunit;

namespace FFMedia.Tests.Media;

public class FfprobeParsingTests
{
    private const string VideoWithAudioJson = """
    {
      "streams": [
        { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
          "avg_frame_rate": "30000/1001", "pix_fmt": "yuv420p" },
        { "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 2 }
      ],
      "format": { "format_name": "mov,mp4,m4a", "duration": "12.500000" }
    }
    """;

    private const string SilentVideoJson = """
    {
      "streams": [
        { "codec_type": "video", "codec_name": "vp9", "width": 1080, "height": 1920,
          "avg_frame_rate": "60/1", "pix_fmt": "yuv420p" }
      ],
      "format": { "format_name": "matroska,webm", "duration": "3.000000" }
    }
    """;

    [Fact]
    public void Parse_ReadsVideoAndAudio()
    {
        var info = FfprobeParsing.Parse(VideoWithAudioJson);

        Assert.NotNull(info);
        Assert.Equal(TimeSpan.FromSeconds(12.5), info!.Duration);
        Assert.Equal("mov,mp4,m4a", info.ContainerFormat);
        Assert.NotNull(info.Video);
        Assert.Equal(1920, info.Video!.Width);
        Assert.Equal(1080, info.Video.Height);
        Assert.Equal("h264", info.Video.CodecName);
        Assert.Equal("yuv420p", info.Video.PixelFormat);
        Assert.Equal(new FrameRate(30000, 1001), info.Video.FrameRate);
        Assert.True(info.HasAudio);
        Assert.Equal("aac", info.Audio!.CodecName);
        Assert.Equal(48000, info.Audio.SampleRate);
        Assert.Equal(2, info.Audio.Channels);
    }

    [Fact]
    public void Parse_HandlesMissingAudioStream()
    {
        var info = FfprobeParsing.Parse(SilentVideoJson);

        Assert.NotNull(info);
        Assert.False(info!.HasAudio);
        Assert.Null(info.Audio);
        Assert.Equal(new FrameRate(60, 1), info.Video!.FrameRate);
    }

    [Fact]
    public void Parse_ReadsRotationFromSideData()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "avg_frame_rate": "30/1", "pix_fmt": "yuv420p",
              "side_data_list": [ { "rotation": -90 } ] }
          ],
          "format": { "format_name": "mov", "duration": "1.0" }
        }
        """;

        Assert.Equal(-90, FfprobeParsing.Parse(json)!.Video!.Rotation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{ "streams": [], "format": { "format_name": "x", "duration": "1.0" } }""")]
    public void Parse_ReturnsNull_WhenUnusable(string json)
    {
        Assert.Null(FfprobeParsing.Parse(json));
    }

    [Theory]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("25/1", 25, 1)]
    public void FrameRate_TryParse_ReadsRational(string text, int num, int den)
    {
        Assert.True(FrameRate.TryParse(text, out var rate));
        Assert.Equal(new FrameRate(num, den), rate);
    }

    [Theory]
    [InlineData("0/0")]
    [InlineData("garbage")]
    [InlineData("30/0")]
    public void FrameRate_TryParse_RejectsUnusable(string text)
    {
        Assert.False(FrameRate.TryParse(text, out _));
    }

    [Fact]
    public void FrameRate_FormatsForFfmpeg()
    {
        Assert.Equal("30000/1001", new FrameRate(30000, 1001).ToFfmpegString());
        Assert.Equal(29.97, Math.Round(new FrameRate(30000, 1001).Value, 2));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfprobeParsingTests"`
Expected: FAIL — compile error, the type or namespace `FFMedia.Media` types do not exist.

- [ ] **Step 4: Write the models**

`src/FFMedia.Media/FrameRate.cs`:

```csharp
using System.Globalization;

namespace FFMedia.Media;

/// <summary>An exact frame rate as ffmpeg reports it (e.g. 30000/1001 for 29.97 fps).</summary>
public readonly record struct FrameRate(int Numerator, int Denominator)
{
    /// <summary>Frames per second as a decimal. Zero when the denominator is zero.</summary>
    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>The exact rational, suitable for ffmpeg's <c>fps=</c> filter.</summary>
    public string ToFfmpegString() => $"{Numerator}/{Denominator}";

    /// <summary>Parses ffmpeg's "num/den" rational. Rejects zero numerator or denominator.</summary>
    public static bool TryParse(string? text, out FrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
            || num <= 0 || den <= 0)
        {
            return false;
        }

        rate = new FrameRate(num, den);
        return true;
    }
}
```

`src/FFMedia.Media/VideoStreamInfo.cs`:

```csharp
namespace FFMedia.Media;

/// <summary>The first video stream of a media file.</summary>
public sealed record VideoStreamInfo(
    int Width,
    int Height,
    FrameRate FrameRate,
    string CodecName,
    string PixelFormat,
    int Rotation);
```

`src/FFMedia.Media/AudioStreamInfo.cs`:

```csharp
namespace FFMedia.Media;

/// <summary>The first audio stream of a media file.</summary>
public sealed record AudioStreamInfo(string CodecName, int SampleRate, int Channels);
```

`src/FFMedia.Media/MediaInfo.cs`:

```csharp
namespace FFMedia.Media;

/// <summary>What ffprobe tells us about a media file. <see cref="Audio"/> is null when the file
/// carries no audio stream — the case that breaks a naive ffmpeg concat.</summary>
public sealed record MediaInfo(
    TimeSpan Duration,
    string ContainerFormat,
    VideoStreamInfo? Video,
    AudioStreamInfo? Audio)
{
    public bool HasAudio => Audio is not null;
}
```

- [ ] **Step 5: Write `FfprobeParsing`**

`src/FFMedia.Media/FfprobeParsing.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace FFMedia.Media;

/// <summary>Pure parser for <c>ffprobe -print_format json -show_format -show_streams</c> output.
/// Returns null for anything unusable (no video stream, malformed JSON, missing duration).</summary>
public static class FfprobeParsing
{
    public static MediaInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format)
                || !root.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var video = ParseVideo(streams);
            if (video is null)
            {
                return null;
            }

            if (!TryGetDouble(format, "duration", out var seconds))
            {
                return null;
            }

            var container = GetString(format, "format_name") ?? "";
            return new MediaInfo(TimeSpan.FromSeconds(seconds), container, video, ParseAudio(streams));
        }
    }

    private static VideoStreamInfo? ParseVideo(JsonElement streams)
    {
        foreach (var stream in streams.EnumerateArray())
        {
            if (GetString(stream, "codec_type") != "video")
            {
                continue;
            }

            if (!stream.TryGetProperty("width", out var w) || !stream.TryGetProperty("height", out var h)
                || !FrameRate.TryParse(GetString(stream, "avg_frame_rate"), out var rate))
            {
                continue;
            }

            return new VideoStreamInfo(
                w.GetInt32(),
                h.GetInt32(),
                rate,
                GetString(stream, "codec_name") ?? "",
                GetString(stream, "pix_fmt") ?? "",
                ParseRotation(stream));
        }

        return null;
    }

    private static AudioStreamInfo? ParseAudio(JsonElement streams)
    {
        foreach (var stream in streams.EnumerateArray())
        {
            if (GetString(stream, "codec_type") != "audio")
            {
                continue;
            }

            if (!TryGetInt(stream, "sample_rate", out var sampleRate)
                || !stream.TryGetProperty("channels", out var channels))
            {
                continue;
            }

            return new AudioStreamInfo(GetString(stream, "codec_name") ?? "", sampleRate, channels.GetInt32());
        }

        return null;
    }

    private static int ParseRotation(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.TryGetProperty("rotation", out var rotation) && rotation.TryGetInt32(out var degrees))
            {
                return degrees;
            }
        }

        return 0;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ffprobe emits numbers as JSON strings ("48000", "12.500000") in most fields.
    private static bool TryGetDouble(JsonElement element, string name, out double result)
    {
        result = 0;
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out result),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }

    private static bool TryGetInt(JsonElement element, string name, out int result)
    {
        result = 0;
        return TryGetDouble(element, name, out var value)
            && value is >= int.MinValue and <= int.MaxValue
            && int.TryParse(((int)value).ToString(CultureInfo.InvariantCulture), out result);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfprobeParsingTests"`
Expected: PASS, 12 tests.

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Media src/FFMedia.Tests/FFMedia.Tests.csproj src/FFMedia.Tests/Media/FfprobeParsingTests.cs
git commit -m "feat(media): add MediaInfo models and pure ffprobe JSON parser"
```

---

### Task 4: `IMediaAnalyzer` / `FfprobeMediaAnalyzer`

**Files:**
- Create: `src/FFMedia.Media/IMediaAnalyzer.cs`
- Create: `src/FFMedia.Media/FfprobeMediaAnalyzer.cs`
- Test: `src/FFMedia.Tests/Media/FfprobeMediaAnalyzerTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner.RunAsync(string, IReadOnlyList<string>, IProgress<string>?, CancellationToken)` → `ProcessResult(int ExitCode, string StandardOutput, string StandardError)`; `IBinaryProvider.GetPath/Exists`; `FfprobeParsing.Parse`; `Result<T>`.
- Produces: `interface IMediaAnalyzer { Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default); }` and `sealed class FfprobeMediaAnalyzer(IProcessRunner, IBinaryProvider) : IMediaAnalyzer`.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/Media/FfprobeMediaAnalyzerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Media;
using Xunit;

namespace FFMedia.Tests.Media;

public class FfprobeMediaAnalyzerTests
{
    private const string Json = """
    {
      "streams": [
        { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720,
          "avg_frame_rate": "30/1", "pix_fmt": "yuv420p" }
      ],
      "format": { "format_name": "mov", "duration": "5.0" }
    }
    """;

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly ProcessResult _result;
        public List<string> Arguments { get; } = new();
        public string? FileName { get; private set; }
        public FakeRunner(ProcessResult result) => _result = result;

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IProgress<string>? onOutputLine = null, CancellationToken ct = default)
        {
            FileName = fileName;
            Arguments.AddRange(arguments);
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IProgress<string>? onOutputLine = null, CancellationToken ct = default)
            => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified.");
    }

    private sealed class StubBinaryProvider : IBinaryProvider
    {
        public bool FfprobePresent { get; set; } = true;
        public string GetPath(ExternalBinary binary) => $@"C:\bin\{binary}.exe";
        public bool Exists(ExternalBinary binary) => FfprobePresent;
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsParsedInfo_AndPassesJsonFlags()
    {
        var runner = new FakeRunner(new ProcessResult(0, Json, ""));
        var analyzer = new FfprobeMediaAnalyzer(runner, new StubBinaryProvider());

        var result = await analyzer.AnalyzeAsync(@"C:\clips\a.mp4");

        Assert.True(result.IsSuccess);
        Assert.Equal(1280, result.Value!.Video!.Width);
        Assert.Contains("-print_format", runner.Arguments);
        Assert.Contains("json", runner.Arguments);
        Assert.Contains("-show_streams", runner.Arguments);
        Assert.Contains("-show_format", runner.Arguments);
        Assert.Equal(@"C:\clips\a.mp4", runner.Arguments[^1]);
        Assert.Equal(@"C:\bin\Ffprobe.exe", runner.FileName);
    }

    [Fact]
    public async Task AnalyzeAsync_FailsWhenBinaryMissing()
    {
        var provider = new StubBinaryProvider { FfprobePresent = false };
        var analyzer = new FfprobeMediaAnalyzer(new FakeRunner(new ProcessResult(0, Json, "")), provider);

        var result = await analyzer.AnalyzeAsync(@"C:\clips\a.mp4");

        Assert.False(result.IsSuccess);
        Assert.Contains("fetch-binaries", result.Error!);
    }

    [Fact]
    public async Task AnalyzeAsync_FailsOnNonZeroExit()
    {
        var analyzer = new FfprobeMediaAnalyzer(
            new FakeRunner(new ProcessResult(1, "", "a.mp4: Invalid data found")), new StubBinaryProvider());

        var result = await analyzer.AnalyzeAsync(@"C:\clips\a.mp4");

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid data found", result.Error!);
    }

    [Fact]
    public async Task AnalyzeAsync_FailsOnUnparseableOutput()
    {
        var analyzer = new FfprobeMediaAnalyzer(
            new FakeRunner(new ProcessResult(0, "not json", "")), new StubBinaryProvider());

        var result = await analyzer.AnalyzeAsync(@"C:\clips\a.mp4");

        Assert.False(result.IsSuccess);
        Assert.Contains("Could not read", result.Error!);
    }

    [Fact]
    public async Task AnalyzeAsync_FailsWhenProcessLaunchThrows()
    {
        var analyzer = new FfprobeMediaAnalyzer(new ThrowingRunner(), new StubBinaryProvider());

        var result = await analyzer.AnalyzeAsync(@"C:\clips\a.mp4");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var analyzer = new FfprobeMediaAnalyzer(new FakeRunner(new ProcessResult(0, Json, "")), new StubBinaryProvider());

        // ThrowsAnyAsync, not ThrowsAsync: the guard throws OperationCanceledException, and
        // xUnit's ThrowsAsync<T> demands the exact type.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(@"C:\clips\a.mp4", cts.Token));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfprobeMediaAnalyzerTests"`
Expected: FAIL — `FfprobeMediaAnalyzer` does not exist.

- [ ] **Step 3: Write the interface**

`src/FFMedia.Media/IMediaAnalyzer.cs`:

```csharp
using FFMedia.Core.Results;

namespace FFMedia.Media;

/// <summary>Probes a media file's duration and stream parameters.</summary>
public interface IMediaAnalyzer
{
    /// <summary>Probes <paramref name="filePath"/>. Returns a failure (never throws) when the
    /// binary is missing, the file is unreadable, or the output is unusable. Cancellation
    /// propagates as <see cref="OperationCanceledException"/>.</summary>
    Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default);
}
```

- [ ] **Step 4: Write the implementation**

`src/FFMedia.Media/FfprobeMediaAnalyzer.cs`:

```csharp
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Core.Results;

namespace FFMedia.Media;

/// <summary>Probes media via the bundled <c>ffprobe.exe</c>, through the <see cref="IProcessRunner"/> seam.</summary>
public sealed class FfprobeMediaAnalyzer : IMediaAnalyzer
{
    private readonly IProcessRunner _runner;
    private readonly IBinaryProvider _binaries;

    public FfprobeMediaAnalyzer(IProcessRunner runner, IBinaryProvider binaries)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(binaries);
        _runner = runner;
        _binaries = binaries;
    }

    public async Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        if (!_binaries.Exists(ExternalBinary.Ffprobe))
        {
            return Result<MediaInfo>.Failure("ffprobe.exe is missing. Run build/fetch-binaries.ps1.");
        }

        string[] arguments =
        [
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath,
        ];

        ProcessResult process;
        try
        {
            process = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffprobe), arguments, null, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<MediaInfo>.Failure($"Could not run ffprobe: {ex.Message}");
        }

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(process.StandardError)
                ? $"exit code {process.ExitCode}"
                : process.StandardError.Trim();
            return Result<MediaInfo>.Failure($"ffprobe could not read '{Path.GetFileName(filePath)}': {detail}");
        }

        var info = FfprobeParsing.Parse(process.StandardOutput);
        return info is null
            ? Result<MediaInfo>.Failure(
                $"Could not read video streams from '{Path.GetFileName(filePath)}'. Is it a video file?")
            : Result<MediaInfo>.Success(info);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfprobeMediaAnalyzerTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Media/IMediaAnalyzer.cs src/FFMedia.Media/FfprobeMediaAnalyzer.cs src/FFMedia.Tests/Media/FfprobeMediaAnalyzerTests.cs
git commit -m "feat(media): add IMediaAnalyzer backed by bundled ffprobe"
```

---

### Task 5: ffmpeg progress parsing

`ffmpeg -progress pipe:1 -nostats` writes repeated `key=value` blocks to **stdout**, each terminated by a `progress=continue` (or `progress=end`) line. `IProcessRunner` streams stdout lines via `IProgress<string>`, so we accumulate keys and emit a snapshot per block.

**Files:**
- Create: `src/FFMedia.Media/FfmpegProgress.cs`
- Create: `src/FFMedia.Media/FfmpegProgressAccumulator.cs`
- Test: `src/FFMedia.Tests/Media/FfmpegProgressAccumulatorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed record FfmpegProgress(TimeSpan Position, double Speed, bool IsFinal)`
  - `sealed class FfmpegProgressAccumulator { FfmpegProgress? Add(string line); }` — returns non-null exactly once per `progress=` line.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/Media/FfmpegProgressAccumulatorTests.cs`:

```csharp
using FFMedia.Media;
using Xunit;

namespace FFMedia.Tests.Media;

public class FfmpegProgressAccumulatorTests
{
    [Fact]
    public void Add_EmitsNothing_UntilProgressLine()
    {
        var accumulator = new FfmpegProgressAccumulator();

        Assert.Null(accumulator.Add("frame=30"));
        Assert.Null(accumulator.Add("out_time_us=1000000"));
        Assert.Null(accumulator.Add("speed=2.5x"));
    }

    [Fact]
    public void Add_EmitsSnapshot_OnProgressContinue()
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=1500000");
        accumulator.Add("speed=2.5x");

        var progress = accumulator.Add("progress=continue");

        Assert.NotNull(progress);
        Assert.Equal(TimeSpan.FromSeconds(1.5), progress!.Position);
        Assert.Equal(2.5, progress.Speed);
        Assert.False(progress.IsFinal);
    }

    [Fact]
    public void Add_MarksFinal_OnProgressEnd()
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=3000000");

        var progress = accumulator.Add("progress=end");

        Assert.NotNull(progress);
        Assert.True(progress!.IsFinal);
        Assert.Equal(TimeSpan.FromSeconds(3), progress.Position);
    }

    [Fact]
    public void Add_CarriesLastKnownValues_AcrossBlocks()
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=1000000");
        accumulator.Add("speed=4.0x");
        accumulator.Add("progress=continue");

        accumulator.Add("out_time_us=2000000");
        var progress = accumulator.Add("progress=continue");

        Assert.Equal(TimeSpan.FromSeconds(2), progress!.Position);
        Assert.Equal(4.0, progress.Speed); // speed absent from the second block
    }

    [Theory]
    [InlineData("speed=N/A")]
    [InlineData("speed=   ")]
    public void Add_TreatsUnknownSpeedAsZero(string speedLine)
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=1000000");
        accumulator.Add(speedLine);

        Assert.Equal(0, accumulator.Add("progress=continue")!.Speed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-equals-sign")]
    [InlineData("out_time_us=garbage")]
    public void Add_IgnoresJunkLines(string line)
    {
        var accumulator = new FfmpegProgressAccumulator();
        Assert.Null(accumulator.Add(line));
        Assert.Equal(TimeSpan.Zero, accumulator.Add("progress=continue")!.Position);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfmpegProgressAccumulatorTests"`
Expected: FAIL — `FfmpegProgressAccumulator` does not exist.

- [ ] **Step 3: Write the implementation**

`src/FFMedia.Media/FfmpegProgress.cs`:

```csharp
namespace FFMedia.Media;

/// <summary>A single progress snapshot from ffmpeg's <c>-progress</c> stream.</summary>
/// <param name="Position">How far into the output ffmpeg has written.</param>
/// <param name="Speed">Encode throughput as a multiple of realtime (ffmpeg's <c>speed=2.5x</c>); 0 when unknown.</param>
/// <param name="IsFinal">True for the terminal <c>progress=end</c> block.</param>
public sealed record FfmpegProgress(TimeSpan Position, double Speed, bool IsFinal);
```

`src/FFMedia.Media/FfmpegProgressAccumulator.cs`:

```csharp
using System.Globalization;

namespace FFMedia.Media;

/// <summary>Accumulates ffmpeg's <c>-progress pipe:1</c> key=value lines, emitting one
/// <see cref="FfmpegProgress"/> per <c>progress=</c> terminator. Deterministic and IO-free.</summary>
public sealed class FfmpegProgressAccumulator
{
    private TimeSpan _position;
    private double _speed;

    /// <summary>Feeds one stdout line. Returns a snapshot on a <c>progress=</c> line, else null.</summary>
    public FfmpegProgress? Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "out_time_us" or "out_time_ms":
                // Despite the name, ffmpeg reports out_time_ms in MICROseconds too.
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) && micros >= 0)
                {
                    _position = TimeSpan.FromMicroseconds(micros);
                }

                return null;

            case "speed":
                var trimmed = value.TrimEnd('x');
                _speed = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0
                    ? speed
                    : 0;
                return null;

            case "progress":
                return new FfmpegProgress(_position, _speed, value == "end");

            default:
                return null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfmpegProgressAccumulatorTests"`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Media/FfmpegProgress.cs src/FFMedia.Media/FfmpegProgressAccumulator.cs src/FFMedia.Tests/Media/FfmpegProgressAccumulatorTests.cs
git commit -m "feat(media): parse ffmpeg -progress output into snapshots"
```

---

### Task 6: `IFfmpegRunner` / `FfmpegRunner`

**Files:**
- Create: `src/FFMedia.Media/IFfmpegRunner.cs`
- Create: `src/FFMedia.Media/FfmpegRunner.cs`
- Test: `src/FFMedia.Tests/Media/FfmpegRunnerTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `IBinaryProvider`, `FfmpegProgressAccumulator`, `FFMedia.Core.Results.Result` (non-generic, Task 1).
- Produces: `interface IFfmpegRunner { Task<Result> RunAsync(IReadOnlyList<string> arguments, IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default); }` — `FfmpegRunner` prepends `-hide_banner -nostdin -y` and appends `-progress pipe:1 -nostats`, and on failure returns the **last 10 non-empty stderr lines** as the error.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/Media/FfmpegRunnerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Media;
using Xunit;

namespace FFMedia.Tests.Media;

public class FfmpegRunnerTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        private readonly ProcessResult _result;
        private readonly string[] _stdoutLines;
        public List<string> Arguments { get; } = new();
        public string? FileName { get; private set; }

        public FakeRunner(ProcessResult result, params string[] stdoutLines)
        {
            _result = result;
            _stdoutLines = stdoutLines;
        }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IProgress<string>? onOutputLine = null, CancellationToken ct = default)
        {
            FileName = fileName;
            Arguments.AddRange(arguments);
            foreach (var line in _stdoutLines)
            {
                onOutputLine?.Report(line);
            }

            return Task.FromResult(_result);
        }
    }

    private sealed class StubBinaryProvider : IBinaryProvider
    {
        public bool Present { get; set; } = true;
        public string GetPath(ExternalBinary binary) => $@"C:\bin\{binary}.exe";
        public bool Exists(ExternalBinary binary) => Present;
    }

    [Fact]
    public async Task RunAsync_PrependsAndAppendsStandardFlags()
    {
        var runner = new FakeRunner(new ProcessResult(0, "", ""));
        var ffmpeg = new FfmpegRunner(runner, new StubBinaryProvider());

        var result = await ffmpeg.RunAsync(["-i", "a.mp4", "out.mkv"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\bin\Ffmpeg.exe", runner.FileName);
        Assert.Equal("-hide_banner", runner.Arguments[0]);
        Assert.Contains("-nostdin", runner.Arguments);
        Assert.Contains("-y", runner.Arguments);
        Assert.Contains("-nostats", runner.Arguments);
        var progressFlag = runner.Arguments.IndexOf("-progress");
        Assert.True(progressFlag >= 0);
        Assert.Equal("pipe:1", runner.Arguments[progressFlag + 1]);
        // caller args survive, in order
        Assert.True(runner.Arguments.IndexOf("-i") < runner.Arguments.IndexOf("a.mp4"));
    }

    // Note: the sink below is a synchronous IProgress<T>. Do not use BCL Progress<T> here —
    // it posts to the captured SynchronizationContext, so reports would arrive after the await
    // and the assertions would race. Same reason the download queue uses a sync adapter (SDD §12).
    [Fact]
    public async Task RunAsync_ForwardsProgressSynchronously()
    {
        var runner = new FakeRunner(
            new ProcessResult(0, "", ""),
            "out_time_us=1000000", "speed=3.0x", "progress=continue",
            "out_time_us=2000000", "progress=end");
        var ffmpeg = new FfmpegRunner(runner, new StubBinaryProvider());
        var seen = new List<FfmpegProgress>();
        var sink = new SynchronousProgress<FfmpegProgress>(seen.Add);

        await ffmpeg.RunAsync(["-i", "a.mp4"], sink);

        Assert.Equal(2, seen.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), seen[0].Position);
        Assert.Equal(3.0, seen[0].Speed);
        Assert.True(seen[1].IsFinal);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [Fact]
    public async Task RunAsync_FailsWhenBinaryMissing()
    {
        var ffmpeg = new FfmpegRunner(
            new FakeRunner(new ProcessResult(0, "", "")), new StubBinaryProvider { Present = false });

        var result = await ffmpeg.RunAsync(["-i", "a.mp4"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("fetch-binaries", result.Error!);
    }

    [Fact]
    public async Task RunAsync_ReturnsStderrTail_OnNonZeroExit()
    {
        var stderr = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var ffmpeg = new FfmpegRunner(new FakeRunner(new ProcessResult(1, "", stderr)), new StubBinaryProvider());

        var result = await ffmpeg.RunAsync(["-i", "a.mp4"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("line 20", result.Error!);
        Assert.Contains("line 11", result.Error!);
        Assert.DoesNotContain("line 10", result.Error!);
    }

    [Fact]
    public async Task RunAsync_FailsWhenLaunchThrows()
    {
        var ffmpeg = new FfmpegRunner(new ThrowingRunner(), new StubBinaryProvider());

        var result = await ffmpeg.RunAsync(["-i", "a.mp4"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("Could not run ffmpeg", result.Error!);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IProgress<string>? onOutputLine = null, CancellationToken ct = default)
            => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified.");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfmpegRunnerTests"`
Expected: FAIL — `FfmpegRunner` does not exist.

- [ ] **Step 3: Write the interface**

`src/FFMedia.Media/IFfmpegRunner.cs`:

```csharp
using FFMedia.Core.Results;

namespace FFMedia.Media;

/// <summary>Runs the bundled ffmpeg with a caller-supplied argument list, streaming progress.</summary>
public interface IFfmpegRunner
{
    /// <summary>Runs ffmpeg. Standard flags (<c>-hide_banner -nostdin -y</c>) are prepended and
    /// <c>-progress pipe:1 -nostats</c> appended by the implementation. Returns a failure carrying
    /// ffmpeg's stderr tail on a non-zero exit; cancellation propagates.</summary>
    Task<Result> RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken ct = default);
}
```

- [ ] **Step 4: Write the implementation**

`src/FFMedia.Media/FfmpegRunner.cs`:

```csharp
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Core.Results;

namespace FFMedia.Media;

/// <summary>Runs the bundled <c>ffmpeg.exe</c> through the <see cref="IProcessRunner"/> seam,
/// translating its <c>-progress</c> stdout stream into <see cref="FfmpegProgress"/> snapshots.</summary>
public sealed class FfmpegRunner : IFfmpegRunner
{
    private const int StderrTailLines = 10;

    private readonly IProcessRunner _runner;
    private readonly IBinaryProvider _binaries;

    public FfmpegRunner(IProcessRunner runner, IBinaryProvider binaries)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(binaries);
        _runner = runner;
        _binaries = binaries;
    }

    public async Task<Result> RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ct.ThrowIfCancellationRequested();

        if (!_binaries.Exists(ExternalBinary.Ffmpeg))
        {
            return Result.Failure("ffmpeg.exe is missing. Run build/fetch-binaries.ps1.");
        }

        var full = new List<string>(arguments.Count + 6) { "-hide_banner", "-nostdin", "-y" };
        full.AddRange(arguments);
        full.AddRange(["-progress", "pipe:1", "-nostats"]);

        var accumulator = new FfmpegProgressAccumulator();
        IProgress<string>? lineSink = progress is null
            ? null
            : new LineSink(line =>
            {
                var snapshot = accumulator.Add(line);
                if (snapshot is not null)
                {
                    progress.Report(snapshot);
                }
            });

        ProcessResult process;
        try
        {
            process = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffmpeg), full, lineSink, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure($"Could not run ffmpeg: {ex.Message}");
        }

        return process.ExitCode == 0
            ? Result.Success()
            : Result.Failure($"ffmpeg failed (exit {process.ExitCode}):\n{Tail(process.StandardError)}");
    }

    private static string Tail(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= StderrTailLines
            ? string.Join('\n', lines)
            : string.Join('\n', lines[^StderrTailLines..]);
    }

    /// <summary>Reports synchronously on the calling thread — a late callback must never race past
    /// the process exit (same rationale as the download queue's progress adapter, SDD §12).</summary>
    private sealed class LineSink : IProgress<string>
    {
        private readonly Action<string> _handler;
        public LineSink(Action<string> handler) => _handler = handler;
        public void Report(string value) => _handler(value);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~FfmpegRunnerTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Media/IFfmpegRunner.cs src/FFMedia.Media/FfmpegRunner.cs src/FFMedia.Tests/Media/FfmpegRunnerTests.cs
git commit -m "feat(media): add IFfmpegRunner with progress streaming and stderr tail"
```

---

### Task 7: `FFMedia.Tools.VideoMerger` scaffold + target models + `MergeTargetDerivation`

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/FFMedia.Tools.VideoMerger.csproj`
- Modify: `FFMedia.sln` (add the project)
- Modify: `src/FFMedia.Tests/FFMedia.Tests.csproj` (add a `ProjectReference` to the new module)
- Create: `src/FFMedia.Tools.VideoMerger/Models/FitMode.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeVideoCodec.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeAudioCodec.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeContainer.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeTarget.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/MergeTargetDerivation.cs`
- Test: `src/FFMedia.Tests/VideoMerger/MergeTargetDerivationTests.cs`

**Interfaces:**
- Consumes: `MediaInfo`, `VideoStreamInfo`, `AudioStreamInfo`, `FrameRate` (Task 3).
- Produces:
  - `enum FitMode { Fit, Fill, Stretch }`
  - `enum MergeVideoCodec { H264, H265 }`, `enum MergeAudioCodec { Aac, Opus }`, `enum MergeContainer { Mp4, Mkv }`
  - `sealed record MergeTarget(int Width, int Height, FrameRate FrameRate, MergeVideoCodec VideoCodec, int Crf, MergeContainer Container, MergeAudioCodec AudioCodec, int AudioSampleRate, int AudioChannels, FitMode FitMode)` with `static MergeTarget Default`, `long PixelCount => (long)Width * Height`, and `long EstimatedBitsPerSecond`.
  - `static class MergeTargetDerivation { public static MergeTarget Derive(IReadOnlyList<MediaInfo> clips); }` — throws `ArgumentException` on an empty list.

- [ ] **Step 1: Create the project and wire the solution**

Create `src/FFMedia.Tools.VideoMerger/FFMedia.Tools.VideoMerger.csproj` (mirrors the YouTubeDownloader module; WPF is enabled now so PR 2 can add Views without touching this file):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\FFMedia.Core\FFMedia.Core.csproj" />
    <ProjectReference Include="..\FFMedia.Media\FFMedia.Media.csproj" />
  </ItemGroup>

</Project>
```

Run:

```bash
dotnet sln FFMedia.sln add src/FFMedia.Tools.VideoMerger/FFMedia.Tools.VideoMerger.csproj
```

Add to `src/FFMedia.Tests/FFMedia.Tests.csproj`'s `ProjectReference` `ItemGroup`:

```xml
    <ProjectReference Include="..\FFMedia.Tools.VideoMerger\FFMedia.Tools.VideoMerger.csproj" />
```

Run: `dotnet build FFMedia.sln`
Expected: succeeds, 0 errors.

- [ ] **Step 2: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/MergeTargetDerivationTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergeTargetDerivationTests
{
    private static MediaInfo Clip(
        int width, int height, int fpsNum, int fpsDen = 1,
        string videoCodec = "h264", string container = "mov,mp4,m4a",
        string? audioCodec = "aac", int sampleRate = 48000, int channels = 2)
        => new(
            TimeSpan.FromSeconds(10),
            container,
            new VideoStreamInfo(width, height, new FrameRate(fpsNum, fpsDen), videoCodec, "yuv420p", 0),
            audioCodec is null ? null : new AudioStreamInfo(audioCodec, sampleRate, channels));

    [Fact]
    public void Derive_TakesLargestFrameArea()
    {
        var target = MergeTargetDerivation.Derive([Clip(1280, 720, 30), Clip(1920, 1080, 24)]);

        Assert.Equal(1920, target.Width);
        Assert.Equal(1080, target.Height);
    }

    [Fact]
    public void Derive_TakesHighestFrameRate()
    {
        var target = MergeTargetDerivation.Derive([Clip(1920, 1080, 24), Clip(1280, 720, 60)]);

        Assert.Equal(60, target.FrameRate.Value);
    }

    [Fact]
    public void Derive_SnapsNearStandardRateToTheStandardRate()
    {
        // 29.97 reported as an ugly 2997/100 snaps to the exact 30000/1001.
        var target = MergeTargetDerivation.Derive([Clip(1920, 1080, 2997, 100)]);

        Assert.Equal(new FrameRate(30000, 1001), target.FrameRate);
    }

    [Fact]
    public void Derive_KeepsExactRate_WhenFarFromAnyStandardRate()
    {
        var target = MergeTargetDerivation.Derive([Clip(1920, 1080, 12, 1)]);

        Assert.Equal(new FrameRate(12, 1), target.FrameRate);
    }

    [Fact]
    public void Derive_TakesMaxAudioSampleRateAndChannels()
    {
        var target = MergeTargetDerivation.Derive(
        [
            Clip(1920, 1080, 30, sampleRate: 44100, channels: 2),
            Clip(1920, 1080, 30, sampleRate: 48000, channels: 6),
        ]);

        Assert.Equal(48000, target.AudioSampleRate);
        Assert.Equal(6, target.AudioChannels);
    }

    [Fact]
    public void Derive_UsesDefaultAudioSpec_WhenNoClipHasAudio()
    {
        var target = MergeTargetDerivation.Derive([Clip(1920, 1080, 30, audioCodec: null)]);

        Assert.Equal(48000, target.AudioSampleRate);
        Assert.Equal(2, target.AudioChannels);
    }

    [Fact]
    public void Derive_PicksMkv_WhenMostClipsAreMatroska()
    {
        var target = MergeTargetDerivation.Derive(
        [
            Clip(1920, 1080, 30, container: "matroska,webm"),
            Clip(1920, 1080, 30, container: "matroska,webm"),
            Clip(1920, 1080, 30, container: "mov,mp4,m4a"),
        ]);

        Assert.Equal(MergeContainer.Mkv, target.Container);
    }

    [Fact]
    public void Derive_PicksMp4_ByDefault()
    {
        var target = MergeTargetDerivation.Derive([Clip(1920, 1080, 30)]);

        Assert.Equal(MergeContainer.Mp4, target.Container);
        Assert.Equal(MergeVideoCodec.H264, target.VideoCodec);
        Assert.Equal(MergeAudioCodec.Aac, target.AudioCodec);
        Assert.Equal(FitMode.Fit, target.FitMode);
    }

    [Fact]
    public void Derive_PicksH265_WhenMostClipsAreHevc()
    {
        var target = MergeTargetDerivation.Derive(
        [
            Clip(1920, 1080, 30, videoCodec: "hevc"),
            Clip(1920, 1080, 30, videoCodec: "hevc"),
            Clip(1920, 1080, 30, videoCodec: "h264"),
        ]);

        Assert.Equal(MergeVideoCodec.H265, target.VideoCodec);
    }

    [Fact]
    public void Derive_RejectsEmptyList()
    {
        Assert.Throws<ArgumentException>(() => MergeTargetDerivation.Derive(new List<MediaInfo>()));
    }

    [Fact]
    public void Derive_IgnoresClipsWithoutVideo()
    {
        var audioOnly = new MediaInfo(TimeSpan.FromSeconds(5), "mp3", null, new AudioStreamInfo("mp3", 44100, 2));
        var target = MergeTargetDerivation.Derive([audioOnly, Clip(1280, 720, 30)]);

        Assert.Equal(1280, target.Width);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~MergeTargetDerivationTests"`
Expected: FAIL — the `FFMedia.Tools.VideoMerger.Models` namespace does not exist.

- [ ] **Step 4: Write the enums**

`src/FFMedia.Tools.VideoMerger/Models/FitMode.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>How a clip is conformed when its aspect ratio differs from the merge target.</summary>
public enum FitMode
{
    /// <summary>Scale to fit and pad with black bars. Never crops, never distorts.</summary>
    Fit,

    /// <summary>Scale to cover, then centre-crop. Fills the frame; loses edges.</summary>
    Fill,

    /// <summary>Scale to the exact target. Distorts.</summary>
    Stretch,
}
```

`src/FFMedia.Tools.VideoMerger/Models/MergeVideoCodec.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>Video codec of the merged output.</summary>
public enum MergeVideoCodec
{
    H264,
    H265,
}
```

`src/FFMedia.Tools.VideoMerger/Models/MergeAudioCodec.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>Audio codec of the merged output.</summary>
public enum MergeAudioCodec
{
    Aac,
    Opus,
}
```

`src/FFMedia.Tools.VideoMerger/Models/MergeContainer.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>Container of the merged output.</summary>
public enum MergeContainer
{
    Mp4,
    Mkv,
}
```

- [ ] **Step 5: Write `MergeTarget`**

`src/FFMedia.Tools.VideoMerger/Models/MergeTarget.cs`:

```csharp
using FFMedia.Media;

namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>The standardization target every clip is conformed to before concatenation.</summary>
public sealed record MergeTarget(
    int Width,
    int Height,
    FrameRate FrameRate,
    MergeVideoCodec VideoCodec,
    int Crf,
    MergeContainer Container,
    MergeAudioCodec AudioCodec,
    int AudioSampleRate,
    int AudioChannels,
    FitMode FitMode)
{
    /// <summary>Bits per pixel per frame, a rough x264 CRF-20 constant used only for size estimation.</summary>
    private const double BitsPerPixel = 0.08;

    private const long AudioBitsPerSecond = 192_000;

    public static MergeTarget Default { get; } = new(
        1920, 1080, new FrameRate(30, 1), MergeVideoCodec.H264, 20,
        MergeContainer.Mp4, MergeAudioCodec.Aac, 48_000, 2, FitMode.Fit);

    public long PixelCount => (long)Width * Height;

    /// <summary>Heuristic output bitrate, used to size temp files and the disk-space guard.
    /// H.265 is assumed ~35 % more efficient than H.264 at the same perceived quality.</summary>
    public long EstimatedBitsPerSecond
    {
        get
        {
            var videoBits = PixelCount * FrameRate.Value * BitsPerPixel;
            if (VideoCodec == MergeVideoCodec.H265)
            {
                videoBits *= 0.65;
            }

            return (long)videoBits + AudioBitsPerSecond;
        }
    }
}
```

- [ ] **Step 6: Write `MergeTargetDerivation`**

`src/FFMedia.Tools.VideoMerger/Services/MergeTargetDerivation.cs`:

```csharp
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure derivation of a sensible <see cref="MergeTarget"/> from the probed clips:
/// largest frame area, highest frame rate (snapped to a standard rate), majority codec/container,
/// and the maximum audio sample rate and channel count.</summary>
public static class MergeTargetDerivation
{
    /// <summary>Standard broadcast/web rates a near-miss measured rate snaps to.</summary>
    private static readonly FrameRate[] StandardRates =
    [
        new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001),
        new(30, 1), new(50, 1), new(60000, 1001), new(60, 1),
    ];

    /// <summary>Relative tolerance for snapping (0.5 %). 2997/100 → 30000/1001; 12/1 stays 12/1.</summary>
    private const double SnapTolerance = 0.005;

    public static MergeTarget Derive(IReadOnlyList<MediaInfo> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var videos = clips.Where(c => c.Video is not null).Select(c => c.Video!).ToList();
        if (videos.Count == 0)
        {
            throw new ArgumentException("At least one clip must have a video stream.", nameof(clips));
        }

        var largest = videos.MaxBy(v => (long)v.Width * v.Height)!;
        var fastest = videos.MaxBy(v => v.FrameRate.Value)!.FrameRate;

        var audios = clips.Where(c => c.Audio is not null).Select(c => c.Audio!).ToList();
        var sampleRate = audios.Count == 0 ? 48_000 : audios.Max(a => a.SampleRate);
        var channels = audios.Count == 0 ? 2 : audios.Max(a => a.Channels);

        var hevcCount = videos.Count(v => v.CodecName is "hevc" or "h265");
        var codec = hevcCount * 2 > videos.Count ? MergeVideoCodec.H265 : MergeVideoCodec.H264;

        var matroskaCount = clips.Count(c => c.ContainerFormat.Contains("matroska", StringComparison.OrdinalIgnoreCase));
        var container = matroskaCount * 2 > clips.Count ? MergeContainer.Mkv : MergeContainer.Mp4;

        return MergeTarget.Default with
        {
            Width = largest.Width,
            Height = largest.Height,
            FrameRate = Snap(fastest),
            VideoCodec = codec,
            Container = container,
            AudioCodec = MergeAudioCodec.Aac,
            AudioSampleRate = sampleRate,
            AudioChannels = channels,
        };
    }

    private static FrameRate Snap(FrameRate measured)
    {
        foreach (var standard in StandardRates)
        {
            if (Math.Abs(measured.Value - standard.Value) / standard.Value <= SnapTolerance)
            {
                return standard;
            }
        }

        return measured;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~MergeTargetDerivationTests"`
Expected: PASS, 11 tests.

- [ ] **Step 8: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger FFMedia.sln src/FFMedia.Tests/FFMedia.Tests.csproj src/FFMedia.Tests/VideoMerger/MergeTargetDerivationTests.cs
git commit -m "feat(merger): scaffold module, add MergeTarget and pure target derivation"
```

---

### Task 8: `ConformanceCheck`

The keystone: one function drives the fast path, the per-clip UI badge, and the ETA.

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Models/Conformance.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/ConformanceCheck.cs`
- Test: `src/FFMedia.Tests/VideoMerger/ConformanceCheckTests.cs`

**Interfaces:**
- Consumes: `MediaInfo`, `MergeTarget`, `MergeVideoCodec`, `MergeAudioCodec`.
- Produces:
  - `sealed record Conformance(bool IsConforming, IReadOnlyList<string> Mismatches)`
  - `static class ConformanceCheck { public static Conformance Evaluate(MediaInfo clip, MergeTarget target); }`

Rules: a clip conforms **iff** resolution, frame rate, video codec, pixel format (`yuv420p`), audio codec, audio sample rate and audio channel count all match, **and** the clip has an audio stream. A missing audio stream is a mismatch (`"no audio track"`).

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/ConformanceCheckTests.cs`:

```csharp
using System;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class ConformanceCheckTests
{
    private static readonly MergeTarget Target = MergeTarget.Default; // 1920x1080 @30, h264, yuv420p, aac 48k/2

    private static MediaInfo Conforming() => new(
        TimeSpan.FromSeconds(5),
        "mov,mp4,m4a",
        new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
        new AudioStreamInfo("aac", 48000, 2));

    [Fact]
    public void Evaluate_ConformingClip_HasNoMismatches()
    {
        var result = ConformanceCheck.Evaluate(Conforming(), Target);

        Assert.True(result.IsConforming);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void Evaluate_FlagsResolution()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1280, 720, new FrameRate(30, 1), "h264", "yuv420p", 0),
        };

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.False(result.IsConforming);
        Assert.Contains(result.Mismatches, m => m.Contains("resolution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsFrameRate()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(60, 1), "h264", "yuv420p", 0),
        };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains("frame rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsVideoCodec()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "vp9", "yuv420p", 0),
        };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains("video codec", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsPixelFormat()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv444p", 0),
        };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains("pixel format", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_FlagsRotation()
    {
        var clip = Conforming() with
        {
            Video = new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", -90),
        };

        Assert.False(ConformanceCheck.Evaluate(clip, Target).IsConforming);
    }

    [Fact]
    public void Evaluate_FlagsMissingAudioTrack()
    {
        var clip = Conforming() with { Audio = null };

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.False(result.IsConforming);
        Assert.Contains(result.Mismatches, m => m.Contains("no audio track", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("opus", 48000, 2, "audio codec")]
    [InlineData("aac", 44100, 2, "sample rate")]
    [InlineData("aac", 48000, 6, "channel")]
    public void Evaluate_FlagsAudioMismatches(string codec, int rate, int channels, string expected)
    {
        var clip = Conforming() with { Audio = new AudioStreamInfo(codec, rate, channels) };

        Assert.Contains(ConformanceCheck.Evaluate(clip, Target).Mismatches,
            m => m.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_ClipWithoutVideo_NeverConforms()
    {
        var audioOnly = new MediaInfo(TimeSpan.FromSeconds(5), "mp3", null, new AudioStreamInfo("aac", 48000, 2));

        Assert.False(ConformanceCheck.Evaluate(audioOnly, Target).IsConforming);
    }

    [Fact]
    public void Evaluate_CollectsEveryMismatch()
    {
        var clip = new MediaInfo(
            TimeSpan.FromSeconds(5), "webm",
            new VideoStreamInfo(640, 480, new FrameRate(15, 1), "vp9", "yuv444p", 0),
            null);

        var result = ConformanceCheck.Evaluate(clip, Target);

        Assert.Equal(5, result.Mismatches.Count); // resolution, frame rate, video codec, pixel format, no audio
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~ConformanceCheckTests"`
Expected: FAIL — `ConformanceCheck` does not exist.

- [ ] **Step 3: Write `Conformance`**

`src/FFMedia.Tools.VideoMerger/Models/Conformance.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>Whether a clip already matches the merge target, and if not, why. A conforming clip
/// is concatenated as-is (no re-encode) — this drives the fast path, the UI badge, and the ETA.</summary>
public sealed record Conformance(bool IsConforming, IReadOnlyList<string> Mismatches);
```

- [ ] **Step 4: Write `ConformanceCheck`**

`src/FFMedia.Tools.VideoMerger/Services/ConformanceCheck.cs`:

```csharp
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure test of whether a probed clip can be stream-copied straight into the concat,
/// or must first be re-encoded to the target.</summary>
public static class ConformanceCheck
{
    /// <summary>The pixel format every normalized clip is encoded to (broadest player support).</summary>
    public const string TargetPixelFormat = "yuv420p";

    public static Conformance Evaluate(MediaInfo clip, MergeTarget target)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(target);

        var mismatches = new List<string>();

        if (clip.Video is null)
        {
            mismatches.Add("no video track");
        }
        else
        {
            var video = clip.Video;
            if (video.Width != target.Width || video.Height != target.Height)
            {
                mismatches.Add($"resolution {video.Width}x{video.Height} != {target.Width}x{target.Height}");
            }

            if (video.FrameRate != target.FrameRate)
            {
                mismatches.Add($"frame rate {video.FrameRate.Value:0.###} != {target.FrameRate.Value:0.###}");
            }

            if (!MatchesCodec(video.CodecName, target.VideoCodec))
            {
                mismatches.Add($"video codec {video.CodecName} != {CodecName(target.VideoCodec)}");
            }

            if (!string.Equals(video.PixelFormat, TargetPixelFormat, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"pixel format {video.PixelFormat} != {TargetPixelFormat}");
            }

            if (video.Rotation != 0)
            {
                mismatches.Add($"rotation {video.Rotation}° must be baked in");
            }
        }

        if (clip.Audio is null)
        {
            mismatches.Add("no audio track");
        }
        else
        {
            var audio = clip.Audio;
            if (!MatchesCodec(audio.CodecName, target.AudioCodec))
            {
                mismatches.Add($"audio codec {audio.CodecName} != {CodecName(target.AudioCodec)}");
            }

            if (audio.SampleRate != target.AudioSampleRate)
            {
                mismatches.Add($"sample rate {audio.SampleRate} != {target.AudioSampleRate}");
            }

            if (audio.Channels != target.AudioChannels)
            {
                mismatches.Add($"channel count {audio.Channels} != {target.AudioChannels}");
            }
        }

        return new Conformance(mismatches.Count == 0, mismatches);
    }

    private static bool MatchesCodec(string actual, MergeVideoCodec expected) => expected switch
    {
        MergeVideoCodec.H264 => actual.Equals("h264", StringComparison.OrdinalIgnoreCase),
        MergeVideoCodec.H265 => actual is "hevc" or "h265",
        _ => false,
    };

    private static bool MatchesCodec(string actual, MergeAudioCodec expected)
        => actual.Equals(CodecName(expected), StringComparison.OrdinalIgnoreCase);

    private static string CodecName(MergeVideoCodec codec) => codec == MergeVideoCodec.H264 ? "h264" : "hevc";

    private static string CodecName(MergeAudioCodec codec) => codec == MergeAudioCodec.Aac ? "aac" : "opus";
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~ConformanceCheckTests"`
Expected: PASS, 12 tests.

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Models/Conformance.cs src/FFMedia.Tools.VideoMerger/Services/ConformanceCheck.cs src/FFMedia.Tests/VideoMerger/ConformanceCheckTests.cs
git commit -m "feat(merger): add pure ConformanceCheck driving the fast path"
```

---

### Task 9: `NormalizeArgsBuilder`

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Services/NormalizeArgsBuilder.cs`
- Test: `src/FFMedia.Tests/VideoMerger/NormalizeArgsBuilderTests.cs`

**Interfaces:**
- Consumes: `MediaInfo`, `MergeTarget`, `FitMode`, `ConformanceCheck.TargetPixelFormat`.
- Produces: `static class NormalizeArgsBuilder { public static IReadOnlyList<string> Build(string sourcePath, MediaInfo info, MergeTarget target, string outputPath); }`

Filter graphs (spec §6.3):

| `FitMode` | video filter |
|---|---|
| `Fit` | `scale=W:H:force_original_aspect_ratio=decrease,pad=W:H:(ow-iw)/2:(oh-ih)/2` |
| `Fill` | `scale=W:H:force_original_aspect_ratio=increase,crop=W:H` |
| `Stretch` | `scale=W:H` |

All suffixed with `,fps=<rational>,setsar=1`. A clip with no audio additionally gets an `anullsrc` input and `-shortest`.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/NormalizeArgsBuilderTests.cs`:

```csharp
using System;
using System.Linq;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class NormalizeArgsBuilderTests
{
    private static readonly MergeTarget Target = MergeTarget.Default with { FrameRate = new FrameRate(30000, 1001) };

    private static MediaInfo Clip(bool withAudio = true) => new(
        TimeSpan.FromSeconds(5),
        "mov,mp4,m4a",
        new VideoStreamInfo(1280, 720, new FrameRate(24, 1), "h264", "yuv420p", 0),
        withAudio ? new AudioStreamInfo("aac", 44100, 2) : null);

    private static string Filter(System.Collections.Generic.IReadOnlyList<string> args)
        => args[args.ToList().IndexOf("-vf") + 1];

    [Fact]
    public void Build_Fit_ScalesAndPads()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target with { FitMode = FitMode.Fit }, @"C:\t\000.mkv");

        Assert.Equal(
            "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=30000/1001,setsar=1",
            Filter(args));
    }

    [Fact]
    public void Build_Fill_ScalesAndCrops()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target with { FitMode = FitMode.Fill }, @"C:\t\000.mkv");

        Assert.Equal(
            "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,fps=30000/1001,setsar=1",
            Filter(args));
    }

    [Fact]
    public void Build_Stretch_ScalesOnly()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target with { FitMode = FitMode.Stretch }, @"C:\t\000.mkv");

        Assert.Equal("scale=1920:1080,fps=30000/1001,setsar=1", Filter(args));
    }

    [Fact]
    public void Build_EncodesVideoAndAudioToTarget()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target, @"C:\t\000.mkv");

        Assert.Contains("libx264", args);
        Assert.Contains("-crf", args);
        Assert.Contains("20", args);
        Assert.Contains("yuv420p", args);
        Assert.Contains("aac", args);
        Assert.Contains("-ar", args);
        Assert.Contains("48000", args);
        Assert.Contains("-ac", args);
        Assert.Contains("2", args);
        Assert.Equal(@"C:\t\000.mkv", args[^1]);
    }

    [Fact]
    public void Build_UsesLibx265_ForH265Target()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target with { VideoCodec = MergeVideoCodec.H265 }, @"C:\t\000.mkv");

        Assert.Contains("libx265", args);
        Assert.DoesNotContain("libx264", args);
    }

    [Fact]
    public void Build_SilentClip_AddsAnullsrcInputAndShortest()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(withAudio: false), Target, @"C:\t\000.mkv");

        Assert.Contains("lavfi", args);
        Assert.Contains(args, a => a.StartsWith("anullsrc=", StringComparison.Ordinal));
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", args);
        Assert.Contains("-shortest", args);
        Assert.Contains("1:a:0", args);
    }

    [Fact]
    public void Build_ClipWithAudio_MapsItsOwnAudioAndOmitsShortest()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target, @"C:\t\000.mkv");

        Assert.Contains("0:a:0", args);
        Assert.DoesNotContain("-shortest", args);
        Assert.DoesNotContain("lavfi", args);
    }

    [Theory]
    [InlineData(1, "mono")]
    [InlineData(2, "stereo")]
    [InlineData(6, "5.1")]
    public void Build_MapsChannelCountToLayout(int channels, string layout)
    {
        var args = NormalizeArgsBuilder.Build(
            @"C:\a.mp4", Clip(withAudio: false), Target with { AudioChannels = channels }, @"C:\t\000.mkv");

        Assert.Contains($"anullsrc=channel_layout={layout}:sample_rate=48000", args);
    }

    [Fact]
    public void Build_AlwaysMapsFirstVideoStream()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target, @"C:\t\000.mkv");

        Assert.Contains("0:v:0", args);
    }

    [Fact]
    public void Build_SourceIsTheFirstInput()
    {
        var args = NormalizeArgsBuilder.Build(@"C:\a.mp4", Clip(), Target, @"C:\t\000.mkv");

        Assert.Equal("-i", args[0]);
        Assert.Equal(@"C:\a.mp4", args[1]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~NormalizeArgsBuilderTests"`
Expected: FAIL — `NormalizeArgsBuilder` does not exist.

- [ ] **Step 3: Write the implementation**

`src/FFMedia.Tools.VideoMerger/Services/NormalizeArgsBuilder.cs`:

```csharp
using System.Globalization;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure builder for the ffmpeg arguments that re-encode one non-conforming clip to the
/// merge target. <c>-hide_banner -nostdin -y</c> and the <c>-progress</c> flags are added by
/// <see cref="IFfmpegRunner"/>, not here.</summary>
public static class NormalizeArgsBuilder
{
    public static IReadOnlyList<string> Build(string sourcePath, MediaInfo info, MergeTarget target, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var args = new List<string> { "-i", sourcePath };
        var silent = !info.HasAudio;

        if (silent)
        {
            args.AddRange(["-f", "lavfi", "-i", AnullsrcSpec(target)]);
        }

        args.AddRange(["-map", "0:v:0", "-map", silent ? "1:a:0" : "0:a:0"]);
        args.AddRange(["-vf", VideoFilter(target)]);
        args.AddRange(["-c:v", VideoEncoder(target.VideoCodec)]);
        args.AddRange(["-crf", target.Crf.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-preset", "medium"]);
        args.AddRange(["-pix_fmt", ConformanceCheck.TargetPixelFormat]);
        args.AddRange(["-c:a", AudioEncoder(target.AudioCodec)]);
        args.AddRange(["-b:a", "192k"]);
        args.AddRange(["-ar", target.AudioSampleRate.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-ac", target.AudioChannels.ToString(CultureInfo.InvariantCulture)]);

        if (silent)
        {
            // anullsrc is infinite; bound it to the video stream's length.
            args.Add("-shortest");
        }

        args.Add(outputPath);
        return args;
    }

    private static string VideoFilter(MergeTarget target)
    {
        var w = target.Width.ToString(CultureInfo.InvariantCulture);
        var h = target.Height.ToString(CultureInfo.InvariantCulture);

        var fit = target.FitMode switch
        {
            FitMode.Fit => $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2",
            FitMode.Fill => $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}",
            FitMode.Stretch => $"scale={w}:{h}",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.FitMode, "Unknown fit mode."),
        };

        return $"{fit},fps={target.FrameRate.ToFfmpegString()},setsar=1";
    }

    private static string AnullsrcSpec(MergeTarget target)
        => $"anullsrc=channel_layout={ChannelLayout(target.AudioChannels)}:sample_rate={target.AudioSampleRate.ToString(CultureInfo.InvariantCulture)}";

    private static string ChannelLayout(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => "stereo",
    };

    private static string VideoEncoder(MergeVideoCodec codec)
        => codec == MergeVideoCodec.H264 ? "libx264" : "libx265";

    private static string AudioEncoder(MergeAudioCodec codec)
        => codec == MergeAudioCodec.Aac ? "aac" : "libopus";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~NormalizeArgsBuilderTests"`
Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Services/NormalizeArgsBuilder.cs src/FFMedia.Tests/VideoMerger/NormalizeArgsBuilderTests.cs
git commit -m "feat(merger): build normalize ffmpeg args for all three fit modes"
```

---

### Task 10: `ConcatArgsBuilder`

The concat demuxer's list file uses `file '<path>'` lines; a literal `'` in a path must be written `'\''`. This is a real bug source — test it hard.

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Services/ConcatArgsBuilder.cs`
- Test: `src/FFMedia.Tests/VideoMerger/ConcatArgsBuilderTests.cs`

**Interfaces:**
- Consumes: `MergeContainer`.
- Produces:
  - `static class ConcatArgsBuilder`
  - `public static string BuildListFile(IReadOnlyList<string> segmentPaths)`
  - `public static IReadOnlyList<string> BuildArgs(string listFilePath, string outputPath, MergeContainer container)`

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/ConcatArgsBuilderTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class ConcatArgsBuilderTests
{
    [Fact]
    public void BuildListFile_WritesOneQuotedLinePerSegment()
    {
        var content = ConcatArgsBuilder.BuildListFile([@"C:\t\000.mkv", @"C:\clips\b.mp4"]);

        Assert.Equal("file 'C:\\t\\000.mkv'\nfile 'C:\\clips\\b.mp4'\n", content);
    }

    [Fact]
    public void BuildListFile_EscapesSingleQuotesInPaths()
    {
        var content = ConcatArgsBuilder.BuildListFile([@"C:\Bob's clips\a.mp4"]);

        // ffmpeg concat escaping: close the quote, emit an escaped quote, reopen.
        Assert.Equal("file 'C:\\Bob'\\''s clips\\a.mp4'\n", content);
    }

    [Fact]
    public void BuildListFile_RejectsEmptyList()
    {
        Assert.Throws<ArgumentException>(() => ConcatArgsBuilder.BuildListFile(new List<string>()));
    }

    [Fact]
    public void BuildArgs_StreamCopiesWithSafeZero()
    {
        var args = ConcatArgsBuilder.BuildArgs(@"C:\t\list.txt", @"C:\out\merged.mkv", MergeContainer.Mkv);

        Assert.Contains("-f", args);
        Assert.Contains("concat", args);
        Assert.Contains("-safe", args);
        Assert.Contains("0", args);
        Assert.Contains(@"C:\t\list.txt", args);
        Assert.Contains("-c", args);
        Assert.Contains("copy", args);
        Assert.Equal(@"C:\out\merged.mkv", args[^1]);
    }

    [Fact]
    public void BuildArgs_AddsFaststart_ForMp4Only()
    {
        var mp4 = ConcatArgsBuilder.BuildArgs(@"C:\t\list.txt", @"C:\out\merged.mp4", MergeContainer.Mp4);
        var mkv = ConcatArgsBuilder.BuildArgs(@"C:\t\list.txt", @"C:\out\merged.mkv", MergeContainer.Mkv);

        Assert.Contains("-movflags", mp4);
        Assert.Contains("+faststart", mp4);
        Assert.DoesNotContain("-movflags", mkv);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~ConcatArgsBuilderTests"`
Expected: FAIL — `ConcatArgsBuilder` does not exist.

- [ ] **Step 3: Write the implementation**

`src/FFMedia.Tools.VideoMerger/Services/ConcatArgsBuilder.cs`:

```csharp
using System.Text;
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure builder for the concat-demuxer list file and the stream-copy ffmpeg arguments.</summary>
public static class ConcatArgsBuilder
{
    /// <summary>Renders the <c>-f concat</c> list file. Paths are single-quoted; an embedded
    /// apostrophe is escaped the shell way: close quote, escaped quote, reopen quote.</summary>
    public static string BuildListFile(IReadOnlyList<string> segmentPaths)
    {
        ArgumentNullException.ThrowIfNull(segmentPaths);
        if (segmentPaths.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segmentPaths));
        }

        var builder = new StringBuilder();
        foreach (var path in segmentPaths)
        {
            builder.Append("file '").Append(path.Replace("'", @"'\''", StringComparison.Ordinal)).Append("'\n");
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> BuildArgs(string listFilePath, string outputPath, MergeContainer container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var args = new List<string>
        {
            "-f", "concat",
            "-safe", "0",
            "-i", listFilePath,
            "-c", "copy",
        };

        if (container == MergeContainer.Mp4)
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.Add(outputPath);
        return args;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~ConcatArgsBuilderTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Services/ConcatArgsBuilder.cs src/FFMedia.Tests/VideoMerger/ConcatArgsBuilderTests.cs
git commit -m "feat(merger): build concat list file and stream-copy args"
```

---

### Task 11: `Ordering.Shuffle` — random order with locked indices

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Services/Ordering.cs`
- Test: `src/FFMedia.Tests/VideoMerger/OrderingTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class Ordering { public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, Func<T, int?> lockedIndexSelector, int seed); }`

Contract: an item whose selector returns `i` lands at index `i`. Unlocked items are Fisher–Yates-shuffled into the remaining slots, in order. Seeded ⇒ deterministic. Throws `ArgumentOutOfRangeException` for a locked index outside `[0, Count)` and `ArgumentException` for two items locked to the same index.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/OrderingTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class OrderingTests
{
    private sealed record Clip(string Name, int? Locked);

    private static IReadOnlyList<Clip> Clips(params (string Name, int? Locked)[] items)
        => items.Select(i => new Clip(i.Name, i.Locked)).ToList();

    [Fact]
    public void Shuffle_KeepsEveryItemExactlyOnce()
    {
        var input = Clips(("a", null), ("b", null), ("c", null), ("d", null), ("e", null));

        var result = Ordering.Shuffle(input, c => c.Locked, seed: 1234);

        Assert.Equal(input.Count, result.Count);
        Assert.Equal(input.OrderBy(c => c.Name), result.OrderBy(c => c.Name));
    }

    [Fact]
    public void Shuffle_HonorsLockedIndices()
    {
        var input = Clips(("a", null), ("b", 0), ("c", null), ("d", 3), ("e", null));

        var result = Ordering.Shuffle(input, c => c.Locked, seed: 99);

        Assert.Equal("b", result[0].Name);
        Assert.Equal("d", result[3].Name);
    }

    [Fact]
    public void Shuffle_IsDeterministicForASeed()
    {
        var input = Clips(("a", null), ("b", null), ("c", null), ("d", null));

        var first = Ordering.Shuffle(input, c => c.Locked, seed: 7);
        var second = Ordering.Shuffle(input, c => c.Locked, seed: 7);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Shuffle_DiffersAcrossSeeds()
    {
        var input = Clips(("a", null), ("b", null), ("c", null), ("d", null), ("e", null), ("f", null));

        var first = Ordering.Shuffle(input, c => c.Locked, seed: 1);
        var second = Ordering.Shuffle(input, c => c.Locked, seed: 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Shuffle_AllLocked_ReturnsExactOrder()
    {
        var input = Clips(("a", 2), ("b", 0), ("c", 1));

        var result = Ordering.Shuffle(input, c => c.Locked, seed: 5);

        Assert.Equal(["b", "c", "a"], result.Select(c => c.Name));
    }

    [Fact]
    public void Shuffle_SingleItem_IsIdentity()
    {
        var input = Clips(("a", null));

        Assert.Equal("a", Ordering.Shuffle(input, c => c.Locked, seed: 3)[0].Name);
    }

    [Fact]
    public void Shuffle_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(Ordering.Shuffle(new List<Clip>(), c => c.Locked, seed: 3));
    }

    [Fact]
    public void Shuffle_RejectsOutOfRangeLock()
    {
        var input = Clips(("a", 5), ("b", null));

        Assert.Throws<ArgumentOutOfRangeException>(() => Ordering.Shuffle(input, c => c.Locked, seed: 1));
    }

    [Fact]
    public void Shuffle_RejectsNegativeLock()
    {
        var input = Clips(("a", -1), ("b", null));

        Assert.Throws<ArgumentOutOfRangeException>(() => Ordering.Shuffle(input, c => c.Locked, seed: 1));
    }

    [Fact]
    public void Shuffle_RejectsDuplicateLocks()
    {
        var input = Clips(("a", 1), ("b", 1), ("c", null));

        Assert.Throws<ArgumentException>(() => Ordering.Shuffle(input, c => c.Locked, seed: 1));
    }

    [Fact]
    public void Shuffle_UnlockedItemsNeverLandOnLockedSlots()
    {
        var input = Clips(("a", null), ("b", 1), ("c", null), ("d", null));

        for (var seed = 0; seed < 50; seed++)
        {
            var result = Ordering.Shuffle(input, c => c.Locked, seed);
            Assert.Equal("b", result[1].Name);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~OrderingTests"`
Expected: FAIL — `Ordering` does not exist.

- [ ] **Step 3: Write the implementation**

`src/FFMedia.Tools.VideoMerger/Services/Ordering.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure clip ordering. Locked items are pinned to their index; the rest are
/// Fisher–Yates-shuffled into the remaining slots. Seeded, so tests are deterministic.</summary>
public static class Ordering
{
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, Func<T, int?> lockedIndexSelector, int seed)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(lockedIndexSelector);

        if (items.Count == 0)
        {
            return [];
        }

        var slots = new T?[items.Count];
        var taken = new bool[items.Count];
        var unlocked = new List<T>();

        foreach (var item in items)
        {
            var locked = lockedIndexSelector(item);
            if (locked is null)
            {
                unlocked.Add(item);
                continue;
            }

            var index = locked.Value;
            if (index < 0 || index >= items.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items), index, $"Locked index must be within [0, {items.Count - 1}].");
            }

            if (taken[index])
            {
                throw new ArgumentException($"Two clips are locked to index {index}.", nameof(items));
            }

            slots[index] = item;
            taken[index] = true;
        }

        // Fisher–Yates over the unlocked items only.
        var random = new Random(seed);
        for (var i = unlocked.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (unlocked[i], unlocked[j]) = (unlocked[j], unlocked[i]);
        }

        var next = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            if (!taken[i])
            {
                slots[i] = unlocked[next++];
            }
        }

        return slots.Select(s => s!).ToList();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~OrderingTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Services/Ordering.cs src/FFMedia.Tests/VideoMerger/OrderingTests.cs
git commit -m "feat(merger): seeded shuffle honoring locked clip indices"
```

---

### Task 12: `SpeedProfile` — persisted rolling average of encode throughput

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Models/SpeedProfile.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/ISpeedProfileStore.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/SpeedProfileStore.cs`
- Test: `src/FFMedia.Tests/VideoMerger/SpeedProfileTests.cs`

**Interfaces:**
- Consumes: `JsonStore<T>` (`FFMedia.Core.Persistence`, ctor `(string filePath, ILogger logger)`, `T Load(Func<T> defaultFactory)`, `void Save(T value)`), `MergeVideoCodec`, `MergeTarget`.
- Produces:
  - `sealed class SpeedProfile { public Dictionary<string, SpeedSample> Samples { get; set; } }`
  - `sealed class SpeedSample { public double Average { get; set; } public int Count { get; set; } }`
  - `static string SpeedProfile.KeyFor(MergeTarget target)` → e.g. `"H264/HD1080"`
  - `double SpeedProfile.GetFactor(MergeTarget target)` → measured average, else a seeded constant
  - `void SpeedProfile.Record(MergeTarget target, double measuredSpeed)` — rolling average over the last 10 samples
  - `double SpeedProfile.BandFor(MergeTarget target)` → `0.35` with no samples, narrowing by `0.04` per sample, floor `0.15`
  - `interface ISpeedProfileStore { SpeedProfile Load(); void Save(SpeedProfile profile); }` + `SpeedProfileStore(string dataDirectory, ILogger<SpeedProfileStore> logger)` writing `encode-speed.json`

Pixel buckets: `≤921600` → `SD`, `≤2073600` → `HD1080`, `≤8294400` → `UHD4K`, else `HUGE`.
Seed factors (encoded video-seconds per wall-second): H264 `SD 8.0, HD1080 3.5, UHD4K 0.8, HUGE 0.3`; H265 is half of each.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/SpeedProfileTests.cs`:

```csharp
using System.IO;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class SpeedProfileTests
{
    private static MergeTarget Hd => MergeTarget.Default;
    private static MergeTarget Uhd => MergeTarget.Default with { Width = 3840, Height = 2160 };

    [Fact]
    public void KeyFor_BucketsByCodecAndPixels()
    {
        Assert.Equal("H264/HD1080", SpeedProfile.KeyFor(Hd));
        Assert.Equal("H264/UHD4K", SpeedProfile.KeyFor(Uhd));
        Assert.Equal("H265/HD1080", SpeedProfile.KeyFor(Hd with { VideoCodec = MergeVideoCodec.H265 }));
        Assert.Equal("H264/SD", SpeedProfile.KeyFor(Hd with { Width = 1280, Height = 720 }));
    }

    [Fact]
    public void GetFactor_UsesSeedConstant_WhenNoSamples()
    {
        var profile = new SpeedProfile();

        Assert.Equal(3.5, profile.GetFactor(Hd));
        Assert.Equal(0.8, profile.GetFactor(Uhd));
        Assert.Equal(1.75, profile.GetFactor(Hd with { VideoCodec = MergeVideoCodec.H265 }));
    }

    [Fact]
    public void Record_FirstSample_BecomesTheAverage()
    {
        var profile = new SpeedProfile();
        profile.Record(Hd, 5.0);

        Assert.Equal(5.0, profile.GetFactor(Hd));
    }

    [Fact]
    public void Record_RollsTheAverage()
    {
        var profile = new SpeedProfile();
        profile.Record(Hd, 4.0);
        profile.Record(Hd, 6.0);

        Assert.Equal(5.0, profile.GetFactor(Hd));
    }

    [Fact]
    public void Record_KeysAreIndependent()
    {
        var profile = new SpeedProfile();
        profile.Record(Hd, 9.0);

        Assert.Equal(0.8, profile.GetFactor(Uhd));
    }

    [Fact]
    public void Record_IgnoresNonPositiveSpeeds()
    {
        var profile = new SpeedProfile();
        profile.Record(Hd, 0);
        profile.Record(Hd, -1);

        Assert.Equal(3.5, profile.GetFactor(Hd)); // still the seed
    }

    [Fact]
    public void BandFor_NarrowsWithSamples_ToAFloor()
    {
        var profile = new SpeedProfile();
        Assert.Equal(0.35, profile.BandFor(Hd));

        profile.Record(Hd, 3.0);
        Assert.Equal(0.31, profile.BandFor(Hd), 3);

        for (var i = 0; i < 20; i++)
        {
            profile.Record(Hd, 3.0);
        }

        Assert.Equal(0.15, profile.BandFor(Hd), 3);
    }

    [Fact]
    public void Store_RoundTripsThroughDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ffmedia-speed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SpeedProfileStore(directory, NullLogger<SpeedProfileStore>.Instance);
            var profile = store.Load();
            profile.Record(Hd, 4.25);
            store.Save(profile);

            var reloaded = new SpeedProfileStore(directory, NullLogger<SpeedProfileStore>.Instance).Load();

            Assert.Equal(4.25, reloaded.GetFactor(Hd));
            Assert.True(File.Exists(Path.Combine(directory, "encode-speed.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Store_ReturnsDefault_WhenFileAbsent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ffmedia-speed-" + Guid.NewGuid().ToString("N"));
        var store = new SpeedProfileStore(directory, NullLogger<SpeedProfileStore>.Instance);

        Assert.Equal(3.5, store.Load().GetFactor(Hd));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~SpeedProfileTests"`
Expected: FAIL — `SpeedProfile` does not exist.

- [ ] **Step 3: Write `SpeedProfile`**

`src/FFMedia.Tools.VideoMerger/Models/SpeedProfile.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>One measured throughput bucket: encoded video-seconds per wall-clock second.</summary>
public sealed class SpeedSample
{
    public double Average { get; set; }
    public int Count { get; set; }
}

/// <summary>Rolling average of this machine's real encode throughput, keyed by codec + resolution
/// bucket. Persisted to encode-speed.json so the merge-time estimate improves with use.</summary>
public sealed class SpeedProfile
{
    /// <summary>How many samples the rolling average remembers.</summary>
    private const int Window = 10;

    /// <summary>Conservative starting guesses (H.264, encoded seconds per wall second).</summary>
    private static readonly IReadOnlyDictionary<string, double> SeedFactors = new Dictionary<string, double>
    {
        ["SD"] = 8.0,
        ["HD1080"] = 3.5,
        ["UHD4K"] = 0.8,
        ["HUGE"] = 0.3,
    };

    public Dictionary<string, SpeedSample> Samples { get; set; } = new();

    public static string KeyFor(MergeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"{target.VideoCodec}/{Bucket(target.PixelCount)}";
    }

    /// <summary>Measured average for this target, or the seeded constant when never measured.</summary>
    public double GetFactor(MergeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Samples.TryGetValue(KeyFor(target), out var sample) && sample.Count > 0)
        {
            return sample.Average;
        }

        var seed = SeedFactors[Bucket(target.PixelCount)];
        return target.VideoCodec == MergeVideoCodec.H265 ? seed / 2 : seed;
    }

    /// <summary>Folds a real measured <c>speed=</c> value into the rolling average.</summary>
    public void Record(MergeTarget target, double measuredSpeed)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (measuredSpeed <= 0)
        {
            return;
        }

        var key = KeyFor(target);
        if (!Samples.TryGetValue(key, out var sample))
        {
            sample = new SpeedSample();
            Samples[key] = sample;
        }

        var weight = Math.Min(sample.Count + 1, Window);
        sample.Average += (measuredSpeed - sample.Average) / weight;
        sample.Count++;
    }

    /// <summary>Relative half-width of the estimate range: ±35 % with no data, narrowing 4 points
    /// per sample to a ±15 % floor. The estimate is honest about being a guess.</summary>
    public double BandFor(MergeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var count = Samples.TryGetValue(KeyFor(target), out var sample) ? sample.Count : 0;
        return Math.Max(0.15, 0.35 - (0.04 * count));
    }

    private static string Bucket(long pixels) => pixels switch
    {
        <= 921_600 => "SD",
        <= 2_073_600 => "HD1080",
        <= 8_294_400 => "UHD4K",
        _ => "HUGE",
    };
}
```

- [ ] **Step 4: Write the store**

`src/FFMedia.Tools.VideoMerger/Services/ISpeedProfileStore.cs`:

```csharp
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Persists the machine's measured encode throughput across runs.</summary>
public interface ISpeedProfileStore
{
    SpeedProfile Load();

    void Save(SpeedProfile profile);
}
```

`src/FFMedia.Tools.VideoMerger/Services/SpeedProfileStore.cs`:

```csharp
using FFMedia.Core.Persistence;
using FFMedia.Tools.VideoMerger.Models;
using Microsoft.Extensions.Logging;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>JSON-backed <see cref="ISpeedProfileStore"/> at &lt;dataDirectory&gt;\encode-speed.json,
/// reusing Core's atomic <see cref="JsonStore{T}"/> (temp-file write + corrupt-file quarantine).</summary>
public sealed class SpeedProfileStore : ISpeedProfileStore
{
    public const string FileName = "encode-speed.json";

    private readonly JsonStore<SpeedProfile> _store;

    public SpeedProfileStore(string dataDirectory, ILogger<SpeedProfileStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = new JsonStore<SpeedProfile>(Path.Combine(dataDirectory, FileName), logger);
    }

    public SpeedProfile Load() => _store.Load(() => new SpeedProfile());

    public void Save(SpeedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _store.Save(profile);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~SpeedProfileTests"`
Expected: PASS, 9 tests.

Note: `SpeedProfile.cs` holds two public types (`SpeedSample`, `SpeedProfile`). This is the one place the "one public type per file" convention bends — they are a value and its container, meaningless apart. If a reviewer objects, split `SpeedSample` into its own file.

- [ ] **Step 6: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Models/SpeedProfile.cs src/FFMedia.Tools.VideoMerger/Services/ISpeedProfileStore.cs src/FFMedia.Tools.VideoMerger/Services/SpeedProfileStore.cs src/FFMedia.Tests/VideoMerger/SpeedProfileTests.cs
git commit -m "feat(merger): persist a rolling encode-throughput profile"
```

---

### Task 13: `MergeEstimator` + `DiskSpaceGuard`

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeClip.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeEstimate.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/MergeEstimator.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/DiskSpaceGuard.cs`
- Test: `src/FFMedia.Tests/VideoMerger/MergeEstimatorTests.cs`
- Test: `src/FFMedia.Tests/VideoMerger/DiskSpaceGuardTests.cs`

**Interfaces:**
- Consumes: `MediaInfo`, `MergeTarget`, `SpeedProfile`, `ConformanceCheck.Evaluate`, `FFMedia.Core.Results.Result`.
- Produces:
  - `sealed record MergeClip(string SourcePath, MediaInfo Info)`
  - `sealed record MergeEstimate(TimeSpan OutputDuration, TimeSpan LowEta, TimeSpan HighEta, long TempBytesEstimate, int ReencodeCount, bool IsFastPath)`
  - `static class MergeEstimator { public static MergeEstimate Estimate(IReadOnlyList<MergeClip> clips, MergeTarget target, SpeedProfile profile); }`
  - `static class DiskSpaceGuard { public const double SafetyMargin = 1.2; public static Result Evaluate(long freeBytes, long requiredBytes); }`

Math: `OutputDuration = Σ clip durations` (exact — no transitions). `encodeSeconds = Σ(non-conforming durations) / profile.GetFactor(target)`. `concatSeconds = totalOutputBytes / 200 MB/s` copy throughput. `point = encodeSeconds + concatSeconds`; `Low = point × (1 - band)`, `High = point × (1 + band)`. `TempBytesEstimate = Σ(non-conforming durations) × target.EstimatedBitsPerSecond / 8` — conforming clips are referenced in place and cost no temp disk. `IsFastPath = ReencodeCount == 0`.

- [ ] **Step 1: Write the failing tests**

Create `src/FFMedia.Tests/VideoMerger/MergeEstimatorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergeEstimatorTests
{
    private static readonly MergeTarget Target = MergeTarget.Default;

    private static MergeClip Conforming(string path, double seconds) => new(path, new MediaInfo(
        TimeSpan.FromSeconds(seconds), "mov,mp4,m4a",
        new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
        new AudioStreamInfo("aac", 48000, 2)));

    private static MergeClip NonConforming(string path, double seconds) => new(path, new MediaInfo(
        TimeSpan.FromSeconds(seconds), "matroska,webm",
        new VideoStreamInfo(1280, 720, new FrameRate(60, 1), "vp9", "yuv420p", 0),
        null));

    [Fact]
    public void Estimate_OutputDurationIsTheExactSum()
    {
        var estimate = MergeEstimator.Estimate(
            [Conforming("a", 10), NonConforming("b", 5.5)], Target, new SpeedProfile());

        Assert.Equal(TimeSpan.FromSeconds(15.5), estimate.OutputDuration);
    }

    [Fact]
    public void Estimate_AllConforming_IsFastPath()
    {
        var estimate = MergeEstimator.Estimate([Conforming("a", 10), Conforming("b", 20)], Target, new SpeedProfile());

        Assert.True(estimate.IsFastPath);
        Assert.Equal(0, estimate.ReencodeCount);
        Assert.Equal(0, estimate.TempBytesEstimate);
    }

    [Fact]
    public void Estimate_CountsOnlyNonConformingClips()
    {
        var estimate = MergeEstimator.Estimate(
            [Conforming("a", 10), NonConforming("b", 5), NonConforming("c", 5)], Target, new SpeedProfile());

        Assert.False(estimate.IsFastPath);
        Assert.Equal(2, estimate.ReencodeCount);
    }

    [Fact]
    public void Estimate_EncodeTimeScalesWithMeasuredSpeed()
    {
        var slow = new SpeedProfile();
        slow.Record(Target, 1.0); // 1x realtime
        var fast = new SpeedProfile();
        fast.Record(Target, 10.0);

        var slowEstimate = MergeEstimator.Estimate([NonConforming("a", 100)], Target, slow);
        var fastEstimate = MergeEstimator.Estimate([NonConforming("a", 100)], Target, fast);

        Assert.True(slowEstimate.HighEta > fastEstimate.HighEta);
    }

    [Fact]
    public void Estimate_BandBracketsThePointEstimate()
    {
        var profile = new SpeedProfile();
        profile.Record(Target, 2.0); // 100 s of video / 2.0 = 50 s encode

        var estimate = MergeEstimator.Estimate([NonConforming("a", 100)], Target, profile);

        Assert.True(estimate.LowEta < estimate.HighEta);
        Assert.True(estimate.LowEta > TimeSpan.Zero);
        // band after one sample is 0.31 → low ≈ 0.69 × point, high ≈ 1.31 × point
        Assert.True(estimate.HighEta.TotalSeconds / estimate.LowEta.TotalSeconds > 1.5);
    }

    [Fact]
    public void Estimate_TempBytesCoversOnlyNonConformingClips()
    {
        var estimate = MergeEstimator.Estimate([Conforming("a", 100), NonConforming("b", 10)], Target, new SpeedProfile());

        var expected = (long)(10 * Target.EstimatedBitsPerSecond / 8.0);
        Assert.Equal(expected, estimate.TempBytesEstimate);
    }

    [Fact]
    public void Estimate_FastPathStillReportsAShortNonZeroEta()
    {
        var estimate = MergeEstimator.Estimate([Conforming("a", 600)], Target, new SpeedProfile());

        Assert.True(estimate.HighEta > TimeSpan.Zero);
        Assert.True(estimate.HighEta < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Estimate_RejectsEmptyList()
    {
        Assert.Throws<ArgumentException>(() =>
            MergeEstimator.Estimate(new List<MergeClip>(), Target, new SpeedProfile()));
    }
}
```

Create `src/FFMedia.Tests/VideoMerger/DiskSpaceGuardTests.cs`:

```csharp
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class DiskSpaceGuardTests
{
    [Fact]
    public void Evaluate_PassesWithAmpleSpace()
    {
        Assert.True(DiskSpaceGuard.Evaluate(freeBytes: 10_000, requiredBytes: 1_000).IsSuccess);
    }

    [Fact]
    public void Evaluate_AppliesA20PercentMargin()
    {
        // 1000 required → 1200 needed. 1199 fails, 1200 passes.
        Assert.False(DiskSpaceGuard.Evaluate(1_199, 1_000).IsSuccess);
        Assert.True(DiskSpaceGuard.Evaluate(1_200, 1_000).IsSuccess);
    }

    [Fact]
    public void Evaluate_ExplainsTheShortfall()
    {
        var result = DiskSpaceGuard.Evaluate(freeBytes: 0, requiredBytes: 5_000_000_000);

        Assert.False(result.IsSuccess);
        Assert.Contains("GB", result.Error!);
        Assert.Contains("free", result.Error!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ZeroRequirementAlwaysPasses()
    {
        Assert.True(DiskSpaceGuard.Evaluate(0, 0).IsSuccess);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~MergeEstimatorTests|FullyQualifiedName~DiskSpaceGuardTests"`
Expected: FAIL — `MergeEstimator` / `DiskSpaceGuard` do not exist.

- [ ] **Step 3: Write the models**

`src/FFMedia.Tools.VideoMerger/Models/MergeClip.cs`:

```csharp
using FFMedia.Media;

namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>A source clip together with what ffprobe found in it.</summary>
public sealed record MergeClip(string SourcePath, MediaInfo Info);
```

`src/FFMedia.Tools.VideoMerger/Models/MergeEstimate.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>What the user sees before committing to a merge. <see cref="OutputDuration"/> is exact;
/// the ETA is a range, and is replaced by ffmpeg's real figure once merging starts.</summary>
public sealed record MergeEstimate(
    TimeSpan OutputDuration,
    TimeSpan LowEta,
    TimeSpan HighEta,
    long TempBytesEstimate,
    int ReencodeCount,
    bool IsFastPath);
```

- [ ] **Step 4: Write `MergeEstimator`**

`src/FFMedia.Tools.VideoMerger/Services/MergeEstimator.cs`:

```csharp
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure pre-merge estimate. Only non-conforming clips cost encode time and temp disk;
/// conforming clips are referenced in place (spec §6.5).</summary>
public static class MergeEstimator
{
    /// <summary>Assumed stream-copy throughput of the concat pass, in bytes per second.</summary>
    private const double CopyBytesPerSecond = 200L * 1024 * 1024;

    public static MergeEstimate Estimate(IReadOnlyList<MergeClip> clips, MergeTarget target, SpeedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);
        if (clips.Count == 0)
        {
            throw new ArgumentException("At least one clip is required.", nameof(clips));
        }

        var outputDuration = TimeSpan.FromTicks(clips.Sum(c => c.Info.Duration.Ticks));

        var nonConforming = clips
            .Where(c => !ConformanceCheck.Evaluate(c.Info, target).IsConforming)
            .ToList();

        var encodeSeconds = nonConforming.Count == 0
            ? 0
            : nonConforming.Sum(c => c.Info.Duration.TotalSeconds) / profile.GetFactor(target);

        var outputBytes = outputDuration.TotalSeconds * target.EstimatedBitsPerSecond / 8.0;
        var concatSeconds = outputBytes / CopyBytesPerSecond;

        var point = encodeSeconds + concatSeconds;
        var band = profile.BandFor(target);

        var tempBytes = (long)(nonConforming.Sum(c => c.Info.Duration.TotalSeconds) * target.EstimatedBitsPerSecond / 8.0);

        return new MergeEstimate(
            outputDuration,
            TimeSpan.FromSeconds(point * (1 - band)),
            TimeSpan.FromSeconds(point * (1 + band)),
            tempBytes,
            nonConforming.Count,
            nonConforming.Count == 0);
    }
}
```

- [ ] **Step 5: Write `DiskSpaceGuard`**

`src/FFMedia.Tools.VideoMerger/Services/DiskSpaceGuard.cs`:

```csharp
using FFMedia.Core.Results;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure preflight check: is there room for the normalize phase's temp intermediates?
/// Kept free of <see cref="System.IO.DriveInfo"/> so it is testable without a real volume.</summary>
public static class DiskSpaceGuard
{
    /// <summary>Headroom over the raw estimate — the bitrate heuristic can undershoot.</summary>
    public const double SafetyMargin = 1.2;

    public static Result Evaluate(long freeBytes, long requiredBytes)
    {
        var needed = (long)(requiredBytes * SafetyMargin);
        if (freeBytes >= needed)
        {
            return Result.Success();
        }

        return Result.Failure(
            $"Not enough disk space for temporary files: {Gb(needed)} needed, {Gb(freeBytes)} free.");
    }

    private static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~MergeEstimatorTests|FullyQualifiedName~DiskSpaceGuardTests"`
Expected: PASS, 12 tests.

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Models/MergeClip.cs src/FFMedia.Tools.VideoMerger/Models/MergeEstimate.cs src/FFMedia.Tools.VideoMerger/Services/MergeEstimator.cs src/FFMedia.Tools.VideoMerger/Services/DiskSpaceGuard.cs src/FFMedia.Tests/VideoMerger/MergeEstimatorTests.cs src/FFMedia.Tests/VideoMerger/DiskSpaceGuardTests.cs
git commit -m "feat(merger): add pre-merge estimator and disk-space guard"
```

---

### Task 14: `IMergeService` / `MergeService`

The orchestrator. Phase 0 preflight → Phase 1 normalize non-conforming clips concurrently → Phase 2 stream-copy concat. Temp cleanup on **every** exit path.

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeJobStatus.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeProgress.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Models/MergeRequest.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/IMergeService.cs`
- Create: `src/FFMedia.Tools.VideoMerger/Services/MergeService.cs`
- Test: `src/FFMedia.Tests/VideoMerger/MergeServiceTests.cs`

**Interfaces:**
- Consumes: `IFfmpegRunner.RunAsync(IReadOnlyList<string>, IProgress<FfmpegProgress>?, CancellationToken) → Task<Result>`; `ISpeedProfileStore`; `MergeEstimator.Estimate`; `DiskSpaceGuard.Evaluate`; `ConformanceCheck.Evaluate`; `NormalizeArgsBuilder.Build`; `ConcatArgsBuilder.BuildListFile/BuildArgs`; `Result<T>`.
- Produces:
  - `enum MergeJobStatus { Idle, Normalizing, Concatenating, Completed, Canceled, Failed }`
  - `sealed record MergeProgress(MergeJobStatus Status, double OverallPercent, string? CurrentClip)`
  - `sealed record MergeRequest(IReadOnlyList<MergeClip> Clips, MergeTarget Target, string OutputPath)`
  - `interface IMergeService { Task<Result<string>> MergeAsync(MergeRequest request, IProgress<MergeProgress>? progress = null, CancellationToken ct = default); }`
  - `sealed class MergeService(IFfmpegRunner, ISpeedProfileStore, Func<string, long> getFreeBytes, string tempRoot, int maxConcurrency, ILogger<MergeService>)`

Weighting: encode = 95 % of the bar, concat = 5 %. Fast path (no non-conforming clips) skips Phase 1 entirely and never calls `NormalizeArgsBuilder`.

- [ ] **Step 1: Write the failing test**

Create `src/FFMedia.Tests/VideoMerger/MergeServiceTests.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class MergeServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "ffmedia-merge-" + Guid.NewGuid().ToString("N"));
    private static readonly MergeTarget Target = MergeTarget.Default;

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static MergeClip Conforming(string path, double seconds = 5) => new(path, new MediaInfo(
        TimeSpan.FromSeconds(seconds), "mov,mp4,m4a",
        new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0),
        new AudioStreamInfo("aac", 48000, 2)));

    private static MergeClip NonConforming(string path, double seconds = 5) => new(path, new MediaInfo(
        TimeSpan.FromSeconds(seconds), "matroska,webm",
        new VideoStreamInfo(1280, 720, new FrameRate(60, 1), "vp9", "yuv420p", 0),
        null));

    private sealed class FakeFfmpeg : IFfmpegRunner
    {
        private readonly Func<IReadOnlyList<string>, Result> _behavior;
        public ConcurrentQueue<IReadOnlyList<string>> Invocations { get; } = new();
        public int MaxObservedConcurrency;
        private int _current;

        public FakeFfmpeg(Func<IReadOnlyList<string>, Result>? behavior = null)
            => _behavior = behavior ?? (_ => Result.Success());

        public async Task<Result> RunAsync(IReadOnlyList<string> arguments,
            IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            Invocations.Enqueue(arguments);
            var now = Interlocked.Increment(ref _current);
            InterlockedMax(ref MaxObservedConcurrency, now);

            // Touch the output file so the concat phase sees real segments.
            var output = arguments[^1];
            if (!output.EndsWith(".txt", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, "segment", ct);
            }

            progress?.Report(new FfmpegProgress(TimeSpan.FromSeconds(5), 4.0, IsFinal: true));
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref _current);
            return _behavior(arguments);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }
    }

    private sealed class HangingFfmpeg : IFfmpegRunner
    {
        public async Task<Result> RunAsync(IReadOnlyList<string> arguments,
            IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Result.Success();
        }
    }

    private sealed class FakeSpeedStore : ISpeedProfileStore
    {
        public SpeedProfile Profile { get; set; } = new();
        public int SaveCount { get; private set; }
        public SpeedProfile Load() => Profile;
        public void Save(SpeedProfile profile) { Profile = profile; SaveCount++; }
    }

    private MergeService Build(IFfmpegRunner ffmpeg, ISpeedProfileStore? store = null, long freeBytes = long.MaxValue,
        int maxConcurrency = 2)
        => new(ffmpeg, store ?? new FakeSpeedStore(), _ => freeBytes, _tempRoot, maxConcurrency,
            NullLogger<MergeService>.Instance);

    private static MergeRequest Request(params MergeClip[] clips)
        => new(clips, Target, Path.Combine(Path.GetTempPath(), "merged-" + Guid.NewGuid().ToString("N") + ".mp4"));

    [Fact]
    public async Task MergeAsync_FastPath_SkipsNormalizationEntirely()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(Conforming("a.mp4"), Conforming("b.mp4"));

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Single(ffmpeg.Invocations); // concat only
        Assert.Contains("concat", ffmpeg.Invocations.Single());
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_NormalizesOnlyNonConformingClips()
    {
        var ffmpeg = new FakeFfmpeg();
        var request = Request(Conforming("a.mp4"), NonConforming("b.webm"), NonConforming("c.webm"));

        var result = await Build(ffmpeg).MergeAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, ffmpeg.Invocations.Count); // 2 normalize + 1 concat
        Assert.Equal(2, ffmpeg.Invocations.Count(a => a.Contains("-vf")));
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_RespectsConcurrencyCap()
    {
        var ffmpeg = new FakeFfmpeg();
        var clips = Enumerable.Range(0, 6).Select(i => NonConforming($"c{i}.webm")).ToArray();

        await Build(ffmpeg, maxConcurrency: 2).MergeAsync(Request(clips));

        Assert.True(ffmpeg.MaxObservedConcurrency <= 2, $"saw {ffmpeg.MaxObservedConcurrency}");
    }

    [Fact]
    public async Task MergeAsync_FailsPreflight_WhenDiskIsFull()
    {
        var ffmpeg = new FakeFfmpeg();

        var result = await Build(ffmpeg, freeBytes: 1).MergeAsync(Request(NonConforming("a.webm", seconds: 600)));

        Assert.False(result.IsSuccess);
        Assert.Contains("disk space", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ffmpeg.Invocations); // nothing ran
    }

    [Fact]
    public async Task MergeAsync_FailsWhenAClipFailsToNormalize()
    {
        var ffmpeg = new FakeFfmpeg(args => args.Contains("-vf") ? Result.Failure("codec explosion") : Result.Success());

        var result = await Build(ffmpeg).MergeAsync(Request(NonConforming("bad.webm")));

        Assert.False(result.IsSuccess);
        Assert.Contains("bad.webm", result.Error!);
        Assert.Contains("codec explosion", result.Error!);
    }

    [Fact]
    public async Task MergeAsync_CleansTempDirectory_OnSuccess()
    {
        var request = Request(NonConforming("a.webm"));

        await Build(new FakeFfmpeg()).MergeAsync(request);

        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.GetDirectories(_tempRoot) : []);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_CleansTempDirectory_OnFailure()
    {
        var ffmpeg = new FakeFfmpeg(args => args.Contains("-vf") ? Result.Failure("nope") : Result.Success());

        await Build(ffmpeg).MergeAsync(Request(NonConforming("a.webm")));

        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.GetDirectories(_tempRoot) : []);
    }

    [Fact]
    public async Task MergeAsync_CleansTempDirectory_OnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var task = Build(new HangingFfmpeg()).MergeAsync(Request(NonConforming("a.webm")), null, cts.Token);
        await cts.CancelAsync();

        var result = await task;

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.GetDirectories(_tempRoot) : []);
    }

    [Fact]
    public async Task MergeAsync_ReportsCanceled_RatherThanThrowing()
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<MergeProgress>();
        var task = Build(new HangingFfmpeg()).MergeAsync(
            Request(NonConforming("a.webm")), new SyncProgress<MergeProgress>(seen.Add), cts.Token);
        await cts.CancelAsync();

        var result = await task;

        Assert.False(result.IsSuccess);
        Assert.Contains(seen, p => p.Status == MergeJobStatus.Canceled);
    }

    [Fact]
    public async Task MergeAsync_ProgressIsMonotonicAndEndsAt100()
    {
        var seen = new List<MergeProgress>();
        var request = Request(NonConforming("a.webm"), NonConforming("b.webm"), Conforming("c.mp4"));

        var result = await Build(new FakeFfmpeg(), maxConcurrency: 1)
            .MergeAsync(request, new SyncProgress<MergeProgress>(seen.Add));

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(seen);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].OverallPercent >= seen[i - 1].OverallPercent,
                $"progress went backwards: {seen[i - 1].OverallPercent} → {seen[i].OverallPercent}");
        }

        Assert.Equal(100, seen[^1].OverallPercent, 3);
        Assert.Equal(MergeJobStatus.Completed, seen[^1].Status);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_RecordsMeasuredSpeed()
    {
        var store = new FakeSpeedStore();
        var request = Request(NonConforming("a.webm"));

        await Build(new FakeFfmpeg(), store).MergeAsync(request);

        Assert.True(store.SaveCount > 0);
        Assert.Equal(4.0, store.Profile.GetFactor(Target)); // the fake reports speed=4.0x
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_ReturnsOutputPath()
    {
        var request = Request(Conforming("a.mp4"));

        var result = await Build(new FakeFfmpeg()).MergeAsync(request);

        Assert.Equal(request.OutputPath, result.Value);
        File.Delete(request.OutputPath);
    }

    [Fact]
    public async Task MergeAsync_RejectsEmptyClipList()
    {
        var request = new MergeRequest([], Target, "out.mp4");

        var result = await Build(new FakeFfmpeg()).MergeAsync(request);

        Assert.False(result.IsSuccess);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~MergeServiceTests"`
Expected: FAIL — `MergeService` does not exist.

- [ ] **Step 3: Write the models**

`src/FFMedia.Tools.VideoMerger/Models/MergeJobStatus.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>Lifecycle of a single merge. Only one merge runs at a time (spec D7).</summary>
public enum MergeJobStatus
{
    Idle,
    Normalizing,
    Concatenating,
    Completed,
    Canceled,
    Failed,
}
```

`src/FFMedia.Tools.VideoMerger/Models/MergeProgress.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>A weighted snapshot of merge progress: normalizing is 95 % of the bar, concat 5 %.</summary>
public sealed record MergeProgress(MergeJobStatus Status, double OverallPercent, string? CurrentClip);
```

`src/FFMedia.Tools.VideoMerger/Models/MergeRequest.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>Everything <c>IMergeService</c> needs: the clips in final order, the standardization
/// target, and where the merged file goes.</summary>
public sealed record MergeRequest(IReadOnlyList<MergeClip> Clips, MergeTarget Target, string OutputPath);
```

- [ ] **Step 4: Write the interface**

`src/FFMedia.Tools.VideoMerger/Services/IMergeService.cs`:

```csharp
using FFMedia.Core.Results;
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Normalizes the non-conforming clips and concatenates everything into one file.</summary>
public interface IMergeService
{
    /// <summary>Runs the merge. Returns the output path on success; a friendly failure otherwise
    /// (including cancellation). Never throws for an expected failure.</summary>
    Task<Result<string>> MergeAsync(
        MergeRequest request,
        IProgress<MergeProgress>? progress = null,
        CancellationToken ct = default);
}
```

- [ ] **Step 5: Write `MergeService`**

`src/FFMedia.Tools.VideoMerger/Services/MergeService.cs`:

```csharp
using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using Microsoft.Extensions.Logging;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Two-phase merge (spec §6.4): re-encode only the non-conforming clips to temp
/// intermediates under a concurrency cap, then stream-copy concat. Conforming clips are referenced
/// in place. Temp files are removed on every exit path.</summary>
public sealed class MergeService : IMergeService
{
    /// <summary>Encoding dominates the wall clock; the copy-concat is nearly free.</summary>
    private const double EncodeWeight = 0.95;

    private readonly IFfmpegRunner _ffmpeg;
    private readonly ISpeedProfileStore _speedStore;
    private readonly Func<string, long> _getFreeBytes;
    private readonly string _tempRoot;
    private readonly int _maxConcurrency;
    private readonly ILogger<MergeService> _logger;

    public MergeService(
        IFfmpegRunner ffmpeg,
        ISpeedProfileStore speedStore,
        Func<string, long> getFreeBytes,
        string tempRoot,
        int maxConcurrency,
        ILogger<MergeService> logger)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(speedStore);
        ArgumentNullException.ThrowIfNull(getFreeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentNullException.ThrowIfNull(logger);

        _ffmpeg = ffmpeg;
        _speedStore = speedStore;
        _getFreeBytes = getFreeBytes;
        _tempRoot = tempRoot;
        _maxConcurrency = maxConcurrency;
        _logger = logger;
    }

    public async Task<Result<string>> MergeAsync(
        MergeRequest request,
        IProgress<MergeProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Clips.Count == 0)
        {
            return Result<string>.Failure("Add at least one clip to merge.");
        }

        var profile = _speedStore.Load();
        var estimate = MergeEstimator.Estimate(request.Clips, request.Target, profile);

        // Phase 0 — preflight.
        var space = DiskSpaceGuard.Evaluate(_getFreeBytes(_tempRoot), estimate.TempBytesEstimate);
        if (!space.IsSuccess)
        {
            Report(progress, MergeJobStatus.Failed, 0, null);
            return Result<string>.Failure(space.Error!);
        }

        var workingDirectory = Path.Combine(_tempRoot, "merge-" + Guid.NewGuid().ToString("N"));
        var measuredSpeeds = new List<double>();

        try
        {
            Directory.CreateDirectory(workingDirectory);

            // Phase 1 — normalize the non-conforming clips.
            var segments = await NormalizeAsync(request, workingDirectory, progress, measuredSpeeds, ct)
                .ConfigureAwait(false);
            if (!segments.IsSuccess)
            {
                Report(progress, MergeJobStatus.Failed, EncodeWeight * 100, null);
                return Result<string>.Failure(segments.Error!);
            }

            // Phase 2 — stream-copy concat.
            Report(progress, MergeJobStatus.Concatenating, EncodeWeight * 100, null);

            var listPath = Path.Combine(workingDirectory, "list.txt");
            await File.WriteAllTextAsync(listPath, ConcatArgsBuilder.BuildListFile(segments.Value!), ct)
                .ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            var concat = await _ffmpeg
                .RunAsync(ConcatArgsBuilder.BuildArgs(listPath, request.OutputPath, request.Target.Container), null, ct)
                .ConfigureAwait(false);

            if (!concat.IsSuccess)
            {
                Report(progress, MergeJobStatus.Failed, EncodeWeight * 100, null);
                return Result<string>.Failure(concat.Error!);
            }

            RecordSpeeds(profile, request.Target, measuredSpeeds);
            Report(progress, MergeJobStatus.Completed, 100, null);
            return Result<string>.Success(request.OutputPath);
        }
        catch (OperationCanceledException)
        {
            Report(progress, MergeJobStatus.Canceled, 0, null);
            return Result<string>.Failure("Merge canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge failed unexpectedly.");
            Report(progress, MergeJobStatus.Failed, 0, null);
            return Result<string>.Failure($"Merge failed: {ex.Message}");
        }
        finally
        {
            TryCleanup(workingDirectory);
        }
    }

    private async Task<Result<IReadOnlyList<string>>> NormalizeAsync(
        MergeRequest request,
        string workingDirectory,
        IProgress<MergeProgress>? progress,
        List<double> measuredSpeeds,
        CancellationToken ct)
    {
        var segments = new string?[request.Clips.Count];
        var work = new List<(int Index, MergeClip Clip)>();

        for (var i = 0; i < request.Clips.Count; i++)
        {
            var clip = request.Clips[i];
            if (ConformanceCheck.Evaluate(clip.Info, request.Target).IsConforming)
            {
                segments[i] = clip.SourcePath; // referenced in place — no temp file, no encode
            }
            else
            {
                work.Add((i, clip));
            }
        }

        if (work.Count == 0)
        {
            Report(progress, MergeJobStatus.Normalizing, EncodeWeight * 100, null);
            return Result<IReadOnlyList<string>>.Success(segments.Select(s => s!).ToList());
        }

        using var gate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var completed = 0;
        var failure = null as string;
        var speedLock = new object();

        var tasks = work.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var output = Path.Combine(workingDirectory, $"{item.Index:D4}.mkv");
                var args = NormalizeArgsBuilder.Build(item.Clip.SourcePath, item.Clip.Info, request.Target, output);

                var sink = new SyncProgress<FfmpegProgress>(p =>
                {
                    if (p.Speed > 0)
                    {
                        lock (speedLock)
                        {
                            measuredSpeeds.Add(p.Speed);
                        }
                    }
                });

                var result = await _ffmpeg.RunAsync(args, sink, ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    Interlocked.CompareExchange(
                        ref failure, $"Could not standardize '{Path.GetFileName(item.Clip.SourcePath)}': {result.Error}", null);
                    return;
                }

                segments[item.Index] = output;

                var done = Interlocked.Increment(ref completed);
                Report(progress, MergeJobStatus.Normalizing,
                    done / (double)work.Count * EncodeWeight * 100,
                    Path.GetFileName(item.Clip.SourcePath));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (failure is not null)
        {
            return Result<IReadOnlyList<string>>.Failure(failure);
        }

        return Result<IReadOnlyList<string>>.Success(segments.Select(s => s!).ToList());
    }

    private void RecordSpeeds(SpeedProfile profile, MergeTarget target, IReadOnlyList<double> speeds)
    {
        if (speeds.Count == 0)
        {
            return;
        }

        foreach (var speed in speeds)
        {
            profile.Record(target, speed);
        }

        try
        {
            _speedStore.Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the encode-speed profile.");
        }
    }

    private void TryCleanup(string workingDirectory)
    {
        try
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove temp directory {Path}.", workingDirectory);
        }
    }

    private static void Report(IProgress<MergeProgress>? progress, MergeJobStatus status, double percent, string? clip)
        => progress?.Report(new MergeProgress(status, Math.Clamp(percent, 0, 100), clip));

    /// <summary>Reports on the calling thread so a late callback can't race past a terminal status.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~MergeServiceTests"`
Expected: PASS, 13 tests.

If `MergeAsync_ProgressIsMonotonicAndEndsAt100` flakes with concurrency > 1, that is the test's fault, not the code's — it already pins `maxConcurrency: 1`. Concurrent per-clip completion reports are inherently unordered; do **not** "fix" this by serializing the real code.

- [ ] **Step 7: Commit**

```bash
git add src/FFMedia.Tools.VideoMerger/Models/MergeJobStatus.cs src/FFMedia.Tools.VideoMerger/Models/MergeProgress.cs src/FFMedia.Tools.VideoMerger/Models/MergeRequest.cs src/FFMedia.Tools.VideoMerger/Services/IMergeService.cs src/FFMedia.Tools.VideoMerger/Services/MergeService.cs src/FFMedia.Tests/VideoMerger/MergeServiceTests.cs
git commit -m "feat(merger): add MergeService with preflight, bounded normalize, concat"
```

---

### Task 15: Orphan sweep, DI registration, docs, and final verification

**Files:**
- Create: `src/FFMedia.Tools.VideoMerger/Services/TempDirectorySweeper.cs`
- Create: `src/FFMedia.Tools.VideoMerger/ServiceCollectionExtensions.cs`
- Test: `src/FFMedia.Tests/VideoMerger/TempDirectorySweeperTests.cs`
- Modify: `SDD.md` (Changelog + §17 M7 row)
- Modify: `CLAUDE.md` (Progress Log)

**Interfaces:**
- Consumes: `IMergeService`, `MergeService`, `ISpeedProfileStore`, `SpeedProfileStore`, `IMediaAnalyzer`, `FfprobeMediaAnalyzer`, `IFfmpegRunner`, `FfmpegRunner`.
- Produces:
  - `static class TempDirectorySweeper { public static int SweepOrphans(string tempRoot, TimeSpan olderThan, DateTime utcNow); }`
  - `static class ServiceCollectionExtensions { public static IServiceCollection AddVideoMergerEngine(this IServiceCollection services, string dataDirectory, string tempRoot, int maxConcurrency); }`

No `ITool` / `IToolPage` registration in this PR — the module has no page yet, so the shell must not try to navigate to one.

- [ ] **Step 1: Write the failing sweeper test**

Create `src/FFMedia.Tests/VideoMerger/TempDirectorySweeperTests.cs`:

```csharp
using System;
using System.IO;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class TempDirectorySweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ffmedia-sweep-" + Guid.NewGuid().ToString("N"));

    public TempDirectorySweeperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string MakeDirectory(string name, DateTime writeTimeUtc)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, writeTimeUtc);
        return path;
    }

    [Fact]
    public void SweepOrphans_RemovesOldMergeDirectories()
    {
        var now = DateTime.UtcNow;
        var old = MakeDirectory("merge-old", now.AddHours(-25));

        var removed = TempDirectorySweeper.SweepOrphans(_root, TimeSpan.FromHours(24), now);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void SweepOrphans_KeepsRecentMergeDirectories()
    {
        var now = DateTime.UtcNow;
        var recent = MakeDirectory("merge-recent", now.AddHours(-1));

        Assert.Equal(0, TempDirectorySweeper.SweepOrphans(_root, TimeSpan.FromHours(24), now));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void SweepOrphans_IgnoresUnrelatedDirectories()
    {
        var now = DateTime.UtcNow;
        var other = MakeDirectory("something-else", now.AddDays(-9));

        Assert.Equal(0, TempDirectorySweeper.SweepOrphans(_root, TimeSpan.FromHours(24), now));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void SweepOrphans_MissingRootIsNoOp()
    {
        Assert.Equal(0, TempDirectorySweeper.SweepOrphans(
            Path.Combine(_root, "nope"), TimeSpan.FromHours(24), DateTime.UtcNow));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~TempDirectorySweeperTests"`
Expected: FAIL — `TempDirectorySweeper` does not exist.

- [ ] **Step 3: Write the sweeper**

`src/FFMedia.Tools.VideoMerger/Services/TempDirectorySweeper.cs`:

```csharp
namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Removes <c>merge-*</c> temp directories orphaned by a crash or a hard kill.
/// <see cref="MergeService"/> cleans up its own directory on every normal exit path.</summary>
public static class TempDirectorySweeper
{
    public const string DirectoryPrefix = "merge-";

    /// <summary>Deletes orphaned directories older than <paramref name="olderThan"/>. Returns how
    /// many were removed. Never throws — a locked directory is simply skipped.</summary>
    public static int SweepOrphans(string tempRoot, TimeSpan olderThan, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);

        if (!Directory.Exists(tempRoot))
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in Directory.GetDirectories(tempRoot, DirectoryPrefix + "*"))
        {
            try
            {
                if (utcNow - Directory.GetLastWriteTimeUtc(directory) < olderThan)
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use, or not ours to delete. Leave it for next launch.
            }
        }

        return removed;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test FFMedia.sln --filter "FullyQualifiedName~TempDirectorySweeperTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Write the DI registration**

`src/FFMedia.Tools.VideoMerger/ServiceCollectionExtensions.cs`:

```csharp
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFMedia.Tools.VideoMerger;

/// <summary>Registers the Video Merger engine. The ITool/IToolPage registration lands with the UI.</summary>
public static class ServiceCollectionExtensions
{
    /// <param name="dataDirectory">Where encode-speed.json lives, e.g. %AppData%\FFMedia.</param>
    /// <param name="tempRoot">Root for merge-&lt;guid&gt; working directories, e.g. %Temp%\FFMedia.</param>
    /// <param name="maxConcurrency">Simultaneous clip normalizations.</param>
    public static IServiceCollection AddVideoMergerEngine(
        this IServiceCollection services, string dataDirectory, string tempRoot, int maxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        services.AddSingleton<IMediaAnalyzer>(sp => new FfprobeMediaAnalyzer(
            sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IBinaryProvider>()));
        services.AddSingleton<IFfmpegRunner>(sp => new FfmpegRunner(
            sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IBinaryProvider>()));
        services.AddSingleton<ISpeedProfileStore>(sp => new SpeedProfileStore(
            dataDirectory,
            sp.GetService<ILogger<SpeedProfileStore>>() ?? NullLogger<SpeedProfileStore>.Instance));
        services.AddSingleton<IMergeService>(sp => new MergeService(
            sp.GetRequiredService<IFfmpegRunner>(),
            sp.GetRequiredService<ISpeedProfileStore>(),
            path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace,
            tempRoot,
            maxConcurrency,
            sp.GetService<ILogger<MergeService>>() ?? NullLogger<MergeService>.Instance));

        return services;
    }
}
```

Add a registration test to `src/FFMedia.Tests/VideoMerger/` mirroring `CoreServiceCollectionExtensionsTests`:

```csharp
using System;
using System.IO;
using FFMedia.Core;
using FFMedia.Tools.VideoMerger;
using FFMedia.Tools.VideoMerger.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class VideoMergerServiceCollectionTests
{
    [Fact]
    public void AddVideoMergerEngine_ResolvesTheMergeService()
    {
        var temp = Path.GetTempPath();
        var provider = new ServiceCollection()
            .AddFFMediaCore(temp, temp)
            .AddVideoMergerEngine(temp, temp, maxConcurrency: 2)
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IMergeService>());
        Assert.NotNull(provider.GetRequiredService<FFMedia.Media.IMediaAnalyzer>());
        Assert.NotNull(provider.GetRequiredService<FFMedia.Media.IFfmpegRunner>());
        Assert.NotNull(provider.GetRequiredService<ISpeedProfileStore>());
    }

    [Fact]
    public void AddVideoMergerEngine_RejectsZeroConcurrency()
    {
        var temp = Path.GetTempPath();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddVideoMergerEngine(temp, temp, maxConcurrency: 0));
    }
}
```

Save it as `src/FFMedia.Tests/VideoMerger/VideoMergerServiceCollectionTests.cs`.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test FFMedia.sln --filter "Category!=Integration"`
Expected: **0 failing.** The baseline before this plan is **189 passing**; this plan adds roughly 120 tests, so expect a total in the low 300s. **Record the actual number printed** — you need it for Step 8's docs, and do not copy the approximate figure from this plan into them.

- [ ] **Step 7: Verify a clean Release build**

Run: `dotnet build FFMedia.sln --configuration Release`
Expected: **0 warnings, 0 errors** (`FFMedia.Core` and `FFMedia.Media` treat warnings as errors).

If `FFMedia.App` fails to resolve `AddVideoMergerEngine`, you have accidentally wired the module into the shell — don't. PR 1 registers nothing in `App`.

- [ ] **Step 8: Update the docs**

`SDD.md` — in the **§8 table**, rename the row `FfmpegProgressParsing` → **`FfmpegProgressAccumulator`**. The spec named it `…Parsing`, but it carries state across lines (it accumulates `key=value` pairs and emits one snapshot per `progress=` terminator), so "Parsing" misdescribes it. Keep the row's "pure" classification — it is deterministic and IO-free.

Then bump the version line to **0.14**, and add a Changelog row at the **top** of the table:

```
| 2026-07-10 | 0.14 | M7 PR 1 (engine) delivered. `FFMedia.Media` is realized: `MediaInfo`/`FrameRate` models, `IMediaAnalyzer`/`FfprobeMediaAnalyzer` (ffprobe JSON via `IProcessRunner`), `IFfmpegRunner`/`FfmpegRunner` (`-progress pipe:1`, stderr tail on failure), and the pure `FfprobeParsing`/`FfmpegProgressAccumulator`. New module `FFMedia.Tools.VideoMerger` holds the pure engine — `MergeTargetDerivation`, `ConformanceCheck` (the keystone: drives the fast path, the badge, and the ETA), `NormalizeArgsBuilder` (Fit/Fill/Stretch + `anullsrc` for silent clips), `ConcatArgsBuilder` (list-file `'` escaping), `Ordering.Shuffle` (seeded Fisher–Yates honoring locked indices), `MergeEstimator`, `SpeedProfile`/`SpeedProfileStore` (`encode-speed.json`), `DiskSpaceGuard`, `TempDirectorySweeper` — plus `IMergeService`/`MergeService` (preflight → bounded-concurrency normalize → stream-copy concat; temp cleanup on every exit path). `ExternalBinary.Ffprobe` added; `fetch-binaries.ps1` extracts `ffprobe.exe` from the same pinned, SHA-256-verified BtbN zip. Core gains a non-generic `Result`. No UI — `ITool`/page registration lands in PR 2. Release build 0/0; <N>/<N> unit tests pass (substitute the real count from Step 6). |
```

Update the §17 **M7** row: change `📐 **designed**` to `🚧 **in progress** — **PR 1 delivered** (branch `feat/m7-merge-engine`): engine + `FFMedia.Media` + `ffprobe`. **PR 2 pending:** module VM + page + nav + history/notifications wiring.`

`CLAUDE.md` — add a Progress Log entry at the **top**:

```markdown
### 2026-07-10 — M7 PR 1: Video Merger engine (no UI)

- **Done:** realized `FFMedia.Media` (`IMediaAnalyzer` over ffprobe, `IFfmpegRunner` with
  `-progress` streaming, pure `FfprobeParsing`/`FfmpegProgressAccumulator`) and the new
  `FFMedia.Tools.VideoMerger` engine: target derivation, `ConformanceCheck`, normalize/concat arg
  builders, seeded shuffle with locked indices, estimator + `SpeedProfile`, disk guard, temp
  sweeper, and `MergeService` (preflight → bounded-concurrency normalize → stream-copy concat,
  temp cleanup on every exit path). Added `ExternalBinary.Ffprobe` + `fetch-binaries.ps1`
  extraction from the same pinned zip, and a non-generic `Result` in Core.
- **Verified:** Release build 0/0; `<N>`/`<N>` unit tests pass (`Category!=Integration` — substitute
  the real count from Step 6). **Not
  verified:** a real end-to-end merge with actual ffmpeg — no integration test in this PR, and no
  UI to drive it. That lands with PR 2.
- **Next:** M7 PR 2 — `MergerViewModel`, `MergerPage`, `ITool`/nav registration, history +
  notifications wiring, and a trait-gated integration test merging three real `testsrc` clips.
- SDD → v0.14.
```

- [ ] **Step 9: Commit and open the PR**

```bash
git add SDD.md CLAUDE.md src/FFMedia.Tools.VideoMerger/Services/TempDirectorySweeper.cs src/FFMedia.Tools.VideoMerger/ServiceCollectionExtensions.cs src/FFMedia.Tests/VideoMerger/
git commit -m "feat(merger): sweep orphaned temp dirs, register engine in DI, sync docs"
git push -u origin feat/m7-merge-engine
gh pr create --draft --base main --title "feat(m7): Video Merger engine (PR 1, no UI)" --body "Implements docs/superpowers/plans/2026-07-10-m7-merge-engine.md. Engine only; UI lands in PR 2."
```

---

## Spec Coverage

| Spec section | Task |
|---|---|
| §4 `ffprobe.exe` plumbing | Task 2 |
| §5.1 `FFMedia.Media` (analyzer, runner, parsers) | Tasks 3, 4, 5, 6 |
| §5.2 module scaffold | Task 7 |
| §6.1 models | Tasks 7, 8, 13, 14 |
| §6.2 `MergeTargetDerivation` | Task 7 |
| §6.2 `ConformanceCheck` | Task 8 |
| §6.2 `NormalizeArgsBuilder` / §6.3 filter graphs | Task 9 |
| §6.2 `ConcatArgsBuilder` (+ `'` escaping) | Task 10 |
| §6.2 `Ordering.Shuffle` (locks, seeded) | Task 11 |
| §6.2 `DiskSpaceGuard` | Task 13 |
| §6.2 `MergeEstimator` / §6.5 `SpeedProfile` | Tasks 12, 13 |
| §6.4 phases, concurrency, cancel, cleanup | Task 14 |
| §6.4 startup orphan sweep | Task 15 |
| §7 UI | **PR 2 — deliberately out of scope** |
| §8 error handling (`Result`, stderr tail, named failing clip) | Tasks 1, 6, 14 |
| §9 testing | Every task (TDD) |
| §10 delivery | Task 15 |

**Deferred to PR 2 (not a gap):** `MergerViewModel`, `MergerPage`, `ITool`/`IToolPage` registration, history/notification wiring, and the trait-gated integration test that merges three real `testsrc` clips.

