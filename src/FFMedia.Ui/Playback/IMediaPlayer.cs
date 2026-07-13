namespace FFMedia.Ui.Playback;

/// <summary>The narrow seam between the ViewModel and an actual video player.
///
/// <para>It exists so <see cref="ViewModels.VideoPreviewViewModel"/> can be tested <b>headlessly</b>: a
/// real <c>MediaElement</c> cannot be driven without a window and a message pump, and the behaviour that
/// most needs testing — <b>the source fails, so fall back to a proxy</b> — is impossible to trigger on
/// demand with a real one.</para></summary>
public interface IMediaPlayer
{
    /// <summary>Where the player currently is. This is the value a capture reads.</summary>
    TimeSpan Position { get; set; }

    TimeSpan? Duration { get; }

    bool IsPlaying { get; }

    /// <summary>Raised when the media opened successfully.</summary>
    event EventHandler? MediaOpened;

    /// <summary>Raised when the player cannot play this file at all — e.g. VP9/WebM, which Windows Media
    /// Foundation does not decode. This is the signal that triggers the proxy fallback.</summary>
    event EventHandler<string>? MediaFailed;

    void Open(string path);

    void Play();

    void Pause();
}
