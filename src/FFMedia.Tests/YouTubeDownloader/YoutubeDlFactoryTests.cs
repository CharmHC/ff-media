using FFMedia.Core.Binaries;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class YoutubeDlFactoryTests
{
    private sealed class FakeBinaryProvider : IBinaryProvider
    {
        public string GetPath(ExternalBinary binary) =>
            binary == ExternalBinary.YtDlp ? @"C:\bin\yt-dlp.exe" : @"C:\bin\ffmpeg.exe";
        public bool Exists(ExternalBinary binary) => true;
    }

    [Fact]
    public void Create_SetsYtDlpAndFfmpegPathsFromBinaryProvider()
    {
        var factory = new YoutubeDlFactory(new FakeBinaryProvider());

        var ytdl = factory.Create();

        Assert.Equal(@"C:\bin\yt-dlp.exe", ytdl.YoutubeDLPath);
        Assert.Equal(@"C:\bin\ffmpeg.exe", ytdl.FFmpegPath);
    }
}
