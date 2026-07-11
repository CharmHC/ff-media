using FFMedia.Media;
using FFMedia.Tools.VideoMerger.Models;

namespace FFMedia.Tools.VideoMerger.Services;

/// <summary>Pure test of whether a probed clip can be stream-copied straight into the concat,
/// or must first be re-encoded to the target. A false "conforming" verdict corrupts the merge
/// (mismatched streams fed to ffmpeg's concat demuxer), so every check below is deliberately
/// exact — no near-enough tolerances.</summary>
public static class ConformanceCheck
{
    /// <summary>The pixel format every normalized clip is encoded to (broadest player support).</summary>
    public const string TargetPixelFormat = "yuv420p";

    public static Conformance Evaluate(MediaInfo clip, MergeTarget target)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(target);

        var mismatches = new List<string>();

        if (clip.Video is null)
        {
            mismatches.Add("no video track");
        }
        else
        {
            var video = clip.Video;
            if (video.Width != target.Width || video.Height != target.Height)
            {
                mismatches.Add($"resolution {video.Width}x{video.Height} != {target.Width}x{target.Height}");
            }

            if (!FrameRatesMatch(video.FrameRate, target.FrameRate))
            {
                mismatches.Add($"frame rate {video.FrameRate.Value:0.###} != {target.FrameRate.Value:0.###}");
            }

            if (!MatchesCodec(video.CodecName, target.VideoCodec))
            {
                mismatches.Add($"video codec {video.CodecName} != {CodecName(target.VideoCodec)}");
            }

            if (!string.Equals(video.PixelFormat, TargetPixelFormat, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"pixel format {video.PixelFormat} != {TargetPixelFormat}");
            }

            if (video.Rotation != 0)
            {
                mismatches.Add($"rotation {video.Rotation}° must be baked in");
            }
        }

        if (clip.Audio is null)
        {
            mismatches.Add("no audio track");
        }
        else
        {
            var audio = clip.Audio;
            if (!MatchesCodec(audio.CodecName, target.AudioCodec))
            {
                mismatches.Add($"audio codec {audio.CodecName} != {CodecName(target.AudioCodec)}");
            }

            if (audio.SampleRate != target.AudioSampleRate)
            {
                mismatches.Add($"sample rate {audio.SampleRate} != {target.AudioSampleRate}");
            }

            if (audio.Channels != target.AudioChannels)
            {
                mismatches.Add($"channel count {audio.Channels} != {target.AudioChannels}");
            }
        }

        // Concat requires an identical stream LAYOUT, not merely matching video and audio (spec D4).
        // A clip carrying anything extra — an embedded subtitle track, a second audio language, a
        // data stream — cannot be stream-copied alongside a two-stream clip: ffmpeg matches segments
        // by stream INDEX, so the other clips' audio lands on this clip's subtitle slot, ffmpeg exits
        // 0, and the user gets an output whose later clips are silently mute. Re-encode it instead;
        // normalization maps only 0:v:0 plus one audio stream, which drops the extras.
        if (clip.ExtraStreamCount > 0)
        {
            mismatches.Add(
                $"{clip.ExtraStreamCount} extra stream(s) (e.g. subtitles) must be dropped");
        }

        return new Conformance(mismatches);
    }

    /// <summary>Compares frame rates as rationals (cross-multiplication), not as raw
    /// numerator/denominator pairs: <see cref="FrameRate"/>'s default record-struct equality
    /// would treat 30/1 and 60/2 as different rates even though they are the same 30 fps —
    /// ffprobe's avg_frame_rate is not guaranteed to already be in lowest terms.</summary>
    private static bool FrameRatesMatch(FrameRate a, FrameRate b)
        => (long)a.Numerator * b.Denominator == (long)b.Numerator * a.Denominator;

    /// <summary>ffprobe's <c>codec_name</c> is a lowercase string ("h264", "hevc", "aac", "opus")
    /// that never matches a <see cref="MergeVideoCodec"/>/<see cref="MergeAudioCodec"/> member's
    /// own spelling for H.265 (ffprobe says "hevc", never "h265") — always compare against the
    /// mapped ffprobe name, case-insensitively.</summary>
    private static bool MatchesCodec(string actual, MergeVideoCodec expected)
        => string.Equals(actual, CodecName(expected), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesCodec(string actual, MergeAudioCodec expected)
        => string.Equals(actual, CodecName(expected), StringComparison.OrdinalIgnoreCase);

    private static string CodecName(MergeVideoCodec codec) => codec == MergeVideoCodec.H264 ? "h264" : "hevc";

    private static string CodecName(MergeAudioCodec codec) => codec == MergeAudioCodec.Aac ? "aac" : "opus";
}
