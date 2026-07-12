using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Services;

namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>What the user is ALLOWED to choose, given the clips they added.
///
/// <para>Derivation takes the maximum across the clips (largest frame, fastest rate, highest sample
/// rate, most channels) — a deliberate "never degrade a source" rule. Anything ABOVE that ceiling is
/// not merely suboptimal, it is pointless: 60 fps from 30 fps clips duplicates every frame, 4K from
/// 1080p invents pixels, 5.1 from stereo adds four silent channels. Bigger file, longer encode, no
/// new information. So those values are not offered at all.</para>
///
/// <para><b>The derived target is always the first entry of each list.</b> These lists are built from
/// the derivation's own maxima rather than recomputed, so the options the UI offers and the target
/// derivation picks cannot drift — the discipline <c>ConformanceCheck</c> already enforces for the
/// fast path.</para></summary>
public sealed record TargetBounds(
    IReadOnlyList<Resolution> Resolutions,
    IReadOnlyList<FrameRate> FrameRates,
    IReadOnlyList<int> SampleRates,
    IReadOnlyList<int> ChannelCounts)
{
    /// <summary>Standard heights the ladder steps down through, tallest first.</summary>
    private static readonly int[] StandardHeights = [2160, 1440, 1080, 900, 720, 540, 480, 360, 240];

    private static readonly int[] StandardSampleRates = [96_000, 48_000, 44_100, 32_000, 22_050];

    private static readonly int[] StandardChannelCounts = [8, 6, 2, 1];

    /// <summary>No clips, so no source to bound against — the page disables the Output section.</summary>
    public static TargetBounds Empty { get; } = new([], [], [], []);

    /// <param name="clips">The probed clips. Must contain at least one with a video stream — the same
    /// precondition <see cref="MergeTargetDerivation.Derive"/> enforces.</param>
    public static TargetBounds From(IReadOnlyList<MediaInfo> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        if (clips.Count == 0 || clips.All(c => c.Video is null))
        {
            return Empty;
        }

        // Ask DERIVATION for the ceiling; never recompute it here. This is the whole invariant.
        var derived = MergeTargetDerivation.Derive(clips);

        return new TargetBounds(
            ResolutionLadder(derived.Width, derived.Height),
            RatesUpTo(derived.FrameRate),
            [.. Descending(StandardSampleRates, derived.AudioSampleRate)],
            [.. Descending(StandardChannelCounts, derived.AudioChannels)]);
    }

    /// <summary>The source resolution, then standard heights below it — each scaled to the SOURCE's
    /// aspect ratio and rounded even. Stepping the height and deriving the width (rather than
    /// offering a fixed 16:9 table) is what keeps a 9:16 phone clip portrait instead of silently
    /// rotating it.</summary>
    private static List<Resolution> ResolutionLadder(int width, int height)
    {
        var ladder = new List<Resolution> { new(width, height) };
        var aspect = width / (double)height;

        foreach (var stepHeight in StandardHeights)
        {
            if (stepHeight >= height)
            {
                continue; // never offer a step at or above the source: that is the upscaling we exist to forbid
            }

            var stepWidth = ToEven((int)Math.Round(stepHeight * aspect));
            var step = new Resolution(stepWidth, ToEven(stepHeight));
            if (stepWidth >= 2 && !ladder.Contains(step))
            {
                ladder.Add(step);
            }
        }

        return ladder;
    }

    /// <summary>Every standard rate at or below <paramref name="fastest"/>, fastest first — with
    /// <paramref name="fastest"/> itself at the head. A 12 fps source snaps to NO standard rate and is
    /// slower than all of them, so filtering the standard list alone would leave the ComboBox empty.</summary>
    private static List<FrameRate> RatesUpTo(FrameRate fastest)
    {
        var rates = new List<FrameRate> { fastest };

        foreach (var rate in MergeTargetDerivation.StandardRates
                     .Where(r => r.Value < fastest.Value)
                     .OrderByDescending(r => r.Value))
        {
            rates.Add(rate);
        }

        return rates;
    }

    /// <summary><paramref name="ceiling"/> first, then every standard value strictly below it.
    /// The ceiling is included even when it is not a standard value (an oddball 37 kHz source).</summary>
    private static IEnumerable<int> Descending(int[] standards, int ceiling)
        => new[] { ceiling }.Concat(standards.Where(s => s < ceiling).OrderByDescending(s => s));

    /// <summary>Rounds DOWN to even: yuv420p's 2×2 chroma subsampling makes libx264 reject an odd
    /// width or height outright. Mirrors <c>MergeTargetDerivation.ToEven</c>.</summary>
    private static int ToEven(int dimension) => Math.Max(2, dimension - (dimension & 1));
}
