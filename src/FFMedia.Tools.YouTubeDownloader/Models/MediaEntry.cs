namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>One media item resolved from a URL: a single video, or one entry of a playlist/channel.</summary>
public sealed record MediaEntry(string Url, string Title);
