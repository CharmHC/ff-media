using System.IO;
using FFMedia.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace FFMedia.Core.Settings;

/// <summary>JSON-file-backed <see cref="ISettingsService"/> (settings.json under the data directory).</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly JsonStore<AppSettings> _store;

    public SettingsService(string dataDirectory, ILogger<SettingsService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _store = new JsonStore<AppSettings>(Path.Combine(dataDirectory, "settings.json"), logger);
        Current = _store.Load(() => AppSettings.Default);
    }

    public AppSettings Current { get; private set; }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.Save(settings);
        Current = settings;
        Changed?.Invoke(this, settings);
    }

    public event EventHandler<AppSettings>? Changed;
}
