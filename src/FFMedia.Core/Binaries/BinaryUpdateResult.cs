namespace FFMedia.Core.Binaries;

/// <summary>Outcome of a yt-dlp self-update attempt.</summary>
public sealed record BinaryUpdateResult(bool Updated, string? FromVersion, string? ToVersion, string Message);
