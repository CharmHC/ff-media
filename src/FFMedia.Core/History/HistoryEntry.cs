namespace FFMedia.Core.History;

/// <summary>One finished download recorded for the history screen.</summary>
public sealed record HistoryEntry(
    string Title,
    string Url,
    string? OutputPath,
    string Format,
    DateTimeOffset Timestamp,
    string Status);
