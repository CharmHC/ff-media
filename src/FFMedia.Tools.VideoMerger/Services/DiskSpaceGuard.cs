using System.Globalization;
using FFMedia.Core.Results;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure preflight check: is there room for the normalize phase's temp intermediates?
/// Kept free of <see cref="System.IO.DriveInfo"/> so it is testable without a real volume.</summary>
/// <remarks>This is the last thing between the user and a half-written merge on a full disk, so it
/// fails fast and its message says exactly how much is needed, how much is free, and how much to
/// free up — in units a human reads.</remarks>
public static class DiskSpaceGuard
{
    /// <summary>Headroom over the raw estimate — the bitrate heuristic can undershoot.</summary>
    public const double SafetyMargin = 1.2;

    public static Result Evaluate(long freeBytes, long requiredBytes)
    {
        // Negative inputs are nonsense from a broken estimate or a failed volume query: nothing to
        // reserve, and no space at all, respectively.
        var required = Math.Max(0L, requiredBytes);
        var free = Math.Max(0L, freeBytes);

        var needed = WithMargin(required);
        if (free >= needed)
        {
            return Result.Success();
        }

        return Result.Failure(
            $"Not enough disk space for the merge's temporary files: {Format(needed)} needed "
            + $"(estimate {Format(required)} + 20% margin), only {Format(free)} free. "
            + $"Free up {Format(needed - free)} and try again.");
    }

    /// <summary>Applies <see cref="SafetyMargin"/> in exact integer arithmetic (+1/5, rounded up),
    /// so the pass/fail boundary does not float on 1.2 being inexact in binary — a
    /// <c>(long)(bytes * 1.2)</c> under-applies the margin for most values. Saturates rather than
    /// overflowing on a huge estimate.</summary>
    private static long WithMargin(long bytes)
    {
        var extra = (bytes / 5) + (bytes % 5 == 0 ? 0 : 1);
        return bytes > long.MaxValue - extra ? long.MaxValue : bytes + extra;
    }

    private static string Format(long bytes)
    {
        const double Kb = 1024;
        const double Mb = Kb * 1024;
        const double Gb = Mb * 1024;
        const double Tb = Gb * 1024;

        return bytes switch
        {
            >= (long)Tb => Scaled(bytes / Tb, "TB"),
            >= (long)Gb => Scaled(bytes / Gb, "GB"),
            >= (long)Mb => Scaled(bytes / Mb, "MB"),
            >= (long)Kb => Scaled(bytes / Kb, "KB"),
            _ => $"{bytes.ToString(CultureInfo.InvariantCulture)} B",
        };

        // Invariant culture on purpose: these strings are asserted in tests and read by a user whose
        // Windows locale may use a comma as the decimal separator.
        static string Scaled(double value, string unit)
            => $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {unit}";
    }
}
