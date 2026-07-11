namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>What the user sees before committing to a merge. <see cref="OutputDuration"/> is exact;
/// the ETA is a range, and is replaced by ffmpeg's real figure once merging starts.</summary>
/// <param name="OutputDuration">Sum of the clip durations — exact, since nothing overlaps.</param>
/// <param name="LowEta">Optimistic end of the estimated merge time.</param>
/// <param name="HighEta">Pessimistic end of the estimated merge time.</param>
/// <param name="TempBytesEstimate">Temp disk the normalize phase needs for its intermediates.
/// Counts ONLY the re-encoded clips — conforming ones are concatenated from where they already
/// sit — and excludes the merged output file, so it is legitimately <c>0</c> on the fast path.
/// A caller sizing a disk-space check must therefore add the expected output size; reserving this
/// figure alone would wave a two-hour all-conforming merge onto a full disk.</param>
/// <param name="ReencodeCount">How many clips do not conform and must be normalized first.</param>
/// <param name="IsFastPath">No clip needs re-encoding: the merge is a stream copy of ~1s.</param>
public sealed record MergeEstimate(
    TimeSpan OutputDuration,
    TimeSpan LowEta,
    TimeSpan HighEta,
    long TempBytesEstimate,
    int ReencodeCount,
    bool IsFastPath);
