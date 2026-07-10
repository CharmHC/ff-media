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
