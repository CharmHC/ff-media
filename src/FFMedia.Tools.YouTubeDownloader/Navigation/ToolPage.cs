using FFMedia.Core.Tools;

namespace FFMedia.Tools.YouTubeDownloader.Navigation;

public sealed class ToolPage : IToolPage
{
    public ToolPage(string toolId, Type pageType)
    {
        ToolId = toolId;
        PageType = pageType;
    }

    public string ToolId { get; }
    public Type PageType { get; }
}
