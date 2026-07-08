using System;
using System.IO;
using FFMedia.Core.Persistence;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class AppSettingsYtDlpFlagTests
{
    [Fact]
    public void Default_EnablesYtDlpStartupCheck_AndIsVersion3()
    {
        var d = AppSettings.Default;
        Assert.True(d.CheckYtDlpForUpdatesOnStartup);
        Assert.Equal(3, d.Version);
    }

    [Fact]
    public void LoadingV2FileWithoutFlag_DefaultsFlagToTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Version": 2, "MaxConcurrency": 3, "Theme": "System", "CheckForUpdatesOnStartup": true }""");

        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var loaded = store.Load(() => AppSettings.Default);

        Assert.True(loaded.CheckYtDlpForUpdatesOnStartup); // missing field → default true
    }

    [Fact]
    public void FlagRoundTripsThroughJsonStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var original = AppSettings.Default with { CheckYtDlpForUpdatesOnStartup = false };

        store.Save(original);

        Assert.Equal(original, store.Load(() => AppSettings.Default));
    }
}
