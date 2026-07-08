using System.Globalization;

namespace FFMedia.Core.Binaries;

/// <summary>Compares yt-dlp version tags. yt-dlp uses dot-separated date-based tags
/// (e.g. <c>2026.07.04</c>, occasionally with a same-day suffix like <c>2026.07.04.1</c>).</summary>
public static class YtDlpVersion
{
    /// <summary>True when <paramref name="latest"/> is strictly newer than
    /// <paramref name="installed"/>. If the installed version is unknown, any non-empty
    /// <paramref name="latest"/> counts as newer (surface it). If either tag can't be parsed
    /// as dot-separated integers, falls back to case-insensitive inequality so an unexpected
    /// tag format still surfaces rather than being silently swallowed.</summary>
    public static bool IsNewer(string? latest, string? installed)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(installed))
        {
            return true;
        }

        if (TryParse(latest, out var l) && TryParse(installed, out var i))
        {
            return Compare(l, i) > 0;
        }

        return !string.Equals(latest.Trim(), installed.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParse(string version, out int[] parts)
    {
        var segments = version.Trim().Split('.');
        parts = new int[segments.Length];
        for (var k = 0; k < segments.Length; k++)
        {
            if (!int.TryParse(segments[k], NumberStyles.None, CultureInfo.InvariantCulture, out parts[k]))
            {
                parts = [];
                return false;
            }
        }

        return parts.Length > 0;
    }

    private static int Compare(int[] a, int[] b)
    {
        var n = Math.Max(a.Length, b.Length);
        for (var k = 0; k < n; k++)
        {
            var av = k < a.Length ? a[k] : 0;
            var bv = k < b.Length ? b[k] : 0;
            if (av != bv)
            {
                return av.CompareTo(bv);
            }
        }

        return 0;
    }
}
