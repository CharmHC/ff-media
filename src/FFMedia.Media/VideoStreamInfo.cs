namespace FFMedia.Media;

/// <summary>The first video stream of a media file.</summary>
public sealed record VideoStreamInfo(
    int Width,
    int Height,
    FrameRate FrameRate,
    string CodecName,
    string PixelFormat,
    int Rotation);
