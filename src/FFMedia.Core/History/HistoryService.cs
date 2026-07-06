using System.IO;
using FFMedia.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace FFMedia.Core.History;

/// <summary>JSON-file-backed <see cref="IHistoryService"/> (history.json under the data directory).</summary>
public sealed class HistoryService : IHistoryService
{
    private readonly JsonStore<HistoryDocument> _store;
    private readonly object _gate = new();
    private HistoryDocument _document;

    public HistoryService(string dataDirectory, ILogger<HistoryService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = new JsonStore<HistoryDocument>(Path.Combine(dataDirectory, "history.json"), logger);
        _document = _store.Load(() => HistoryDocument.Empty);
    }

    public IReadOnlyList<HistoryEntry> Query() => _document.Entries;

    public void Append(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var entries = new List<HistoryEntry>(_document.Entries.Count + 1) { entry };
            entries.AddRange(_document.Entries); // newest first
            _document = _document with { Entries = entries };
            _store.Save(_document);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _document = _document with { Entries = Array.Empty<HistoryEntry>() };
            _store.Save(_document);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
}
