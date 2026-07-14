using System;
using System.Globalization;
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
    [InlineData(1.9996, "0:02")]      // was "0:011" -- a two-second capture rendered as ELEVEN
    [InlineData(30.9996, "0:31")]     // was "0:301" -- unparseable
    [InlineData(59.9996, "1:00")]     // was "0:591" -- the carry has to reach the MINUTES, not just the seconds
    [InlineData(119.9997, "2:00")]    // was "1:591"
    [InlineData(3599.9999, "1:00:00")] // was "59:591" -- the carry has to reach the HOURS
    [InlineData(0.9996, "0:01")]      // was "1" -- the sub-second branch has to give way to the m\:ss one
    public void Format_RoundsAtTheCarryBoundary_RatherThanGluingTheCarryOntoTheSeconds(double seconds, string expected)
    {
        // The round-trip theory below cannot catch all of these on its own: 0.9996 formatted as the bare
        // "1", which TryParse happens to read back as one second, so the round trip was ACCIDENTALLY
        // intact while the string on screen was wrong. The rendered TEXT is what the user reads and what
        // lands in the Start/End box, so the text itself has to be pinned.
        Assert.Equal(expected, CoreTrimParsing.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.25)]
    [InlineData(7.125)]
    [InlineData(83.45)]
    [InlineData(3723.25)]
    [InlineData(5999.999)]
    [InlineData(90000)]        // 25h exactly -- TimeSpan's "h" specifier is hours-OF-DAY (0-23), not
                                // total hours, so a naive Format(25h) emits "1:00:00" and loses a day.
    [InlineData(91845.5)]      // 25:30:45.5 -- a 24h+ VOD trimmed at a fractional second.
    // ---- the CARRY BOUNDARY. Every case above stops SHORT of it, which is exactly why the bug below
    // survived a green suite. The whole part was built by TRUNCATION (span.Seconds / the m\:ss
    // specifier) while the fraction was rendered with ".###", which ROUNDS -- so any fraction >= 0.9995
    // rounded up to a bare "1" that was then GLUED ONTO the seconds digits, and the carry never reached
    // them: 1.9996s formatted as "0:011", which TryParse reads back as ELEVEN SECONDS. A user pausing at
    // 1.9996s and clicking Set Start got a GIF cut from 11s, silently, with CanCreate still true. The
    // other cases below format as "0:301"/"0:591"/"1:591"/"59:591" -- unparseable, so a probed source
    // duration landing in that band writes garbage into EndText on LOAD and greys Create out on a
    // perfectly good video. Both values M9 newly feeds through Format -- the player's live position and
    // ffprobe's probed duration -- are ARBITRARY machine-produced sub-second numbers, so this band is not
    // an edge case: it is 1-in-2500 of every capture.
    [InlineData(1.9996)]
    [InlineData(30.9996)]
    [InlineData(59.9996)]
    [InlineData(119.9997)]
    [InlineData(3599.9999)]
    [InlineData(0.9996)]       // the SUB-SECOND branch's own carry: "0.###" emits "1", which parses as
                                // one second by luck rather than by design. It must round to "0:01".
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

    [Fact]
    public void Format_OnAGermanLocale_StillEmitsAndParsesInvariantly()
    {
        // The InvariantCulture arguments throughout Format/TryParse are pinned by nothing unless a test
        // actually runs under a comma-decimal culture. On en-US CI, dropping one of those arguments
        // passes every test in this file and only breaks on a real European user's machine, where
        // Format would emit "1:23,45" and TryParse would reject its own output -- the round-trip
        // invariant silently broken in production for a subset of users.
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        var german = new CultureInfo("de-DE");
        try
        {
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            var span = TimeSpan.FromSeconds(3723.25);
            var formatted = CoreTrimParsing.Format(span);

            Assert.Equal("1:02:03.25", formatted);
            Assert.DoesNotContain(',', formatted);

            var parsed = CoreTrimParsing.TryParse(formatted);
            Assert.NotNull(parsed);
            Assert.Equal(span.TotalMilliseconds, parsed!.Value.TotalMilliseconds, 0);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }
}
