using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;
using FFMedia.Tools.GifMaker.Services;
using Xunit;

namespace FFMedia.Tests.GifMaker;

public class GifServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ffmedia-gif-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>A recognisable byte sequence for a GIF the user already had. Used, byte for byte, to
    /// prove a "survives" assertion actually means untouched -- a truncating overwrite would also leave
    /// SOME file at the path, so existence alone would not catch it.</summary>
    private static readonly byte[] PreExistingGifBytes = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0xDE, 0xAD, 0xBE, 0xEF];

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

    /// <summary>Puts a "GIF the user already had" at the destination before the unhappy path runs.</summary>
    private static void PlacePreExistingGif(GifRequest request) => File.WriteAllBytes(request.OutputPath, PreExistingGifBytes);

    /// <summary>Not merely "the file still exists" -- a truncating overwrite would satisfy that too.
    /// Byte-for-byte is the only assertion a sibling-write regression cannot pass by accident.</summary>
    private static void AssertPreExistingGifSurvives(GifRequest request)
    {
        Assert.True(File.Exists(request.OutputPath), "a failed/cancelled render must not cost the user the GIF they already had");
        Assert.Equal(PreExistingGifBytes, File.ReadAllBytes(request.OutputPath));
    }

    /// <summary>Writes a plausible-looking output file whenever the RENDER pass runs, so the service has
    /// something to verify. Scriptable per pass.</summary>
    private sealed class FakeFfmpeg : IFfmpegRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Func<int, Result> Behavior { get; set; } = _ => Result.Success();

        /// <summary>Bytes written for the render pass. 0 = write nothing (simulating a failed render).</summary>
        public int OutputBytes { get; set; } = 1024;

        public Task<Result> RunAsync(
            IReadOnlyList<string> arguments, IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            Calls.Add(arguments);

            // Written to arguments[^1] -- wherever GifService actually told ffmpeg to write, which
            // since FIX 1 is a PENDING sibling for the render pass, not necessarily request.OutputPath.
            // A fake that ignored the args and wrote to a path captured at construction would silently
            // defeat every test in this file that proves the destination is left alone.
            //
            // Written BEFORE the cancellation check, deliberately: real ffmpeg can already have written
            // bytes to disk by the moment it is killed. A fake that throws before writing anything can
            // never expose a caller that forgets to clean up a genuinely half-written file.
            var isRender = arguments.Contains("-lavfi");
            if (isRender)
            {
                if (OutputBytes > 0)
                {
                    File.WriteAllBytes(arguments[^1], new byte[OutputBytes]);
                }
            }
            else
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
        var ffmpeg = new FakeFfmpeg();
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
    public async Task CreateAsync_MovesTheVerifiedRenderOntoTheRealDestination_OnSuccess()
    {
        // FIX 1: the render never targets request.OutputPath directly -- it lands on a PENDING sibling
        // first. A successful, verified render must still end up at the real filename, and no sibling
        // debris may be left behind next to it.
        var (service, _, _, _, request) = Build();

        var result = await service.CreateAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(request.OutputPath, result.Value);
        var gifFiles = Directory.GetFiles(_dir, "*.gif");
        Assert.Equal([request.OutputPath], gifFiles); // exactly the destination -- no leftover sibling
    }

    [Fact]
    public async Task CreateAsync_WhenTheRenderFails_ThePreExistingGifAtTheDestinationSurvives()
    {
        // THE CRITICAL FIX (final whole-branch review, FIX 1). Before it, a failing render deleted
        // request.OutputPath directly -- and since this tool's whole workflow is "load once, tune,
        // re-render to the SAME filename", that path very often already held a GOOD gif from the user's
        // previous attempt. Prove the fix: put a real file at the destination BEFORE the failing
        // render, and assert it comes out untouched, BYTE FOR BYTE -- existence alone would not catch a
        // truncating overwrite.
        var (service, ffmpeg, _, _, request) = Build();
        ffmpeg.Behavior = call => call == 2 ? Result.Failure("Error while opening encoder") : Result.Success();
        PlacePreExistingGif(request);

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_dir, "*.png"));
        AssertPreExistingGifSurvives(request);

        // And no half-written sibling was left lying around either -- the ONLY .gif in the folder is
        // the pre-existing one, untouched.
        Assert.Equal([request.OutputPath], Directory.GetFiles(_dir, "*.gif"));
    }

    [Fact]
    public async Task CreateAsync_WhenTheOutputIsProbedAndFoundCorrupt_ThePreExistingGifAtTheDestinationSurvives()
    {
        // THE RULE: ffmpeg's exit code is exactly what cannot be trusted -- its concat demuxer exits 0
        // having silently dropped segments. A GIF that exits 0 but is not a GIF must NOT be handed over
        // as a success, and the sibling that failed verification must be deleted -- WITHOUT ever
        // touching whatever GOOD gif the user already had at the destination.
        //
        // The analyzer must succeed on the SOURCE (so preflight passes and both ffmpeg passes actually
        // run) and fail on anything else (the PENDING sibling VerifyAsync probes -- its exact name is a
        // fresh GUID the test cannot predict). A path-blind fake here would let this test pass for the
        // wrong reason: rejecting the source before ffmpeg is ever invoked.
        var (service, ffmpeg, analyzer, _, request) = Build();
        analyzer.Behavior = path => path == request.SourcePath
            ? Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(3), "mov,mp4,m4a",
                new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0), null))
            : Result<MediaInfo>.Failure("Invalid data found when processing input");
        PlacePreExistingGif(request);

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, ffmpeg.Calls.Count); // both passes actually ran -- this is VerifyAsync's rejection, not preflight's
        AssertPreExistingGifSurvives(request);
        Assert.Equal([request.OutputPath], Directory.GetFiles(_dir, "*.gif")); // the failed sibling is gone too
    }

    [Fact]
    public async Task CreateAsync_WhenTheOutputIsEmpty_ThePreExistingGifAtTheDestinationSurvives()
    {
        var (service, ffmpeg, _, _, request) = Build();
        ffmpeg.OutputBytes = 0; // ffmpeg "succeeded" but wrote nothing
        PlacePreExistingGif(request);

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
        AssertPreExistingGifSurvives(request);
    }

    [Fact]
    public async Task CreateAsync_WhenTheOutputIsSuspiciouslyShort_ItIsDeletedAndFails_AndThePreExistingGifSurvives()
    {
        // FIX 3: the re-probe used to check only "a readable video with a video stream" -- which a GIF
        // that is far shorter than requested (a filtergraph that dies after the first frame, a
        // truncated write) satisfies just fine while exiting 0. The duration must be checked too.
        var (service, ffmpeg, analyzer, _, request) = Build(); // request.Duration == 3s
        analyzer.Behavior = path => path == request.SourcePath
            ? Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(3), "mov,mp4,m4a",
                new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0), null))
            : Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(0.2), "gif", // one frame's worth -- far short of the 3s asked for
                new VideoStreamInfo(480, 270, new FrameRate(15, 1), "gif", "bgra", 0), null));
        PlacePreExistingGif(request);

        var result = await service.CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Contains("0.2", result.Error);
        AssertPreExistingGifSurvives(request);
        Assert.Equal([request.OutputPath], Directory.GetFiles(_dir, "*.gif")); // the short sibling is gone
    }

    [Fact]
    public async Task CreateAsync_ToleratesASmallDurationDifference_FromFrameRoundingAndGifTiming()
    {
        // Frame-count rounding and the GIF format's own centisecond timing granularity mean the real
        // duration is never going to match the requested one exactly -- only a PROPORTIONAL tolerance
        // survives that without also hiding a genuinely truncated render (the previous test).
        var (service, _, analyzer, _, request) = Build(); // request.Duration == 3s
        analyzer.Behavior = path => path == request.SourcePath
            ? Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(3), "mov,mp4,m4a",
                new VideoStreamInfo(1920, 1080, new FrameRate(30, 1), "h264", "yuv420p", 0), null))
            : Result<MediaInfo>.Success(new MediaInfo(
                TimeSpan.FromSeconds(2.85), "gif", // 0.15s short -- comfortably inside the 0.5s floor
                new VideoStreamInfo(480, 270, new FrameRate(15, 1), "gif", "bgra", 0), null));

        var result = await service.CreateAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(File.Exists(request.OutputPath));
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
    public async Task CreateAsync_WhenTheOutputFolderIsMissing_ItIsCreatedAndTheRenderSucceeds()
    {
        // FIX 4: nothing else creates the output folder, and the Folder box is free text -- a typo'd or
        // since-deleted folder is one keystroke away. ffmpeg's own "No such file or directory" would
        // otherwise be misreported by GifErrors.Explain as "The video could not be found", blaming a
        // perfectly good source for a missing destination folder.
        var (service, _, _, _, request) = Build();
        var missingFolder = Path.Combine(_dir, "does-not-exist-yet", "nested");
        request = request with { OutputPath = Path.Combine(missingFolder, "out.gif") };

        Assert.False(Directory.Exists(missingFolder));

        var result = await service.CreateAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(Directory.Exists(missingFolder));
        Assert.True(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task CreateAsync_WhenTheDestinationCannotBeReplaced_FailsAndLeavesNoSiblingAndPreservesTheExistingGif()
    {
        // The final File.Move is unguarded no longer. This is the tool's core loop -- the user is very
        // likely LOOKING at the GIF they just made (a viewer, a browser tab, Explorer's preview pane)
        // while tuning the next render -- so the destination being held open by another process is not
        // exotic. Prove the REAL behaviour (not a mocked File.Move): hold an exclusive lock on the
        // destination for the duration of the call and let the real move attempt really fail.
        var (service, _, _, _, request) = Build();
        PlacePreExistingGif(request);

        Result<string> result;
        using (new FileStream(request.OutputPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await service.CreateAsync(request);
        }

        Assert.False(result.IsSuccess);
        Assert.Contains("could not be replaced", result.Error);
        AssertPreExistingGifSurvives(request);

        // The verified sibling must not become litter in the user's OWN output folder just because the
        // move failed -- the only *.gif here is the pre-existing one, untouched.
        Assert.Equal([request.OutputPath], Directory.GetFiles(_dir, "*.gif"));
    }

    [Fact]
    public async Task CreateAsync_WhenCancelled_ThePreExistingGifAtTheDestinationSurvives()
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
        PlacePreExistingGif(request);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateAsync(request, progress: null, cts.Token));

        Assert.Empty(Directory.GetFiles(_dir, "*.png"));
        AssertPreExistingGifSurvives(request);

        // Cancelling during the PALETTE pass is the sharpest case: the render pass never even ran, so
        // it never opened anything, and a naive "clean up OutputPath" would have destroyed a file
        // ffmpeg never touched.
        Assert.Equal([request.OutputPath], Directory.GetFiles(_dir, "*.gif"));
    }
}
