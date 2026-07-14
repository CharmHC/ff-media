using FFMedia.Core.Results;

namespace FFMedia.Media.Preview;

public interface IPreviewProxyService
{
    /// <summary>Returns a path the player can definitely open — reusing a cached proxy when there is
    /// one, and transcoding otherwise.</summary>
    Task<Result<string>> GetOrCreateAsync(
        string sourcePath, MediaInfo info, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Deletes proxies nothing is using any more. A hard kill must not leak them forever.</summary>
    void SweepStale();
}
