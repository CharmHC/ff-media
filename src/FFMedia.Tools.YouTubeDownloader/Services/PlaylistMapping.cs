using FFMedia.Tools.YouTubeDownloader.Models;
using YoutubeDLSharp.Metadata;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Maps a probed <see cref="VideoData"/> to the media entries to enqueue (pure).</summary>
public static class PlaylistMapping
{
    /// <summary>A playlist/channel (Entries populated) → one entry per child; otherwise a single entry.</summary>
    public static IReadOnlyList<MediaEntry> ToEntries(VideoData data, string requestedUrl)
    {
        if (data.Entries is { Length: > 0 })
            return data.Entries
                .Select(e => new MediaEntry(EntryUrl(e) ?? requestedUrl, e.Title ?? string.Empty))
                .ToList();

        return new[] { new MediaEntry(EntryUrl(data) ?? requestedUrl, data.Title ?? string.Empty) };
    }

    private static string? EntryUrl(VideoData v) =>
        !string.IsNullOrWhiteSpace(v.WebpageUrl) ? v.WebpageUrl
        : !string.IsNullOrWhiteSpace(v.Url) ? v.Url
        : null;
}
