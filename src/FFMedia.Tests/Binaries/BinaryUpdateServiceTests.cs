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
