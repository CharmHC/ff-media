using FFMedia.Media;

namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>The standardization target every clip is conformed to before concatenation.</summary>
public sealed record MergeTarget(
    int Width,
    int Height,
    FrameRate FrameRate,
    MergeVideoCodec VideoCodec,
    int Crf,
    MergeContainer Container,
    MergeAudioCodec AudioCodec,
    int AudioSampleRate,
    int AudioChannels,
    FitMode FitMode)
{
    /// <summary>Bits per pixel per frame, a rough x264 CRF-20 constant used only for size estimation.</summary>
    private const double BitsPerPixel = 0.08;

    private const long AudioBitsPerSecond = 192_000;

    public static MergeTarget Default { get; } = new(
        1920, 1080, new FrameRate(30, 1), MergeVideoCodec.H264, 20,
        MergeContainer.Mp4, MergeAudioCodec.Aac, 48_000, 2, FitMode.Fit);

    public long PixelCount => (long)Width * Height;

    /// <summary>Heuristic output bitrate, used to size temp files and the disk-space guard.
    /// H.265 is assumed ~35 % more efficient than H.264 at the same perceived quality.</summary>
    public long EstimatedBitsPerSecond
    {
        get
        {
            var videoBits = PixelCount * FrameRate.Value * BitsPerPixel;
            if (VideoCodec == MergeVideoCodec.H265)
            {
                videoBits *= 0.65;
            }

            return (long)videoBits + AudioBitsPerSecond;
        }
    }

    /// <summary>Forces this target inside <paramref name="bounds"/>.
    ///
    /// <para>ONE rule, applied to every source-bounded field: <b>take the largest allowed value ≤ the
    /// current one; if none qualifies, take the smallest.</b> That single rule covers both cases the
    /// UI can produce — the ceiling dropped beneath the user's choice (they deleted the only 1080p
    /// clip), and the ladder shifted so their exact choice is no longer on it (the source aspect
    /// changed). It never snaps UP: that would silently reintroduce the upscaling this whole design
    /// exists to forbid.</para>
    ///
    /// <para>Codec, container, CRF and FitMode are untouched — nothing about the source clips makes
    /// H.265 or CRF 28 an invalid choice.</para></summary>
    public MergeTarget ClampTo(TargetBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        if (bounds.Resolutions.Count == 0)
        {
            return this; // no clips: nothing to bound against, and a zeroed target helps no one
        }

        var resolution = LargestNotExceeding(
            bounds.Resolutions, r => r.PixelCount, (long)Width * Height);
        var frameRate = LargestNotExceeding(
            bounds.FrameRates, r => r.Value, FrameRate.Value);
        var sampleRate = LargestNotExceeding(bounds.SampleRates, r => r, AudioSampleRate);
        var channels = LargestNotExceeding(bounds.ChannelCounts, c => c, AudioChannels);

        return this with
        {
            Width = resolution.Width,
            Height = resolution.Height,
            FrameRate = frameRate,
            AudioSampleRate = sampleRate,
            AudioChannels = channels,
        };
    }

    /// <summary>The allowed value with the greatest weight not exceeding <paramref name="current"/>,
    /// or — when every allowed value is larger — the smallest one. Never returns null: the caller has
    /// already established the list is non-empty.</summary>
    private static T LargestNotExceeding<T, TWeight>(
        IReadOnlyList<T> allowed, Func<T, TWeight> weight, TWeight current)
        where TWeight : IComparable<TWeight>
    {
        var eligible = allowed.Where(a => weight(a).CompareTo(current) <= 0).ToList();
        return eligible.Count > 0 ? eligible.MaxBy(weight)! : allowed.MinBy(weight)!;
    }
}
