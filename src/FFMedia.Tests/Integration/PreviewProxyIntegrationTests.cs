using System;
using System.IO;
using System.Threading.Tasks;
using FFMedia.Core.Binaries;
using FFMedia.Core.Processes;
using FFMedia.Media;
using FFMedia.Media.Preview;
using Xunit;

namespace FFMedia.Tests.Integration;

/// <summary>Proves the preview proxy against REAL ffmpeg, from a REAL VP9/WebM source. Everything up to
/// this task was proven with fakes; this synthesizes an actual VP9 clip, runs it through the real
/// <see cref="PreviewProxyService"/> (a real <see cref="FfmpegRunner"/>), and then <b>probes both the
/// source and the proxy</b> with a real <see cref="FfprobeMediaAnalyzer"/> — because, as the merger and
/// the GIF Maker already taught this project, ffmpeg's exit code is exactly what cannot be trusted.</summary>
[Trait("Category", "Integration")]
public class PreviewProxyIntegrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ffmedia-preview-it-" + Guid.NewGuid().ToString("N"));

    private readonly ProcessRunner _runner = new();
    private readonly IBinaryProvider _binaries =
        new BundledBinaryProvider(Path.Combine(AppContext.BaseDirectory, "assets", "binaries"));

    public PreviewProxyIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private PreviewProxyService NewProxyService() =>
        new(new FfmpegRunner(_runner, _binaries), _dir);

    /// <summary>Synthesizes a REAL VP9/WebM clip with ffmpeg's own <c>testsrc2</c>. 1280x720 — deliberately
    /// WIDER than the proxy's 640px cap, so the downscale is actually exercised. (The brief's own draft
    /// fixture was 640x360 — already AT the cap — so its "width &lt;= 640" assertion would pass even if
    /// the scale filter did nothing at all. A fixture only pins an invariant if it varies along the axis
    /// the invariant is about, and this project has shipped that exact mistake five times running.)</summary>
    private async Task<string> MakeVp9ClipAsync(string name, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var result = await _runner.RunAsync(_binaries.GetPath(ExternalBinary.Ffmpeg), [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate=25:duration={seconds}",
            "-c:v", "libvpx-vp9", "-b:v", "200k", path,
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
    public async Task GetOrCreateAsync_TurnsAVp9WebmIntoAProxyOfTheSameLength()
    {
        // THE FIXTURE MUST VARY ALONG THE AXIS THE INVARIANT IS ABOUT. A VP9/WebM source is the ONLY kind
        // that proves anything here: MediaElement genuinely cannot play it (verified against real files),
        // which is the entire reason the proxy exists. An MP4/H.264 fixture would prove nothing, because
        // the fast path would carry it and PreviewProxyService would never even be exercised.
        var source = await MakeVp9ClipAsync("src.webm", seconds: 4);

        var info = await AnalyzeAsync(source);

        // Assert the source really IS what this test thinks it is. If a future ffmpeg silently produced
        // something else here (a different codec, a container ffprobe doesn't call "webm"), the rest of
        // this test would quietly stop testing anything -- and still pass.
        Assert.Equal("vp9", info.Video!.CodecName);
        Assert.Contains("webm", info.ContainerFormat, StringComparison.OrdinalIgnoreCase);

        var result = await NewProxyService().GetOrCreateAsync(source, info);
        Assert.True(result.IsSuccess, result.Error);

        // Probe the PROXY -- ffmpeg's exit code is exactly what cannot be trusted.
        var proxy = await AnalyzeAsync(result.Value!);
        Assert.Equal("h264", proxy.Video!.CodecName);   // a codec MediaElement can actually open
        Assert.Equal(0, proxy.Video.Width % 2);          // even -- libx264 requires it

        // The downscale, pinned with EXACT numbers rather than an inequality a no-op could satisfy: a
        // 1280x720 source through the proxy's min(640,iw) cap at preserved aspect must land at exactly
        // 640x360.
        Assert.Equal(640, proxy.Video.Width);
        Assert.Equal(360, proxy.Video.Height);

        // THE HARD RULE: the proxy must NOT re-time. If its timeline drifted from the source's, every
        // captured timestamp would be a lie and the GIF would be cut somewhere other than where the user
        // saw.
        //
        // It takes BOTH assertions below to pin that, and duration alone is the weaker one. Measured
        // against real ffmpeg: an `fps=12` filter on this source throws away HALF THE FRAMES and the
        // proxy still probes 4.000000s -- bit-identical to the source. An fps= filter inside the -vf
        // value is precisely how a future "let's also normalize the preview frame rate" edit would
        // arrive, and a duration check is structurally BLIND to it. The user would then be capturing
        // timestamps on frames they were never shown, with this test green.
        //
        // So the frame rate must survive the transcode UNCHANGED. This kills every fps=/-r mutant
        // outright, whatever it does to the duration.
        Assert.Equal(info.Video.FrameRate, proxy.Video.FrameRate);

        // ...and duration catches what the frame rate cannot: a stretched timeline (-itsscale) or a seek
        // (-ss), which preserve the frame rate while moving every timestamp.
        //
        // Tolerance is 0.05s -- roughly one frame at 25fps. NOT a rounding allowance: the real measured
        // drift on this VP9->H.264 pipeline is EXACTLY 0.000s, deterministically, across repeated runs
        // (both containers land on 4.000000), so there is nothing to absorb. It was 0.2s, which let
        // `-r 24` (0.083s) and `-itsscale 1.02` (0.080s) through -- 2.4x too loose to catch the very
        // flag the rule names.
        var expected = info.Duration.TotalSeconds;
        var actual = proxy.Duration.TotalSeconds;
        Assert.True(
            Math.Abs(expected - actual) <= 0.05,
            $"proxy duration drifted from the source's: source={expected}s, proxy={actual}s");
    }
}
