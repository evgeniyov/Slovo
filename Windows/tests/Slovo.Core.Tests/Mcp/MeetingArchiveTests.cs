using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>MeetingArchive: what it reads, what it skips, and that it never changes the data folder.</summary>
public sealed class MeetingArchiveTests
{
    /// <summary>Every path under a folder with its size and dates.</summary>
    static Dictionary<string, string> Snapshot(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Append(root))
        {
            var info = new FileInfo(path);
            snapshot[Path.GetRelativePath(root, path)] = info.Exists
                ? $"{info.Length} {info.LastWriteTimeUtc.Ticks} {info.CreationTimeUtc.Ticks} {info.Attributes}"
                : $"folder {Directory.GetLastWriteTimeUtc(path).Ticks} {Directory.GetCreationTimeUtc(path).Ticks}";
        }
        return snapshot;
    }

    [Fact]
    public void TheSharedDataFolderDecodesAsIntended()
    {
        using var fixture = new McpFixture();
        var archive = new MeetingArchive(fixture.Paths);
        var meetings = archive.Meetings();
        Assert.Equal([
            McpFixture.CallId, McpFixture.CoworkId, McpFixture.StandupId, McpFixture.WaitingId, McpFixture.RecoveredId,
            McpFixture.ExtremeId, McpFixture.PlanningId, McpFixture.HostileId, McpFixture.NotesId, McpFixture.LongId, McpFixture.RetroId,
        ], meetings.Select(meeting => meeting.Record.Id));
        var planning = archive.Meeting(McpFixture.PlanningId)!;
        Assert.Equal(1, planning.Record.FormatVersion);
        Assert.Equal(["me", "others"], planning.Record.EffectiveSpeakers.Select(speaker => speaker.Id));
        // The dates fixture.json gives the copy, to the quarter second cursor fingerprints hash.
        Assert.Equal(811_166_400.25, Slovo.Core.Json.AppleDate.ToReferenceSeconds(archive.Meeting(McpFixture.LongId)!.RecordModified));
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 7, 0, 0, TimeSpan.Zero), archive.Meeting(McpFixture.RecoveredId)!.Record.CreatedAt);
        Assert.Null(archive.Meeting(McpFixture.BrokenId));
        Assert.NotNull(archive.UnreadableMeetingDate(McpFixture.BrokenId));
        Assert.Null(archive.UnreadableMeetingDate(McpFixture.CoworkId));
        Assert.Null(archive.UnreadableMeetingDate(Guid.NewGuid()));
    }

    [Fact]
    public void ReadingChangesNothingAndCachesUntilAFileChanges()
    {
        using var fixture = new McpFixture();
        fixture.WriteRaw("caff", "mic.caf", McpFixture.CallId);
        fixture.WriteRaw("{}", "meeting.corrupt-20260101-000000.json", McpFixture.CoworkId);
        Directory.CreateDirectory(Path.Combine(fixture.Paths.MeetingsFolder, "not-a-meeting"));
        File.WriteAllText(fixture.Paths.HistoryFile, "[]");
        var before = Snapshot(fixture.Base);

        var archive = new MeetingArchive(fixture.Paths);
        var first = archive.Meetings();
        Assert.Same(first.Single(meeting => meeting.Record.Id == McpFixture.CoworkId),
            archive.Meetings().Single(meeting => meeting.Record.Id == McpFixture.CoworkId));
        foreach (var id in first.Select(meeting => meeting.Record.Id).Append(McpFixture.BrokenId))
        {
            archive.Meeting(id);
            archive.UnreadableMeetingDate(id);
        }
        Assert.Equal(before, Snapshot(fixture.Base));

        var renamed = fixture.Record(McpFixture.CoworkId) with { Title = "Новое название" };
        fixture.Write(renamed);
        Assert.Equal("Новое название", archive.Meeting(McpFixture.CoworkId)!.Record.Title);
    }

    [Fact]
    public void HugeFilesAreSkippedAndATranscriptStandsIn()
    {
        using var fixture = new McpFixture(withData: false);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(fixture.MeetingFolder(id));
        using (var stream = File.Create(fixture.RecordPath(id)))
        {
            stream.SetLength(MeetingArchive.MaximumFileBytes + 1);
        }
        var archive = new MeetingArchive(fixture.Paths);
        Assert.Null(archive.Meeting(id));
        Assert.NotNull(archive.UnreadableMeetingDate(id));
    }

    [Fact]
    public void DeletedMeetingsAreForgotten()
    {
        using var fixture = new McpFixture();
        var archive = new MeetingArchive(fixture.Paths);
        Assert.NotNull(archive.Meeting(McpFixture.CoworkId));
        Directory.Delete(fixture.MeetingFolder(McpFixture.CoworkId), recursive: true);
        Assert.Null(archive.Meeting(McpFixture.CoworkId));
        Assert.DoesNotContain(archive.Meetings(), meeting => meeting.Record.Id == McpFixture.CoworkId);
    }

    [Fact]
    public void SymbolicLinksAreNotFollowed()
    {
        using var fixture = new McpFixture(withData: false);
        var outside = Path.Combine(fixture.Base, "outside");
        Directory.CreateDirectory(outside);
        var record = new MeetingRecord { Id = McpFixture.CoworkId, Title = "Снаружи", Status = MeetingStatus.Transcribed, HasAudio = false };
        File.WriteAllBytes(Path.Combine(outside, "meeting.json"), Slovo.Core.Json.SlovoJson.SerializeToUtf8(record));

        var linkedFolder = Guid.NewGuid();
        var linkedFile = Guid.NewGuid();
        try
        {
            Directory.CreateSymbolicLink(fixture.MeetingFolder(linkedFolder), outside);
            Directory.CreateDirectory(fixture.MeetingFolder(linkedFile));
            File.CreateSymbolicLink(fixture.RecordPath(linkedFile), Path.Combine(outside, "meeting.json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Windows without Developer Mode cannot make symbolic links; nothing to check then.
            Assert.Skip("Symbolic links are not available here: " + error.Message);
        }
        var archive = new MeetingArchive(fixture.Paths);
        Assert.Empty(archive.Meetings());
        Assert.Null(archive.Meeting(linkedFolder));
        Assert.Null(archive.Meeting(linkedFile));
    }

    [Fact]
    public void ReplacingTheRecordKeepsAnOpenReaderOnThePreviousVersion()
    {
        using var fixture = new McpFixture();
        var record = fixture.Record(McpFixture.CoworkId);
        var path = fixture.RecordPath(record.Id);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        Slovo.Core.Storage.AtomicFile.Write(path, Slovo.Core.Json.SlovoJson.SerializeToUtf8(record with { Title = "New title" }));

        using var previous = new MemoryStream();
        reader.CopyTo(previous);
        Assert.Equal(record.Title, MeetingArchive.DecodeRecord(previous.ToArray())!.Title);
        Assert.Equal("New title", fixture.Record(record.Id).Title);
    }

    [Fact]
    public async Task ReadsNeverFailWhileTheAppRewritesTheRecord()
    {
        MeetingRecord record;
        using (var source = new McpFixture())
        {
            record = source.Record(McpFixture.CoworkId);
        }
        using var fixture = new McpFixture(withData: false);
        fixture.Write(record);
        var variants = new[]
        {
            Slovo.Core.Json.SlovoJson.SerializeToUtf8(record with { Title = "Коворк A" }),
            Slovo.Core.Json.SlovoJson.SerializeToUtf8(record with
            {
                Title = "Коворк B",
                Segments = [.. record.Segments, .. Enumerable.Range(0, 50).Select(index => new TranscriptSegment
                {
                    Start = 2_000 + index * 10, End = 2_005 + index * 10, Speaker = Speaker.Me, Text = $"Дополнительная строка {index}.", SpeakerId = "s3",
                })],
            }),
        };
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                Slovo.Core.Storage.AtomicFile.Write(fixture.RecordPath(record.Id), variants[index % 2]);
            }
        }, TestContext.Current.CancellationToken);
        var archive = new MeetingArchive(fixture.Paths);
        var iterations = 0;
        while (!writer.IsCompleted || iterations < 20)
        {
            var meeting = Assert.Single(archive.Meetings());
            Assert.StartsWith("Коворк", meeting.Record.Title, StringComparison.Ordinal);
            iterations++;
        }
        await writer;
    }
}
