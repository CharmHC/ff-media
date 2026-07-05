using System;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using Xunit;

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
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TrimParsing.TryParse(text));
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
        Assert.Null(TrimParsing.TryParse(text));
    }

    [Fact]
    public void TryParse_FractionalSeconds_ParsesWithInvariantCulture()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), TrimParsing.TryParse("1.5"));
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
}
