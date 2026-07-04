using FFMedia.Core.Binaries;
using FFMedia.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace FFMedia.Core;

/// <summary>Registers UI-agnostic FFMedia core services.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <param name="binariesDirectory">Directory holding bundled yt-dlp.exe / ffmpeg.exe.</param>
    public static IServiceCollection AddFFMediaCore(this IServiceCollection services, string binariesDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binariesDirectory);

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IBinaryProvider>(_ => new BundledBinaryProvider(binariesDirectory));
        return services;
    }
}
