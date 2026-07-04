namespace FFMedia.Core.Binaries;

/// <summary>Resolves binaries bundled under a given directory (e.g. app-relative assets/binaries).</summary>
public sealed class BundledBinaryProvider : IBinaryProvider
{
    private static readonly IReadOnlyDictionary<ExternalBinary, string> FileNames =
        new Dictionary<ExternalBinary, string>
        {
            [ExternalBinary.YtDlp] = "yt-dlp.exe",
            [ExternalBinary.Ffmpeg] = "ffmpeg.exe",
        };

    private readonly string _binariesDirectory;

    public BundledBinaryProvider(string binariesDirectory)
    {
        ArgumentNullException.ThrowIfNull(binariesDirectory);
        _binariesDirectory = binariesDirectory;
    }

    public string GetPath(ExternalBinary binary)
        => Path.Combine(_binariesDirectory, FileNames[binary]);

    public bool Exists(ExternalBinary binary)
        => File.Exists(GetPath(binary));
}
