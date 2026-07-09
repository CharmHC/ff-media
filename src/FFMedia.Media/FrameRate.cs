using System.Globalization;

namespace FFMedia.Media;

/// <summary>An exact frame rate as ffmpeg reports it (e.g. 30000/1001 for 29.97 fps).</summary>
public readonly record struct FrameRate(int Numerator, int Denominator)
{
    /// <summary>Frames per second as a decimal. Zero when the denominator is zero.</summary>
    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>The exact rational, suitable for ffmpeg's <c>fps=</c> filter.</summary>
    public string ToFfmpegString() => $"{Numerator}/{Denominator}";

    /// <summary>Parses ffmpeg's "num/den" rational. Rejects zero numerator or denominator.</summary>
    public static bool TryParse(string? text, out FrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
            || num <= 0 || den <= 0)
        {
            return false;
        }

        rate = new FrameRate(num, den);
        return true;
    }
}
