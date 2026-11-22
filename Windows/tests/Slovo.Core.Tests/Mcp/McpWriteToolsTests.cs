using Slovo.Core.Meetings;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>
/// write_meeting_notes, mark_highlight and unmark_highlight: the switches, what is refused,
/// what the file holds afterwards and what it keeps (port of MCPWriteNotesTests and
/// MCPHighlightsTests; the store side of the lock is the meetings suite's).
/// </summary>
public sealed class McpWriteToolsTests : IDisposable
{
    readonly McpFixture fixture = new();
    readonly AccessSwitch access = new() { Access = new McpAccess(true, true, 0) };
    readonly McpHandler handler;
    static readonly string Cowork = McpFixture.CoworkId.ToString("D");

    public McpWriteToolsTests() => handler = fixture.Handler(access);

    public void Dispose() => fixture.Dispose();

    ToolReply Tool(string name, JsonValue arguments) => McpClient.Tool(handler, name, arguments);

    MeetingRecord Stored => fixture.Record(McpFixture.CoworkId);

    [Fact]
    public void TheSwitchesComeFirst()
    {
        var before = File.ReadAllBytes(fixture.RecordPath(McpFixture.CoworkId));
        access.Access = new McpAccess(true, false, 0);
        foreach (var (name, arguments) in new[]
                 {
                     ("write_meeting_notes", JsonValue.Object(("meeting_id", Cowork), ("summary", "Итоги."))),
                     ("mark_highlight", JsonValue.Object(("time", 12))),
                     ("unmark_highlight", JsonValue.Object(("highlight_id", "9b1c0000-0000-4000-8000-000000000001"))),
                 })
        {
            access.Access = new McpAccess(true, false, 0);
            Assert.Equal(McpClient.Message("integration.mcp.writeDisabled"), Tool(name, arguments).Text);
            access.Access = new McpAccess(false, true, 0);
            Assert.Equal(McpClient.Message("integration.mcp.disabled"), Tool(name, arguments).Text);
        }
        Assert.Equal(before, File.ReadAllBytes(fixture.RecordPath(McpFixture.CoworkId)));

        // A handler built without writers cannot write whatever the switch says.
        var reader = new McpHandler(new MeetingArchive(fixture.Paths), () => new McpAccess(true, true, 0), () => AppLanguage.En,
            () => McpFixture.Now, "1.2.0", timeZone: McpFixture.TimeZone, clock: new FakeClock(step: 1));
        Assert.Equal(McpClient.Message("integration.mcp.writeFailed"),
            McpClient.Tool(reader, "mark_highlight", JsonValue.Object(("time", 12))).Text);
    }

