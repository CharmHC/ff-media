namespace FFMedia.Media;

/// <summary>What ffprobe tells us about a media file. <see cref="Audio"/> is null when the file
/// carries no audio stream — the case that breaks a naive ffmpeg concat.</summary>
public sealed record MediaInfo(
    TimeSpan Duration,
    string ContainerFormat,
    VideoStreamInfo? Video,
    AudioStreamInfo? Audio)
{
    public bool HasAudio => Audio is not null;

    /// <summary>Streams beyond the first video and the first audio — subtitles, extra audio tracks,
    /// data/attachment streams, a second video.</summary>
    /// <remarks><para><see cref="Video"/> and <see cref="Audio"/> describe only the FIRST stream of
    /// each kind, so without this the rest of the file is invisible. That invisibility is dangerous
    /// in the one direction that matters: a clip with an embedded subtitle track would look
    /// <em>fully conforming</em>, take the fast path, and be stream-copied into the concat — where
    /// ffmpeg index-matches the second clip's audio onto the first clip's subtitle slot, exits
    /// <b>0</b>, and hands the user a video whose later clips are silently mute. Concat's
    /// identical-stream-layout requirement (spec D4) is about ALL the streams, not just the two we
    /// happen to model.</para>
    /// <para>Not exotic: FFMedia's own YouTube Downloader writes exactly these files when
    /// "embed subtitles" is on. Such a clip is simply non-conforming — normalization re-encodes it
    /// mapping only <c>0:v:0</c> and one audio stream, which drops the extras.</para>
    /// <para>Defaults to 0 (no extras) so a hand-built fixture states the ordinary case; the real
    /// count always comes from <see cref="FfprobeParsing"/>.</para></remarks>
    public int ExtraStreamCount { get; init; }
}
