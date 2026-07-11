using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure pre-merge estimate. Only non-conforming clips cost encode time and temp disk;
/// conforming clips are referenced in place and stream-copied (spec §6.5).</summary>
/// <remarks>Every number here reaches the user's eyes, and the inputs come from ffprobe — i.e. from
/// files we did not write. A malformed probe (negative or absurd duration) and a pathological
/// persisted speed sample must therefore produce a silly-but-finite estimate, never an exception,
/// an overflowed negative byte count, or a NaN.</remarks>
public static class MergeEstimator
{
    /// <summary>Assumed stream-copy throughput of the concat pass, in bytes per second.</summary>
    private const double CopyBytesPerSecond = 200L * 1024 * 1024;

    /// <exception cref="ArgumentException"><paramref name="clips"/> is empty or contains a null entry.</exception>
    public static MergeEstimate Estimate(IReadOnlyList<MergeClip> clips, MergeTarget target, SpeedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);
        if (clips.Count == 0)
        {
            throw new ArgumentException("At least one clip is required.", nameof(clips));
        }

        long totalTicks = 0;
        double reencodeSeconds = 0;
        var reencodeCount = 0;

        foreach (var clip in clips)
        {
            if (clip is null)
            {
                throw new ArgumentException("Clips must not contain a null entry.", nameof(clips));
            }

            // A negative duration is meaningless: clamp it to zero rather than let it shorten the
            // merge, subtract from the temp-space requirement, or drive a negative ETA.
            var ticks = Math.Max(0L, clip.Info.Duration.Ticks);
            totalTicks = SaturatingAdd(totalTicks, ticks);

            // The estimator must agree with ConformanceCheck exactly — it is the same predicate the
            // merge itself uses to decide what to normalize, so any divergence would describe a
            // different plan than the one that runs.
            if (!ConformanceCheck.Evaluate(clip.Info, target).IsConforming)
            {
                reencodeCount++;
                reencodeSeconds += ticks / (double)TimeSpan.TicksPerSecond;
            }
        }

        var outputDuration = TimeSpan.FromTicks(totalTicks);

        // SpeedProfile.GetFactor is documented to return a positive, finite factor (a nonsense
        // persisted sample falls back to the seed), so this cannot divide by zero or yield NaN.
        // It can still be denormally small, which yields +infinity — ToDuration absorbs that.
        var encodeSeconds = reencodeCount == 0 ? 0 : reencodeSeconds / profile.GetFactor(target);

        var outputBytes = outputDuration.TotalSeconds * target.EstimatedBitsPerSecond / 8.0;
        var concatSeconds = outputBytes / CopyBytesPerSecond;

        var point = encodeSeconds + concatSeconds;
        var band = profile.BandFor(target);

        return new MergeEstimate(
            outputDuration,
            ToDuration(point * (1 - band)),
            ToDuration(point * (1 + band)),
            ToBytes(reencodeSeconds * target.EstimatedBitsPerSecond / 8.0),
            reencodeCount,
            reencodeCount == 0);
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>Seconds to a <see cref="TimeSpan"/> without ever throwing: infinite, NaN and
    /// out-of-range inputs saturate. Built from ticks rather than
    /// <see cref="TimeSpan.FromSeconds(double)"/>, which <em>throws</em> on infinity — reachable
    /// here from a denormally small persisted speed factor.</summary>
    private static TimeSpan ToDuration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var ticks = seconds * TimeSpan.TicksPerSecond;
        return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>Byte count, clamped to non-negative. Since .NET 7 a double→long cast saturates
    /// rather than wrapping, so the explicit bounds are belt-and-braces — but this number is what
    /// the disk-space guard reserves, and a negative one would wave a huge merge onto a full disk,
    /// so state the floor rather than lean on a runtime detail.</summary>
    private static long ToBytes(double bytes)
    {
        if (double.IsNaN(bytes) || bytes <= 0)
        {
            return 0;
        }

        return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
    }
}
