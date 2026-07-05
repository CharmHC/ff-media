using FFMedia.Tools.YouTubeDownloader.Models;
using Xunit;

namespace FFMedia.Tests.YouTubeDownloader;

public class DownloadJobTests
{
    private static DownloadJob NewJob() =>
        new("https://x", "Title", DownloadConfig.Default, @"C:\out");

    [Fact]
    public void NewJob_StartsQueued_NotTerminal_WithIdentityFields()
    {
        var job = NewJob();
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.False(job.IsTerminal);
        Assert.Equal(0, job.Progress);
        Assert.Equal("https://x", job.Url);
        Assert.Equal("Title", job.Title);
        Assert.Equal(@"C:\out", job.OutputFolder);
        Assert.NotEqual(System.Guid.Empty, job.Id);
    }

    [Theory]
    [InlineData(JobStatus.Completed, true)]
    [InlineData(JobStatus.Canceled, true)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Queued, false)]
    [InlineData(JobStatus.Downloading, false)]
    [InlineData(JobStatus.Processing, false)]
    public void IsTerminal_TrueOnlyForFinishedStates(JobStatus status, bool expected)
    {
        var job = NewJob();
        job.Status = status;
        Assert.Equal(expected, job.IsTerminal);
    }

    [Fact]
    public void SettingProgress_RaisesPropertyChanged()
    {
        var job = NewJob();
        var raised = false;
        job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadJob.Progress)) raised = true; };
        job.Progress = 42;
        Assert.True(raised);
    }
}
