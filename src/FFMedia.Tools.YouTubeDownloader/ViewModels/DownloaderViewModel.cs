using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFMedia.Core.Presets;
using FFMedia.Core.Settings;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;

namespace FFMedia.Tools.YouTubeDownloader.ViewModels;

public partial class DownloaderViewModel : ObservableObject
{
    private readonly IPlaylistProbe _playlistProbe;
    private readonly IDownloadManager _manager;
    private readonly IPresetService _presets;

    public DownloaderViewModel(
        IPlaylistProbe playlistProbe, IDownloadManager manager, ISettingsService settings, IPresetService presets)
    {
        ArgumentNullException.ThrowIfNull(playlistProbe);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(presets);
        _playlistProbe = playlistProbe;
        _manager = manager;
        _presets = presets;
        OutputFolder = settings.Current.DefaultOutputFolder;
        ReloadPresets();
    }

    /// <summary>The live queue, bound directly to the page's job list.</summary>
    public ReadOnlyObservableCollection<DownloadJob> Jobs => _manager.Jobs;

    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _outputFolder;

    [ObservableProperty] private OutputKind _selectedKind = OutputKind.Video;
    [ObservableProperty] private VideoContainer _selectedContainer = VideoContainer.Mp4;
    [ObservableProperty] private VideoResolution _selectedResolution = VideoResolution.P1080;
    [ObservableProperty] private AudioFormat _selectedAudioFormat = AudioFormat.Mp3;
    [ObservableProperty] private AudioBitrate _selectedBitrate = AudioBitrate.Best;

    public IReadOnlyList<OutputKind> Kinds { get; } = Enum.GetValues<OutputKind>();
    public IReadOnlyList<VideoContainer> Containers { get; } = Enum.GetValues<VideoContainer>();
    public IReadOnlyList<VideoResolution> Resolutions { get; } = Enum.GetValues<VideoResolution>();
    public IReadOnlyList<AudioFormat> AudioFormats { get; } = Enum.GetValues<AudioFormat>();
    public IReadOnlyList<AudioBitrate> Bitrates { get; } = Enum.GetValues<AudioBitrate>();

    public bool IsVideo => SelectedKind == OutputKind.Video;
    public bool IsAudio => SelectedKind == OutputKind.Audio;

    partial void OnSelectedKindChanged(OutputKind value)
    {
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsAudio));
    }

    [ObservableProperty] private string _trimStart = string.Empty;
    [ObservableProperty] private string _trimEnd = string.Empty;
    [ObservableProperty] private bool _preciseCut;
    [ObservableProperty] private bool _embedSubtitles;
    [ObservableProperty] private string _subtitleLanguage = "en";
    [ObservableProperty] private bool _embedMetadata = true;
    [ObservableProperty] private bool _embedThumbnail = true;
    [ObservableProperty] private string _trimHint = string.Empty;

    public ObservableCollection<Preset> Presets { get; } = new();
    [ObservableProperty] private Preset? _selectedPreset;
    [ObservableProperty] private string _newPresetName = string.Empty;

    partial void OnTrimStartChanged(string value) => UpdateTrimHint();
    partial void OnTrimEndChanged(string value) => UpdateTrimHint();

    private void UpdateTrimHint()
    {
        var requested = !(string.IsNullOrWhiteSpace(TrimStart) && string.IsNullOrWhiteSpace(TrimEnd));
        TrimHint = requested && TrimParsing.ParseRange(TrimStart, TrimEnd) is null
            ? "Enter valid Start/End (HH:MM:SS or seconds), End after Start."
            : string.Empty;
    }

    private ProcessingOptions BuildProcessing() => new(
        TrimParsing.ParseRange(TrimStart, TrimEnd),
        PreciseCut, EmbedSubtitles, SubtitleLanguage, EmbedMetadata, EmbedThumbnail);

    private DownloadConfig BuildConfig() => new(
        SelectedKind, SelectedContainer, SelectedResolution, SelectedAudioFormat, SelectedBitrate,
        BuildProcessing());

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        IsBusy = true;
        StatusMessage = "Resolving…";
        try
        {
            var config = BuildConfig();

            var result = await _playlistProbe.ExpandAsync(Url, CancellationToken.None);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error ?? "Could not resolve URL";
                return;
            }

            foreach (var entry in result.Value!)
                _manager.Enqueue(new DownloadJob(entry.Url, entry.Title, config, OutputFolder));

            StatusMessage = $"Added {result.Value.Count} item(s) to the queue";
            Url = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelJob(DownloadJob job) => _manager.Cancel(job);

    [RelayCommand]
    private void CancelAll() => _manager.CancelAll();

    [RelayCommand]
    private void ClearCompleted() => _manager.ClearCompleted();

    [RelayCommand]
    private void SaveAsPreset()
    {
        if (string.IsNullOrWhiteSpace(NewPresetName)) return;
        _presets.Save(new Preset(NewPresetName.Trim(), PresetMapping.Serialize(BuildConfig())));
        ReloadPresets();
        NewPresetName = string.Empty;
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset is null) return;
        ApplyConfig(PresetMapping.Deserialize(SelectedPreset.Payload));
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is null) return;
        _presets.Delete(SelectedPreset.Name);
        ReloadPresets();
    }

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var preset in _presets.List())
            Presets.Add(preset);
    }

    private void ApplyConfig(DownloadConfig config)
    {
        SelectedKind = config.Kind;
        SelectedContainer = config.Container;
        SelectedResolution = config.Resolution;
        SelectedAudioFormat = config.AudioFormat;
        SelectedBitrate = config.Bitrate;
        PreciseCut = config.Processing.PreciseCut;
        EmbedSubtitles = config.Processing.EmbedSubtitles;
        SubtitleLanguage = config.Processing.SubtitleLanguage;
        EmbedMetadata = config.Processing.EmbedMetadata;
        EmbedThumbnail = config.Processing.EmbedThumbnail;
        if (config.Processing.Trim is { } trim)
        {
            TrimStart = trim.Start.ToString(@"hh\:mm\:ss");
            TrimEnd = trim.End.ToString(@"hh\:mm\:ss");
        }
        else
        {
            TrimStart = string.Empty;
            TrimEnd = string.Empty;
        }
    }
}
