using System.ComponentModel;

namespace FFMedia.Tools.YouTubeDownloader.Services;

/// <summary>Maps unexpected exceptions from the yt-dlp process into user-facing messages.</summary>
internal static class YtDlpErrors
{
    // ERROR_FILE_NOT_FOUND from Win32 CreateProcess when yt-dlp.exe/ffmpeg.exe is absent.
    private const int ErrorFileNotFound = 2;

    public static string Describe(Exception ex) => ex switch
    {
        Win32Exception { NativeErrorCode: ErrorFileNotFound } =>
            "yt-dlp or ffmpeg was not found. Run build/fetch-binaries.ps1 to download the bundled binaries.",
        _ => ex.Message,
    };
}
