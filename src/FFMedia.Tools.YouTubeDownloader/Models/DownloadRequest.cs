namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>A request to download a single media item as MP4 (M1 scope).</summary>
public sealed record DownloadRequest(string Url, string OutputFolder);
