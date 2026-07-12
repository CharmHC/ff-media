namespace FFMedia.Tools.GifMaker.Models;

/// <summary>How heavy the user's GIFs actually turn out, in bytes per pixel per frame.
///
/// <para>The seed is a guess; the user's own GIFs are evidence. This is the merger's
/// <c>SpeedProfile</c> pattern applied to the same class of unknowable — persisted to
/// <c>gif-size.json</c>, so it survives restarts and keeps improving.</para></summary>
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

    /// <summary>Half-width of the estimate range, as a fraction. Narrows with evidence.</summary>
    public double Band => Math.Max(MinBand, MaxBand - (SampleCount * BandNarrowingPerSample));

    /// <summary>Folds a real, measured GIF into the rolling average.</summary>
    /// <remarks>Nonsense is IGNORED, not folded in: a zero-byte or zero-frame result means something
    /// went wrong, and the profile PERSISTS — poisoning it would corrupt every future estimate long
    /// after the session that did it.</remarks>
    public void Record(long actualBytes, long pixelsPerFrame, int frames)
    {
        if (actualBytes <= 0 || pixelsPerFrame <= 0 || frames <= 0)
        {
            return;
        }

        var measured = actualBytes / (double)pixelsPerFrame / frames;
        var weight = Math.Min(SampleCount, Window - 1);
        BytesPerPixelPerFrame = ((BytesPerPixelPerFrame * weight) + measured) / (weight + 1);
        SampleCount++;
    }
}
