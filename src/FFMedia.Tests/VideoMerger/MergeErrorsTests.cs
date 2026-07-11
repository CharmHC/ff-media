using FFMedia.Tools.VideoMerger.Services;
using Xunit;

namespace FFMedia.Tests.VideoMerger;

/// <summary>The fixtures below are <em>real</em> stderr tails, captured from the bundled
/// ffmpeg (n8.1.2) by actually provoking each failure, then formatted exactly as
/// <c>FfmpegRunner</c> formats them: <c>"ffmpeg failed (exit N):\n"</c> + the last 10 stderr
/// lines, trimmed and newline-joined. Inventing the fixtures would let the matcher pass the
/// tests while missing everything ffmpeg actually prints.</summary>
public class MergeErrorsTests
{
    private const string DiskFull =
        "The disk filled up during the merge. Free some space and try again.";

    private const string CorruptClip =
        "One of the clips is corrupt or is not a video file FFMedia can read.";

    private const string OutputUnwritable =
        "FFMedia could not write the output file. Check the folder's permissions, or pick another folder.";

    private const string ClipUnreadable =
        "FFMedia could not read one of the clips. Check the file's permissions, or remove it from the list.";

    private const string OutputFolderMissing =
        "The output folder could not be found. Pick another folder.";

    private const string ClipMissing =
        "One of the clips could not be found. It may have been moved or deleted since you added it.";

    private const string UnknownEncoder =
        "This ffmpeg build cannot encode the chosen video codec. Pick H.264, or re-run build/fetch-binaries.ps1.";

    [Fact]
    public void Describe_MapsADiskThatFilledUpMidWrite()
    {
        // ffmpeg surfaces ENOSPC through the muxer's write call.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[out#0/mp4 @ 000001f77e62e8c0] Error submitting a packet to the muxer: No space left on device\n"
            + "av_interleaved_write_frame(): No space left on device\n"
            + "Error writing trailer of C:\\Videos\\merged.mp4: No space left on device";

        Assert.Equal(DiskFull, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_MapsACorruptClip()
    {
        // Verified: ffmpeg -i <20 KB of /dev/urandom named .mp4>.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[in#0 @ 0000020e40342f40] moov atom not found\n"
            + "[in#0 @ 0000020e40317040] Error opening input: Invalid data found when processing input\n"
            + "Error opening input file C:\\Videos\\clip2.mp4.\n"
            + "Error opening input files: Invalid data found when processing input";

        Assert.Equal(CorruptClip, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_MapsAnUnwritableOutput()
    {
        // Verified: ffmpeg ... -c copy C:/Windows/System32/out.mp4.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[out#0/mp4 @ 000002c18e350d40] Error opening output C:\\Videos\\merged.mp4: Permission denied\n"
            + "Error opening output file C:\\Videos\\merged.mp4.\n"
            + "Error opening output files: Permission denied";

        Assert.Equal(OutputUnwritable, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_DoesNotBlameTheOutput_WhenItIsAClipThatCannotBeRead()
    {
        // Real ffmpeg prints "Permission denied" for an unreadable *input* too (verified by denying
        // read on a clip). Matching the bare phrase would tell the user to fix their output folder's
        // permissions while the actual culprit is a clip — a confidently wrong message is worse than
        // the raw text.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[in#0 @ 0000018c832a0440] Error opening input: Permission denied\n"
            + "Error opening input file C:\\Videos\\clip1.mp4.\n"
            + "Error opening input files: Permission denied";

        Assert.Equal(ClipUnreadable, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_MapsAnOutputFolderThatNoLongerExists()
    {
        // Verified: ffmpeg ... -c copy nodir/out.mp4.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[out#0/mp4 @ 0000027b07433d80] Error opening output C:\\Gone\\merged.mp4: No such file or directory\n"
            + "Error opening output file C:\\Gone\\merged.mp4.\n"
            + "Error opening output files: No such file or directory";

        Assert.Equal(OutputFolderMissing, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_MapsAClipThatWasMovedOrDeleted()
    {
        // Verified: ffmpeg -i <path that does not exist>. Same errno as the case above, opposite side.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[in#0 @ 000002a57e501100] Error opening input: No such file or directory\n"
            + "Error opening input file C:\\Videos\\clip3.mp4.\n"
            + "Error opening input files: No such file or directory";

        Assert.Equal(ClipMissing, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_MapsAnEncoderThisBuildDoesNotHave()
    {
        // Verified: ffmpeg ... -c:v libbogus (an LGPL build would say this for libx264/libx265).
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[vost#0:0 @ 0000027027f7eac0] Unknown encoder 'libx265'\n"
            + "[vost#0:0 @ 0000027027f7eac0] Error selecting an encoder\n"
            + "Error opening output file C:\\Videos\\merged.mp4.\n"
            + "Error opening output files: Encoder not found";

        Assert.Equal(UnknownEncoder, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_DoesNotBlameTheOutputPath_ForAnEncoderThisBuildDoesNotHave()
    {
        // The unknown-encoder tail also contains "Error opening output file ..." — a matcher that
        // keyed on that alone would misfile it as a permissions problem. This pins the precedence.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[vost#0:0 @ 0000027027f7eac0] Unknown encoder 'libx264'\n"
            + "Error opening output file C:\\Videos\\merged.mp4.\n"
            + "Error opening output files: Encoder not found";

        Assert.NotEqual(OutputUnwritable, MergeErrors.Describe(raw));
        Assert.Equal(UnknownEncoder, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_PassesAnUnrecognizedFailureThrough_RatherThanSwallowingIt()
    {
        // A message we cannot improve on must survive verbatim: a generic "merge failed" would
        // destroy the only diagnostic the user (or a bug report) has.
        const string raw = "ffmpeg failed (exit 1):\n"
            + "[out#0 @ 000001c7950f0700] Error initializing the muxer for C:\\Videos\\merged.mp4: Invalid argument\n"
            + "Error opening output file C:\\Videos\\merged.mp4.\n"
            + "Error opening output files: Invalid argument";

        Assert.Equal(raw, MergeErrors.Describe(raw));
    }

    [Fact]
    public void Describe_PassesTheRunnersOwnFailuresThrough_Verbatim()
    {
        // FfmpegRunner's non-ffmpeg failure paths are already user-facing; re-describing them would
        // only lose the instruction they carry.
        const string missing = "ffmpeg.exe is missing. Run build/fetch-binaries.ps1.";

        Assert.Equal(missing, MergeErrors.Describe(missing));
    }

    [Fact]
    public void Describe_PreservesTheRawTextExactly_IncludingItsWhitespace()
    {
        // No trimming, no normalizing: the pass-through is byte-for-byte what ffmpeg said.
        const string raw = "  ffmpeg failed (exit 8):\n  weird\r\n\tindented tail  ";

        Assert.Equal(raw, MergeErrors.Describe(raw));
    }

    [Theory]
    [InlineData("NO SPACE LEFT ON DEVICE")]
    [InlineData("no space left on device")]
    public void Describe_IsCaseInsensitive(string raw)
    {
        Assert.Equal(DiskFull, MergeErrors.Describe(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    [InlineData(null)]
    public void Describe_HandlesAnEmptyError(string? raw)
    {
        // null is not hypothetical: Result.Error is string?, so the ViewModel hands us exactly that.
        Assert.Equal("The merge failed, but ffmpeg reported no reason.", MergeErrors.Describe(raw));
    }
}
