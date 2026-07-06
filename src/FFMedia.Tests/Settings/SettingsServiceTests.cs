using System.IO;
using FFMedia.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Settings;

public class SettingsServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Current_WhenNoFile_IsDefault()
    {
        var svc = new SettingsService(TempDir(), NullLogger<SettingsService>.Instance);
        Assert.Equal(AppSettings.Default, svc.Current);
    }

    [Fact]
    public void Save_UpdatesCurrent_RaisesChanged_AndPersists()
    {
        var dir = TempDir();
        var svc = new SettingsService(dir, NullLogger<SettingsService>.Instance);
        AppSettings? observed = null;
        svc.Changed += (_, s) => observed = s;

        var updated = AppSettings.Default with { MaxConcurrency = 6, Theme = AppTheme.Light };
        svc.Save(updated);

        Assert.Equal(updated, svc.Current);
        Assert.Equal(updated, observed);
        // A fresh service over the same directory reloads the saved value.
        Assert.Equal(updated, new SettingsService(dir, NullLogger<SettingsService>.Instance).Current);
    }
}
