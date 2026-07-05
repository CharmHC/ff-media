namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>
/// The user's chosen output for a download. All fields are always present; the group
/// irrelevant to <see cref="Kind"/> (video vs audio) is simply unused when options are built.
/// </summary>
public sealed record DownloadConfig(
    OutputKind Kind,
    VideoContainer Container,
    VideoResolution Resolution,
    AudioFormat AudioFormat,
    AudioBitrate Bitrate)
{
    /// <summary>App default: 1080p MP4 video.</summary>
    public static DownloadConfig Default { get; } =
        new(OutputKind.Video, VideoContainer.Mp4, VideoResolution.P1080, AudioFormat.Mp3, AudioBitrate.Best);
}
