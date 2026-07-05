using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Fetches media metadata for a URL without downloading.</summary>
public interface IMediaProbe
{
    Task<Result<MediaInfo>> ProbeAsync(string url, CancellationToken ct);
}
