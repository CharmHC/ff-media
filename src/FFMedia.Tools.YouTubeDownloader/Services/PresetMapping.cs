using System.Text.Json;
using System.Text.Json.Serialization;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Serializes a <see cref="DownloadConfig"/> to/from a preset payload string.
/// Deserialization is tolerant: blank or malformed input yields <see cref="DownloadConfig.Default"/>.</summary>
public static class PresetMapping
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(DownloadConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, Options);
    }

    public static DownloadConfig Deserialize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return DownloadConfig.Default;
        }

        try
        {
            var config = JsonSerializer.Deserialize<DownloadConfig>(payload, Options);
            if (config is null)
            {
                return DownloadConfig.Default;
            }
            return config.Processing is null
                ? config with { Processing = ProcessingOptions.Default }
                : config;
        }
        catch (JsonException)
        {
            return DownloadConfig.Default;
        }
    }
}
