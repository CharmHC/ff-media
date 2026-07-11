using System.Text.Json.Serialization;

namespace FFMedia.Core.History;

/// <summary>One finished job recorded for the history screen.</summary>
/// <param name="Url">The source URL for a download. Empty for a merge, which has no URL —
/// its inputs are local files.</param>
/// <param name="Source">Which tool produced this row. Defaults to <see cref="HistorySource.Download"/>
/// so a history.json written before merges existed — which has no Source property at all — still
/// deserializes instead of throwing (a throw would make JsonStore quarantine the file and hand back
/// an empty history). Read through <see cref="TolerantHistorySourceConverter"/> for the same reason:
/// a value we cannot interpret must degrade this one field, never destroy the whole file.</param>
public sealed record HistoryEntry(
    string Title,
    string Url,
    string? OutputPath,
    string Format,
    DateTimeOffset Timestamp,
    string Status,
    [property: JsonConverter(typeof(TolerantHistorySourceConverter))]
    HistorySource Source = HistorySource.Download);
