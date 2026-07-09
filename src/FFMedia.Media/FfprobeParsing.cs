using System.Globalization;
using System.Text.Json;

namespace FFMedia.Media;

/// <summary>Pure parser for <c>ffprobe -print_format json -show_format -show_streams</c> output.
/// Returns null for anything unusable (no video stream, malformed JSON, missing duration).</summary>
public static class FfprobeParsing
{
    public static MediaInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format)
                || !root.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var video = ParseVideo(streams);
            if (video is null)
            {
                return null;
            }

            if (!TryGetDouble(format, "duration", out var seconds))
            {
                return null;
            }

            var container = GetString(format, "format_name") ?? "";
            return new MediaInfo(TimeSpan.FromSeconds(seconds), container, video, ParseAudio(streams));
        }
    }

    private static VideoStreamInfo? ParseVideo(JsonElement streams)
    {
        foreach (var stream in streams.EnumerateArray())
        {
            if (GetString(stream, "codec_type") != "video")
            {
                continue;
            }

            if (!stream.TryGetProperty("width", out var w) || !stream.TryGetProperty("height", out var h)
                || !FrameRate.TryParse(GetString(stream, "avg_frame_rate"), out var rate))
            {
                continue;
            }

            return new VideoStreamInfo(
                w.GetInt32(),
                h.GetInt32(),
                rate,
                GetString(stream, "codec_name") ?? "",
                GetString(stream, "pix_fmt") ?? "",
                ParseRotation(stream));
        }

        return null;
    }

    private static AudioStreamInfo? ParseAudio(JsonElement streams)
    {
        foreach (var stream in streams.EnumerateArray())
        {
            if (GetString(stream, "codec_type") != "audio")
            {
                continue;
            }

            if (!TryGetInt(stream, "sample_rate", out var sampleRate)
                || !stream.TryGetProperty("channels", out var channels))
            {
                continue;
            }

            return new AudioStreamInfo(GetString(stream, "codec_name") ?? "", sampleRate, channels.GetInt32());
        }

        return null;
    }

    private static int ParseRotation(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.TryGetProperty("rotation", out var rotation) && rotation.TryGetInt32(out var degrees))
            {
                return degrees;
            }
        }

        return 0;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ffprobe emits numbers as JSON strings ("48000", "12.500000") in most fields.
    private static bool TryGetDouble(JsonElement element, string name, out double result)
    {
        result = 0;
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out result),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }

    private static bool TryGetInt(JsonElement element, string name, out int result)
    {
        result = 0;
        return TryGetDouble(element, name, out var value)
            && value is >= int.MinValue and <= int.MaxValue
            && int.TryParse(((int)value).ToString(CultureInfo.InvariantCulture), out result);
    }
}
