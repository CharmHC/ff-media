namespace FFMedia.Core.History;

/// <summary>Persisted record of finished downloads. Newest entry first.</summary>
public interface IHistoryService
{
    /// <summary>All recorded entries, newest first.</summary>
    IReadOnlyList<HistoryEntry> Query();

    /// <summary>Append an entry and persist.</summary>
    void Append(HistoryEntry entry);

    /// <summary>Remove all entries and persist.</summary>
    void Clear();

    /// <summary>Raised after the history changes (append or clear).</summary>
    event EventHandler? Changed;
}
