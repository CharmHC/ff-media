using FFMedia.Core.Tools;

namespace FFMedia.Tools.VideoMerger;

/// <summary>FFMedia's second tool: standardize a set of local clips and join them into one video.</summary>
public sealed class VideoMergerTool : ITool
{
    public string Id => "video-merger";

    public string DisplayName => "Video Merger";

    public string Description => "Standardize and join clips into one video.";

    /// <summary>A WPF-UI <c>SymbolRegular</c> name. Verified against Wpf.Ui.dll 4.3.0:
    /// <c>MergeDuplicate24</c> does NOT exist; <c>VideoClipMultiple24</c> does. The shell falls back
    /// to <c>Apps24</c> on an unparseable name (see <c>MainWindowViewModel</c>), so a typo here
    /// degrades silently — <c>VideoMergerServiceCollectionTests</c> parses it to keep it honest.</summary>
    public string IconGlyph => "VideoClipMultiple24";

    /// <summary>Ordering is ASCENDING and the YouTube Downloader is 10, so this lands second — which
    /// is what the spec means by "the second tool". (The spec literally says <c>2</c>, which would
    /// sort it FIRST, above the downloader. That is a spec bug.)</summary>
    public int SortOrder => 20;
}
