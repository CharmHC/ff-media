namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>Maximum video height; yt-dlp picks the best stream at or below it. Best = no cap.</summary>
public enum VideoResolution { Best, P2160, P1440, P1080, P720, P480 }
