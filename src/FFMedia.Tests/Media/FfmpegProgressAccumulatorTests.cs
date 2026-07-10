using FFMedia.Media;
using Xunit;

namespace FFMedia.Tests.Media;

public class FfmpegProgressAccumulatorTests
{
    [Fact]
    public void Add_EmitsNothing_UntilProgressLine()
    {
        var accumulator = new FfmpegProgressAccumulator();

        Assert.Null(accumulator.Add("frame=30"));
        Assert.Null(accumulator.Add("out_time_us=1000000"));
        Assert.Null(accumulator.Add("speed=2.5x"));
    }

    [Fact]
    public void Add_EmitsSnapshot_OnProgressContinue()
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=1500000");
        accumulator.Add("speed=2.5x");

        var progress = accumulator.Add("progress=continue");

        Assert.NotNull(progress);
        Assert.Equal(TimeSpan.FromSeconds(1.5), progress!.Position);
        Assert.Equal(2.5, progress.Speed);
        Assert.False(progress.IsFinal);
    }

    [Fact]
    public void Add_MarksFinal_OnProgressEnd()
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=3000000");

        var progress = accumulator.Add("progress=end");

        Assert.NotNull(progress);
        Assert.True(progress!.IsFinal);
        Assert.Equal(TimeSpan.FromSeconds(3), progress.Position);
    }

    [Fact]
    public void Add_CarriesLastKnownValues_AcrossBlocks()
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=1000000");
        accumulator.Add("speed=4.0x");
        accumulator.Add("progress=continue");

        accumulator.Add("out_time_us=2000000");
        var progress = accumulator.Add("progress=continue");

        Assert.Equal(TimeSpan.FromSeconds(2), progress!.Position);
        Assert.Equal(4.0, progress.Speed); // speed absent from the second block
    }

    [Theory]
    [InlineData("speed=N/A")]
    [InlineData("speed=   ")]
    public void Add_TreatsUnknownSpeedAsZero(string speedLine)
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=1000000");
        accumulator.Add(speedLine);

        Assert.Equal(0, accumulator.Add("progress=continue")!.Speed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-equals-sign")]
    [InlineData("out_time_us=garbage")]
    public void Add_IgnoresJunkLines(string line)
    {
        var accumulator = new FfmpegProgressAccumulator();
        Assert.Null(accumulator.Add(line));
        Assert.Equal(TimeSpan.Zero, accumulator.Add("progress=continue")!.Position);
    }

    [Theory]
    [InlineData("-1")]                     // ffmpeg's early negative sentinel
    [InlineData("-8775277097")]
    [InlineData("99999999999999999999")]   // wider than long
    public void Add_KeepsLastPosition_WhenOutTimeIsUnusable(string outTime)
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_us=2000000");
        accumulator.Add($"out_time_us={outTime}");

        Assert.Equal(TimeSpan.FromSeconds(2), accumulator.Add("progress=continue")!.Position);
    }

    [Fact]
    public void Add_TreatsOutTimeMsAsMicroseconds()
    {
        // Despite the name, ffmpeg reports out_time_ms in microseconds.
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("out_time_ms=1500000");

        Assert.Equal(TimeSpan.FromSeconds(1.5), accumulator.Add("progress=continue")!.Position);
    }

    [Fact]
    public void Add_ParsesScientificNotationSpeed()
    {
        var accumulator = new FfmpegProgressAccumulator();
        accumulator.Add("speed=1.0e+02x");

        Assert.Equal(100.0, accumulator.Add("progress=continue")!.Speed);
    }
}
