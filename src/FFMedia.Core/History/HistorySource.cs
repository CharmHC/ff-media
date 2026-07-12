namespace FFMedia.Core.History;

/// <summary>Which tool produced a history row. Persisted by name (see <c>JsonStore&lt;T&gt;</c>).</summary>
public enum HistorySource
{
    /// <summary>A download (YouTube Downloader). The zero value, so it is also the back-compat default.</summary>
    Download,

    /// <summary>A local merge (Video Merger).</summary>
    Merge,
}
