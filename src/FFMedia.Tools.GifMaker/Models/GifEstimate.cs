namespace FFMedia.Tools.GifMaker.Models;

/// <summary>An estimated GIF size, always a RANGE. GIF size depends on content — a static talking head
/// compresses far better than confetti — so a single number would be false precision.</summary>
public sealed record GifEstimate(long LowBytes, long HighBytes)
{
    public string Describe() => LowBytes == HighBytes
        ? Megabytes(LowBytes)
        : $"{Megabytes(LowBytes)}–{Megabytes(HighBytes)}";

    private static string Megabytes(long bytes) => $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
