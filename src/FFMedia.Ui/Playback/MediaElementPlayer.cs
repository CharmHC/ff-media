using System.Windows;
using System.Windows.Controls;

namespace FFMedia.Ui.Playback;

/// <summary><see cref="IMediaPlayer"/> over a real WPF <see cref="MediaElement"/>.
///
/// <para><b>This is the only place that knows about <c>MediaElement</c>.</b> Everything else talks to the
/// interface, which is what makes the proxy-fallback logic testable without a window.</para>
///
/// <para><b>Constructible before the element exists.</b> <see cref="ViewModels.VideoPreviewViewModel"/>
/// takes its <see cref="IMediaPlayer"/> in its <b>constructor</b>, but the real <c>MediaElement</c> does
/// not exist until <c>VideoPreview</c>'s XAML has actually parsed — so DI cannot hand the VM a real
/// player at construction time. The resolution: this class has a parameterless constructor and is
/// registered as the app's single <see cref="IMediaPlayer"/> singleton, so the VM is constructor-injected
/// normally; <see cref="Attach"/> is then called by <c>VideoPreview</c>, on the SAME singleton instance,
/// right after its own <c>InitializeComponent()</c> — to hand over the real element.</para>
///
/// <para><b>Attach runs once per <c>VideoPreview</c>, i.e. once per page visit — NOT once per app run.</b>
/// The control is transient and this player is a singleton, so navigating away and back builds a fresh
/// <c>MediaElement</c> and attaches it here. That element has no <c>Source</c> of its own — the source
/// only ever arrives through <see cref="Open"/>, which nothing calls again on a revisit — so a player that
/// simply swapped elements went <b>black</b> while the (also singleton) ViewModel still reported
/// <c>IsReady</c>: the capture buttons stayed armed over a dead player and captured
/// <see cref="TimeSpan.Zero"/>. Hence <see cref="Open"/> REMEMBERS the current source for as long as it is
/// current, and <see cref="Attach"/> replays it onto the new element and restores the position the user
/// left off at.</para>
///
/// <para><b>Before <see cref="Attach"/>, every member degrades safely instead of throwing:</b>
/// <see cref="Position"/> reads <see cref="TimeSpan.Zero"/>, <see cref="Duration"/> is <c>null</c>, and
/// <see cref="Play"/>/<see cref="Pause"/> are no-ops. <see cref="Open"/> specifically does NOT no-op —
/// it RECORDS the requested path and <see cref="Attach"/> replays it. A silent no-op here would never
/// raise <see cref="MediaOpened"/>/<see cref="MediaFailed"/>, and <c>VideoPreviewViewModel.LoadAsync</c>
/// awaits exactly one of those — so a load that raced the control's own startup would hang forever on the
/// UI thread, which is precisely the class of bug Task 3 found and fixed for concurrent loads. In
/// practice <c>VideoPreview</c> attaches synchronously in its constructor, before any caller has a chance
/// to call <c>LoadAsync</c> — but queueing costs nothing and closes the gap outright rather than relying
/// on that ordering.</para>
///
/// <para><c>ScrubbingEnabled</c> is <b>load-bearing</b>: without it, setting <c>Position</c> while paused
/// does not render the new frame — so the user would capture a timestamp for a frame they never saw.</para></summary>
public sealed class MediaElementPlayer : IMediaPlayer
{
    private MediaElement? _element;

    /// <summary>The path the player is currently showing — kept for as long as it is current, NOT cleared
    /// once consumed. It is what <see cref="Attach"/> replays onto a new element, which is the only thing
    /// that keeps a page revisit from blanking the preview.</summary>
    private string? _currentSource;

    /// <summary>Where to seek the NEXT element once it opens. A <see cref="MediaElement"/> that has not
    /// opened its media ignores a <c>Position</c> write, so the restore must wait for its
    /// <c>MediaOpened</c>.</summary>
    private TimeSpan? _resumeTo;

    public event EventHandler? MediaOpened;

