using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure derivation of a sensible <see cref="MergeTarget"/> from the probed clips:
/// largest frame area, highest frame rate (snapped to a standard rate), majority codec/container
/// (ties deterministically default to H.264/MP4), and the maximum audio sample rate and channel
/// count.</summary>
public static class MergeTargetDerivation
{
    /// <summary>Standard broadcast/web rates a near-miss measured rate snaps to. Drop-frame rates
    /// (24000/1001, 30000/1001, 60000/1001) sit alongside their integer look-alikes (24, 30, 60)
    /// because both can legitimately be the closest match — <see cref="Snap"/> picks whichever
    /// candidate is numerically closest, so list order here is not significant.</summary>
    public static IReadOnlyList<FrameRate> StandardRates { get; } =
    [
        new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001),
        new(30, 1), new(50, 1), new(60000, 1001), new(60, 1),
    ];

    /// <summary>Relative tolerance for snapping (0.5 %). 2997/100 → 30000/1001; 12/1 stays 12/1.</summary>
    private const double SnapTolerance = 0.005;

    /// <summary>Derives a standardization target from the probed clips. Clips without a video
    /// stream (audio-only) do not participate in the video-side derivation but still count
    /// towards the audio spec and container vote.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="clips"/> is empty, or none of its clips has a video stream — there is
    /// nothing to derive a video target from.
    /// </exception>
    public static MergeTarget Derive(IReadOnlyList<MediaInfo> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var videos = clips.Where(c => c.Video is not null).Select(c => c.Video!).ToList();
        if (videos.Count == 0)
        {
            throw new ArgumentException("At least one clip must have a video stream.", nameof(clips));
        }

        var largest = videos.MaxBy(v => (long)v.Width * v.Height)!;
        var fastest = videos.MaxBy(v => v.FrameRate.Value)!.FrameRate;

        var audios = clips.Where(c => c.Audio is not null).Select(c => c.Audio!).ToList();
        var sampleRate = audios.Count == 0 ? 48_000 : audios.Max(a => a.SampleRate);
        var channels = audios.Count == 0 ? 2 : audios.Max(a => a.Channels);

        // Strict majority required; a tie (or no clear majority) deterministically keeps the
        // default rather than depending on enumeration/insertion order.
        var hevcCount = videos.Count(v => v.CodecName is "hevc" or "h265");
        var codec = hevcCount * 2 > videos.Count ? MergeVideoCodec.H265 : MergeVideoCodec.H264;

        var matroskaCount = clips.Count(c => c.ContainerFormat.Contains("matroska", StringComparison.OrdinalIgnoreCase));
        var container = matroskaCount * 2 > clips.Count ? MergeContainer.Mkv : MergeContainer.Mp4;

        return MergeTarget.Default with
        {
            Width = ToEven(largest.Width),
            Height = ToEven(largest.Height),
            FrameRate = Snap(fastest),
            VideoCodec = codec,
            Container = container,
            AudioCodec = MergeAudioCodec.Aac,
            AudioSampleRate = sampleRate,
            AudioChannels = channels,
        };
    }

    /// <summary>Rounds a frame dimension DOWN to an even number (floor of 2). The normalize phase
    /// encodes to yuv420p, whose 2x2 chroma subsampling requires both dimensions to be even —
    /// libx264 rejects an odd width or height outright ("width not divisible by 2"). Real-world
    /// clips are even, so an odd dimension only reaches us from an exotic or malformed file; taking
    /// the raw dimension would derive a target that could not be encoded at all. Every fit mode's
    /// filtergraph scales to whatever target it is given, so down-vs-up is free — round down, and a
    /// target derived from an odd source never exceeds the source frame it came from.</summary>
    /// <remarks>Masks the low bit rather than using <c>Math.Abs</c>, which throws on
    /// <see cref="int.MinValue"/>. Nonsense dimensions clamp to 2; they never throw.</remarks>
    private static int ToEven(int dimension) => Math.Max(2, dimension - (dimension & 1));

    /// <summary>Snaps to the closest standard rate within <see cref="SnapTolerance"/>, or keeps the
    /// exact measured rate if none is close enough. Deliberately picks the closest candidate rather
    /// than the first one found within tolerance: an exact 60 fps measurement is within 0.5% of
    /// both 60000/1001 (59.94) and 60/1, and only comparing against the closest avoids incorrectly
    /// snapping an exact integer rate down to its drop-frame neighbour.</summary>
    private static FrameRate Snap(FrameRate measured)
    {
        FrameRate? closest = null;
        var closestRelativeDiff = double.MaxValue;

        foreach (var standard in StandardRates)
        {
            var relativeDiff = Math.Abs(measured.Value - standard.Value) / standard.Value;
            if (relativeDiff <= SnapTolerance && relativeDiff < closestRelativeDiff)
            {
                closestRelativeDiff = relativeDiff;
                closest = standard;
            }
        }

        return closest ?? measured;
    }
}
