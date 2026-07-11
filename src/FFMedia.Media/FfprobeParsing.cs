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
            var audio = ParseAudio(streams);

            // Everything past the first video and the first audio: subtitles, extra audio tracks,
            // data streams. Video/Audio above describe only the first of each, so this is the only
            // record that the rest of the file exists — and concat's identical-layout requirement
            // is about all of them.
            var accounted = 1 + (audio is null ? 0 : 1);
            var extras = Math.Max(0, streams.GetArrayLength() - accounted);

            return new MediaInfo(TimeSpan.FromSeconds(seconds), container, video, audio)
            {
                ExtraStreamCount = extras,
            };
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

            if (!TryGetInt(stream, "width", out var width) || !TryGetInt(stream, "height", out var height)
                || !FrameRate.TryParse(GetString(stream, "avg_frame_rate"), out var rate))
            {
                continue;
            }

            return new VideoStreamInfo(
                width,
                height,
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
                || !TryGetInt(stream, "channels", out var channels))
            {
                continue;
            }

            return new AudioStreamInfo(GetString(stream, "codec_name") ?? "", sampleRate, channels);
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

    // Every field read through this helper (width, height, sample_rate, channels) is
    // integral by definition, so a fractional value means the stream is unusable.
    private static bool TryGetInt(JsonElement element, string name, out int result)
    {
        result = 0;
        if (!TryGetDouble(element, name, out var value)
            || value is < int.MinValue or > int.MaxValue
            || Math.Truncate(value) != value)
        {
            return false;
        }

        result = (int)value;
        return true;
    }
}
