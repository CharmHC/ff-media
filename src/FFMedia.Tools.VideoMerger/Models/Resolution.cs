namespace FFMedia.Tools.VideoMerger.Models;

/// <summary>One selectable output resolution. Exists so the page can bind a single ComboBox to a
/// width/height PAIR: two independent text boxes let the user build 1920 × 102 — positive, even,
/// encodable, and absurd. <see cref="MergeTarget"/> keeps its flat Width/Height (the engine and
/// every existing test read them); this is the UI's unit of choice.</summary>
public sealed record Resolution(int Width, int Height)
{
    public long PixelCount => (long)Width * Height;

    /// <summary>What the ComboBox shows. The × is U+00D7, not the letter x.</summary>
    public override string ToString() => $"{Width} × {Height}";
}
