using System.Text.Json;
using Slovo.Core.Json;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Model;

public class MeetingRecordJsonTests
{
    const string FormatOne = """
    {
      "id" : "E621E1F8-C36C-495A-93FC-0C247A3E6E5F",
      "title" : "Коворк",
      "createdAt" : 780000000.5,
      "duration" : 125.5,
      "status" : "transcribed",
      "hasSystemAudio" : true,
      "hasAudio" : false,
      "engineID" : "gigaam-v3-ctc",
      "segments" : [
        { "start" : 0, "end" : 4.5, "speaker" : "me", "text" : "Привет" },
        { "start" : 5, "end" : 9, "speaker" : "others", "text" : "Hello" }
      ]
    }
    """;

    [Fact]
    public void ReadsFormatOneFromTheMacApp()
    {
        var record = JsonSerializer.Deserialize<MeetingRecord>(FormatOne, SlovoJson.Options)!;

        Assert.Equal(Guid.Parse("E621E1F8-C36C-495A-93FC-0C247A3E6E5F"), record.Id);
        Assert.Equal("Коворк", record.Title);
        Assert.Equal(AppleDate.FromReferenceSeconds(780000000.5), record.CreatedAt);
        Assert.Equal(MeetingStatus.Transcribed, record.Status);
        Assert.Equal(EngineId.GigaamCtc, record.EngineId);
        Assert.Equal(1, record.FormatVersion);
        Assert.Equal(2, record.Segments.Count);
        Assert.Empty(record.Speakers);
        Assert.Equal(["me", "others"], record.EffectiveSpeakers.Select(s => s.Id));
    }

    [Fact]
    public void UnknownValuesFallBackInsteadOfFailing()
    {
        const string json = """
        {
          "id": "E621E1F8-C36C-495A-93FC-0C247A3E6E5F", "title": "x", "createdAt": 1, "duration": -5,
          "status": "somethingNew", "hasSystemAudio": false, "hasAudio": true, "engineID": "future-engine",
          "segments": [],
          "speakers": [
            { "id": "s1", "track": "microphone", "number": 1, "colorIndex": 13, "isMe": true },
            { "id": "s01", "track": "microphone" },
            { "id": "s2", "track": "somewhere" },
            { "id": "s3", "track": "system", "isMe": true },
            { "id": "s1", "track": "system" }
          ],
          "highlights": [
            { "id": "0F2A3C44-1D2B-4E6F-8A9B-0C1D2E3F4A5B", "time": 12.5, "source": "brandNew" },
            { "id": "not-a-uuid", "time": 1 }
          ],
          "claudeNotes": { "summary": "   ", "decisions": [] },
          "source": { "origin": "watch" }
        }
        """;

        var record = JsonSerializer.Deserialize<MeetingRecord>(json, SlovoJson.Options)!;

        Assert.Equal(MeetingStatus.Recorded, record.Status);
        Assert.Null(record.EngineId);
        Assert.Equal(0, record.Duration);
        Assert.Equal(["s1", "s3"], record.Speakers.Select(s => s.Id));
        Assert.Equal(5, record.Speakers[0].ColorIndex);
        Assert.True(record.Speakers[0].IsMe);
        Assert.False(record.Speakers[1].IsMe);
        Assert.Single(record.Highlights);
        Assert.Equal(HighlightSource.Transcript, record.Highlights[0].Source);
        Assert.Null(record.ClaudeNotes);
        Assert.Null(record.Source);
    }

    [Fact]
    public void RoundTripsWithSortedKeysAndUpperCaseIds()
    {
        var record = new MeetingRecord
        {
            Id = Guid.Parse("e621e1f8-c36c-495a-93fc-0c247a3e6e5f"),
            Title = "Встреча",
            CreatedAt = AppleDate.FromReferenceSeconds(800_000_000),
            Duration = 60,
            Status = MeetingStatus.Transcribed,
            HasAudio = true,
            Segments = [new TranscriptSegment { Start = 1, End = 2, Speaker = Speaker.Me, Text = "да", SpeakerId = "s1" }],
            Speakers = [new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0, DisplayName = "Аня" }],
            ClaudeNotes = new MeetingNotes("Итоги\nвстречи", ["Решили"], [], "Claude", AppleDate.FromReferenceSeconds(800_000_100)),
        };

        var text = SlovoJson.Serialize(record);
        var back = JsonSerializer.Deserialize<MeetingRecord>(text, SlovoJson.Options)!;

        Assert.Contains("\"id\": \"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\"", text);
        Assert.Contains("\"createdAt\": 800000000", text);
        Assert.True(text.IndexOf("\"claudeNotes\"", StringComparison.Ordinal) < text.IndexOf("\"createdAt\"", StringComparison.Ordinal));
        Assert.Equal("Итоги встречи", back.ClaudeNotes!.Summary);
        Assert.Equal(record, back);
    }
}
