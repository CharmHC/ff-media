namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>A progress update during a download.</summary>
public sealed record DownloadUpdate(double Percent, string? Speed, string? Eta, string Stage);
