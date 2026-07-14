using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FFMedia.Ui.Playback;
using FFMedia.Ui.ViewModels;

namespace FFMedia.Ui.Views;

/// <summary>The video preview: player, transport, and the capture buttons. Code-behind holds only what
/// genuinely needs the visual tree — attaching the real <see cref="MediaElement"/> to the player, and the
/// <see cref="DispatcherTimer"/> that drives the position readout while playing (a view concern; the VM
/// stays headless). Everything else lives in <see cref="VideoPreviewViewModel"/>, which is headless and
/// unit-tested.
///
/// <para>Takes the <see cref="MediaElementPlayer"/> as its OWN constructor parameter, separately from the
/// <see cref="VideoPreviewViewModel"/> that already holds it as its <see cref="IMediaPlayer"/> — DI
/// resolves both parameters to the SAME singleton instance. That is what lets the VM be
/// constructor-injected in the ordinary way (its <c>IMediaPlayer</c> exists at DI-container build time)
/// while the real <c>MediaElement</c> only becomes available here, once <c>InitializeComponent()</c> has
/// run.</para></summary>
public partial class VideoPreview : UserControl
{
    private readonly VideoPreviewViewModel _viewModel;

    /// <summary>FINDING 2: nothing drove <see cref="VideoPreviewViewModel.Position"/> while the video was
    /// PLAYING — Play()/Pause()/Step() are the only things that ever pushed a fresh position into the VM,
    /// so the slider thumb and the readout froze the moment playback started. This timer polls the real
    /// player's already-current position a few times a second and asks the VM to refresh its bindings —
    /// it does not seek or otherwise write to the player, only reads.</summary>
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public VideoPreview(VideoPreviewViewModel viewModel, MediaElementPlayer player)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(player);

        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // The visual tree exists now — Player (the MediaElement) was created by InitializeComponent().
        // Attached here rather than on Loaded because there is nothing to wait for: the element is
        // already a real object the moment the XAML has parsed.
        player.Attach(Player);

        _positionTimer.Tick += OnPositionTimerTick;

        // Started/stopped from the VM's own IsPlaying notification rather than wiring the transport
        // buttons directly — Play/Pause/Step already raise it, so this control doesn't need to know
        // which gesture changed play state, only that it did.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // CRITICAL (Finding 2): a timer that keeps ticking after the user navigates away is a leak, and
        // the VM it would go on touching is a DI singleton that outlives this control — it can survive
        // long after the page (and this control instance) is gone. Torn down on Unloaded, not just
        // Dispose (UserControl has none): stop the timer AND unsubscribe from the VM, so this control
        // instance isn't kept alive by the VM's own PropertyChanged subscriber list either.
        Unloaded += OnUnloaded;
    }

    private void OnPositionTimerTick(object? sender, EventArgs e) => _viewModel.RefreshPosition();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VideoPreviewViewModel.IsPlaying))
        {
            return;
        }

        if (_viewModel.IsPlaying)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // FIRST, and before anything is torn down: STOP THE VIDEO. The XAML sets
        // UnloadedBehavior="Manual", which is exactly the flag that says "a MediaElement pulled out of the
        // visual tree does not stop" -- so navigating away from a playing preview left the audio playing,
        // indefinitely, from a page that is no longer on screen. The player's only Close() lives in
        // Attach(), which runs when the user comes BACK: the wrong end of the journey. (Attach() reads the
        // element's position to restore it, and Pause does not move it, so the resume is unaffected.)
        _viewModel.Pause();

        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Unloaded -= OnUnloaded;
    }
}
