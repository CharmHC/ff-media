namespace FFMedia.Core.Settings;

/// <summary>Loads and persists <see cref="AppSettings"/>; notifies listeners on change.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    void Save(AppSettings settings);

    event EventHandler<AppSettings>? Changed;
}
