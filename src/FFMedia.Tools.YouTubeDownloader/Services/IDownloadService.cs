using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Downloads a media item, reporting progress; returns the output file path.</summary>
public interface IDownloadService
{
    Task<Result<string>> DownloadAsync(DownloadRequest request, IProgress<DownloadUpdate> progress, CancellationToken ct);
}
