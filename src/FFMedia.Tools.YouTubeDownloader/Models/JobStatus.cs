namespace FFMedia.Tools.YouTubeDownloader.Models;

/// <summary>Lifecycle state of a queued download. Fetching happens at add-time before a job exists.</summary>
public enum JobStatus { Queued, Downloading, Processing, Completed, Canceled, Failed }
