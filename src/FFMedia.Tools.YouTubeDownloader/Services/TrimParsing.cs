using System.Globalization;
using System.Linq;
using FFMedia.Tools.YouTubeDownloader.Models;

namespace FFMedia.Tools.YouTubeDownloader.Services;

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
        if (parts.Length is 2 or 3 &&
            parts.All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            var n = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            var (h, m, s) = parts.Length == 3 ? (n[0], n[1], n[2]) : (0, n[0], n[1]);
            if (h >= 0 && m is >= 0 and < 60 && s is >= 0 and < 60)
            {
                try
                {
                    return new TimeSpan(h, m, s);
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
        }

        return null;
    }

    /// <summary>A <see cref="TrimRange"/> only when both parse and End &gt; Start; otherwise null.</summary>
    public static TrimRange? ParseRange(string? start, string? end)
    {
        return TryParse(start) is { } s && TryParse(end) is { } e && e > s
            ? new TrimRange(s, e)
            : null;
    }
}