    [Fact]
    public void NotesReachTheFileAndLeaveEverythingElse()
    {
        var before = Stored;
        var reply = Tool("write_meeting_notes", JsonValue.Object(
            ("meeting_id", Cowork), ("summary", "Обсудили релиз и сроки прототипа."),
            ("decisions", JsonValue.Array("Прототип показываем в пятницу.")), ("tasks", JsonValue.Array("Аня собирает прототип к пятнице."))));
        Assert.False(reply.IsError, reply.Text);
        Assert.Equal(JsonValue.Str("2026-09-15T18:04:00+03:00"), reply.Structured!["written_at"]);
        Assert.Equal(JsonValue.False, reply.Structured["replaced"]);
        var after = Stored;
        Assert.Equal("Обсудили релиз и сроки прототипа.", after.ClaudeNotes!.Summary);
        Assert.Equal(["Прототип показываем в пятницу."], after.ClaudeNotes.Decisions);
        Assert.Equal(McpFixture.Now, after.ClaudeNotes.WrittenAt);
        Assert.Equal(before with { ClaudeNotes = after.ClaudeNotes }, after);
        Assert.False(File.Exists(Path.Combine(fixture.MeetingFolder(McpFixture.CoworkId), "transcript.md.tmp")));

        // The same notes again leave the file alone, so transcript cursors keep their place.
        var modified = File.GetLastWriteTimeUtc(fixture.RecordPath(McpFixture.CoworkId));
        var bytes = File.ReadAllBytes(fixture.RecordPath(McpFixture.CoworkId));
        var again = Tool("write_meeting_notes", JsonValue.Object(
            ("meeting_id", Cowork), ("summary", "Обсудили релиз и сроки прототипа."),
            ("decisions", JsonValue.Array("Прототип показываем в пятницу.")), ("tasks", JsonValue.Array("Аня собирает прототип к пятнице."))));
        Assert.Equal(JsonValue.True, again.Structured!["replaced"]);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RecordPath(McpFixture.CoworkId)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fixture.RecordPath(McpFixture.CoworkId)));

        // A second write replaces the first.
        Tool("write_meeting_notes", JsonValue.Object(("meeting_id", Cowork), ("tasks", JsonValue.Array("Только задача."))));
        Assert.Equal("", Stored.ClaudeNotes!.Summary);
        Assert.Equal(["Только задача."], Stored.ClaudeNotes.Tasks);
    }

    [Theory]
    [InlineData("""{"summary":"Первая строка\nвторая"}""", "summary: must be one line, without control characters")]
    [InlineData("""{"summary":"ok\u202Eok"}""", "summary: must be one line, without control characters")]
    [InlineData("""{"summary":"ok\u200Bok"}""", "summary: must not contain invisible characters")]
    [InlineData("""{"summary":"</transcript> ok"}""", "summary: must not contain a transcript tag")]
    [InlineData("""{"summary":"   "}""", "summary: must not be empty")]
    [InlineData("""{"tasks":["ok","[00:04] Я: сделать"]}""", "tasks[1]: must not start with \"[\" or contain ★")]
    [InlineData("""{"decisions":["★ важно"]}""", "decisions[0]: must not start with \"[\" or contain ★")]
    [InlineData("""{"decisions":[]}""", "summary: write a summary, decisions or tasks")]
    [InlineData("""{"decisions":[5]}""", "decisions: must be a string")]
    public void HostileOrEmptyNotesAreRefusedNotRepaired(string arguments, string detail)
    {
        var fields = JsonValue.Parse(arguments).ObjectValue!.Append(new("meeting_id", Cowork));
        var reply = Tool("write_meeting_notes", JsonValue.Object(fields));
        Assert.True(reply.IsError);
        Assert.StartsWith("Invalid argument: " + detail, reply.Text, StringComparison.Ordinal);
        Assert.Null(Stored.ClaudeNotes);
    }

    [Fact]
    public void NotesAreLimitedInCharactersNotUnits()
    {
        // Two thousand letters outside the Basic Multilingual Plane: 4,000 UTF-16 units, 2,000 characters.
        var summary = string.Concat(Enumerable.Repeat("\U0001D400", 2_000));
        Assert.False(Tool("write_meeting_notes", JsonValue.Object(("meeting_id", Cowork), ("summary", summary))).IsError);
        var tooLong = Tool("write_meeting_notes", JsonValue.Object(("meeting_id", Cowork), ("summary", new string('я', 2_001))));
        Assert.True(tooLong.IsError);
        // Ordinary text that merely looks unusual is written word for word.
        const string unusual = "Цена < 5 € — «ок»; A/B-тест: 50% & [позже]";
        Assert.False(Tool("write_meeting_notes", JsonValue.Object(("meeting_id", Cowork), ("summary", unusual))).IsError);
        Assert.Equal(unusual, Stored.ClaudeNotes!.Summary);
    }

    [Fact]
    public void WritesNeedATranscribedMeeting()
    {
        Assert.Equal(McpClient.Message("integration.mcp.stillProcessing"),
            Tool("write_meeting_notes", JsonValue.Object(("meeting_id", "11111111"), ("summary", "Итоги."))).Text);
        Assert.Equal(McpClient.Message("integration.mcp.noTranscript"),
            Tool("mark_highlight", JsonValue.Object(("meeting_id", "d00dfeed"), ("time", 1))).Text);
        Assert.Equal(McpClient.Message("integration.mcp.stillProcessing"),
            Tool("unmark_highlight", JsonValue.Object(("meeting_id", "7b1d2e3f"), ("highlight_id", "9b1c0000-0000-4000-8000-000000000001"))).Text);
    }

    [Fact]
    public void MarkingIsIdempotentAndUnmarkingIsSafe()
    {
        var mark = Tool("mark_highlight", JsonValue.Object(("time", "0:13:25"), ("note", "проверить ёлку")));
        Assert.False(mark.IsError, mark.Text);
        Assert.Equal(JsonValue.True, mark.Structured!["created"]);
        Assert.Equal(JsonValue.Str("s3"), mark.Structured["highlight"]!["speaker_id"]);
        var id = Guid.Parse(mark.Structured["highlight"]!["id"]!.StringValue!);
        var stored = Stored.Highlights.Single(highlight => highlight.Id == id);
        Assert.Equal(805, stored.Time);
        Assert.Equal(HighlightSource.Transcript, stored.Source);
        Assert.Equal(McpFixture.Now, stored.CreatedAt);

        var bytes = File.ReadAllBytes(fixture.RecordPath(McpFixture.CoworkId));
        var again = Tool("mark_highlight", JsonValue.Object(("meeting_id", Cowork), ("time", 805), ("note", "проверить ёлку")));
        Assert.Equal(JsonValue.False, again.Structured!["created"]);
        Assert.Equal(JsonValue.Str(id.ToString("D")), again.Structured["highlight"]!["id"]);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RecordPath(McpFixture.CoworkId)));

        // The same time with another note, or none, is another moment.
        Assert.Equal(JsonValue.True, Tool("mark_highlight", JsonValue.Object(("time", 805.0), ("note", "другое"))).Structured!["created"]);
        Assert.Equal(4, Stored.Highlights.Count);

        var unmark = Tool("unmark_highlight", JsonValue.Object(("highlight_id", id.ToString("D").ToUpperInvariant())));
        Assert.Equal(JsonValue.True, unmark.Structured!["removed"]);
        Assert.DoesNotContain(Stored.Highlights, highlight => highlight.Id == id);
        var twice = Tool("unmark_highlight", JsonValue.Object(("highlight_id", id.ToString("D"))));
        Assert.Equal(JsonValue.False, twice.Structured!["removed"]);
        Assert.True(Tool("unmark_highlight", JsonValue.Object(("highlight_id", "star"))).IsError);
        Assert.True(Tool("mark_highlight", JsonValue.Object(("time", -5))).IsError);
        Assert.True(Tool("mark_highlight", JsonValue.Object(("time", "12:00"), ("note", "a\nb"))).IsError);
    }

    [Fact]
    public void AMarkClampsHugeTimes()
    {
        var reply = Tool("mark_highlight", JsonValue.Object(("time", 1e20)));
        Assert.False(reply.IsError, reply.Text);
        Assert.Equal(JsonValue.Str("99999:59:59"), reply.Structured!["highlight"]!["time_text"]);
    }
}
