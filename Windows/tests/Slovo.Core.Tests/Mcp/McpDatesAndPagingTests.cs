using System.Globalization;
using Slovo.Core.Json;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>Dates, search words, cursors and the pieces of paging that need no meeting.</summary>
public sealed class McpDatesAndPagingTests
{
    static readonly McpDates Dates = new(McpFixture.Now, McpFixture.TimeZone);

    static DateTimeOffset Date(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public void IsoDatesCarryTheLocalOffset()
    {
        Assert.Equal("2026-09-15T18:04:00+03:00", Dates.Iso(McpFixture.Now));
        Assert.Equal("2026-09-15T11:02:07+03:00", Dates.Iso(Date("2026-09-15T08:02:07.9Z")));
        Assert.Equal("Europe/Moscow", Dates.TimeZoneIdentifier);
        var newYork = new McpDates(McpFixture.Now, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal("2026-01-15T07:00:00-05:00", newYork.Iso(Date("2026-01-15T12:00:00Z")));
        Assert.Equal("America/New_York", newYork.TimeZoneIdentifier);
        // Windows names its zones differently; results carry the IANA name the Mac reports.
        Assert.Equal("Europe/Moscow", McpDates.IanaName(TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time")));
        var india = new McpDates(McpFixture.Now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        Assert.Equal("2026-01-15T17:30:00+05:30", india.Iso(Date("2026-01-15T12:00:00Z")));
    }

    [Theory]
    [InlineData("today", "2026-09-15T00:00:00+03:00")]
    [InlineData("yesterday", "2026-09-14T00:00:00+03:00")]
    [InlineData("1d", "2026-09-14T18:04:00+03:00")]
    [InlineData("365d", "2025-09-15T18:04:00+03:00")]
    [InlineData("2026-09-01", "2026-09-01T00:00:00+03:00")]
    [InlineData("2026-09-15T12:00", "2026-09-15T12:00:00+03:00")]
    [InlineData("2026-09-15T12:00:30", "2026-09-15T12:00:30+03:00")]
    [InlineData("2026-09-15T09:00:00Z", "2026-09-15T12:00:00+03:00")]
    [InlineData("2026-09-15T11:02:07+05:00", "2026-09-15T09:02:07+03:00")]
    [InlineData("2026-09-15T11:02-01:30", "2026-09-15T15:32:00+03:00")]
    [InlineData("2024-02-29", "2024-02-29T00:00:00+03:00")]
    public void AcceptedDates(string text, string expected)
    {
        var parsed = Dates.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal(expected, Dates.Iso(parsed.Value));
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("0d")]
    [InlineData("366d")]
    [InlineData("07d")]
    [InlineData("d")]
    [InlineData("2026-02-30")]
    [InlineData("2025-02-29")]
    [InlineData("2026-13-01")]
    [InlineData("2026-00-10")]
    [InlineData("2026-09-15T24:00")]
    [InlineData("2026-09-15T12:60")]
    [InlineData("2026-09-15T12:00:60")]
    [InlineData("2026-09-15T12:00+19:00")]
    [InlineData("2026-09-15T12:00+03")]
    [InlineData("2026-09-15 12:00")]
    [InlineData("2026-09-15T12:00Zx")]
    [InlineData("0000-01-01")]
    public void RefusedDates(string text) => Assert.Null(Dates.Parse(text));

    /// <summary>Days are added on the calendar, so a clock change keeps the local time of day.</summary>
    [Fact]
    public void DaysAcrossAClockChangeKeepTheTimeOfDay()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var dates = new McpDates(Date("2026-03-30T10:00:00+02:00"), berlin);
        Assert.Equal("2026-03-28T10:00:00+01:00", dates.Iso(dates.Parse("2d")!.Value));
        Assert.Equal("2026-03-29T00:00:00+01:00", dates.Iso(dates.Parse("yesterday")!.Value));
        // 02:30 does not exist that night; the calendar moves it past the gap.
        Assert.Equal("2026-03-29T03:30:00+02:00", dates.Iso(dates.Parse("2026-03-29T02:30")!.Value));
        Assert.Equal("2026-03-29T03:30:00+02:00", dates.Iso(dates.AddingDays(Date("2026-03-30T02:30:00+02:00"), -1)));
        // 02:30 happens twice in October: a date is the first; a day back from the second is the second.
        Assert.Equal("2026-10-25T02:30:00+02:00", dates.Iso(dates.Parse("2026-10-25T02:30")!.Value));
        Assert.Equal("2026-10-25T02:30:00+01:00", dates.Iso(dates.AddingDays(Date("2026-10-26T02:30:00+01:00"), -1)));
        // Santiago skips midnight when its clocks go forward; that day starts at 01:00.
        var santiago = new McpDates(Date("2026-09-06T12:00:00-03:00"), TimeZoneInfo.FindSystemTimeZoneById("America/Santiago"));
        Assert.Equal("2026-09-06T01:00:00-03:00", santiago.Iso(santiago.Parse("today")!.Value));
    }

    [Fact]
    public void LocalizedDatesAreWrittenAsOnTheMac()
    {
        var date = Date("2026-09-15T11:02:07+03:00");
        Assert.Equal("September 15, 2026 at 11:02\u202FAM", Dates.Localized(date, AppLanguage.En));
        Assert.Equal("15 сентября 2026\u202Fг. в 11:02", Dates.Localized(date, AppLanguage.Ru));
        Assert.Equal("January 3, 2026 at 12:05\u202FAM", Dates.Localized(Date("2026-01-03T00:05:00+03:00"), AppLanguage.En));
        Assert.Equal("May 3, 2026 at 12:05\u202FPM", Dates.Localized(Date("2026-05-03T12:05:00+03:00"), AppLanguage.En));
        Assert.Equal("3 мая 2026\u202Fг. в 00:05", Dates.Localized(Date("2026-05-03T00:05:00+03:00"), AppLanguage.Ru));
        var local = Date("2026-02-13T09:05:00+03:00");
        Assert.Equal("Feb 13, 2026 at 9:05\u202FAM", Slovo.Core.Meetings.MeetingDates.MediumDateShortTime(local, AppLanguage.En, McpFixture.TimeZone));
        Assert.Equal("13 февр. 2026\u202Fг., 09:05", Slovo.Core.Meetings.MeetingDates.MediumDateShortTime(local, AppLanguage.Ru, McpFixture.TimeZone));
    }

    [Fact]
    public void SearchWordsAndSnippets()
    {
        Assert.Equal("cafe ежик иод", McpSearch.Normalized("  Café  ЁЖИК\tЙод "));
        const string sample = "Привет, Мир! ЁЛКА «кавычки» — тире… A-Z 0-9 йогурт\u00A0nbsp ёжик";
        Assert.Equal(McpSearch.SlowNormalized(sample), McpSearch.FastNormalized(sample));
        Assert.Null(McpSearch.FastNormalized("café"));
        Assert.Equal(["прототип", "к", "пятнице"], McpSearch.Words("«Прототип», к пятнице!"));
        Assert.Empty(McpSearch.Words("!! — …"));

        var text = string.Concat(Enumerable.Repeat("а ", 200)) + "Нужная Ёлка" + string.Concat(Enumerable.Repeat(" б", 200));
        var snippet = McpSearch.Snippet(text, ["елка"]);
        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
        Assert.Contains("Ёлка", snippet, StringComparison.Ordinal);
        Assert.True(Slovo.Core.Text.TextElements.Count(snippet) <= 2 + 120 * 2 + 4);
        Assert.Equal("short text", McpSearch.Snippet("short text", ["nothing"]));
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("758", 758.0)]
    [InlineData("700.5", 700.5)]
    [InlineData(" 12:40 ", 760.0)]
    [InlineData("0:13:20", 800.0)]
    [InlineData("1:04:12", 3852.0)]
    [InlineData("99:59", 5999.0)]
    [InlineData("1.", 1.0)]
    [InlineData(".5", 0.5)]
    public void TimelinePositionsFromText(string text, double expected) =>
        Assert.Equal(expected, McpTranscriptPager.TimelinePosition(JsonValue.Str(text)));

    [Theory]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("1:2:3")]
    [InlineData("12:75")]
    [InlineData("1:00:00:00")]
    [InlineData("12:")]
    [InlineData(".")]
    [InlineData("1.2.3")]
    [InlineData("12345678901234567")]
    [InlineData("soon")]
    public void RefusedTimelinePositions(string text) => Assert.Null(McpTranscriptPager.TimelinePosition(JsonValue.Str(text)));

    [Fact]
    public void TimelinePositionsFromNumbers()
    {
        Assert.Equal(758.0, McpTranscriptPager.TimelinePosition(758));
        Assert.Equal(12.5, McpTranscriptPager.TimelinePosition(12.5));
        Assert.Null(McpTranscriptPager.TimelinePosition(-1));
        Assert.Null(McpTranscriptPager.TimelinePosition(-0.5));
        Assert.Null(McpTranscriptPager.TimelinePosition(true));
        Assert.Null(McpTranscriptPager.TimelinePosition(JsonValue.Null));
    }

    [Fact]
    public void CutsFallOnCharacterAndWordBoundaries()
    {
        Assert.Equal(0, McpTranscriptPager.CharacterIndex("abc", 0));
        // Inside an emoji sequence the cut backs off to its start.
        Assert.Equal(2, McpTranscriptPager.CharacterIndex("ab👩\u200D💻c", 3));
        Assert.Equal(7, McpTranscriptPager.CharacterIndex("ab👩\u200D💻c", 7));
        Assert.Equal(6, McpTranscriptPager.CutIndex("слово слово слово", 10));
        Assert.Equal(4, McpTranscriptPager.CutIndex("ыыыыыыыы", 4));
        // At least one character, so paging moves on.
        Assert.Equal(5, McpTranscriptPager.CutIndex("👩\u200D💻ab", 3));
        Assert.Equal(3, McpTranscriptPager.CutIndex("abc", 10));
    }

    [Fact]
    public void CursorsRoundTripAndHashTheSameTextAsSwift()
    {
        // FNV-1a of "a", as the macOS server computes it.
        Assert.Equal("af63dc4c8601ec8c", McpHash.Hex("a"));
        var cursor = new McpTranscriptCursor("3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11", 84, 12, "5c2e", "q");
        Assert.Equal(cursor, McpTranscriptCursor.Decode(cursor.Encoded));
        Assert.Null(McpTranscriptCursor.Decode("not-a-cursor!"));
        Assert.Null(McpTranscriptCursor.Decode(McpBase64Url.Encode("{}"u8.ToArray())));
        Assert.Null(McpTranscriptCursor.Decode(McpBase64Url.Encode("""{"c":0,"f":"x","i":1.0,"m":"3f2a9c1e-5b7d-4c1a-9e0f-2b6d8a4c7e11","q":"x"}"""u8.ToArray())));
        var selection = new McpTranscriptSelection { From = 5, To = 12.5, SpeakerIds = ["s2", "s1"], MaxChars = 2000 };
        Assert.Equal(McpHash.Hex("get_transcript|s1,s2|5.0|12.5|2000"), selection.FilterHash);

        var list = new McpListCursor { Time = AppleDate.ToReferenceSeconds(Date("2026-09-15T11:02:07+03:00")), Id = "3F2A9C1E-5B7D-4C1A-9E0F-2B6D8A4C7E11", Filter = "f" };
        Assert.Equal(list, McpListCursor.Decode(list.Encoded));
        Assert.Null(McpListCursor.Decode(McpBase64Url.Encode("""{"k":"m"}"""u8.ToArray())));
        Assert.Null(McpBase64Url.Decode("a"));
        Assert.Null(McpBase64Url.Decode("ab+c"));
    }
}
