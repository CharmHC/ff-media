using FFMedia.Tools.GifMaker.Models;

namespace FFMedia.Tools.GifMaker.Services;

/// <summary>Estimates the finished GIF's size. Pure.</summary>
public static class GifSizeEstimator
{
    public static GifEstimate Estimate(GifRequest request, GifSizeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);

        var pixelsPerFrame = (long)request.Size.Width * request.Size.Height;
        var centre = pixelsPerFrame * request.FrameCount * profile.EffectiveBytesPerPixelPerFrame;
        var band = profile.Band;

        return new GifEstimate(
            (long)Math.Max(1, centre * (1 - band)),
            (long)Math.Max(2, centre * (1 + band)));
    }
}
