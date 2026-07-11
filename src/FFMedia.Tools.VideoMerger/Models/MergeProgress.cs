namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>A weighted snapshot of merge progress. When anything needs re-encoding the normalize
/// phase owns 95 % of the bar and the stream-copy concat the last 5 %; on the fast path there is
/// nothing to encode, so the concat owns the whole bar.</summary>
/// <param name="OverallPercent">0–100, and never decreasing across a single merge — a bar that
/// retreats when a fast clip finishes ahead of a slow one reads as a bug to the user.</param>
/// <param name="ClipPercents">0–100 per clip, in <c>MergeRequest.Clips</c> order, so the UI can give
/// each row its own bar. A clip that already conforms is <c>100</c> from the first report: it needs
/// no encoding, and showing it as pending work would be a lie.</param>
public sealed record MergeProgress(
    MergeJobStatus Status,
    double OverallPercent,
    string? CurrentClip,
    IReadOnlyList<double> ClipPercents);
