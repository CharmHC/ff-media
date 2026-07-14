using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.Media;
using FFMedia.Media;
using FFMedia.Media.Preview;
using FFMedia.Ui.Playback;

namespace FFMedia.Ui.ViewModels;

/// <summary>Drives the video preview: load, play/pause, step, and capture the current moment.
///
/// <para><b>Headless by construction.</b> Every dependency is an interface — including the player itself
/// (<see cref="IMediaPlayer"/>) — so the behaviour that matters most, <i>the source fails so we fall back
/// to a proxy</i>, is provable in a unit test rather than only by hand.</para>
///
/// <para>It does <b>not</b> know what Start and End mean. It raises <see cref="StartCaptured"/> /
/// <see cref="EndCaptured"/> with a position and lets the host decide — which is what keeps this control
/// reusable by the Merger and the Downloader (M10) rather than welded to the GIF Maker.</para></summary>
public partial class VideoPreviewViewModel : ObservableObject
{
    private readonly IMediaAnalyzer _analyzer;
    private readonly IPreviewProxyService _proxies;
    private readonly IMediaPlayer _player;

    private MediaInfo? _info;

    /// <summary>Cancels + supersedes whatever <see cref="LoadAsync"/> call is currently in flight. A
    /// fresh call cancels the previous one's gate BEFORE doing anything else, so a superseded load is
    /// abandoned synchronously rather than racing the new one or (Finding 1) leaving its caller's task
    /// hanging on an answer that will never be correlated back to it.</summary>
    private CancellationTokenSource? _loadGate;

    public VideoPreviewViewModel(IMediaAnalyzer analyzer, IPreviewProxyService proxies, IMediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentNullException.ThrowIfNull(player);

        _analyzer = analyzer;
        _proxies = proxies;
        _player = player;

        // Playback running off the end is the one thing that stops the player without anyone asking it
        // to -- so it is the one state change nothing was told about. IsPlaying reads straight through to
        // the player, so its VALUE was already right; what was missing was the NOTIFICATION, and WPF only
        // refreshes a binding whose exact path was notified. Without it the transport kept showing a Pause
        // button over a stopped video and the view's 200 ms position timer (started/stopped purely off
        // this notification) polled on forever. Subscribed for the lifetime of the VM: both this and the
        // player are DI singletons, so there is nothing to unsubscribe from and nothing to leak.
        _player.MediaEnded += (_, _) =>
        {
            OnPropertyChanged(nameof(IsPlaying));
            RefreshPosition();
        };
    }

    /// <summary>The user captured the current moment as the range's START.</summary>
    public event EventHandler<TimeSpan>? StartCaptured;

    /// <summary>The user captured the current moment as the range's END.</summary>
    public event EventHandler<TimeSpan>? EndCaptured;

    [ObservableProperty] private bool _isReady;

    [ObservableProperty] private bool _isPreparingProxy;

    [ObservableProperty] private double _proxyPercent;

    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Whether the host currently allows capturing. The GIF Maker sets this <c>false</c> while a
    /// render is in flight: the render holds a <b>snapshot</b>, so a page that can still change Start/End
    /// describes a job that is not the one running.</summary>
    [ObservableProperty] private bool _canCapture = true;

    public TimeSpan Position
    {
        get => _player.Position;
        set
        {
            _player.Position = value;

            // FINDING 1: a slider drag sets this property, but nothing downstream of it was ever told
            // to refresh -- so the PositionText readout froze even on a manual scrub, exactly like every
            // other interaction. RefreshPosition covers both notifications from the one call site that
            // actually goes through this setter.
            RefreshPosition();
        }
    }

    public TimeSpan Duration => _info?.Duration ?? TimeSpan.Zero;

    public bool IsPlaying => _player.IsPlaying;

