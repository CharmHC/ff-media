using System;
using System.IO;
using FFMedia.Tools.YouTubeDownloader.Models;
using YoutubeDLSharp.Options;
using ModelAudioFormat = FFMedia.Tools.YouTubeDownloader.Models.AudioFormat;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Builds a yt-dlp <see cref="OptionSet"/> from a <see cref="DownloadConfig"/>. Pure — the tested core of M2.</summary>
public static class OptionSetBuilder
{
    public static OptionSet Build(DownloadConfig config, string outputFolder)
    {
        var output = Path.Combine(outputFolder, "%(title)s.%(ext)s");
        var options = config.Kind == OutputKind.Audio
            ? BuildAudio(config, output)
            : BuildVideo(config, output);
        ApplyProcessing(options, config);
        return options;
    }

    private static OptionSet BuildVideo(DownloadConfig config, string output)
    {
        var h = HeightFilter(config.Resolution);
        var format = config.Container switch
        {
            VideoContainer.Mp4  => $"bv*{h}[ext=mp4]+ba[ext=m4a]/b{h}[ext=mp4]/bv*{h}+ba/b{h}",
            VideoContainer.Webm => $"bv*{h}[ext=webm]+ba[ext=webm]/b{h}[ext=webm]/bv*{h}+ba/b{h}",
            VideoContainer.Mkv  => $"bv*{h}+ba/b{h}", // Mkv holds any codec — no ext preference
            _ => throw new ArgumentOutOfRangeException(nameof(config.Container), config.Container, "Unhandled VideoContainer."),
        };
        return new OptionSet
        {
            Format = format,
            MergeOutputFormat = config.Container switch
            {
                VideoContainer.Mp4  => DownloadMergeFormat.Mp4,
                VideoContainer.Webm => DownloadMergeFormat.Webm,
                VideoContainer.Mkv  => DownloadMergeFormat.Mkv,
                _ => throw new ArgumentOutOfRangeException(nameof(config.Container), config.Container, "Unhandled VideoContainer."),
            },
            NoPlaylist = true,
            Output = output,
        };
    }

    private static OptionSet BuildAudio(DownloadConfig config, string output)
    {
        var options = new OptionSet
        {
            ExtractAudio = true,
            AudioFormat = config.AudioFormat switch
            {
                ModelAudioFormat.Mp3  => AudioConversionFormat.Mp3,
                ModelAudioFormat.Wav  => AudioConversionFormat.Wav,
                ModelAudioFormat.M4a  => AudioConversionFormat.M4a,
                ModelAudioFormat.Opus => AudioConversionFormat.Opus,
                ModelAudioFormat.Flac => AudioConversionFormat.Flac,
                _ => throw new ArgumentOutOfRangeException(nameof(config.AudioFormat), config.AudioFormat, "Unhandled AudioFormat."),
            },
            Format = "ba/b",
            NoPlaylist = true,
            Output = output,
        };

        // Specific bitrate applies only to lossy formats; WAV/FLAC are lossless.
        if (config.Bitrate != AudioBitrate.Best && IsLossy(config.AudioFormat))
            options.AddCustomOption("--audio-quality", BitrateValue(config.Bitrate));

        return options;
    }

    private static void ApplyProcessing(OptionSet options, DownloadConfig config)
    {
        var p = config.Processing;

        if (p.Trim is { } trim)
        {
            options.DownloadSections = $"*{FormatSeconds(trim.Start)}-{FormatSeconds(trim.End)}";
            if (p.PreciseCut) options.ForceKeyframesAtCuts = true;
        }

        // Subtitles only apply to video output.
        if (config.Kind == OutputKind.Video && p.EmbedSubtitles)
        {
            options.WriteSubs = true;
            options.WriteAutoSubs = true;
            options.EmbedSubs = true;
            options.SubLangs = p.SubtitleLanguage;
        }

        options.EmbedMetadata = p.EmbedMetadata;
        options.EmbedThumbnail = p.EmbedThumbnail;
    }

    private static string FormatSeconds(TimeSpan t) =>
        t.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string HeightFilter(VideoResolution r) => r switch
    {
        VideoResolution.P2160 => "[height<=2160]",
        VideoResolution.P1440 => "[height<=1440]",
        VideoResolution.P1080 => "[height<=1080]",
        VideoResolution.P720  => "[height<=720]",
        VideoResolution.P480  => "[height<=480]",
        VideoResolution.Best  => "", // Best: no cap
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, "Unhandled VideoResolution."),
    };

    private static bool IsLossy(ModelAudioFormat f) =>
        f is ModelAudioFormat.Mp3 or ModelAudioFormat.M4a or ModelAudioFormat.Opus;

    private static string BitrateValue(AudioBitrate b) => b switch
    {
        AudioBitrate.K320 => "320K",
        AudioBitrate.K256 => "256K",
        AudioBitrate.K192 => "192K",
        AudioBitrate.K128 => "128K",
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, "Unhandled AudioBitrate."),
    };
}
