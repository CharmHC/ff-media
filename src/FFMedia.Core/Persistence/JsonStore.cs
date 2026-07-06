using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FFMedia.Core.Persistence;

/// <summary>
/// Atomic JSON persistence for a single value at a fixed file path. <see cref="Save"/> writes to a
/// temp file then moves it into place; <see cref="Load"/> returns a caller-supplied default
/// (quarantining a corrupt file to "&lt;path&gt;.bak") rather than throwing.
/// </summary>
public sealed class JsonStore<T>
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ILogger _logger;

    public JsonStore(string filePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);
        _filePath = filePath;
        _logger = logger;
    }

    public T Load(Func<T> defaultFactory)
    {
        ArgumentNullException.ThrowIfNull(defaultFactory);
        if (!File.Exists(_filePath))
        {
            return defaultFactory();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<T>(json, Options) ?? defaultFactory();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Corrupt or unreadable store at {Path}; quarantining and using default.", _filePath);
            TryQuarantine();
            return defaultFactory();
        }
    }

    public void Save(T value)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, _filePath, overwrite: true);
    }

    private void TryQuarantine()
    {
        try
        {
            File.Move(_filePath, _filePath + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to quarantine corrupt store at {Path}.", _filePath);
        }
    }
}
