using System;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;
using CoreTrimParsing = FFMedia.Core.Media.TrimParsing;

namespace FFMedia.Tests.YouTubeDownloader;

public class TrimParsingTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("01:02:03", 3723)]
    [InlineData("0", 0)]
    public void TryParse_ParsesSecondsAndClockFormats(string text, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CoreTrimParsing.TryParse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1:70")]      // minutes/seconds field out of range
    [InlineData("-5")]
    [InlineData("70:00")]     // minutes field out of range (clock format)
    [InlineData("-1:30")]     // negative clock
    [InlineData("1000000000000000000000")]  // seconds magnitude overflows TimeSpan
    [InlineData("999999999:00:00")]          // hours magnitude overflows TimeSpan
    public void TryParse_BlankOrInvalid_ReturnsNull(string? text)
    {
        Assert.Null(CoreTrimParsing.TryParse(text));
    }

    [Fact]
    public void TryParse_FractionalSeconds_ParsesWithInvariantCulture()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), CoreTrimParsing.TryParse("1.5"));
    }

    [Fact]
    public void ParseRange_ValidPair_ReturnsRange()
    {
        var r = TrimParsing.ParseRange("0:05", "0:10");
        Assert.NotNull(r);
        Assert.Equal(TimeSpan.FromSeconds(5), r!.Start);
        Assert.Equal(TimeSpan.FromSeconds(10), r.End);
    }

    [Theory]
    [InlineData("0:10", "0:05")]   // end <= start
    [InlineData("0:05", "0:05")]   // end == start
    [InlineData("", "0:10")]        // one side blank
    [InlineData("x", "0:10")]       // one side invalid
    public void ParseRange_InvalidOrIncomplete_ReturnsNull(string start, string end)
    {
        Assert.Null(TrimParsing.ParseRange(start, end));
    }

    [Theory]
    [InlineData("1:23.45", 83.45)]
    [InlineData("0:05.5", 5.5)]
    [InlineData("1:02:03.25", 3723.25)]
    [InlineData("0:00.1", 0.1)]
    public void TryParse_AcceptsAFractionalSecondInTheColonForm(string text, double expectedSeconds)
    {
        // THE WHOLE POINT OF M9. A capture button reads the player's position -- 1:23.45 -- and writes
        // it here. Before this, the colon form parsed each part with int.TryParse, so this returned
        // NULL: the range went invalid and Create greyed out. The feature was broken on arrival.
        var parsed = CoreTrimParsing.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Equal(expectedSeconds, parsed!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("1:60.5")]   // 60 seconds is not a second
    [InlineData("1:-3.5")]
    [InlineData("1:2:3:4.5")]
    [InlineData("1:aa.5")]
    public void TryParse_StillRejectsNonsense(string text)
        => Assert.Null(CoreTrimParsing.TryParse(text));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(83, "1:23")]
    [InlineData(83.45, "1:23.45")]
    [InlineData(3723.25, "1:02:03.25")]
    [InlineData(0.5, "0.5")]
    public void Format_RendersATimestampAHumanRecognises(double seconds, string expected)
        => Assert.Equal(expected, CoreTrimParsing.Format(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0)]
    [InlineData(0.25)]
    [InlineData(7.125)]
    [InlineData(83.45)]
    [InlineData(3723.25)]
    [InlineData(5999.999)]
    public void Format_RoundTripsThroughTryParse_ToTheSameInstant(double seconds)
    {
        // THE INVARIANT. Capture formats a position into the box; the tool parses it straight back out
        // to build the request. If those two disagreed by even a little, the GIF would be cut somewhere
        // other than where the user saw -- silently.
        var original = TimeSpan.FromSeconds(seconds);

        var round = CoreTrimParsing.TryParse(CoreTrimParsing.Format(original));

        Assert.NotNull(round);
        Assert.Equal(original.TotalMilliseconds, round!.Value.TotalMilliseconds, 0);
    }
}
