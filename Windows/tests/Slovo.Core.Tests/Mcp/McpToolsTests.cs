using System.Globalization;
using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>
/// The tools against the shared data folder: paging invariants, cursors, size limits and
/// reference resolution (the exact replies are the golden exchanges' business).
/// </summary>
public sealed class McpToolsTests : IDisposable
{
    readonly McpFixture fixture = new();
    readonly AccessSwitch access = new();
    readonly McpHandler handler;

    public McpToolsTests() => handler = fixture.Handler(access);

    public void Dispose() => fixture.Dispose();

    ToolReply Tool(string name, JsonValue? arguments = null) => McpClient.Tool(handler, name, arguments);

    static string Lower(Guid id) => id.ToString("D");

    static List<string> Ids(ToolReply reply) =>
        reply.Structured?["meetings"]?.ArrayValue?.Select(meeting => meeting["id"]!.StringValue!).ToList() ?? [];

    /// <summary>400 short lines, one 38,000-character line with a 5,000-character word, and highlights (MCPToolsTests.longRecord).</summary>
    internal static MeetingRecord LongRecord(int highlightNoteLength = 0, int highlightCount = 10)
    {
        string[] speakerIds = ["s1", "s2", "s3"];
        var segments = new List<TranscriptSegment>();
        for (var index = 0; index < 400; index++)
        {
            var start = index * 12.0;
            var text = string.Join(" ", Enumerable.Range(0, 25).Select(word => $"слово{index}x{word}"));
            if (index == 200)
            {
                text = string.Join(" ", Enumerable.Range(0, 3_000).Select(word => $"длинное{word}")) + " " + new string('ы', 5_000) + " конец";
            }
            segments.Add(new TranscriptSegment { Start = start, End = start + 8, Speaker = Speaker.Me, Text = text, SpeakerId = speakerIds[index % 3] });
        }
        var highlights = Enumerable.Range(0, highlightCount).Select(index => new MeetingHighlight
        {
            Id = Guid.Parse($"20000000-0000-4000-8000-{index:D12}"),
            Time = index * 40 % 400 * 12 + 1,
            Note = highlightNoteLength > 0 ? new string('н', highlightNoteLength) : $"заметка {index}",
            Source = HighlightSource.Transcript,
            CreatedAt = DateTimeOffset.Parse("2026-09-01T12:00:00+03:00", CultureInfo.InvariantCulture),
        }).ToList();
        var speakers = speakerIds.Select((id, offset) => new MeetingSpeaker
        {
            Id = id, Track = MeetingTrack.Microphone, Number = offset + 1, ColorIndex = offset,
        }).ToList();
        return new MeetingRecord
        {
            Id = McpFixture.LongId, Title = "Длинная встреча", CreatedAt = DateTimeOffset.Parse("2026-09-01T10:00:00+03:00", CultureInfo.InvariantCulture),
            Duration = 4_800, Status = MeetingStatus.Transcribed, HasAudio = false, Segments = segments, Speakers = speakers,
            Highlights = highlights,
        };
    }

