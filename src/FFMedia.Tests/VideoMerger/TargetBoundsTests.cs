using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

public class TargetBoundsTests
{
    [Fact]
    public void Resolution_RendersAsWidthByHeight_ForTheComboBox()
    {
        Assert.Equal("1920 × 1080", new Resolution(1920, 1080).ToString());
    }

    [Fact]
    public void StandardRates_IsExposed_SoTargetBoundsAndDerivationCannotDrift()
    {
        // TargetBounds must offer the SAME rates the derivation snaps to. A second, copied array
        // would let the offered rate and the derived rate disagree — the drift this design exists
        // to prevent.
        Assert.Contains(new FrameRate(30, 1), MergeTargetDerivation.StandardRates);
        Assert.Contains(new FrameRate(60, 1), MergeTargetDerivation.StandardRates);
        Assert.Equal(8, MergeTargetDerivation.StandardRates.Count);
    }
}
