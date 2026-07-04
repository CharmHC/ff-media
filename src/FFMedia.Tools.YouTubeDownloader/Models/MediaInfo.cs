namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>Metadata about a media item probed from a URL.</summary>
public sealed record MediaInfo(string Title, TimeSpan? Duration, string? ThumbnailUrl, string? Uploader);
