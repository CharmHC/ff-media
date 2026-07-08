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
