using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>Resources and prompts: strict URIs, size limits, and Claude's notes kept out of the transcript.</summary>
public sealed class McpResourcesPromptsTests : IDisposable
{
    readonly McpFixture fixture = new();
    readonly McpHandler handler;

    public McpResourcesPromptsTests() => handler = fixture.Handler();

    public void Dispose() => fixture.Dispose();

    string ReadText(string uri) =>
        McpClient.Result(handler, "resources/read", JsonValue.Object(("uri", uri)))["contents"]!.ArrayValue![0]["text"]!.StringValue!;

    static string TranscriptUri(Guid id) => $"slovo://meetings/{id:D}/transcript.md";

    [Fact]
    public void ResourceUrisAreParsedStrictly()
    {
        var cowork = McpFixture.CoworkId.ToString("D");
        string[] invalid =
        [
            "slovo://meetings/", "slovo://meetings/latest", "slovo://meetings/latest/transcript.md?x=1",
            "slovo://meetings/latest/transcript.md#top", "slovo://Meetings/latest/transcript.md",
            "SLOVO://meetings/latest/transcript.md", "slovo://meetings/../transcript.md", "slovo://meetings/transcript.md",
            "slovo://meetings/3f2a9c1e/transcript.md", "slovo://meetings/latest/latest/transcript.md",
            $"slovo://meetings/{cowork}/transcript.md/", $"slovo://meetings/{cowork}/meeting.json",
            "slovo://meetings/%33f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11/transcript.md", "file:///etc/passwd",
            "slovo://meetings/../../history.json", " slovo://meetings", "slovo://meetings/{meeting_id}/transcript.md",
            TranscriptUri(McpFixture.StandupId), TranscriptUri(McpFixture.CallId), TranscriptUri(McpFixture.WaitingId),
            TranscriptUri(McpFixture.BrokenId),
        ];
        foreach (var uri in invalid)
        {
            var reply = McpClient.Send(handler, McpClient.Request("resources/read", JsonValue.Object(("uri", uri))));
            Assert.Equal(-32002, McpClient.ErrorCode(reply));
            Assert.Equal(JsonValue.Str(uri), reply!["error"]!["data"]!["uri"]);
        }
        Assert.StartsWith("Slovo meeting «Коворк 15 сентября»",
            ReadText($"slovo://meetings/{McpFixture.CoworkId.ToUpperString()}/transcript.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongDocumentIsCutOnALineAt100000Characters()
    {
        var record = McpToolsTests.LongRecord();
        fixture.Write(record);
        var id = McpFixture.LongId.ToString("D");
        var text = ReadText(TranscriptUri(McpFixture.LongId));
        Assert.True(text.Length <= 100_000);
        var tail = $"</transcript>\n\nTruncated. Use the get_transcript tool with meeting_id {id} to read the rest.\n";
        Assert.EndsWith(tail, text, StringComparison.Ordinal);
        const string open = "<transcript source=\"slovo\" untrusted=\"true\">\n";
        var kept = text[(text.IndexOf(open, StringComparison.Ordinal) + open.Length)..^tail.Length];
        var markdown = TranscriptMerger.Markdown(record, AppLanguage.En, timeZone: McpFixture.TimeZone);
        Assert.StartsWith(kept, markdown, StringComparison.Ordinal);
        Assert.EndsWith("\n", kept, StringComparison.Ordinal);
        Assert.True(kept.Length > 50_000);

        // Too long to embed in a prompt: the prompt says how to page instead.
        var prompt = McpClient.Result(handler, "prompts/get", JsonValue.Object(("name", "meeting_summary"),
            ("arguments", JsonValue.Object(("meeting_id", id), ("detail", "full")))));
        var attachment = prompt["messages"]!.ArrayValue![1]["content"]!;
        Assert.Equal(JsonValue.Str("text"), attachment["type"]);
        Assert.Equal(JsonValue.Str(McpClient.Message("integration.prompt.paging").Replace("{meeting_id}", id, StringComparison.Ordinal)),
            attachment["text"]);
    }

    /// <summary>
    /// What Claude wrote is not speech, and the wrapper's notice says its contents are. So the
    /// notes stay out of the document altogether, and only a line above the notice says they exist.
    /// </summary>
    [Fact]
    public void ClaudesOwnNotesAreNeverServedAsTranscript()
    {
        var notes = new MeetingNotes("Договорились о сроках прототипа.", ["Прототип показываем в пятницу."], ["Аня собирает прототип."],
            "Claude", DateTimeOffset.Parse("2026-09-15T17:00:00+03:00", System.Globalization.CultureInfo.InvariantCulture));
        var record = fixture.Record(McpFixture.CoworkId) with { ClaudeNotes = notes };
        fixture.Write(record);
        var spoken = TranscriptMerger.Markdown(record with { ClaudeNotes = null }, AppLanguage.En, timeZone: McpFixture.TimeZone);
        string[] claudeText = [notes.Summary, .. notes.Decisions, .. notes.Tasks];

        var document = ReadText(TranscriptUri(McpFixture.CoworkId));
        foreach (var text in claudeText)
        {
            Assert.DoesNotContain(text, document, StringComparison.Ordinal);
        }
        Assert.Contains("<transcript source=\"slovo\" untrusted=\"true\">\n" + spoken + "</transcript>", document, StringComparison.Ordinal);
        Assert.Contains(McpText.ClaudeNotesLine("Claude", "2026-09-15T17:00:00+03:00") + "\n" + McpText.TranscriptNotice, document,
            StringComparison.Ordinal);
        foreach (var name in new[] { "meeting_tasks", "meeting_summary" })
        {
            var prompt = McpClient.Result(handler, "prompts/get", JsonValue.Object(("name", name)));
            Assert.Equal(JsonValue.Str(document), prompt["messages"]!.ArrayValue![1]["content"]!["resource"]!["text"]);
        }
        var page = McpClient.Tool(handler, "get_transcript", JsonValue.Object(("meeting_id", "latest"), ("max_chars", 8000)));
        foreach (var text in claudeText)
        {
            Assert.DoesNotContain(text, page.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ResourcesListShowsTranscribedMeetingsWithTheirFileDates()
    {
        var resources = McpClient.Result(handler, "resources/list")["resources"]!.ArrayValue!;
        Assert.Equal(
            ["slovo://meetings", "slovo://meetings/latest/transcript.md", TranscriptUri(McpFixture.CoworkId), TranscriptUri(McpFixture.RecoveredId),
             TranscriptUri(McpFixture.ExtremeId), TranscriptUri(McpFixture.PlanningId), TranscriptUri(McpFixture.HostileId),
             TranscriptUri(McpFixture.NotesId), TranscriptUri(McpFixture.LongId), TranscriptUri(McpFixture.RetroId)],
            resources.Select(resource => resource["uri"]!.StringValue!));
        var cowork = resources[2];
        Assert.Equal(JsonValue.Str("transcript-3f2a9c1e"), cowork["name"]);
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(fixture.RecordPath(McpFixture.CoworkId)), TimeSpan.Zero);
        Assert.Equal(JsonValue.Str(new McpDates(McpFixture.Now, McpFixture.TimeZone).Iso(modified)), cowork["annotations"]!["lastModified"]);
    }
}
