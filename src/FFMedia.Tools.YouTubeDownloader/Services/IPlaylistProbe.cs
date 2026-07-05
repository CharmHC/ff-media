using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Resolves a URL into the media entries to download (1 for a video, N for a playlist/channel).</summary>
public interface IPlaylistProbe
{
    Task<Result<IReadOnlyList<MediaEntry>>> ExpandAsync(string url, CancellationToken ct);
}
