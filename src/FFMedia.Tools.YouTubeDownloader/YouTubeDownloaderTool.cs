using FFMedia.Core.Tools;

namespace FFMedia.Tools.YouTubeDownloader;

public sealed class YouTubeDownloaderTool : ITool
{
    public string Id => "youtube-downloader";
    public string DisplayName => "YouTube Downloader";
    public string Description => "Download YouTube videos and audio.";
    public string IconGlyph => ""; // Segoe Fluent Icons "Download" glyph
    public int SortOrder => 10;
}
