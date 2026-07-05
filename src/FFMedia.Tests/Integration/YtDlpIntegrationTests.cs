using FFMedia.Core.Binaries;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

namespace FFMedia.Tests.Integration;

[Trait("Category", "Integration")]
public class YtDlpIntegrationTests
{
    // Resolves the real bundled binaries relative to the test output's app base.
    // The FFMedia.Tests.csproj copies assets/binaries/ into the test output when present
    // (fetched locally via build/fetch-binaries.ps1; the folder is git-ignored).
    private static IBinaryProvider RealBinaries()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "binaries");
        return new BundledBinaryProvider(dir);
    }

    // A short, stable, license-permissive video. "Me at the zoo" — the first YouTube video (~19s).
    private const string TestUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    [Fact]
    public async Task Probe_ReturnsTitle()
    {
        var binaries = RealBinaries();
        Assert.True(binaries.Exists(ExternalBinary.YtDlp), "Run build/fetch-binaries.ps1 first.");
        var probe = new YtDlpMediaProbe(new YoutubeDlFactory(binaries));

        var result = await probe.ProbeAsync(TestUrl, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.Title));
    }

    [Fact]
    public async Task Download_ProducesMp4File()
    {
        var binaries = RealBinaries();
        Assert.True(binaries.Exists(ExternalBinary.Ffmpeg), "Run build/fetch-binaries.ps1 first.");
        var outDir = Path.Combine(Path.GetTempPath(), "ffmedia-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var svc = new YtDlpDownloadService(new YoutubeDlFactory(binaries));
            var updates = new List<DownloadUpdate>();
            var progress = new Progress<DownloadUpdate>(updates.Add);

            var result = await svc.DownloadAsync(new DownloadRequest(TestUrl, outDir, DownloadConfig.Default), progress, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error);
            Assert.NotEmpty(Directory.GetFiles(outDir, "*.mp4"));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }
}
