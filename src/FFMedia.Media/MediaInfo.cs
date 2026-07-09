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
}