    /// <summary>Loads a video: probe it, try to play it directly, and fall back to a proxy if the player
    /// cannot decode it.</summary>
    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // FINDING 1: this VM is a SINGLETON and this method awaits real I/O (a probe, maybe a whole
        // transcode) -- so a second LoadAsync arriving before the first has answered is a directly
        // reachable path (drop clip A, then drop clip B before A's player has answered), not an edge
        // case. Cancel + replace the gate BEFORE anything else, so the previous call is abandoned
        // synchronously rather than left racing this one.
        _loadGate?.Cancel();
        _loadGate?.Dispose();
        var gate = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadGate = gate;
        var token = gate.Token;

        try
        {
            IsReady = false;
            IsPreparingProxy = false;
            StatusMessage = "";
            _info = null;

            var probe = await _analyzer.AnalyzeAsync(path, token).ConfigureAwait(true);
            if (!probe.IsSuccess || probe.Value is null)
            {
                // The ANALYZER'S OWN REASON -- never a generic "not a video". That mistake blamed a
                // user's perfectly good mp4 for a missing ffprobe binary (CLAUDE.md, M7).
                StatusMessage = probe.Error ?? "That video could not be read.";
                return;
            }

            if (probe.Value.Video is null)
            {
                StatusMessage = "That file has no video track, so there is nothing to preview.";
                return;
            }

            _info = probe.Value;
            OnPropertyChanged(nameof(Duration));

            if (await TryOpenAsync(path, token).ConfigureAwait(true))
            {
                IsReady = true;
                return;
            }

            // The player cannot decode this file -- VP9/WebM being the case that actually happens, and
            // one OUR OWN DOWNLOADER produces. Transcode something it can open.
            IsPreparingProxy = true;
            ProxyPercent = 0;
            StatusMessage = "Preparing a preview…";

            var progress = new Progress<double>(p => ProxyPercent = p);
            var proxy = await _proxies
                .GetOrCreateAsync(path, _info, progress, token)
                .ConfigureAwait(true);

            IsPreparingProxy = false;

            if (!proxy.IsSuccess || proxy.Value is null)
            {
                // An AID, never a GATE. The timecode boxes still work. The message always names
                // "preview" (never just echoes the raw proxy error) so it reads as a stated limitation
                // rather than an unexplained tool failure -- the underlying reason is still appended
                // for diagnosis.
                StatusMessage = proxy.Error is { Length: > 0 } reason
                    ? $"The preview could not be prepared: {reason}"
                    : "The preview could not be prepared. You can still type times by hand.";
                return;
            }

            if (!await TryOpenAsync(proxy.Value, token).ConfigureAwait(true))
            {
                StatusMessage = "The preview could not be played. You can still type times by hand.";
                return;
            }

            StatusMessage = "";
            IsReady = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer LoadAsync, or the caller's own token was cancelled -- abandon
            // quietly. Whatever call replaced this one (if any) now owns IsReady/StatusMessage;
            // overwriting them here would be a race the user could actually see -- a flash of the WRONG
            // state for the video that is actually on screen now.
        }
    }

    /// <summary>Hands a path to the player and waits for it to say yes or no.
    ///
    /// <para>Each call owns its <b>own</b> <see cref="TaskCompletionSource{TResult}"/> and its own
    /// player-event subscriptions, torn down together the instant this attempt settles -- by the
    /// player answering, OR by <paramref name="ct"/> being cancelled. Nothing here reads a shared
    /// field to decide which attempt an answer belongs to. FINDING 1 was exactly that: one shared
    /// <c>_openAttempt</c> field, resolved by handlers wired ONCE in the constructor, so a stale answer
    /// for a superseded load resolved the WRONG caller's task -- and the right one hung forever.</para>
    /// </summary>
    private Task<bool> TryOpenAsync(string path, CancellationToken ct)
    {
        var attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler? opened = null;
        EventHandler<string>? failed = null;
        var registration = default(CancellationTokenRegistration);

        void Cleanup()
        {
            _player.MediaOpened -= opened;
            _player.MediaFailed -= failed;
            registration.Dispose();
        }

        opened = (_, _) =>
        {
            Cleanup();
            attempt.TrySetResult(true);
        };
        failed = (_, _) =>
        {
            Cleanup();
            attempt.TrySetResult(false);
        };

        // Subscribed BEFORE Open, so a player that answers synchronously can't fire into a void.
        _player.MediaOpened += opened;
        _player.MediaFailed += failed;
        registration = ct.Register(() =>
        {
            Cleanup();
            attempt.TrySetCanceled(ct);
        });

        _player.Open(path);

        return attempt.Task;
    }

    [RelayCommand]
    public void Play()
    {
        _player.Play();
        OnPropertyChanged(nameof(IsPlaying));
    }

    [RelayCommand]
    public void Pause()
    {
        _player.Pause();
        OnPropertyChanged(nameof(IsPlaying));
    }

    /// <summary>One frame of the SOURCE — 40 ms at 25 fps. A fixed step would skip frames on a fast
    /// video and stall on a slow one.</summary>
    private TimeSpan FrameStep
    {
        get
        {
            var fps = _info?.Video?.FrameRate.Value ?? 0;

            return fps > 0 ? TimeSpan.FromSeconds(1.0 / fps) : TimeSpan.FromMilliseconds(40);
        }
    }

    [RelayCommand]
    public void StepForward()
    {
        Pause();
        var next = _player.Position + FrameStep;
        _player.Position = Duration > TimeSpan.Zero && next > Duration ? Duration : next;
        RefreshPosition();
    }

    [RelayCommand]
    public void StepBack()
    {
        Pause();
        var previous = _player.Position - FrameStep;
        _player.Position = previous < TimeSpan.Zero ? TimeSpan.Zero : previous;
        RefreshPosition();
    }

    /// <summary>Whether the capture buttons should look clickable, not just BE clickable. Gated on
    /// <see cref="IsReady"/> as well as <see cref="CanCapture"/>: before any video is loaded, <c>Set
    /// Start</c>/<c>Set End</c> used to be enabled and silently do nothing when clicked -- a control that
    /// looks live but does nothing is the same class of bug as the merger's checkbox (CLAUDE.md).</summary>
    private bool CanCaptureNow => CanCapture && IsReady;

    [RelayCommand(CanExecute = nameof(CanCaptureNow))]
    public void CaptureStart()
    {
        // Guarded in the METHOD as well as in CanExecute, because a gesture that is not a command
        // bypasses CanExecute entirely -- the bug M8 shipped twice.
        if (!CanCapture || !IsReady)
        {
            return;
        }

        StartCaptured?.Invoke(this, _player.Position);
    }

    [RelayCommand(CanExecute = nameof(CanCaptureNow))]
    public void CaptureEnd()
    {
        if (!CanCapture || !IsReady)
        {
            return;
        }

        EndCaptured?.Invoke(this, _player.Position);
    }

    partial void OnCanCaptureChanged(bool value)
    {
        CaptureStartCommand.NotifyCanExecuteChanged();
        CaptureEndCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsReadyChanged(bool value)
    {
        CaptureStartCommand.NotifyCanExecuteChanged();
        CaptureEndCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The current position, formatted the same way the range boxes are — so what the user reads
    /// under the player is exactly what a capture will write into the box.</summary>
    public string PositionText => TrimParsing.Format(Position);

    /// <summary>Refreshes the bindings that read the player's position (the seek slider and
    /// <see cref="PositionText"/>) WITHOUT writing back to the player.
    ///
    /// <para><b>FINDING 1:</b> the VM raised <c>nameof(Position)</c> from <see cref="StepForward"/>/
    /// <see cref="StepBack"/> (which move the player directly, bypassing this class's own
    /// <see cref="Position"/> setter), but WPF only refreshes a binding whose EXACT path was notified --
    /// a "Position" notification never refreshed a "PositionText" binding. So the on-screen readout froze
    /// at whatever it computed at bind time, through every interaction. This is the one place both
    /// notifications are raised together, so the two can no longer drift apart.</para>
    ///
    /// <para>Also called by the control's own polling timer while the video is <i>playing</i> (Finding
    /// 2): nothing else moves the readout while playback is running, since neither Play() nor the
    /// player itself pushes position changes back into this VM.</para></summary>
    public void RefreshPosition()
    {
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(PositionText));
    }
}
