using System.Net.Http;
using FFMedia.Core.Binaries;
using FFMedia.Core.History;
using FFMedia.Core.Presets;
using FFMedia.Core.Processes;
using FFMedia.Core.Settings;
using FFMedia.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFMedia.Core;

/// <summary>Registers UI-agnostic FFMedia core services.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <param name="binariesDirectory">Directory holding bundled yt-dlp.exe / ffmpeg.exe.</param>
    /// <param name="dataDirectory">Directory for persisted JSON (settings/presets/history), e.g. %AppData%\FFMedia.</param>
    public static IServiceCollection AddFFMediaCore(
        this IServiceCollection services, string binariesDirectory, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binariesDirectory);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IBinaryProvider>(_ => new BundledBinaryProvider(binariesDirectory));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IBinaryUpdateService>(sp => new BinaryUpdateService(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IBinaryProvider>(),
            new HttpClient(),
            sp.GetService<ILogger<BinaryUpdateService>>() ?? NullLogger<BinaryUpdateService>.Instance));
        services.AddSingleton<ISettingsService>(sp => new SettingsService(
            dataDirectory,
            sp.GetService<ILogger<SettingsService>>() ?? NullLogger<SettingsService>.Instance));
        services.AddSingleton<IHistoryService>(sp => new HistoryService(
            dataDirectory,
            sp.GetService<ILogger<HistoryService>>() ?? NullLogger<HistoryService>.Instance));
        services.AddSingleton<IPresetService>(sp => new PresetService(
            dataDirectory,
            sp.GetService<ILogger<PresetService>>() ?? NullLogger<PresetService>.Instance));
        return services;
    }
}
