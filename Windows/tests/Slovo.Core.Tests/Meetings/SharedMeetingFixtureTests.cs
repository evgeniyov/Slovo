using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// The golden check shared with the macOS app (Tests/SlovoCoreTests/SharedMeetingFixtureTests.swift):
/// every <c>Shared/fixtures/meetings/&lt;name&gt;.meeting.json</c> must give, byte for byte, the
/// <c>transcript.md</c> the Swift code wrote next to it in each language, and the recovery
/// parser must read that document back into the same segments, speakers and moments. Dates
/// are shown in UTC+03:00 on both sides.
/// </summary>
public sealed class SharedMeetingFixtureTests
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");

    static string Folder => Path.Combine(AppContext.BaseDirectory, "fixtures", "meetings");

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in Directory.GetFiles(Folder, "*.meeting.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file)[..^".meeting.json".Length];
            data.Add(name, "en");
            data.Add(name, "ru");
        }
        return data;
    }

    [Fact]
    public void TheFixtureHoldsEveryKindOfMeeting()
    {
        var names = Directory.GetFiles(Folder, "*.meeting.json").Select(Path.GetFileName).ToList();
        Assert.True(names.Count >= 4, string.Join(", ", names));
        Assert.Contains("format1-call.meeting.json", names);
        Assert.Contains("format2-cowork.meeting.json", names);
        Assert.Contains("damaged.meeting.json", names);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MarkdownIsWhatTheMacAppWritesByteForByte(string name, string code)
    {
        var record = MeetingRecordFile.Decode(File.ReadAllBytes(Path.Combine(Folder, name + ".meeting.json")));
        var language = code == "ru" ? AppLanguage.Ru : AppLanguage.En;
        var expected = File.ReadAllBytes(Path.Combine(Folder, $"{name}.{code}.md"));

        var document = TranscriptMerger.Markdown(record, language, timeZone: Zone);

        Assert.Equal(Encoding.UTF8.GetString(expected), document);
        Assert.Equal(expected, Encoding.UTF8.GetBytes(document));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheDocumentReadsBackLikeOnTheMac(string name, string code)
    {
        var document = File.ReadAllText(Path.Combine(Folder, $"{name}.{code}.md"), Encoding.UTF8);
        var expected = JsonSerializer.Deserialize<ParsedFixture>(File.ReadAllBytes(Path.Combine(Folder, $"{name}.{code}.parsed.json")), SlovoJson.Options)!;

        var parsed = MeetingStore.ParseTranscript(document);

        Assert.Equal(expected.Title, parsed.Title);
        Assert.Equal(expected.Segments, parsed.Segments);
        Assert.Equal(expected.Speakers, parsed.Speakers);
        // Recovered moments get ids from a hash of their line, the same on both systems.
        Assert.Equal(expected.Highlights, parsed.Highlights);
    }

    sealed record ParsedFixture(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("segments")] List<TranscriptSegment> Segments,
        [property: JsonPropertyName("speakers")] List<MeetingSpeaker> Speakers,
        [property: JsonPropertyName("highlights")] List<MeetingHighlight> Highlights);
}
