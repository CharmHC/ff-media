using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using FFMedia.Core.Results;
using FFMedia.Tools.YouTubeDownloader.Models;
using FFMedia.Tools.YouTubeDownloader.Services;
using FFMedia.Tools.YouTubeDownloader.ViewModels;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloaderViewModelTests
{
    private sealed class FakePlaylistProbe : IPlaylistProbe
    {
        public Result<IReadOnlyList<MediaEntry>> Next =
            Result<IReadOnlyList<MediaEntry>>.Success(new[] { new MediaEntry("https://u", "T") });
        public Task<Result<IReadOnlyList<MediaEntry>>> ExpandAsync(string url, CancellationToken ct)
            => Task.FromResult(Next);
    }

    private sealed class FakeManager : IDownloadManager
    {
        private readonly ObservableCollection<DownloadJob> _jobs = new();
        public FakeManager() => Jobs = new ReadOnlyObservableCollection<DownloadJob>(_jobs);
        public ReadOnlyObservableCollection<DownloadJob> Jobs { get; }
        public List<DownloadJob> Enqueued { get; } = new();
        public bool CancelAllCalled { get; private set; }
        public bool ClearCompletedCalled { get; private set; }
        public DownloadJob? Canceled { get; private set; }
        public DownloadJob Enqueue(DownloadJob job) { Enqueued.Add(job); _jobs.Add(job); return job; }
        public void Cancel(DownloadJob job) => Canceled = job;
        public void CancelAll() => CancelAllCalled = true;
        public void ClearCompleted() => ClearCompletedCalled = true;
        public Task IdleAsync() => Task.CompletedTask;
    }

    private static DownloaderViewModel Vm(FakePlaylistProbe probe, FakeManager mgr) => new(probe, mgr);

    [Fact]
    public async Task AddToQueue_SingleEntry_EnqueuesOneJobWithSelectedConfig()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        vm.Url = "https://u";
        vm.SelectedKind = OutputKind.Audio;
        vm.SelectedAudioFormat = AudioFormat.Mp3;
        vm.SelectedBitrate = AudioBitrate.K192;

        await vm.AddToQueueCommand.ExecuteAsync(null);

        var job = Assert.Single(mgr.Enqueued);
        Assert.Equal("https://u", job.Url);
        Assert.Equal("T", job.Title);
        Assert.Equal(OutputKind.Audio, job.Config.Kind);
        Assert.Equal(AudioFormat.Mp3, job.Config.AudioFormat);
        Assert.Equal(AudioBitrate.K192, job.Config.Bitrate);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AddToQueue_Playlist_EnqueuesOneJobPerEntry()
    {
        var mgr = new FakeManager();
        var probe = new FakePlaylistProbe
        {
            Next = Result<IReadOnlyList<MediaEntry>>.Success(new[]
            {
                new MediaEntry("https://a", "A"),
                new MediaEntry("https://b", "B"),
            }),
        };
        var vm = Vm(probe, mgr);
        vm.Url = "https://list";

        await vm.AddToQueueCommand.ExecuteAsync(null);

        Assert.Equal(2, mgr.Enqueued.Count);
        Assert.Equal(new[] { "https://a", "https://b" }, mgr.Enqueued.ConvertAll(j => j.Url).ToArray());
    }

    [Fact]
    public async Task AddToQueue_ExpandFailure_SetsStatus_AndEnqueuesNothing()
    {
        var mgr = new FakeManager();
        var probe = new FakePlaylistProbe { Next = Result<IReadOnlyList<MediaEntry>>.Failure("Video unavailable") };
        var vm = Vm(probe, mgr);
        vm.Url = "https://bad";

        await vm.AddToQueueCommand.ExecuteAsync(null);

        Assert.Empty(mgr.Enqueued);
        Assert.Contains("unavailable", vm.StatusMessage);
    }

    [Fact]
    public async Task AddToQueue_EmptyUrl_DoesNothing()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        vm.Url = "   ";
        await vm.AddToQueueCommand.ExecuteAsync(null);
        Assert.Empty(mgr.Enqueued);
    }

    [Fact]
    public void Jobs_ExposesManagerJobs()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        Assert.Same(mgr.Jobs, vm.Jobs);
    }

    [Fact]
    public void CancelAll_And_ClearCompleted_DelegateToManager()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        vm.CancelAllCommand.Execute(null);
        vm.ClearCompletedCommand.Execute(null);
        Assert.True(mgr.CancelAllCalled);
        Assert.True(mgr.ClearCompletedCalled);
    }

    [Fact]
    public void CancelJob_DelegatesToManager()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        var job = new DownloadJob("https://u", "T", DownloadConfig.Default, @"C:\out");
        vm.CancelJobCommand.Execute(job);
        Assert.Same(job, mgr.Canceled);
    }

    [Fact]
    public void SelectedKind_TogglesIsVideoIsAudio()
    {
        var vm = Vm(new FakePlaylistProbe(), new FakeManager());
        Assert.True(vm.IsVideo);
        vm.SelectedKind = OutputKind.Audio;
        Assert.True(vm.IsAudio);
        Assert.False(vm.IsVideo);
    }

    [Fact]
    public async Task AddToQueue_AssemblesProcessingOptions_FromSelections()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        vm.Url = "https://u";
        vm.TrimStart = "0:05";
        vm.TrimEnd = "0:10";
        vm.PreciseCut = true;
        vm.EmbedSubtitles = true;
        vm.SubtitleLanguage = "es";
        vm.EmbedMetadata = false;
        vm.EmbedThumbnail = false;

        await vm.AddToQueueCommand.ExecuteAsync(null);

        var p = Assert.Single(mgr.Enqueued).Config.Processing;
        Assert.Equal(new TrimRange(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), p.Trim);
        Assert.True(p.PreciseCut);
        Assert.True(p.EmbedSubtitles);
        Assert.Equal("es", p.SubtitleLanguage);
        Assert.False(p.EmbedMetadata);
        Assert.False(p.EmbedThumbnail);
    }

    [Fact]
    public async Task AddToQueue_BlankTrim_ProducesNoTrim()
    {
        var mgr = new FakeManager();
        var vm = Vm(new FakePlaylistProbe(), mgr);
        vm.Url = "https://u";
        await vm.AddToQueueCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(mgr.Enqueued).Config.Processing.Trim);
    }

    [Fact]
    public void InvalidTrim_SetsHint_BlankClearsIt()
    {
        var vm = Vm(new FakePlaylistProbe(), new FakeManager());
        vm.TrimStart = "abc";
        vm.TrimEnd = "0:10";
        Assert.NotEqual(string.Empty, vm.TrimHint);
        vm.TrimStart = string.Empty;
        vm.TrimEnd = string.Empty;
        Assert.Equal(string.Empty, vm.TrimHint);
    }

    [Fact]
    public void EmbedDefaults_MetadataAndThumbnailOn()
    {
        var vm = Vm(new FakePlaylistProbe(), new FakeManager());
        Assert.True(vm.EmbedMetadata);
        Assert.True(vm.EmbedThumbnail);
    }
}