    /// <summary>The transcript lines as get_transcript should show them, built from TranscriptMerger directly.</summary>
    static List<string> ExpectedLines(MeetingRecord record, IReadOnlySet<string>? speakers = null, double? from = null, double? to = null)
    {
        var lines = TranscriptMerger.Lines(record);
        var anchored = new Dictionary<int, List<MeetingHighlight>>();
        foreach (var highlight in record.Highlights.OrderBy(h => h.Time).ThenBy(h => AppleDate.ToReferenceSeconds(h.CreatedAt))
                     .ThenBy(h => h.Id.ToUpperString(), StringComparer.Ordinal))
        {
            if (TranscriptMerger.HighlightLineIndex(highlight, lines) is { } index)
            {
                (anchored.TryGetValue(index, out var list) ? list : anchored[index] = []).Add(highlight);
            }
        }
        var result = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if ((speakers is not null && !speakers.Contains(line.SpeakerId)) || (from is { } f && line.Start < f) || (to is { } t && line.Start >= t))
            {
                continue;
            }
            var label = record.SpeakerWithId(line.SpeakerId) is { } speaker ? TranscriptMerger.SpeakerLabel(speaker, AppLanguage.En) : line.SpeakerId;
            var text = new StringBuilder($"[{TranscriptMerger.Timestamp(line.Start)}] " + (anchored.ContainsKey(index) ? "★ " : "") + label + ": " + line.Text);
            foreach (var highlight in anchored.GetValueOrDefault(index) ?? [])
            {
                if (highlight.Note is { Length: > 0 } note)
                {
                    text.Append("\n  ↳ note: ").Append(note);
                }
            }
            result.Add(text.ToString());
        }
        return result;
    }

    /// <summary>The text between the transcript tags of one page.</summary>
    static string? Body(string page)
    {
        const string open = "<transcript source=\"slovo\" untrusted=\"true\">\n";
        var start = page.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += open.Length;
        var end = page.IndexOf("</transcript>", start, StringComparison.Ordinal);
        var body = page[start..end];
        return body.EndsWith('\n') ? body[..^1] : body;
    }

    static string? NextCursor(string page) =>
        page.Split('\n').FirstOrDefault(line => line.StartsWith("next_cursor: ", StringComparison.Ordinal))?["next_cursor: ".Length..];

    /// <summary>Joins page bodies, continuing a cut line without a line break.</summary>
    static string Reassemble(IReadOnlyList<string> pages)
    {
        var text = new StringBuilder();
        for (var index = 0; index < pages.Count; index++)
        {
            var body = Body(pages[index]) ?? "";
            if (index > 0 && !pages[index].Contains(" continues from the previous page.", StringComparison.Ordinal))
            {
                text.Append('\n');
            }
            text.Append(body);
        }
        return text.ToString();
    }

    List<string> Pages(Dictionary<string, JsonValue> arguments, int limit = 1_000)
    {
        var pages = new List<string>();
        string? cursor = null;
        do
        {
            var paged = new Dictionary<string, JsonValue>(arguments);
            if (cursor is not null)
            {
                paged["cursor"] = cursor;
            }
            var reply = Tool("get_transcript", JsonValue.Object(paged));
            Assert.False(reply.IsError, reply.Text);
            pages.Add(reply.Text);
            cursor = NextCursor(reply.Text);
        } while (cursor is not null && pages.Count < limit);
        return pages;
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(3_500)]
    [InlineData(16_000)]
    [InlineData(48_000)]
    public void PagesConcatenateToTheWholeTranscript(int maxChars)
    {
        var record = LongRecord();
        fixture.Write(record);
        var pages = Pages(new() { ["meeting_id"] = Lower(McpFixture.LongId), ["max_chars"] = maxChars });
        Assert.True(pages.Count > 2);
        Assert.All(pages, page => Assert.True(page.Length <= maxChars));
        Assert.EndsWith("End of transcript.", pages[^1], StringComparison.Ordinal);
        Assert.Equal(string.Join("\n", ExpectedLines(record)), Reassemble(pages));
    }

    /// <summary>A 200-character title and a thousand characters of speaker names give way on the smallest pages.</summary>
    [Fact]
    public void ALongHeaderNeverPushesAPagePastMaxChars()
    {
        var speakers = Enumerable.Range(1, 29).Select(number => new MeetingSpeaker
        {
            Id = $"s{number}", Track = MeetingTrack.Microphone, Number = number,
            DisplayName = $"Участник номер {number} с очень длинным именем 👩\u200D💻 и фамилией",
        }).ToList();
        var segments = Enumerable.Range(0, 58).Select(index => new TranscriptSegment
        {
            Start = index * 10, End = index * 10 + 8, Speaker = Speaker.Me,
            Text = $"Реплика {index}: " + string.Concat(Enumerable.Repeat("слово ", 20)), SpeakerId = $"s{index % 29 + 1}",
        }).ToList();
        var record = new MeetingRecord
        {
            Id = McpFixture.LongId, Title = new string('Т', 200), CreatedAt = McpFixture.Now.AddDays(-14), Duration = 600,
            Status = MeetingStatus.Transcribed, HasAudio = false, Segments = segments, Speakers = speakers,
        };
        fixture.Write(record);
        var pages = Pages(new() { ["meeting_id"] = Lower(McpFixture.LongId), ["max_chars"] = 2_000 });
        Assert.True(pages.Count > 2);
        Assert.All(pages, page =>
        {
            Assert.True(page.Length <= 2_000);
            Assert.Contains("\nSpeakers: s", page, StringComparison.Ordinal);
        });
        Assert.Equal(string.Join("\n", ExpectedLines(record)), Reassemble(pages));
        var roomy = Tool("get_transcript", JsonValue.Object(("meeting_id", Lower(McpFixture.LongId)), ("max_chars", 48_000))).Text;
        Assert.True(Slovo.Core.Text.TextElements.Count(roomy.Split('\n').First(line => line.StartsWith("Speakers: ", StringComparison.Ordinal))) > 900);
    }

    [Fact]
    public void SpeakerAndTimeFiltersMatchTheLines()
    {
        var record = fixture.Record(McpFixture.CoworkId);
        var cases = new (JsonValue Arguments, List<string> Expected)[]
        {
            (JsonValue.Object(("meeting_id", "latest"), ("speaker_ids", JsonValue.Array("s2"))), ExpectedLines(record, speakers: new HashSet<string> { "s2" })),
            (JsonValue.Object(("meeting_id", "latest"), ("from", "12:00"), ("to", "13:30")), ExpectedLines(record, from: 720, to: 810)),
            (JsonValue.Object(("meeting_id", "latest"), ("from", 758), ("to", "0:13:20")), ExpectedLines(record, from: 758, to: 800)),
            (JsonValue.Object(("meeting_id", "latest"), ("from", "700.5"), ("speaker_ids", JsonValue.Array("s1", "s2"))),
                ExpectedLines(record, speakers: new HashSet<string> { "s1", "s2" }, from: 700.5)),
        };
        foreach (var (arguments, expected) in cases)
        {
            var reply = Tool("get_transcript", arguments);
            Assert.False(reply.IsError, reply.Text);
            Assert.NotEmpty(expected);
            Assert.Equal(string.Join("\n", expected), Body(reply.Text));
        }
    }

    [Fact]
    public void CursorsOnlyContinueTheirOwnArguments()
    {
        fixture.Write(LongRecord());
        var id = Lower(McpFixture.LongId);
        var first = Tool("get_transcript", JsonValue.Object(("meeting_id", id), ("max_chars", 2000)));
        var cursor = NextCursor(first.Text)!;
        var tampered = McpTranscriptCursor.Decode(cursor)! with { Index = 100_000 };
        var badCursor = McpClient.Message("integration.mcp.badCursor");
        foreach (var arguments in new[]
                 {
                     JsonValue.Object(("meeting_id", id), ("max_chars", 3000), ("cursor", cursor)),
                     JsonValue.Object(("meeting_id", id), ("cursor", cursor)),
                     JsonValue.Object(("meeting_id", "3f2a9c1e-5b"), ("max_chars", 2000), ("cursor", cursor)),
                     JsonValue.Object(("meeting_id", id), ("max_chars", 2000), ("speaker_ids", JsonValue.Array("s1")), ("cursor", cursor)),
                     JsonValue.Object(("meeting_id", id), ("max_chars", 2000), ("cursor", "not-a-cursor!")),
                     JsonValue.Object(("meeting_id", id), ("max_chars", 2000), ("cursor", McpBase64Url.Encode("{}"u8.ToArray()))),
                     JsonValue.Object(("meeting_id", id), ("max_chars", 2000), ("cursor", tampered.Encoded)),
                 })
        {
            var reply = Tool("get_transcript", arguments);
            Assert.True(reply.IsError, arguments.SerializedString);
            Assert.Equal(badCursor, reply.Text);
        }
        var second = Tool("get_transcript", JsonValue.Object(("meeting_id", id), ("max_chars", 2000), ("cursor", cursor)));
        Assert.False(second.IsError);
        Assert.DoesNotContain(McpText.MeetingChanged, second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedMeetingContinuesWithANote()
    {
        var record = LongRecord();
        fixture.Write(record);
        var id = Lower(McpFixture.LongId);
        var first = Tool("get_transcript", JsonValue.Object(("meeting_id", id), ("max_chars", 2000)));
        var cursorText = NextCursor(first.Text)!;
        var cursor = McpTranscriptCursor.Decode(cursorText)!;

        fixture.Write(record with { Title = "Длинная встреча, переименована" });
        File.SetLastWriteTimeUtc(fixture.RecordPath(McpFixture.LongId), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = Tool("get_transcript", JsonValue.Object(("meeting_id", id), ("max_chars", 2000), ("cursor", cursorText)));
        Assert.False(second.IsError);
        Assert.Contains(McpText.MeetingChanged, second.Text, StringComparison.Ordinal);
        Assert.Contains($"\nLines {cursor.Index + 1}–", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoResultIsLongerThan48000Characters()
    {
        fixture.Write(LongRecord(highlightNoteLength: 500, highlightCount: 400));
        var id = Lower(McpFixture.LongId);
        var meeting = Tool("get_meeting", JsonValue.Object(("meeting_id", id)));
        Assert.Equal(JsonValue.True, meeting.Structured!["highlights_truncated"]);
        Assert.Contains("list_highlights", meeting.Structured["note"]!.StringValue!, StringComparison.Ordinal);
        Assert.True(meeting.Structured["highlights"]!.ArrayValue!.Count > 10);
        Assert.Equal(JsonValue.True, Tool("list_highlights", JsonValue.Object(("meeting_id", id), ("limit", 200))).Structured!["truncated"]);
        var search = Tool("search_meetings", JsonValue.Object(("query", "слово"), ("limit", 50)));
        Assert.Equal(50, search.Structured!["hits"]!.ArrayValue!.Count);
        Assert.Equal(JsonValue.True, search.Structured["truncated"]);
        Assert.False(Tool("list_meetings", JsonValue.Object(("limit", 100))).IsError);
        // McpClient.Tool checks every text against 48,000 characters.
    }

    [Fact]
    public void TheAgeLimitHidesOlderMeetingsEverywhere()
    {
        var retro = Lower(McpFixture.RetroId);
        access.Access = new McpAccess(true, false, 30);
        Assert.DoesNotContain(retro, Ids(Tool("list_meetings")));
        var missing = Tool("get_meeting", JsonValue.Object(("meeting_id", retro)));
        Assert.True(missing.IsError);
        Assert.Equal($"No meeting matches “{retro}”.", missing.Text);
        Assert.Equal(JsonValue.Array(), Tool("search_meetings", JsonValue.Object(("query", "прошлого года"))).Structured!["hits"]);
        Assert.Equal(-32002, McpClient.ErrorCode(McpClient.Send(handler, McpClient.Request("resources/read",
            JsonValue.Object(("uri", $"slovo://meetings/{retro}/transcript.md"))))));
        Assert.Equal(-32602, McpClient.ErrorCode(McpClient.Send(handler, McpClient.Request("prompts/get",
            JsonValue.Object(("name", "meeting_tasks"), ("arguments", JsonValue.Object(("meeting_id", retro))))))));

        access.Access = new McpAccess(true, false, 7);
        var visible = Ids(Tool("list_meetings"));
        Assert.Contains(Lower(McpFixture.PlanningId), visible);
        Assert.DoesNotContain(retro, visible);
        access.Access = new McpAccess(true, false, 0);
        Assert.Contains(retro, Ids(Tool("list_meetings")));
    }

    [Fact]
    public void MeetingReferencesResolve()
    {
        var cowork = Lower(McpFixture.CoworkId);
        var ambiguous = Tool("get_meeting", JsonValue.Object(("meeting_id", "3f2a9c1e")));
        Assert.True(ambiguous.IsError);
        Assert.StartsWith(McpClient.Message("integration.mcp.ambiguous"), ambiguous.Text, StringComparison.Ordinal);
        Assert.Contains(Lower(McpFixture.PlanningId), ambiguous.Text, StringComparison.Ordinal);
        foreach (var reference in new[] { "3F2A9C1E-5B", "3f2a9c1e5b7d", McpFixture.CoworkId.ToUpperString(), cowork })
        {
            Assert.Equal(JsonValue.Str(cowork), Tool("get_meeting", JsonValue.Object(("meeting_id", reference))).Structured!["meeting"]!["id"]);
        }
        Assert.True(Tool("get_meeting", JsonValue.Object(("meeting_id", Guid.NewGuid().ToString("D")))).IsError);
        Assert.Equal(McpClient.Message("integration.mcp.damaged"), Tool("get_meeting", JsonValue.Object(("meeting_id", Lower(McpFixture.BrokenId)))).Text);

        // Without the meeting in progress, latest says nothing about it.
        Directory.Delete(fixture.MeetingFolder(McpFixture.CallId), recursive: true);
        var quiet = Tool("get_meeting", JsonValue.Object(("meeting_id", "latest")));
        Assert.Equal(JsonValue.Str(cowork), quiet.Structured!["meeting"]!["id"]);
        Assert.Null(quiet.Structured["note"]);
    }

    [Fact]
    public void AReadRacingTheAppsDeletionIsNotFound()
    {
        var id = Lower(McpFixture.CoworkId);
        Assert.False(Tool("get_meeting", JsonValue.Object(("meeting_id", id))).IsError);
        Directory.Delete(fixture.MeetingFolder(McpFixture.CoworkId), recursive: true);
        var missing = Tool("get_meeting", JsonValue.Object(("meeting_id", id)));
        Assert.True(missing.IsError);
        Assert.Equal($"No meeting matches “{id}”.", missing.Text);
    }
}
