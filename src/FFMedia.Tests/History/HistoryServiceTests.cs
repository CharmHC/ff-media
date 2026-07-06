using System;
using System.IO;
using System.Linq;
using FFMedia.Core.History;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FFMedia.Tests.History;

public class HistoryServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ffmedia-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HistoryEntry Entry(string title) =>
        new(title, "https://u/" + title, @"C:\out\" + title + ".mp4", "Mp4 P1080", DateTimeOffset.Now, "Completed");

    [Fact]
    public void Append_ThenQuery_ReturnsEntryNewestFirst()
    {
        var svc = new HistoryService(TempDir(), NullLogger<HistoryService>.Instance);

        svc.Append(Entry("A"));
        svc.Append(Entry("B"));

        Assert.Equal(new[] { "B", "A" }, svc.Query().Select(e => e.Title));
    }

    [Fact]
    public void Append_RaisesChanged()
    {
        var svc = new HistoryService(TempDir(), NullLogger<HistoryService>.Instance);
        var raised = 0;
        svc.Changed += (_, _) => raised++;

        svc.Append(Entry("A"));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void History_PersistsAcrossReload()
    {
        var dir = TempDir();
        new HistoryService(dir, NullLogger<HistoryService>.Instance).Append(Entry("A"));

        var reloaded = new HistoryService(dir, NullLogger<HistoryService>.Instance);

        Assert.Equal("A", Assert.Single(reloaded.Query()).Title);
    }

    [Fact]
    public void Clear_EmptiesAndPersists()
    {
        var dir = TempDir();
        var svc = new HistoryService(dir, NullLogger<HistoryService>.Instance);
        svc.Append(Entry("A"));

        svc.Clear();

        Assert.Empty(svc.Query());
        Assert.Empty(new HistoryService(dir, NullLogger<HistoryService>.Instance).Query());
    }

    [Fact]
    public void MissingFile_QueryIsEmpty()
    {
        var svc = new HistoryService(TempDir(), NullLogger<HistoryService>.Instance);

        Assert.Empty(svc.Query());
    }

    [Fact]
    public void Append_IsThreadSafe_UnderConcurrency()
    {
        var svc = new HistoryService(TempDir(), NullLogger<HistoryService>.Instance);

        System.Threading.Tasks.Parallel.For(0, 100, i => svc.Append(Entry("E" + i)));

        Assert.Equal(100, svc.Query().Count);
    }
}
