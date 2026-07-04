using FFMedia.Core.Binaries;
using Xunit;

namespace FFMedia.Tests.Binaries;

public class BundledBinaryProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ffmedia-tests-" + Guid.NewGuid().ToString("N"));

    public BundledBinaryProviderTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData(ExternalBinary.YtDlp, "yt-dlp.exe")]
    [InlineData(ExternalBinary.Ffmpeg, "ffmpeg.exe")]
    public void GetPath_ReturnsExpectedFileUnderBinariesDirectory(ExternalBinary binary, string fileName)
    {
        var provider = new BundledBinaryProvider(_dir);
        Assert.Equal(Path.Combine(_dir, fileName), provider.GetPath(binary));
    }

    [Fact]
    public void Exists_IsFalse_WhenFileMissing()
    {
        var provider = new BundledBinaryProvider(_dir);
        Assert.False(provider.Exists(ExternalBinary.YtDlp));
    }

    [Fact]
    public void Exists_IsTrue_WhenFilePresent()
    {
        File.WriteAllText(Path.Combine(_dir, "ffmpeg.exe"), "stub");
        var provider = new BundledBinaryProvider(_dir);
        Assert.True(provider.Exists(ExternalBinary.Ffmpeg));
    }

    [Fact]
    public void Constructor_Throws_WhenDirectoryNull()
        => Assert.Throws<ArgumentNullException>(() => new BundledBinaryProvider(null!));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
