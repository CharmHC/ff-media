namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>Whether a clip already matches the merge target, and if not, why. A conforming clip
/// is concatenated as-is (no re-encode) — this drives the fast path, the UI badge, and the ETA.</summary>
/// <remarks><see cref="IsConforming"/> is DERIVED from <see cref="Mismatches"/> rather than stored
/// alongside it. The two cannot then drift apart, and the drift that matters is the silent one: a
/// clip reported as conforming while holding a mismatch gets stream-copied into the concat and
/// corrupts the output.</remarks>
public sealed record Conformance(IReadOnlyList<string> Mismatches)
{
    public bool IsConforming => Mismatches.Count == 0;
}
