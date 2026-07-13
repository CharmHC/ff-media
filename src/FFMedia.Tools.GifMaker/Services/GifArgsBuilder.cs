using System.Globalization;
using FFMedia.Media;
using FFMedia.Tools.GifMaker.Models;

namespace FFMedia.Tools.GifMaker.Services;

/// <summary>Builds ffmpeg's two passes. Pure — no I/O, no process.
///
/// <para><b>Why two passes.</b> The obvious <c>ffmpeg -i in.mp4 out.gif</c> quantizes to a GENERIC
/// 256-colour palette and produces visibly banded, dirty output. <c>palettegen</c> analyses the clip and
/// builds a palette from ITS OWN colours; <c>paletteuse</c> then applies it. Verified against the
/// bundled ffmpeg 8.1: the good route costs ~3x of a fraction of a second (0.47 s vs 0.15 s on a 3 s
/// clip), so there is no reason to offer the bad one.</para>
///
/// <para><b>The filter chain must be IDENTICAL in both passes</b> (same fps, same scale). The palette is
/// generated from the frames it will be applied to; if the two disagreed, the palette would be optimal
/// for an image that is never rendered.</para></summary>
public static class GifArgsBuilder
{
    /// <summary>ffmpeg's own default is <c>sierra2_4a</c>; <c>diff_mode=rectangle</c> limits re-dithering
    /// to the parts of the frame that actually changed, which keeps static backgrounds from shimmering
    /// (and compresses far better).</summary>
    private const string PaletteUseOptions = "paletteuse=diff_mode=rectangle";

    /// <summary><c>stats_mode=diff</c> weights the palette toward what MOVES, rather than letting a large
    /// static background dominate the 256 colours.</summary>
    private const string PaletteGenOptions = "palettegen=stats_mode=diff";

    /// <summary>Pass 1 — analyse the clip and write an optimal 256-colour palette.</summary>
    public static IReadOnlyList<string> PalettePass(GifRequest request, string palettePath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(palettePath);

        return
        [
            // -ss/-to BEFORE -i: seeks instead of decoding the whole file and discarding most of it.
            // -to is ABSOLUTE (a position on the source timeline), not a duration from the seek point --
            // verified against ffmpeg 8.1 -- so both go through unmodified.
            "-ss", Seconds(request.Start),
            "-to", Seconds(request.End),
            "-i", request.SourcePath,
            "-vf", $"{Chain(request)},{PaletteGenOptions}",
            palettePath,
        ];
    }

    /// <summary>Pass 2 — render the GIF through that palette.</summary>
    /// <param name="outputPath">Where ffmpeg actually writes. NOT necessarily
    /// <c>request.OutputPath</c> — <c>GifService</c> renders to a temporary sibling and only moves it
    /// onto the real destination once it has been verified, so a failed or cancelled render never
    /// costs the user the GIF that was already sitting there (final whole-branch review, FIX 1).</param>
    public static IReadOnlyList<string> RenderPass(GifRequest request, string palettePath, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(palettePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return
        [
            "-ss", Seconds(request.Start),
            "-to", Seconds(request.End),
            "-i", request.SourcePath,
            "-i", palettePath,
            "-lavfi", $"{Chain(request)}[x];[x][1:v]{PaletteUseOptions}",
            "-loop", "0", // 0 = loop forever, which is what a GIF is for
            outputPath,
        ];
    }

    /// <summary>The scale/rate chain. IDENTICAL in both passes, by construction rather than by
    /// coincidence — see the type's remarks.
    ///
    /// <para><c>-2</c> rather than <c>-1</c>: derive the height from the source aspect AND force it
    /// even.</para>
    ///
    /// <para><b>The frame rate is the exact rational</b> (<see cref="FrameRate.ToFfmpegString"/>), not
    /// <see cref="FrameRate.Value"/> formatted to three decimals. The commonest source rate of all —
    /// 30000/1001, and the DEFAULT selection besides, since the source heads <c>GifBounds</c>' own
    /// list — rounds to "29.97" under three decimals, silently retiming every GIF made from it. The
    /// sibling <c>NormalizeArgsBuilder</c> already uses the rational for the same reason.</para></summary>
    private static string Chain(GifRequest request)
        => $"fps={request.Fps.ToFfmpegString()},scale={request.Size.Width}:-2:flags=lanczos";

    private static string Seconds(TimeSpan value)
        => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
