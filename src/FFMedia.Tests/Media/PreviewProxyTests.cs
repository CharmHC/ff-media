using FFMedia.Core.Results;
using FFMedia.Media;
using FFMedia.Media.Preview;
using Xunit;

namespace FFMedia.Tests.Media;

public class PreviewProxyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ffmedia-proxy-tests-" + Guid.NewGuid().ToString("N"));

    public PreviewProxyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static MediaInfo Info(int w = 1920, int h = 1080, bool audio = true)
        => new(TimeSpan.FromSeconds(30), "matroska,webm",
            new VideoStreamInfo(w, h, new FrameRate(30, 1), "vp9", "yuv420p", 0),
            audio ? new AudioStreamInfo("opus", 48000, 2) : null);

    private sealed class FakeFfmpeg : IFfmpegRunner
    {
        private readonly List<FileStream> _held = new();

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Func<Result> Behavior { get; set; } = Result.Success;

        public int OutputBytes { get; set; } = 2048;

        /// <summary>Keeps a handle open on the output it just wrote — which is what a KILLED ffmpeg
        /// actually looks like from the service's side. <c>ProcessRunner</c> calls <c>Kill()</c> and
        /// rethrows WITHOUT awaiting exit, so the service's own <c>File.Delete</c> races a handle that
        /// is still open and loses, swallowing the <see cref="IOException"/>. The partial file survives.
        /// Nothing in this suite modelled that, so nothing could see where the partial file was LEFT.</summary>
        public bool HoldOutputOpen { get; set; }

        public void ReleaseHeldOutputs()
        {
            foreach (var stream in _held)
            {
                stream.Dispose();
            }

            _held.Clear();
        }

        public Task<Result> RunAsync(
            IReadOnlyList<string> arguments, IProgress<FfmpegProgress>? progress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(arguments);

            // Written BEFORE Behavior() runs: real ffmpeg can write partial/whole output and still
            // exit non-zero (or get cancelled after writing). Writing only on success made the
            // "no half-written proxy" test untriggerable -- there was never a file for the service's
            // cleanup to actually delete, so deleting that cleanup left the test green.
            if (OutputBytes > 0)
            {
                File.WriteAllBytes(arguments[^1], new byte[OutputBytes]);

                if (HoldOutputOpen)
                {
                    _held.Add(File.Open(arguments[^1], FileMode.Open, FileAccess.Read, FileShare.Read));
                }
            }

            return Task.FromResult(Behavior());
        }
    }

    // ---------- PreviewProxyArgs (pure) ----------

    [Fact]
    public void Args_NeverRetimeTheSource()
    {
        // THE ONE HARD RULE. The captured timestamp is read from the PLAYER's position, so if the proxy's
        // timeline differed from the source's by even a little, EVERY captured time would be a lie and the
        // GIF would be cut somewhere other than where the user saw. Rescale only. Never re-time.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");

        Assert.DoesNotContain("-r", args);          // no frame-rate change
        Assert.DoesNotContain("-r:v", args);
        Assert.DoesNotContain("-ss", args);         // no seek
        Assert.DoesNotContain("-t", args);          // no duration cap
        Assert.DoesNotContain("-to", args);
        Assert.DoesNotContain("-vsync", args);
        Assert.DoesNotContain("-fps_mode", args);   // the modern replacement for -vsync
        Assert.DoesNotContain("-itsscale", args);
        Assert.DoesNotContain("-async", args);

        // A future "let's also normalize the frame rate" edit would not add a new FLAG above -- it
        // would express itself INSIDE the -vf value (e.g. appending ",fps=24" or a setpts= term to
        // the filtergraph string). No DoesNotContain on list ELEMENTS can see inside a value, so the
        // filter string itself has to be checked directly.
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        Assert.DoesNotContain("fps=", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("setpts", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("framerate", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void Args_ProduceAFormatMediaElementCanActuallyPlay()
    {
        // MediaElement renders through Windows Media Foundation. VERIFIED: it plays H.264 and FAILS on
        // VP9. The proxy exists precisely to hand it something it can open.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");
        var joined = string.Join(" ", args);

        Assert.Contains("libx264", joined, StringComparison.Ordinal);
        Assert.Contains("yuv420p", joined, StringComparison.Ordinal);
        Assert.Equal(@"C:\tmp\p.mp4", args[^1]);
    }

    [Fact]
    public void Args_EscapeTheCommaInsideTheScaleExpression()
    {
        // A BARE comma inside min(640,iw) would SPLIT THE FILTERGRAPH -- ffmpeg separates filters with
        // commas -- and the whole -vf argument becomes garbage. It must be escaped.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Contains(@"\,", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("min(640,iw)", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void Args_CapTheWidthButNeverUpscaleATinySource()
    {
        // Upscaling a 320px source to 640 invents pixels, costs encode time, and buys nothing.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Contains("min(640", vf, StringComparison.Ordinal);   // a cap, not a target
        Assert.Contains(":h=-2", vf, StringComparison.Ordinal);     // height derived, and forced EVEN
    }

    [Fact]
    public void Args_DropAudioWhenTheSourceHasNone()
    {
        // Asking ffmpeg to encode an audio stream that does not exist fails the whole run.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(audio: false), @"C:\tmp\p.mp4");

        Assert.Contains("-an", args);
    }

    [Fact]
    public void Args_KeepAudioWhenTheSourceHasIt()
    {
        // The user is scrubbing to FIND a moment, and sound is often how a human finds it.
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(audio: true), @"C:\tmp\p.mp4");

        Assert.DoesNotContain("-an", args);
        Assert.Contains("aac", string.Join(" ", args), StringComparison.Ordinal);
    }

    [Fact]
    public void Args_DoNotRepeatTheFlagsTheRunnerAlreadyAdds()
    {
        var args = PreviewProxyArgs.Build(@"C:\in.webm", Info(), @"C:\tmp\p.mp4");

        Assert.DoesNotContain("-y", args);
        Assert.DoesNotContain("-progress", args);
        Assert.DoesNotContain("-hide_banner", args);
    }

    // ---------- PreviewProxyPath (pure) ----------

    [Fact]
    public void Path_ChangesWhenTheSourceChangesOnDisk()
    {
        // A cache keyed on the PATH ALONE would serve a stale proxy of a file the user has since
        // re-encoded or replaced -- they would scrub the OLD video and capture times into the NEW one.
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(file, new byte[100]);
        var before = PreviewProxyPath.For(file, _dir);

        File.WriteAllBytes(file, new byte[200]);            // same path, different content
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        var after = PreviewProxyPath.For(file, _dir);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Path_IsStableForAnUnchangedSource()
    {
        var file = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(file, new byte[100]);

        Assert.Equal(PreviewProxyPath.For(file, _dir), PreviewProxyPath.For(file, _dir));
    }

    // ---------- PreviewProxyService ----------

    private (PreviewProxyService Service, FakeFfmpeg Ffmpeg, string Source) Build()
    {
        var ffmpeg = new FakeFfmpeg();
        var source = Path.Combine(_dir, "src.webm");
        File.WriteAllBytes(source, new byte[128]);

        return (new PreviewProxyService(ffmpeg, _dir), ffmpeg, source);
    }

    [Fact]
    public async Task GetOrCreateAsync_BuildsAProxy_AndReturnsItsPath()
    {
        var (service, ffmpeg, source) = Build();

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(File.Exists(result.Value!));
        Assert.Single(ffmpeg.Calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReusesACachedProxy_RatherThanTranscodingTwice()
    {
        // Re-opening the same video must not pay the transcode again.
        var (service, ffmpeg, source) = Build();

        var first = await service.GetOrCreateAsync(source, Info());
        var second = await service.GetOrCreateAsync(source, Info());

        Assert.Equal(first.Value, second.Value);
        Assert.Single(ffmpeg.Calls);   // still ONE -- the second call was served from cache
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenFfmpegFails_ReportsFailure_AndLeavesNoHalfWrittenProxy()
    {
        var (service, ffmpeg, source) = Build();
        ffmpeg.Behavior = () => Result.Failure("Error while opening encoder");
        ffmpeg.OutputBytes = 512;   // ffmpeg wrote something before dying

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    public async Task GetOrCreateAsync_HandsFfmpegAPartFile_NeverTheCachePathItself()
    {
        // THE RULE THIS SERVICE WAS THE ONE PLACE ON THE BRANCH NOT TO FOLLOW. GifService and
        // MergeService both render to a SIBLING, verify THAT, and only then File.Move a proven-whole
        // file into place -- nothing the user already has is touched until there is something worth
        // replacing it with. Here the "something the user already has" is the CACHE ENTRY, and the cache
        // check is only File.Exists && Length > 0: whatever sits at the cache path IS the proxy, forever
        // (or for seven days). Pointing ffmpeg straight at it means a transcode that dies without
        // exiting cleanly leaves a moov-less, unplayable file that every future open of that video is
        // served, permanently. Verified against real ffmpeg by the reviewer: killing the transcode 1 s in
        // leaves 524 KB that ffprobe rejects with "moov atom not found" -- because -movflags +faststart
        // writes the moov atom LAST.
        var (service, ffmpeg, source) = Build();

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.True(result.IsSuccess, result.Error);

        var cachePath = PreviewProxyPath.For(source, _dir);
        var ffmpegOutput = ffmpeg.Calls[0][^1];

        Assert.NotEqual(cachePath, ffmpegOutput);
        Assert.Contains(".part", ffmpegOutput, StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(cachePath), Path.GetDirectoryName(ffmpegOutput));  // sibling: Move is a free rename

        // ...and it MUST still end in .mp4. ffmpeg picks its MUXER from the output extension and refuses
        // an unknown one outright -- a sibling named "....mp4.part" fails every real transcode with
        // "Unable to choose an output format", while every fake in this file accepts it happily. That is
        // exactly what the real-ffmpeg integration test caught, and this assertion is what keeps a unit
        // test able to catch it too.
        Assert.EndsWith(".mp4", ffmpegOutput, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(cachePath, result.Value);       // ...and only the FINISHED file lands at the cache path
        Assert.True(File.Exists(cachePath));
        Assert.False(File.Exists(ffmpegOutput));     // the part file is consumed by the move, not left behind
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenAKilledTranscodeStrandsItsOutput_TheCachePathStaysClean_SoTheNextOpenRetries()
    {
        // Reachable with NO race at all: quit or crash the app while "Preparing a preview…" is on screen.
        // And on cancel too -- ProcessRunner calls Kill() and rethrows WITHOUT awaiting exit, so the
        // service's own delete races a still-open handle and loses (modelled here by HoldOutputOpen).
        // Whatever ffmpeg was pointed at is what survives; the ONLY thing that keeps that survivor out of
        // the cache is that ffmpeg was never pointed at the cache path.
        var (service, ffmpeg, source) = Build();
        ffmpeg.Behavior = () => Result.Failure("ffmpeg was killed");
        ffmpeg.OutputBytes = 512;          // 512 bytes of a file with no moov atom: unplayable
        ffmpeg.HoldOutputOpen = true;      // ...and the cleanup delete cannot get rid of it

        var first = await service.GetOrCreateAsync(source, Info());
        Assert.False(first.IsSuccess);

        var cachePath = PreviewProxyPath.For(source, _dir);
        Assert.False(
            File.Exists(cachePath),
            "A killed transcode's partial output is sitting AT the cache path. File.Exists && Length > 0 " +
            "will serve it to MediaElement forever -- the preview is dead for this video for 7 days.");

        // And prove the consequence, not just the absence: the next open really does transcode again
        // rather than short-circuiting on garbage.
        ffmpeg.ReleaseHeldOutputs();
        ffmpeg.HoldOutputOpen = false;
        ffmpeg.Behavior = Result.Success;

        var second = await service.GetOrCreateAsync(source, Info());

        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(2, ffmpeg.Calls.Count);
        Assert.Equal(cachePath, second.Value);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenTheProxyIsEmpty_ItFails_RatherThanCachingRubbish()
    {
        // A zero-byte "success" cached forever would poison every future open of this video.
        var (service, ffmpeg, source) = Build();
        ffmpeg.OutputBytes = 0;

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCancelled_LeavesNoHalfWrittenProxy()
    {
        var (service, ffmpeg, source) = Build();
        using var cts = new CancellationTokenSource();
        ffmpeg.Behavior = () => { cts.Cancel(); return Result.Success(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetOrCreateAsync(source, Info(), progress: null, cts.Token));

        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenTheProxyDirectoryIsUnusable_ReportsFailure_RatherThanThrowing()
    {
        // Directory.CreateDirectory and the cache-check are real I/O. A locked-down or unavailable
        // directory must not THROW out of GetOrCreateAsync -- the preview is an aid, never a gate.
        // Simulated here by putting a FILE where the proxy directory needs to be: CreateDirectory
        // throws IOException when a file already occupies that path.
        var blocked = Path.Combine(_dir, "blocked-proxy-dir");
        File.WriteAllBytes(blocked, new byte[10]);

        var ffmpeg = new FakeFfmpeg();
        var source = Path.Combine(_dir, "unusable-src.webm");
        File.WriteAllBytes(source, new byte[64]);
        var service = new PreviewProxyService(ffmpeg, blocked);

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    // ---------- SweepStale ----------

    [Fact]
    public void SweepStale_DeletesOnlyProxiesPastTheCutoff_AndLeavesUnrelatedFilesAlone()
    {
        // The 7-day cutoff, the glob, and the delete-on-match were all UNVERIFIED before this test. A
        // reversed comparison would silently delete every fresh proxy on the first sweep -- or delete
        // nothing, ever -- and nothing else in the suite would notice either way.
        var (service, _, _) = Build();

        var stale = Path.Combine(_dir, "preview-stale0000000000000000.mp4");
        var fresh = Path.Combine(_dir, "preview-fresh0000000000000000.mp4");
        var unrelated = Path.Combine(_dir, "not-a-proxy.txt");   // shared temp space: not ours to touch
        File.WriteAllBytes(stale, new byte[10]);
        File.WriteAllBytes(fresh, new byte[10]);
        File.WriteAllBytes(unrelated, new byte[10]);

        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-8));
        // `fresh` keeps its just-written (now) timestamp.

        service.SweepStale();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void SweepStale_AlsoReapsStrandedPartFiles()
    {
        // The .part indirection keeps a killed transcode's wreckage OUT of the cache -- but the wreckage
        // still exists. An app killed mid-transcode never runs any cleanup at all, so without this the
        // fix would trade a poisoned cache for an unbounded pile of half-transcodes in %Temp%. Same
        // 7-day cutoff, same glob family.
        var (service, _, _) = Build();

        var stalePart = Path.Combine(_dir, "preview-abandoned222222222222.part.mp4");
        var freshPart = Path.Combine(_dir, "preview-inflight333333333333.part.mp4");
        File.WriteAllBytes(stalePart, new byte[10]);
        File.WriteAllBytes(freshPart, new byte[10]);
        File.SetLastWriteTimeUtc(stalePart, DateTime.UtcNow.AddDays(-8));

        service.SweepStale();

        Assert.False(File.Exists(stalePart), "A .part file stranded by a killed transcode is never reclaimed.");
        Assert.True(File.Exists(freshPart), "A transcode that is running RIGHT NOW must not have its output swept.");
    }

    /// <summary>FINDING (Task 5 review, IMPORTANT 2). <see cref="PreviewProxyService.SweepStale"/> was
    /// built and tested — and then called by nothing but the tests. Every fallback transcode leaked a
    /// proxy into <c>%Temp%</c> forever. It is now called from the service's own preflight, the way
    /// <c>MergeService</c> calls <c>TempDirectorySweeper.SweepOrphans</c> from its own: self-contained in
    /// the service rather than bolted onto app startup, where a future host that forgets the call silently
    /// reintroduces the leak.</summary>
    [Fact]
    public async Task GetOrCreateAsync_SweepsStaleProxies_SoAFallbackTranscodeDoesNotLeakForever()
    {
        var (service, _, source) = Build();

        var stale = Path.Combine(_dir, "preview-abandoned000000000000.mp4");
        File.WriteAllBytes(stale, new byte[10]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));

        var result = await service.GetOrCreateAsync(source, Info());

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(File.Exists(stale), "A proxy abandoned by a previous run was never reclaimed.");
    }

    [Fact]
    public async Task GetOrCreateAsync_ServedFromCache_StillSweeps_AndTheSweepIsNeverAGate()
    {
        // The sweep runs on the cached path too -- a user who only ever re-opens the SAME video would
        // otherwise never reclaim anything. And it must never break the proxy: the preview is an aid,
        // never a gate, so a file it cannot delete is skipped rather than raised.
        var (service, ffmpeg, source) = Build();
        var first = await service.GetOrCreateAsync(source, Info());

        var stale = Path.Combine(_dir, "preview-abandoned111111111111.mp4");
        File.WriteAllBytes(stale, new byte[10]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));

        // Held open: File.Delete throws IOException, which SweepStale swallows.
        using (var held = File.Open(stale, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = await service.GetOrCreateAsync(source, Info());

            Assert.True(second.IsSuccess, second.Error);
            Assert.Equal(first.Value, second.Value);
            Assert.Single(ffmpeg.Calls);   // still served from cache
        }
    }
}