    public event EventHandler<string>? MediaFailed;

    public TimeSpan Position
    {
        get => _element?.Position ?? TimeSpan.Zero;
        set
        {
            if (_element is not null)
            {
                _element.Position = value;
            }
        }
    }

    public TimeSpan? Duration
    {
        get
        {
            if (_element is null || !_element.NaturalDuration.HasTimeSpan)
            {
                return null;
            }

            return _element.NaturalDuration.TimeSpan;
        }
    }

    public bool IsPlaying { get; private set; }

    /// <summary>Gives the player its real <see cref="MediaElement"/>. Called by <c>VideoPreview</c>, once
    /// its own <c>InitializeComponent()</c> has run and the element exists — so <b>once per
    /// <c>VideoPreview</c>, which is once per page visit</b>, not once per app run: the control is
    /// transient, this player is a singleton.
    ///
    /// <para>Hence the replay. The new element arrives blank; the source only ever comes from
    /// <see cref="Open"/>, and nothing calls <c>LoadAsync</c> again just because the user navigated back.
    /// So the current source is re-opened onto it and the position the OLD element was left at is
    /// restored, and the user gets back the frame they walked away from rather than a black rectangle with
    /// live capture buttons over it.</para>
    ///
    /// <para>The old element's handlers are unhooked FIRST. It stays alive until the visual tree lets it
    /// go, and Media Foundation can still answer on it — an answer that would otherwise fire into this
    /// singleton's <see cref="MediaOpened"/>/<see cref="MediaFailed"/> and settle a load attempt belonging
    /// to a different element entirely.</para></summary>
    public void Attach(MediaElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var resumeAt = TimeSpan.Zero;
        if (_element is not null)
        {
            resumeAt = _element.Position;
            _element.MediaOpened -= OnElementMediaOpened;
            _element.MediaFailed -= OnElementMediaFailed;
        }

        _element = element;
        _element.LoadedBehavior = MediaState.Manual;
        _element.ScrubbingEnabled = true;
        _element.MediaOpened += OnElementMediaOpened;
        _element.MediaFailed += OnElementMediaFailed;

        // A fresh element is not playing, whatever the one it replaced was doing.
        IsPlaying = false;

        if (_currentSource is { } path)
        {
            // Open() clears _resumeTo (a NEW video starts at the beginning), so the restore is armed
            // AFTER it, not before.
            Open(path);
            _resumeTo = resumeAt > TimeSpan.Zero ? resumeAt : null;
        }
    }

    /// <summary><c>ScrubbingEnabled</c> (set in <see cref="Attach"/>) is what makes the restored paused
    /// frame actually render, instead of leaving the previous frame on screen under a correct-looking
    /// position readout.</summary>
    private void OnElementMediaOpened(object? sender, RoutedEventArgs e)
    {
        if (_resumeTo is { } at && _element is not null)
        {
            _resumeTo = null;
            _element.Position = at;
        }

        MediaOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OnElementMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _resumeTo = null;
        MediaFailed?.Invoke(this, e.ErrorException?.Message ?? "The player could not open this video.");
    }

    public void Open(string path)
    {
        // Recorded whether or not an element exists yet, and NOT cleared once consumed: it is both the
        // queue for a caller that raced the control's own startup (a silent no-op would never raise
        // MediaOpened/MediaFailed, and VideoPreviewViewModel.LoadAsync awaits exactly one of those) AND
        // what Attach replays onto the next element on a page revisit.
        _currentSource = path;
        _resumeTo = null;   // a newly-opened video starts where it starts

        if (_element is null)
        {
            return;
        }

        IsPlaying = false;
        _element.Source = new Uri(path);
    }

    public void Play()
    {
        if (_element is null)
        {
            return;
        }

        _element.Play();
        IsPlaying = true;
    }

    public void Pause()
    {
        if (_element is null)
        {
            return;
        }

        _element.Pause();
        IsPlaying = false;
    }
}
