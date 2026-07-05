namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>Post-processing applied to a download: optional trim plus subtitle/metadata/thumbnail embedding.</summary>
public sealed record ProcessingOptions(
    TrimRange? Trim,
    bool PreciseCut,
    bool EmbedSubtitles,
    string SubtitleLanguage,
    bool EmbedMetadata,
    bool EmbedThumbnail)
{
    /// <summary>Metadata + thumbnail embedded; subtitles and trim off; default subtitle language "en".</summary>
    public static ProcessingOptions Default { get; } =
        new(Trim: null, PreciseCut: false, EmbedSubtitles: false, SubtitleLanguage: "en",
            EmbedMetadata: true, EmbedThumbnail: true);
}
