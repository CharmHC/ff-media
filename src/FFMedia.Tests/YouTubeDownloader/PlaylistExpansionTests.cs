using System.Linq;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using YoutubeDLSharp.Metadata;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class PlaylistExpansionTests
{
    [Fact]
    public void ToEntries_SingleVideo_YieldsOneEntry_FromWebpageUrlAndTitle()
    {
        var data = new VideoData { Title = "Solo", WebpageUrl = "https://youtu.be/solo", Entries = null };
        var entries = PlaylistMapping.ToEntries(data, "https://youtu.be/solo");
        var only = Assert.Single(entries);
        Assert.Equal("https://youtu.be/solo", only.Url);
        Assert.Equal("Solo", only.Title);
    }

    [Fact]
    public void ToEntries_Playlist_YieldsOneEntryPerChild_WithUrlAndTitle()
    {
        var data = new VideoData
        {
            Title = "My Playlist",
            Entries = new[]
            {
                new VideoData { Title = "A", WebpageUrl = "https://youtu.be/a" },
                new VideoData { Title = "B", WebpageUrl = "https://youtu.be/b" },
                new VideoData { Title = "C", Url = "https://youtu.be/c" }, // no WebpageUrl → falls back to Url
            },
        };
        var entries = PlaylistMapping.ToEntries(data, "https://list");
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { "https://youtu.be/a", "https://youtu.be/b", "https://youtu.be/c" },
            entries.Select(e => e.Url).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, entries.Select(e => e.Title).ToArray());
    }

    [Fact]
    public void ToEntries_SingleVideoWithNoUrls_FallsBackToRequestedUrl()
    {
        var data = new VideoData { Title = "T", Url = null, WebpageUrl = null, Entries = null };
        var only = Assert.Single(PlaylistMapping.ToEntries(data, "https://requested"));
        Assert.Equal("https://requested", only.Url);
    }
}
