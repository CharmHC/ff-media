namespace FFMedia.Core.History;

/// <summary>Versioned on-disk shape for history (the <c>Version</c> keeps a future SQLite migration clean).</summary>
public sealed record HistoryDocument(int Version, IReadOnlyList<HistoryEntry> Entries)
{
    public static HistoryDocument Empty { get; } = new(1, Array.Empty<HistoryEntry>());
}
