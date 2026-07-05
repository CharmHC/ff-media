namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>A request to download a single media item with the chosen output config.</summary>
public sealed record DownloadRequest(string Url, string OutputFolder, DownloadConfig Config);
