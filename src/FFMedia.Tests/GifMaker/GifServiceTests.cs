using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;
using FFMedia.Tools.GifMaker.Services;
using Xunit;

namespace FFMedia.Tests.GifMaker;

public class GifServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ffmedia-gif-tests-" + Guid.NewGuid().ToString("N"));

    public GifServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a test that leaked a handle should not also fail the run on cleanup
        }
    }

    /// <summary>Writes a plausible-looking output file whenever the RENDER pass runs, so the service has
    /// something to verify. Scriptable per pass.</summary>
    private sealed class FakeFfmpeg : IFfmpegRunner
    {
        private readonly string _outputPath;

        public FakeFfmpeg(string outputPath) => _outputPath = outputPath;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Func<int, Result> Behavior { get; set; } = _ => Result.Success();

        /// <summary>Bytes written for the render pass. 0 = write nothing (simulating a failed render).</summary>
        public int OutputBytes { get; set; } = 1024;

        public Task<Result> RunAsync(
            IReadOnlyList<string> arguments, IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            Calls.Add(arguments);

            // Written BEFORE the cancellation check, deliberately: real ffmpeg can already have written
            // bytes to disk by the moment it is killed. A fake that throws before writing anything can
            // never expose a caller that forgets to clean up a genuinely half-written file.
            var isRender = arguments.Contains("-lavfi");
            if (isRender && OutputBytes > 0)
            {
                File.WriteAllBytes(_outputPath, new byte[OutputBytes]);
            }
            else if (!isRender)
            {
                File.WriteAllBytes(arguments[^1], new byte[64]); // the palette
            }

            ct.ThrowIfCancellationRequested();

            return Task.FromResult(Behavior(Calls.Count));
        }
    }

    private sealed class FakeAnalyzer : IMediaAnalyzer
    {
        public Func<string, Result<MediaInfo>> Behavior { get; set; } =
            _ => Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(3), "gif",
                new VideoStreamInfo(480, 270, new FrameRate(15, 1), "gif", "bgra", 0), null));

        public Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(Behavior(filePath));
    }

    private sealed class FakeStore : IGifSizeProfileStore
    {
        public GifSizeProfile Profile { get; set; } = new();

        /// <summary>Lets a test simulate a store whose <see cref="Save"/> throws (e.g. a locked or
        /// read-only profile file), scriptable per test.</summary>
        public Action? OnSave { get; set; }

        public GifSizeProfile Load() => Profile;

        public void Save(GifSizeProfile profile)
        {
            OnSave?.Invoke();
            Profile = profile;
        }
    }

    private (GifService Service, FakeFfmpeg Ffmpeg, FakeAnalyzer Analyzer, FakeStore Store, GifRequest Request) Build()
    {
        var output = Path.Combine(_dir, "out.gif");
        var ffmpeg = new FakeFfmpeg(output);
        var analyzer = new FakeAnalyzer();
        var store = new FakeStore();
        var service = new GifService(ffmpeg, analyzer, store, _dir);
        var request = new GifRequest(
            Path.Combine(_dir, "src.mp4"), TimeSpan.Zero, TimeSpan.FromSeconds(3),
            new Resolution(480, 270), new FrameRate(15, 1), output);
        File.WriteAllBytes(request.SourcePath, new byte[128]);

        return (service, ffmpeg, analyzer, store, request);
    }

    [Fact]
    public async Task CreateAsync_RunsBothPasses_PaletteThenRender()
    {
        var (service, ffmpeg, _, _, request) = Build();

        var result = await service.CreateAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, ffmpeg.Calls.Count);
        Assert.Contains("palettegen", string.Join(" ", ffmpeg.Calls[0]), StringComparison.Ordinal);
        Assert.Contains("paletteuse", string.Join(" ", ffmpeg.Calls[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_DeletesTheTempPalette_OnSuccess()
    {
        var (service, _, _, _, request) = Build();

        await service.CreateAsync(request);

        Assert.Empty(Directory.GetFiles(_dir, "*.png"));
    }

    [Fact]
    public async Task CreateAsync_DeletesTheTempPalette_WhenTheRenderFails()
    {
        var (service, ffmpeg, _, _, request) = Build();
        ffmpeg.Behavior = call => call == 2 ? Result.Failure("Error while opening encoder") : Result.Success();

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_dir, "*.png"));
    }

    [Fact]
    public async Task CreateAsync_WhenFfmpegLies_TheOutputIsProbedAndTheCorruptFileIsDeleted()
    {
        // THE RULE: ffmpeg's exit code is exactly what cannot be trusted -- its concat demuxer exits 0
        // having silently dropped segments. A GIF that exits 0 but is not a GIF must NOT be handed over
        // as a success, and must not be left on disk for the user to find and believe.
        //
        // The analyzer must succeed on the SOURCE (so preflight passes and both ffmpeg passes actually
        // run) and fail only on the OUTPUT (so it's VerifyAsync's re-probe -- not PreflightAsync's probe
        // of the source -- that this test is proving). A path-blind fake here would let this test pass
        // for the wrong reason: rejecting the source before ffmpeg is ever invoked.
        var (service, ffmpeg, analyzer, _, request) = Build();
        analyzer.Behavior = path => path == request.OutputPath
            ? Result<MediaInfo>.Failure("Invalid data found when processing input")
            : Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(3), "mov,mp4,m4a",
                new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0), null));

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, ffmpeg.Calls.Count); // both passes actually ran -- this is VerifyAsync's rejection, not preflight's
        Assert.False(File.Exists(request.OutputPath), "a GIF that failed verification must be deleted");
    }

    [Fact]
    public async Task CreateAsync_WhenTheOutputIsEmpty_ItFails()
    {
        var (service, ffmpeg, _, _, request) = Build();
        ffmpeg.OutputBytes = 0; // ffmpeg "succeeded" but wrote nothing

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CreateAsync_RecordsTheActualSize_SoTheEstimateLearns()
    {
        var (service, _, _, store, request) = Build();
        var before = store.Profile.SampleCount;

        var result = await service.CreateAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(before + 1, store.Profile.SampleCount);

        // Recompute the SAME formula GifSizeProfile.Record uses, from the real output file's real byte
        // length -- not just the sample count -- so a hard-coded Record(999, 1, 1) inside GifService
        // cannot pass this test identically to the real `new FileInfo(request.OutputPath).Length`.
        var actualBytes = new FileInfo(request.OutputPath).Length;
        var pixelsPerFrame = (long)request.Size.Width * request.Size.Height;
        var expected = actualBytes / (double)pixelsPerFrame / request.FrameCount;
        Assert.Equal(expected, store.Profile.BytesPerPixelPerFrame, precision: 10);
    }

    [Fact]
    public async Task CreateAsync_WhenTheProfileStoreIsLocked_TheGifIsStillAReportedSuccess()
    {
        // RecordActualSize's own comment promises "a broken profile store must never fail a GIF the
        // user already has" -- but JsonStore<T>.Save throws UnauthorizedAccessException (not IOException)
        // for a read-only/locked destination, and that does not inherit from IOException. A verified,
        // successful GIF must not become an unhandled crash just because gif-size.json is locked.
        var (service, _, _, store, request) = Build();
        store.OnSave = () => throw new UnauthorizedAccessException("gif-size.json is locked");

        var result = await service.CreateAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(File.Exists(request.OutputPath), "the GIF itself must survive a broken profile store");
    }

    [Fact]
    public async Task CreateAsync_RejectsARangeOutsideTheSource_BeforeRunningFfmpeg()
    {
        var (service, ffmpeg, analyzer, _, _) = Build();
        analyzer.Behavior = _ => Result<MediaInfo>.Success(new MediaInfo(
            TimeSpan.FromSeconds(5), "mov,mp4,m4a",
            new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0), null));

        var request = new GifRequest(
            Path.Combine(_dir, "src.mp4"), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(9),
            new Resolution(480, 270), new FrameRate(15, 1), Path.Combine(_dir, "out.gif"));

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Empty(ffmpeg.Calls); // rejected at preflight -- do not spend an encode to find this out
    }

    [Fact]
    public async Task CreateAsync_WhenCancelled_LeavesNoPaletteAndNoHalfWrittenGif()
    {
        var (service, ffmpeg, _, _, request) = Build();
        using var cts = new CancellationTokenSource();
        ffmpeg.Behavior = call =>
        {
            if (call == 1)
            {
                cts.Cancel();
            }

            return Result.Success();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateAsync(request, progress: null, cts.Token));

        Assert.Empty(Directory.GetFiles(_dir, "*.png"));
        Assert.False(File.Exists(request.OutputPath));
    }
}
