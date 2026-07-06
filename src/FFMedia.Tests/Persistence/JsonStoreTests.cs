using System.IO;
using FFMedia.Core.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.Persistence;

public class JsonStoreTests
{
    private sealed record Sample(int N, string Name);

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "ffmedia-tests", Guid.NewGuid().ToString("N"), "data.json");

    private static JsonStore<Sample> Store(string path) =>
        new(path, NullLogger.Instance);

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = Store(TempFile());
        var value = store.Load(() => new Sample(42, "fallback"));
        Assert.Equal(new Sample(42, "fallback"), value);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = TempFile();
        var store = Store(path);
        store.Save(new Sample(7, "seven"));
        var reloaded = Store(path).Load(() => new Sample(0, "x"));
        Assert.Equal(new Sample(7, "seven"), reloaded);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var path = TempFile(); // parent dir does not exist yet
        Store(path).Save(new Sample(1, "a"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_CorruptFile_QuarantinesToBak_AndReturnsDefault()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json ");

        var value = Store(path).Load(() => new Sample(99, "default"));

        Assert.Equal(new Sample(99, "default"), value);
        Assert.True(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path)); // moved aside
    }
}
