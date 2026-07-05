using System.IO;
using YoutubeDLSharp.Options;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Builds yt-dlp option sets. M1: single-video MP4.</summary>
public static class DownloadOptions
{
    public static OptionSet Mp4(string outputFolder) => new()
    {
        RecodeVideo = VideoRecodeFormat.Mp4,
        NoPlaylist = true,
        Output = Path.Combine(outputFolder, "%(title)s.%(ext)s"),
    };
}
