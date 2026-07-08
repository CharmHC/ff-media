using FFMedia.Core.Binaries;
using Xunit;

namespace FFMedia.Tests.Binaries;

public class YtDlpVersionTests
{
    [Theory]
    [InlineData("2026.07.04", "2026.07.01", true)]   // newer date
    [InlineData("2026.07.04", "2026.07.04", false)]  // equal
    [InlineData("2026.07.01", "2026.07.04", false)]  // older
    [InlineData("2026.08.01", "2026.07.31", true)]   // month rollover
    [InlineData("2026.07.04.1", "2026.07.04", true)] // same-day suffix
    [InlineData("2026.07.04", "2026.07.04.1", false)]
    [InlineData("2026.07.4", "2026.07.04", false)]   // zero-padding skew → equal
    public void IsNewer_ComparesNumericComponents(string latest, string installed, bool expected)
    {
        Assert.Equal(expected, YtDlpVersion.IsNewer(latest, installed));
    }

    [Theory]
    [InlineData("2026.07.04", null)]
    [InlineData("2026.07.04", "")]
    [InlineData("2026.07.04", "   ")]
    public void IsNewer_SurfacesLatest_WhenInstalledUnknown(string latest, string? installed)
    {
        Assert.True(YtDlpVersion.IsNewer(latest, installed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsNewer_False_WhenLatestMissing(string? latest)
    {
        Assert.False(YtDlpVersion.IsNewer(latest, "2026.07.04"));
    }

    [Fact]
    public void IsNewer_FallsBackToInequality_ForUnparseableTags()
    {
        // Non-numeric tags can't be ordered, so any difference surfaces (fail-open).
        Assert.True(YtDlpVersion.IsNewer("nightly-b", "nightly-a"));
        Assert.False(YtDlpVersion.IsNewer("nightly-a", "nightly-a"));
    }
}
