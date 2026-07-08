namespace FFMedia.Core.Binaries;

/// <summary>Pure parser for the version token in ffmpeg's <c>-version</c> first line.</summary>
public static class FfmpegVersionParsing
{
    public static string? Parse(string ffmpegVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(ffmpegVersionOutput))
        {
            return null;
        }

        var firstLine = ffmpegVersionOutput.Split('\n')[0].Trim();
        const string marker = "ffmpeg version ";
        var idx = firstLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var rest = firstLine[(idx + marker.Length)..].Trim();
        var token = rest.Split(' ')[0];
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
