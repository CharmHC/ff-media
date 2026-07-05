using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Infrastructure;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

public sealed class YtDlpPlaylistProbe : IPlaylistProbe
{
    private readonly IYoutubeDlFactory _factory;

    public YtDlpPlaylistProbe(IYoutubeDlFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<Result<IReadOnlyList<MediaEntry>>> ExpandAsync(string url, CancellationToken ct)
    {
        try
        {
            var ytdl = _factory.Create();
            // flat: fast playlist enumeration without probing each entry's full metadata.
            var res = await ytdl.RunVideoDataFetch(url, ct: ct, flat: true);
            if (!res.Success)
                return Result<IReadOnlyList<MediaEntry>>.Failure(string.Join(Environment.NewLine, res.ErrorOutput));

            return Result<IReadOnlyList<MediaEntry>>.Success(PlaylistMapping.ToEntries(res.Data, url));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<MediaEntry>>.Failure(ex.Message);
        }
    }
}
