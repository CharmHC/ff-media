namespace FFMedia.Media;

/// <summary>The first audio stream of a media file.</summary>
public sealed record AudioStreamInfo(string CodecName, int SampleRate, int Channels);
