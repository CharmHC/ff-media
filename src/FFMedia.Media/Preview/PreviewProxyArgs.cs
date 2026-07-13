namespace FFMedia.Media.Preview;

/// <summary>Builds the ffmpeg arguments for a **preview proxy**. Pure — no I/O, no process.
///
/// <para><b>Why a proxy exists at all.</b> WPF's <c>MediaElement</c> renders through Windows Media
/// Foundation, so its codec support is <i>Windows'</i>, not ours. Verified against real files: it plays
/// H.264 in both MP4 and MKV, and <b>fails on VP9/WebM</b> — a format <b>our own downloader
/// produces</b>. So an unplayable source is transcoded to something it can definitely open.</para>
///
/// <para><b>The one hard rule: RESCALE ONLY, NEVER RE-TIME.</b> The captured timestamp is read from the
/// <i>player's</i> position. If the proxy's timeline differed from the source's by even a little, every
/// captured time would be a lie and the GIF would be cut somewhere other than where the user saw. So:
/// no <c>-r</c>, no <c>-ss</c>, no <c>-t</c>, no filter that drops or duplicates a frame.</para></summary>
public static class PreviewProxyArgs
{
    /// <summary>Cap the width; derive the height. This is a preview, not a deliverable.</summary>
    private const int MaxWidth = 640;

    public static IReadOnlyList<string> Build(string sourcePath, MediaInfo info, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var args = new List<string>
        {
            "-i", sourcePath,

            // min() CAPS the width rather than setting it, so a 320px source is never upscaled (that
            // would invent pixels and buy nothing). h=-2 derives the height from the source aspect AND
            // forces it even, which libx264 requires. The comma inside min() is ESCAPED: a bare one
            // would split the filtergraph, since ffmpeg separates filters with commas.
            "-vf", $@"scale=w='trunc(min({MaxWidth}\,iw)/2)*2':h=-2",

            "-c:v", "libx264",
            "-preset", "ultrafast",   // disposable preview: spend no time on compression
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
        };

        if (info.HasAudio)
        {
            // The user is scrubbing to FIND a moment, and sound is often how a human finds it.
            args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
        }
        else
        {
            // Encoding an audio stream that does not exist fails the entire run.
            args.Add("-an");
        }

        args.Add(outputPath);

        return args;
    }
}
