using System.IO;
using System.Text.Json;
using FFMedia.Core.Persistence;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class AppSettingsUpdateFlagTests
{
    [Fact]
    public void Default_EnablesStartupUpdateCheck_AndIsVersion2()
    {
        var d = AppSettings.Default;
        Assert.True(d.CheckForUpdatesOnStartup);
        Assert.Equal(2, d.Version);
    }

    [Fact]
    public void LoadingV1FileWithoutFlag_DefaultsFlagToTrue()
    {
        // Simulate an existing v1 settings.json written before the flag existed.
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Version": 1, "MaxConcurrency": 3, "Theme": "System" }""");

        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var loaded = store.Load(() => AppSettings.Default);

        Assert.True(loaded.CheckForUpdatesOnStartup); // missing field → default true
    }

    [Fact]
    public void FlagRoundTripsThroughJsonStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var original = AppSettings.Default with { CheckForUpdatesOnStartup = false };

        store.Save(original);

        Assert.Equal(original, store.Load(() => AppSettings.Default));
    }
}
