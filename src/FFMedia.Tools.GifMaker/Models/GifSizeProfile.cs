namespace FFMedia.Tools.GifMaker.Models;

/// <summary>How heavy the user's GIFs actually turn out, in bytes per pixel per frame.
///
/// <para>The seed is a guess; the user's own GIFs are evidence. This is the merger's
/// <c>SpeedProfile</c> pattern applied to the same class of unknowable — persisted to
/// <c>gif-size.json</c>, so it survives restarts and keeps improving.</para></summary>
/// <remarks>Every read path is defensive: the backing file is user-visible JSON, so a hand-edited or
/// half-written value (a negative <see cref="SampleCount"/>, a NaN or negative
/// <see cref="BytesPerPixelPerFrame"/>) must degrade to the seed / a sane count rather than feed a
/// zero, a negative, or a NaN into the user-facing estimate.</remarks>
public sealed class GifSizeProfile
{
    /// <summary>Roughly what a 480x270, 15 fps GIF of ordinary footage weighs. A starting point only —
    /// the first real GIF the user makes starts correcting it.</summary>
    private const double Seed = 0.75;

    /// <summary>Rolling window. Old GIFs of very different content should not anchor the estimate
    /// forever.</summary>
    private const int Window = 10;

    /// <summary>Band with NO evidence: ±45 %. Wide, because with nothing to go on we genuinely do not
    /// know, and pretending otherwise is the failure mode.</summary>
    private const double MaxBand = 0.45;

    /// <summary>Band the estimate can narrow to once it has seen the user's GIFs. Never zero: content
    /// still varies, so some honest width always remains.</summary>
    private const double MinBand = 0.20;

    private const double BandNarrowingPerSample = 0.04;

    public double BytesPerPixelPerFrame { get; set; } = Seed;

    public int SampleCount { get; set; }

    /// <summary>The value the estimator must actually use: <see cref="BytesPerPixelPerFrame"/> falls
    /// back to the seed when a hand-edited or half-written file has left it non-positive, NaN, or
    /// infinite. <see cref="GifSizeEstimator"/> reads this, never the raw property, so a corrupted
    /// file cannot poison a live estimate.</summary>
    public double EffectiveBytesPerPixelPerFrame => IsUsable(BytesPerPixelPerFrame) ? BytesPerPixelPerFrame : Seed;

    /// <summary><see cref="SampleCount"/>, floored at zero. A persisted negative count (e.g. hand-edited
    /// to -1) would otherwise make <see cref="Record"/>'s weight zero-or-negative and divide by zero.</summary>
    private int EffectiveSampleCount => Math.Max(0, SampleCount);

    /// <summary>Half-width of the estimate range, as a fraction. Narrows with evidence, floored at
    /// <see cref="MinBand"/> so the range can never invert (Low > High) however many samples are
    /// persisted — content still varies, so some honest width always remains.</summary>
    public double Band => Math.Max(MinBand, MaxBand - (EffectiveSampleCount * BandNarrowingPerSample));

    /// <summary>Folds a real, measured GIF into the rolling average.</summary>
    /// <remarks>Nonsense is IGNORED, not folded in: a zero-byte or zero-frame result means something
    /// went wrong, and the profile PERSISTS — poisoning it would corrupt every future estimate long
    /// after the session that did it. A corrupt PERSISTED average is also discarded rather than
    /// blended with — the new sample becomes the average outright, exactly like a fresh profile's
    /// first sample — instead of being averaged against garbage.</remarks>
    public void Record(long actualBytes, long pixelsPerFrame, int frames)
    {
        if (actualBytes <= 0 || pixelsPerFrame <= 0 || frames <= 0)
        {
            return;
        }

        var measured = actualBytes / (double)pixelsPerFrame / frames;

        // Discard (rather than average with) a nonsense persisted state: an unusable average or a
        // negative count would otherwise poison the new average or divide by a bad weight.
        var count = EffectiveSampleCount;
        var average = BytesPerPixelPerFrame;
        if (!IsUsable(average))
        {
            average = 0;
            count = 0;
        }

        var weight = Math.Min(count, Window - 1);
        BytesPerPixelPerFrame = ((average * weight) + measured) / (weight + 1);
        SampleCount = count + 1;
    }

    private static bool IsUsable(double value) => double.IsFinite(value) && value > 0;
}
