using FFMedia.Media;

namespace FFMedia.Tools.GifMaker.Models;

/// <summary>What the user is ALLOWED to choose, given the video they loaded.
///
/// <para>A GIF wider than its source, or at a higher frame rate, contains no more information: the
/// extra pixels are invented and the extra frames are duplicates. Bigger file, longer encode, nothing
/// gained. So those values are not offered at all — the same rule the merger's <c>TargetBounds</c>
/// enforces.</para>
///
/// <para><b>The source is always the first entry of each list</b>, which is also the default. The
/// defaults are therefore derived from the source rather than recomputed by the UI, and the two cannot
/// drift.</para></summary>
public sealed record GifBounds(
    IReadOnlyList<Resolution> Sizes,
    IReadOnlyList<FrameRate> FrameRates)
{
    /// <summary>Standard GIF widths, widest first.</summary>
    private static readonly int[] StandardWidths = [640, 480, 320, 240];

    private static readonly FrameRate[] StandardRates =
        [new(30, 1), new(24, 1), new(20, 1), new(15, 1), new(12, 1), new(10, 1)];

    /// <summary>No video loaded — the page disables its parameters.</summary>
    public static GifBounds Empty { get; } = new([], []);

    public static GifBounds From(MediaInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Video is not { } video)
        {
            return Empty;
        }

        return new GifBounds(SizeLadder(video), RatesUpTo(video.FrameRate));
    }

    /// <summary>The source size, then standard widths below it — each scaled to the SOURCE's aspect
    /// ratio and rounded even. Stepping the WIDTH and deriving the height (rather than offering a fixed
    /// 16:9 table) is what keeps a 9:16 phone clip portrait instead of silently rotating it.</summary>
    private static List<Resolution> SizeLadder(VideoStreamInfo video)
    {
        var width = ToEven(video.Width);
        var height = ToEven(video.Height);
        var ladder = new List<Resolution> { new(width, height) };
        var aspect = width / (double)height;

        foreach (var step in StandardWidths)
        {
            if (step >= width)
            {
                continue; // never offer a step at or above the source: that is the upscaling we forbid
            }

            // No `candidate.Height >= 2` guard here: ToEven already floors to Math.Max(2, ...), so the
            // height can never be less than 2 and the guard could never be false.
            var candidate = new Resolution(ToEven(step), ToEven((int)Math.Round(step / aspect)));
            if (!ladder.Contains(candidate))
            {
                ladder.Add(candidate);
            }
        }

        return ladder;
    }

    /// <summary>The source rate, then every standard rate strictly below it. The source's own rate heads
    /// the list even when it is non-standard (a 12 fps clip) — filtering the standard list alone would
    /// leave the ComboBox empty for a slow source.</summary>
    private static List<FrameRate> RatesUpTo(FrameRate source)
    {
        var rates = new List<FrameRate> { source };
        rates.AddRange(StandardRates
            .Where(r => r.Value < source.Value)
            .OrderByDescending(r => r.Value));

        return rates;
    }

    /// <summary>Rounds DOWN to even. GIF tolerates odd dimensions, but every other path in this app
    /// requires even (yuv420p chroma subsampling), and one uniform guarantee is cheaper than two rules.
    /// Masks the low bit rather than using Math.Abs, which throws on int.MinValue.</summary>
    private static int ToEven(int dimension) => Math.Max(2, dimension - (dimension & 1));
}
