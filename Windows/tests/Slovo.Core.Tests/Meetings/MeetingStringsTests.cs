using Slovo.Core.Localization;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// The meetings copy comes from the macOS Meetings.strings; the Windows tables replace only
/// the lines that name macOS, in both languages, and the errors and titles read from them.
/// </summary>
public sealed class MeetingStringsTests
{
    static readonly string[] MacWords = ["macOS", "System Settings", "Системных настройках", "Finder", "⌘"];

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void NoMeetingsLineNamesMacOS(string code)
    {
        var table = CoreStrings.TableFor(code);
        var offending = table.Keys
            .Where(key => key.StartsWith("meetings.", StringComparison.Ordinal))
            .Where(key => MacWords.Any(word => table.Get(key).Contains(word, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void TheWindowsTablesHaveTheSameKeysInBothLanguagesAndReplaceSharedOnes()
    {
        var assembly = typeof(CoreStrings).Assembly;
        Dictionary<string, string> Table(string code)
        {
            var name = assembly.GetManifestResourceNames()
                .Single(n => n.Replace('\\', '/') == $"Slovo.Strings.CoreWindows/{code}/Meetings.strings");
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            return StringsFile.Parse(reader.ReadToEnd());
        }
        var english = Table("en");
        var russian = Table("ru");
        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), russian.Keys.Order(StringComparer.Ordinal));
        var shared = CoreStrings.TableFor("en");
        foreach (var key in english.Keys)
        {
            Assert.True(shared.Contains(key), key);
            Assert.Equal(english[key], shared.Get(key));
            Assert.Equal(russian[key], CoreStrings.TableFor("ru").Get(key));
        }
    }

    [Fact]
    public void DefaultTitlesAndSpeakerNamesFollowTheLanguage()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");
        var date = new DateTimeOffset(2026, 9, 15, 8, 2, 7, TimeSpan.Zero);
        Assert.Equal("Meeting Sep 15, 2026 at 11:02\u202FAM", MeetingTitles.DefaultTitle(date, AppLanguage.En, zone));
        Assert.Equal("Встреча 15 сент. 2026\u202Fг., 11:02", MeetingTitles.DefaultTitle(date, AppLanguage.Ru, zone));
        Assert.Equal("Я", TranscriptMerger.SpeakerName(Speaker.Me, AppLanguage.Ru));
        Assert.Equal("Others", TranscriptMerger.SpeakerName(Speaker.Others, AppLanguage.En));
    }

    [Theory]
    [InlineData(2026, 1, 1, 0, 5, "January 1, 2026 at 12:05\u202FAM", "1 января 2026\u202Fг. в 00:05")]
    [InlineData(2026, 5, 9, 12, 0, "May 9, 2026 at 12:00\u202FPM", "9 мая 2026\u202Fг. в 12:00")]
    [InlineData(2025, 12, 31, 23, 59, "December 31, 2025 at 11:59\u202FPM", "31 декабря 2025\u202Fг. в 23:59")]
    public void LongDatesAreWrittenTheWayMacOSWritesThem(int year, int month, int day, int hour, int minute, string english, string russian)
    {
        var date = new DateTimeOffset(year, month, day, hour, minute, 30, TimeSpan.Zero);
        Assert.Equal(english, MeetingDates.LongDateShortTime(date, AppLanguage.En, TimeZoneInfo.Utc));
        Assert.Equal(russian, MeetingDates.LongDateShortTime(date, AppLanguage.Ru, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ShortMonthsAreApples()
    {
        string[] russian = ["янв.", "февр.", "марта", "апр.", "мая", "июня", "июля", "авг.", "сент.", "окт.", "нояб.", "дек."];
        for (var month = 1; month <= 12; month++)
        {
            var date = new DateTimeOffset(2026, month, 15, 0, 5, 0, TimeSpan.Zero);
            Assert.Equal($"15 {russian[month - 1]} 2026\u202Fг., 00:05", MeetingDates.MediumDateShortTime(date, AppLanguage.Ru, TimeZoneInfo.Utc));
        }
        Assert.Equal("Sep 15, 2026 at 12:05\u202FAM", MeetingDates.MediumDateShortTime(new DateTimeOffset(2026, 9, 15, 0, 5, 0, TimeSpan.Zero), AppLanguage.En, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ErrorsAreLocalizedExceptions()
    {
        var error = MeetingErrors.SaveFailed(new IOException("disk full"));
        Assert.Contains("disk full", error.Message);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(CoreStrings.Get("meetings.error.mergeAcrossTracks"), MeetingErrors.MergeAcrossTracks().Message);
        Assert.Equal(CoreStrings.Format("meetings.error.tapFailed", "AudioClientInitialize (-5)"), MeetingErrors.TapFailed("AudioClientInitialize", -5).Message);
    }

    [Theory]
    [InlineData(999, "999 bytes")]
    [InlineData(1_500, "2 KB")]
    [InlineData(999_999, "1 MB")]
    [InlineData(12_345_678, "12.3 MB")]
    [InlineData(230_000_000, "230 MB")]
    [InlineData(1_234_567_890, "1.23 GB")]
    public void ByteCountsReadLikeByteCountFormatter(long bytes, string english)
    {
        if (CoreStrings.CurrentCode != "en")
        {
            Assert.Skip("Byte counts follow the interface language, which another test changed.");
        }
        Assert.Equal(english, MeetingErrors.ByteCount(bytes));
    }
}
