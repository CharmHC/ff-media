namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>Target bitrate for lossy audio. Best = yt-dlp default best; ignored for lossless (WAV/FLAC).</summary>
public enum AudioBitrate { Best, K320, K256, K192, K128 }
