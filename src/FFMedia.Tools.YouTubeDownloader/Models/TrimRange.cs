namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>A clip range within a media item.</summary>
public sealed record TrimRange(TimeSpan Start, TimeSpan End);
