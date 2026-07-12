using System.IO;
using FFMedia.Core.Persistence;
using FFMedia.Tools.GifMaker.Models;
using Microsoft.Extensions.Logging;

namespace FFMedia.Tools.GifMaker.Services;

public interface IGifSizeProfileStore
{
    GifSizeProfile Load();

    void Save(GifSizeProfile profile);
}

/// <summary>Persists the profile to <c>gif-size.json</c> beside the app's other data. Mirrors
/// <c>SpeedProfileStore</c> exactly — <c>JsonStore&lt;T&gt;</c> writes atomically and quarantines a
/// corrupt file to <c>.bak</c> rather than throwing.</summary>
public sealed class GifSizeProfileStore : IGifSizeProfileStore
{
    public const string FileName = "gif-size.json";

    private readonly JsonStore<GifSizeProfile> _store;

    public GifSizeProfileStore(string dataDirectory, ILogger<GifSizeProfileStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _store = new JsonStore<GifSizeProfile>(Path.Combine(dataDirectory, FileName), logger);
    }

    // NOTE: JsonStore.Load takes a FACTORY for the default — verified against SpeedProfileStore.
    public GifSizeProfile Load() => _store.Load(() => new GifSizeProfile());

    public void Save(GifSizeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _store.Save(profile);
    }
}
