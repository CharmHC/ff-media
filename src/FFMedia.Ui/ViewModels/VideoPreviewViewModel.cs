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
    private string _sourcePath = "";
    private TaskCompletionSource<bool>? _openAttempt;

    public VideoPreviewViewModel(IMediaAnalyzer analyzer, IPreviewProxyService proxies, IMediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentNullException.ThrowIfNull(player);

        _analyzer = analyzer;
        _proxies = proxies;
        _player = player;

        _player.MediaOpened += (_, _) => _openAttempt?.TrySetResult(true);
        _player.MediaFailed += (_, _) => _openAttempt?.TrySetResult(false);
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
        set => _player.Position = value;
    }

    public TimeSpan Duration => _info?.Duration ?? TimeSpan.Zero;

    public bool IsPlaying => _player.IsPlaying;

    /// <summary>Loads a video: probe it, try to play it directly, and fall back to a proxy if the player
    /// cannot decode it.</summary>
    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IsReady = false;
        IsPreparingProxy = false;
        StatusMessage = "";
        _info = null;
        _sourcePath = path;

        var probe = await _analyzer.AnalyzeAsync(path, ct).ConfigureAwait(true);
        if (!probe.IsSuccess || probe.Value is null)
        {
            // The ANALYZER'S OWN REASON -- never a generic "not a video". That mistake blamed a user's
            // perfectly good mp4 for a missing ffprobe binary (CLAUDE.md, M7).
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

        if (await TryOpenAsync(path).ConfigureAwait(true))
        {
            IsReady = true;
            return;
        }

        // The player cannot decode this file -- VP9/WebM being the case that actually happens, and one
        // OUR OWN DOWNLOADER produces. Transcode something it can open.
        IsPreparingProxy = true;
        ProxyPercent = 0;
        StatusMessage = "Preparing a preview…";

        var progress = new Progress<double>(p => ProxyPercent = p);
        var proxy = await _proxies
            .GetOrCreateAsync(path, _info, progress, ct)
            .ConfigureAwait(true);

        IsPreparingProxy = false;

        if (!proxy.IsSuccess || proxy.Value is null)
        {
            // An AID, never a GATE. The timecode boxes still work. The message always names "preview"
            // (never just echoes the raw proxy error) so it reads as a stated limitation rather than an
            // unexplained tool failure -- the underlying reason is still appended for diagnosis.
            StatusMessage = proxy.Error is { Length: > 0 } reason
                ? $"The preview could not be prepared: {reason}"
                : "The preview could not be prepared. You can still type times by hand.";
            return;
        }

        if (!await TryOpenAsync(proxy.Value).ConfigureAwait(true))
        {
            StatusMessage = "The preview could not be played. You can still type times by hand.";
            return;
        }

        StatusMessage = "";
        IsReady = true;
    }

    /// <summary>Hands a path to the player and waits for it to say yes or no.</summary>
    private Task<bool> TryOpenAsync(string path)
    {
        _openAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _player.Open(path);

        return _openAttempt.Task;
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
        OnPropertyChanged(nameof(Position));
    }

    [RelayCommand]
    public void StepBack()
    {
        Pause();
        var previous = _player.Position - FrameStep;
        _player.Position = previous < TimeSpan.Zero ? TimeSpan.Zero : previous;
        OnPropertyChanged(nameof(Position));
    }

    [RelayCommand(CanExecute = nameof(CanCapture))]
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

    [RelayCommand(CanExecute = nameof(CanCapture))]
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

    /// <summary>The current position, formatted the same way the range boxes are — so what the user reads
    /// under the player is exactly what a capture will write into the box.</summary>
    public string PositionText => TrimParsing.Format(Position);
}
