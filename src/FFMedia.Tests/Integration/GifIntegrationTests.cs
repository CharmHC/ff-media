using System;
using System.IO;
using System.Threading.Tasks;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;
using FFMedia.Tools.GifMaker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Integration;

/// <summary>The first end-to-end proof the GIF Maker actually makes a GIF. Everything up to this task
/// was proven with fakes; this synthesizes a real clip with ffmpeg's own <c>testsrc</c>, runs it through
/// the real <see cref="GifService"/> (both real ffmpeg passes), and then <b>probes the output</b> with a
/// real <see cref="FfprobeMediaAnalyzer"/> — because, as the merger already taught this project, ffmpeg's
/// exit code is exactly what cannot be trusted, so the finished file's own dimensions and duration are
/// the only evidence worth trusting.</summary>
[Trait("Category", "Integration")]
public class GifIntegrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ffmedia-gif-it-" + Guid.NewGuid().ToString("N"));

    private readonly ProcessRunner _runner = new();
    private readonly IBinaryProvider _binaries =
        new BundledBinaryProvider(Path.Combine(AppContext.BaseDirectory, "assets", "binaries"));

    public GifIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private GifService NewService() => new(
        new FfmpegRunner(_runner, _binaries),
        new FfprobeMediaAnalyzer(_runner, _binaries),
        new GifSizeProfileStore(_dir, NullLogger<GifSizeProfileStore>.Instance),
        _dir);

    /// <summary>Synthesizes a clip with ffmpeg's own <c>testsrc</c>, so there is a real source video
    /// with a known size/rate/duration to seek and scale.</summary>
    private async Task<string> MakeClipAsync(string name, string size, int fps, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var result = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffmpeg), [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc=size={size}:rate={fps}:duration={seconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", path,
        ]);

        Assert.Equal(0, result.ExitCode);
        return path;
    }

    private async Task<MediaInfo> AnalyzeAsync(string path)
    {
        var analyzer = new FfprobeMediaAnalyzer(_runner, _binaries);
        var probe = await analyzer.AnalyzeAsync(path);
        Assert.True(probe.IsSuccess, probe.Error);
        return probe.Value!;
    }

    [Fact]
    public async Task CreateAsync_MakesARealGif_OfTheRightSizeAndLength()
    {
        // ffmpeg's exit code is exactly what cannot be trusted, so PROBE the output. And a GIF that is
        // the wrong LENGTH is the specific failure a wrong reading of -ss/-to would cause: -to is
        // ABSOLUTE (a position on the source timeline), so `-ss 2 -to 5` must yield 3 seconds, not 5.
        var source = await MakeClipAsync("src.mp4", "1280x720", fps: 30, seconds: 6);

        var request = new GifRequest(
            source, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
            new Resolution(480, 270), new FrameRate(15, 1), Path.Combine(_dir, "out.gif"));

        var result = await NewService().CreateAsync(request);
        Assert.True(result.IsSuccess, result.Error);

        var info = await AnalyzeAsync(request.OutputPath);
        Assert.NotNull(info.Video);
        Assert.Equal(480, info.Video!.Width);
        Assert.Equal(270, info.Video.Height);            // derived from the source aspect by scale=W:-2
        Assert.Equal(3.0, info.Duration.TotalSeconds, 1); // -to is ABSOLUTE: 5 - 2 = 3, not 5

        Assert.Empty(Directory.GetFiles(_dir, "*.png")); // the temp palette is gone
    }
}
