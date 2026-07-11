namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Turns the ffmpeg stderr tail that <c>FfmpegRunner</c> captures into something a user can
/// act on. Same shape as the downloader's <c>YtDlpErrors</c>: a static, pure, per-module mapper.</summary>
/// <remarks>
/// <para>Two rules keep this honest.</para>
/// <para><b>Anything unrecognized passes through verbatim.</b> A generic "the merge failed" would
/// destroy the only diagnostic the user — or a bug report — has to go on.</para>
/// <para><b>Never guess which side failed.</b> ffmpeg reports the same errno text for an input and an
/// output ("Permission denied", "No such file or directory"), so a phrase match alone would happily
/// tell the user to fix their output folder's permissions when the real culprit is an unreadable
/// clip. Every errno-based message here is qualified by ffmpeg's own
/// <c>Error opening input…</c> / <c>Error opening output…</c> marker, and passes through when neither
/// is present. A confidently wrong message is worse than the raw text.</para>
/// </remarks>
public static class MergeErrors
{
    // ffmpeg's own markers for which side of the pipeline failed. Both the per-context line
    // ("Error opening output C:\out.mp4: Permission denied") and the summary line
    // ("Error opening output files: Permission denied") start this way, and the summary is the last
    // line of stderr, so it always survives FfmpegRunner's 10-line tail.
    private const string InputMarker = "Error opening input";
    private const string OutputMarker = "Error opening output";

    public static string Describe(string? ffmpegError)
    {
        if (string.IsNullOrWhiteSpace(ffmpegError))
        {
            return "The merge failed, but ffmpeg reported no reason.";
        }

        // Checked before the errno cases: the unknown-encoder tail also carries an
        // "Error opening output file …" line, which would otherwise misfile it as a path problem.
        if (Contains(ffmpegError, "unknown encoder"))
        {
            return "This ffmpeg build cannot encode the chosen video codec. "
                + "Pick H.264, or re-run build/fetch-binaries.ps1.";
        }

        // ENOSPC, surfaced through the muxer's write call ("av_interleaved_write_frame(): No space
        // left on device"). Unambiguous: only the output is being written.
        if (Contains(ffmpegError, "no space left"))
        {
            return "The disk filled up during the merge. Free some space and try again.";
        }

        // Only ever an input: a clip is truncated, is not a video, or is in a container ffmpeg cannot
        // demux ("moov atom not found" / "Invalid data found when processing input").
        if (Contains(ffmpegError, "invalid data found"))
        {
            return "One of the clips is corrupt or is not a video file FFMedia can read.";
        }

        if (Contains(ffmpegError, "permission denied"))
        {
            if (Contains(ffmpegError, InputMarker))
            {
                return "FFMedia could not read one of the clips. "
                    + "Check the file's permissions, or remove it from the list.";
            }

            if (Contains(ffmpegError, OutputMarker))
            {
                return "FFMedia could not write the output file. "
                    + "Check the folder's permissions, or pick another folder.";
            }
        }

        if (Contains(ffmpegError, "no such file or directory"))
        {
            if (Contains(ffmpegError, InputMarker))
            {
                return "One of the clips could not be found. "
                    + "It may have been moved or deleted since you added it.";
            }

            if (Contains(ffmpegError, OutputMarker))
            {
                return "The output folder could not be found. Pick another folder.";
            }
        }

        return ffmpegError;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
