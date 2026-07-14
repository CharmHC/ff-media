using System.Globalization;
using System.Linq;

namespace FFMedia.Core.Media;

/// <summary>Parses user-entered trim timestamps ("HH:MM:SS", "MM:SS", or plain seconds).</summary>
public static class TrimParsing
{
    /// <summary>Parses a timestamp; returns null when blank or unparseable.</summary>
    public static TimeSpan? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            try
            {
                return TimeSpan.FromSeconds(seconds);
            }
            catch (OverflowException)
            {
                return null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        var parts = text.Split(':');
        if (parts.Length is 2 or 3)
        {
            // The last part carries the SECONDS and may be fractional (a captured frame is rarely on a
            // whole second). The leading parts are whole hours/minutes. Parsing the last part with
            // int.TryParse -- which is what this did before M9 -- is exactly what made "1:23.45"
            // unparseable, and a capture button unable to write its own result.
            var lead = parts[..^1];
            if (!lead.All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                return null;
            }

            if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
                || secs < 0 || secs >= 60)
            {
                return null;
            }

            var n = lead.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            var (h, m) = n.Length == 2 ? (n[0], n[1]) : (0, n[0]);
            if (h < 0 || m < 0 || m >= 60)
            {
                return null;
            }

            try
            {
                return TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(secs);
            }
            catch (OverflowException)
            {
                return null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Renders a timestamp the way a human writes one — and, critically, one that
    /// <see cref="TryParse"/> reads back as the SAME INSTANT.
    ///
    /// <para>The sub-second part is shown only when it is non-zero, so a hand-typed <c>1:23</c> still
    /// looks like <c>1:23</c> after a round trip, while a frame captured at <c>1:23.45</c> keeps its
    /// fraction. Truncating it — which is what the GIF Maker's own formatter used to do — silently loses
    /// up to a second, in a tool whose entire job is picking an exact moment.</para></summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        // Rounded to the DISPLAYED precision (a millisecond -- the ".###" below) BEFORE anything is
        // decomposed out of it, so a carry propagates into the seconds, the minutes and the hours the way
        // arithmetic requires.
        //
        // The bug this prevents: the whole part used to be built by TRUNCATION (span.Seconds, and the
        // m\:ss specifier) while the fraction was rendered with ".###", which ROUNDS. Any fraction >=
        // 0.9995 therefore rounded up to a bare "1" that was GLUED ONTO the truncated seconds -- the
        // carry never reached them. Format(1.9996s) emitted "0:011", which TryParse reads back as ELEVEN
        // SECONDS: a user who paused at 1.9996s and clicked Set Start got a GIF cut from 11s, silently,
        // with the range still valid and Create still enabled. Others in that band ("0:301", "0:591")
        // are unparseable outright, so a probed source duration landing there greys Create out on a
        // perfectly good freshly-loaded video. BOTH values M9 feeds through here -- the player's live
        // position and ffprobe's probed duration -- are arbitrary machine-produced sub-second numbers, so
        // the band is hit roughly one capture in 2500, not never.
        span = TimeSpan.FromMilliseconds(Math.Round(span.TotalMilliseconds));

        // Under a second there is no minute worth showing; "0.5" is what a human means. (Checked AFTER
        // the rounding: 0.9996s is one second, and belongs in the m\:ss branch below.)
        if (span > TimeSpan.Zero && span.TotalSeconds < 1)
        {
            return span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // NOTE: TimeSpan's custom "h" format specifier renders the hours-OF-DAY component (0-23), not
        // total hours across day boundaries -- so span.ToString(@"h\:mm\:ss") on a 25-hour span emits
        // "1:00:00", which TryParse reads back as ONE hour. Twenty-four hours vanish silently. Total
        // hours must be computed ourselves; span.Minutes/span.Seconds are safe as-is (they are already
        // the minute-of-hour / second-of-minute components regardless of how many days the span spans).
        var whole = span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" +
              span.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
              span.Seconds.ToString("00", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);

        var fraction = span.TotalSeconds - Math.Floor(span.TotalSeconds);

        return fraction < 0.0005
            ? whole
            : whole + fraction.ToString(".###", CultureInfo.InvariantCulture);
    }
}
