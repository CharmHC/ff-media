using System.IO;
using FFMedia.Core.Persistence;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var d = AppSettings.Default;
        Assert.Equal(3, d.Version);
        Assert.Equal(3, d.MaxConcurrency);
        Assert.Equal(AppTheme.System, d.Theme);
        Assert.EndsWith(Path.Combine("Videos", "FFMedia"), d.DefaultOutputFolder);
    }

    [Fact]
    public void RoundTripsThroughJsonStore_WithThemeAsString()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonStore<AppSettings>(path, NullLogger.Instance);
        var original = AppSettings.Default with { MaxConcurrency = 5, Theme = AppTheme.Dark, DefaultOutputFolder = @"C:\videos" };

        store.Save(original);

        Assert.Contains("\"Dark\"", File.ReadAllText(path)); // enum persisted as string
        Assert.Equal(original, store.Load(() => AppSettings.Default));
    }
}
