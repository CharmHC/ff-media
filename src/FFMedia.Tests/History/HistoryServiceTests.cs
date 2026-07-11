using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    // --- HistorySource back-compat -------------------------------------------------------------
    // history.json already exists on real disks, written before merges were a thing: it has no
    // "Source" property at all. If that file failed to deserialize, JsonStore would quarantine it
    // to .bak and hand back an empty document — i.e. the user's history would silently vanish.

    /// <summary>A real, verbatim pre-merge history.json (the exact shape HistoryService wrote).</summary>
    private const string LegacyHistoryJson = """
    {
      "Version": 1,
      "Entries": [
        {
          "Title": "Some Video",
          "Url": "https://example.com/x",
          "OutputPath": "C:\\out\\x.mp4",
          "Format": "Mp4 P1080",
          "Timestamp": "2026-07-01T10:00:00+00:00",
          "Status": "Completed"
        }
      ]
    }
    """;

    [Fact]
    public void LegacyHistoryFile_WithNoSourceProperty_LoadsAsDownload_AndIsNotQuarantined()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "history.json");
        File.WriteAllText(path, LegacyHistoryJson);

        var svc = new HistoryService(dir, NullLogger<HistoryService>.Instance);

        var entry = Assert.Single(svc.Query());
        Assert.Equal(
            new HistoryEntry(
                "Some Video",
                "https://example.com/x",
                @"C:\out\x.mp4",
                "Mp4 P1080",
                new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
                "Completed",
                HistorySource.Download),
            entry);
        Assert.Equal(HistorySource.Download, entry.Source);
        // The whole failure mode we are guarding: a deserialization throw would have moved the
        // user's history aside and returned an empty document instead.
        Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    // A Source written by a FUTURE build of FFMedia that has a tool this build knows nothing about.
    [InlineData("\"Sideloaded\"")]
    [InlineData("null")]
    [InlineData("7")]
    public void UnrecognizedSource_DegradesTheField_AndNeverDestroysTheFile(string sourceLiteral)
    {
        // The failure mode this guards is wildly out of proportion to the fault: the strict enum
        // converter THROWS on a name it does not know, JsonStore quarantines the unparseable file
        // to .bak and returns an empty document — so ONE unrecognized value silently erases the
        // user's ENTIRE history. FFMedia is a toolbox that gains tools; the day a third
        // HistorySource ships, every older build would wipe the history of anyone who ran both.
        // A row whose origin we cannot name is still a row worth keeping.
        var dir = TempDir();
        var path = Path.Combine(dir, "history.json");
        File.WriteAllText(path, $$"""
        {
          "Version": 1,
          "Entries": [
            {
              "Title": "From the future",
              "Url": "",
              "OutputPath": "C:\\out\\x.mp4",
              "Format": "Mp4 P1080",
              "Timestamp": "2026-07-01T10:00:00+00:00",
              "Status": "Completed",
              "Source": {{sourceLiteral}}
            }
          ]
        }
        """);

        var svc = new HistoryService(dir, NullLogger<HistoryService>.Instance);

        var entry = Assert.Single(svc.Query());
        Assert.Equal("From the future", entry.Title);   // the row SURVIVED
        Assert.Equal(HistorySource.Download, entry.Source); // only the unknown field degraded
        Assert.False(File.Exists(path + ".bak"));       // and the file was not quarantined
    }

    [Fact]
    public void KnownSource_IsStillReadExactly_TheToleranceIsNotAWildcard()
    {
        // The tolerant converter must not have become a converter that ignores its input.
        var dir = TempDir();
        var svc = new HistoryService(dir, NullLogger<HistoryService>.Instance);
        svc.Append(new HistoryEntry(
            "holiday.mp4", "", @"C:\out\holiday.mp4", "Mp4 1920x1080",
            DateTimeOffset.UnixEpoch, "Completed", HistorySource.Merge));

        var reloaded = new HistoryService(dir, NullLogger<HistoryService>.Instance);

        Assert.Equal(HistorySource.Merge, Assert.Single(reloaded.Query()).Source);
    }

    [Fact]
    public void HistoryEntry_OmittedSource_DeserializesToDownload_NotAnUninitializedValue()
    {
        // Pins the System.Text.Json behaviour the default relies on: an optional positional
        // record parameter's declared default is used when the JSON property is absent.
        const string json = """
        { "Title": "T", "Url": "U", "OutputPath": null, "Format": "F",
          "Timestamp": "2026-07-01T10:00:00+00:00", "Status": "Completed" }
        """;

        var entry = JsonSerializer.Deserialize<HistoryEntry>(json)!;

        Assert.Equal(HistorySource.Download, entry.Source);
        Assert.Equal(0, (int)entry.Source); // Download is the zero value, so this holds either way…
    }

    [Fact]
    public void HistoryEntry_OmittedSource_HonoursANonZeroDeclaredDefault()
    {
        // …so prove the default is genuinely honoured (not merely default(T)) with a probe type
        // whose declared default is the NON-zero enum member. If STJ ignored declared defaults,
        // this would come back as Download.
        const string json = """{ "Title": "T" }""";

        var probe = JsonSerializer.Deserialize<SourceDefaultProbe>(json)!;

        Assert.Equal(HistorySource.Merge, probe.Source);
    }

    private sealed record SourceDefaultProbe(string Title, HistorySource Source = HistorySource.Merge);

    [Fact]
    public void MergeEntry_PersistsSourceByName_AndSurvivesAReload()
    {
        var dir = TempDir();
        var merge = new HistoryEntry(
            "holiday.mp4", "", @"C:\out\holiday.mp4", "MP4 · 1920x1080 · 30 fps",
            DateTimeOffset.UnixEpoch, "Completed", HistorySource.Merge);

        new HistoryService(dir, NullLogger<HistoryService>.Instance).Append(merge);

        // On disk the enum must be a readable name, not a brittle integer.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "history.json")));
        var written = doc.RootElement.GetProperty("Entries")[0];
        Assert.Equal("Merge", written.GetProperty("Source").GetString());
        Assert.Equal("", written.GetProperty("Url").GetString());

        var reloaded = Assert.Single(new HistoryService(dir, NullLogger<HistoryService>.Instance).Query());
        Assert.Equal(merge, reloaded);
    }
}
