using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Infrastructure;

/// <summary>Creates a YoutubeDL configured with the bundled yt-dlp/ffmpeg paths.</summary>
public interface IYoutubeDlFactory
{
    YoutubeDL Create();
}
