using FFMedia.Core.Binaries;
using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Infrastructure;

public sealed class YoutubeDlFactory : IYoutubeDlFactory
{
    private readonly IBinaryProvider _binaries;

    public YoutubeDlFactory(IBinaryProvider binaries)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        _binaries = binaries;
    }

    public YoutubeDL Create() => new()
    {
        YoutubeDLPath = _binaries.GetPath(ExternalBinary.YtDlp),
        FFmpegPath = _binaries.GetPath(ExternalBinary.Ffmpeg),
    };
}
