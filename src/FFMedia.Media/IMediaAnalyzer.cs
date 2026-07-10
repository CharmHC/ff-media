using FFMedia.Core.Results;

namespace FFMedia.Media;

/// <summary>Probes a media file's duration and stream parameters.</summary>
public interface IMediaAnalyzer
{
    /// <summary>Probes <paramref name="filePath"/>. Returns a failure (never throws) when the
    /// binary is missing, the file is unreadable, or the output is unusable. Cancellation
    /// propagates as <see cref="OperationCanceledException"/>.</summary>
    Task<Result<MediaInfo>> AnalyzeAsync(string filePath, CancellationToken ct = default);
}
