using FFMedia.Tools.YouTubeDownloader.Models;
using YoutubeDLSharp;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Maps YoutubeDLSharp progress to the module's DownloadUpdate.</summary>
public static class ProgressMapping
{
    public static DownloadUpdate ToUpdate(DownloadProgress p) => new(
        Percent: p.Progress * 100.0,
        Speed: p.DownloadSpeed,
        Eta: p.ETA,
        Stage: p.State.ToString());
}
