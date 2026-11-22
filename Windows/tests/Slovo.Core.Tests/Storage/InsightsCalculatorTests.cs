using Slovo.Core.Model;
using Slovo.Core.Storage;

namespace Slovo.Core.Tests.Storage;

/// <summary>Insights of InputRetentionInsightsTests and FixTextInsightsTests, and DayCalendar across daylight saving changes.</summary>
public class InsightsCalculatorTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000); // 2026-09-10 00:26:40 UTC, a Thursday
    const double Day = 86_400;
    static readonly DayCalendar Utc = new(TimeZoneInfo.Utc, DayOfWeek.Monday);

    static HistoryEntry Entry(DateTimeOffset date, int words = 5, double duration = 2, int fixes = 0,
                              AppCategory category = AppCategory.Other, (string Id, string Name)? app = null, bool noApp = false) => new()
    {
        Date = date, Text = "text", RawText = "text", EngineId = EngineId.Parakeet, LanguageMode = LanguageMode.English,
        AppBundleId = noApp ? null : app?.Id ?? "notepad.exe", AppName = noApp ? null : app?.Name ?? "Notepad",
        Category = category, Duration = duration, WordCount = words, FixesCount = fixes,
    };

    static HistoryEntry Aged(double age, int words = 5, double duration = 2, int fixes = 0, AppCategory category = AppCategory.Other,
                             (string Id, string Name)? app = null, bool noApp = false) =>
        Entry(Now.AddSeconds(-age), words, duration, fixes, category, app, noApp);

    [Fact]
    public void TimeSavedComparesWithTypingAtFortyWordsPerMinute()
    {
        // 120 words typed at 40 per minute take 180 s; they were spoken in 60 s.
        var insights = InsightsCalculator.Compute([Aged(60, words: 120, duration: 60)], Now, Utc);
        Assert.Equal(120, insights.TimeSaved, 3);
    }

    [Fact]
    public void TimeSavedIsNeverNegativeAndIgnoresUntimedEntries()
    {
        Assert.Equal(0, InsightsCalculator.TimeSaved([Aged(60, words: 10, duration: 600)]));
        Assert.Equal(0, InsightsCalculator.TimeSaved([Aged(60, words: 500, duration: 0)]));
        Assert.Equal(0, InsightsCalculator.TimeSaved([Aged(60, words: 500, duration: double.PositiveInfinity)]));
    }

    [Fact]
    public void EmptyHistory()
    {
        var insights = InsightsCalculator.Compute([], Now, Utc);
        Assert.Equal(0, insights.TotalWords);
        Assert.Equal(0, insights.WordsPerMinute);
        Assert.Equal(0, insights.CurrentStreak);
        Assert.Equal(0, insights.LongestStreak);
        Assert.Empty(insights.TopApps);
        Assert.Equal(CategoryDefaults.All, insights.CategoryShares.Select(share => share.Category));
        Assert.Equal([0.0, 0, 0, 0], insights.CategoryShares.Select(share => share.Fraction));
        Assert.NotEmpty(insights.DailyWords);
        Assert.All(insights.DailyWords, day => Assert.Equal(0, day.Words));
    }

    [Fact]
    public void TotalsSpeedAndFixes()
    {
        HistoryEntry[] entries =
        [
            Aged(60, words: 60, duration: 30, fixes: 2),
            Aged(120, words: 40, duration: 30, fixes: 1),
            // No duration: counts toward totals, not toward speed.
            Aged(180, words: 10, duration: 0),
        ];
        var insights = InsightsCalculator.Compute(entries, Now, Utc);
        Assert.Equal(110, insights.TotalWords);
        Assert.Equal(3, insights.DictationCount);
        Assert.Equal(100, insights.WordsPerMinute, 9);
        Assert.Equal(3, insights.FixesMade);
    }

    [Fact]
    public void Streaks()
    {
        var today = Utc.StartOfDay(Now);
        HistoryEntry OnDay(int offset) => Entry(today.AddSeconds(offset * Day + 3600));
        // Active: today, -1, -2; gap at -3; a five-day run from -10 to -6.
        var insights = InsightsCalculator.Compute(new[] { 0, -1, -2, -2, -6, -7, -8, -9, -10 }.Select(OnDay).ToList(), Now, Utc);
        Assert.Equal(3, insights.CurrentStreak);
        Assert.Equal(5, insights.LongestStreak);

        // Nothing yet today: the streak still counts from yesterday.
        insights = InsightsCalculator.Compute(new[] { -1, -2 }.Select(OnDay).ToList(), Now, Utc);
        Assert.Equal(2, insights.CurrentStreak);

        insights = InsightsCalculator.Compute(new[] { -2, -3 }.Select(OnDay).ToList(), Now, Utc);
        Assert.Equal(0, insights.CurrentStreak);
        Assert.Equal(2, insights.LongestStreak);
    }

    [Fact]
    public void DailyWordsCoverTwentyWeeksAlignedToWeekStart()
    {
        var today = Utc.StartOfDay(Now);
        HistoryEntry[] entries =
        [
            Entry(today.AddSeconds(600), words: 7),
            Entry(today.AddSeconds(700), words: 5),
            Entry(today.AddSeconds(-Day + 60), words: 3),
            Entry(today.AddSeconds(-400 * Day), words: 100),
        ];
        var daily = InsightsCalculator.Compute(entries, Now, Utc).DailyWords;
        Assert.Equal(DayOfWeek.Monday, daily[0].Day.DayOfWeek);
        Assert.Equal(today, daily[^1].Day);
        // 2026-09-10 is a Thursday: 19 full weeks plus Monday through Thursday.
        Assert.Equal(19 * 7 + 4, daily.Count);
        Assert.Equal(12, daily[^1].Words);
        Assert.Equal(3, daily[^2].Words);
        Assert.Equal(15, daily.Sum(day => day.Words));
        Assert.True(daily.Zip(daily.Skip(1)).All(pair => pair.First.Day < pair.Second.Day), "oldest first");

        Assert.Equal(4, InsightsCalculator.Compute(entries, Now, Utc, weeks: 1).DailyWords.Count);
        Assert.Empty(InsightsCalculator.Compute(entries, Now, Utc, weeks: 0).DailyWords);
        // A Sunday-first calendar starts the week three days earlier.
        Assert.Equal(DayOfWeek.Sunday, InsightsCalculator.Compute(entries, Now, Utc with { FirstDayOfWeek = DayOfWeek.Sunday }, weeks: 1).DailyWords[0].Day.DayOfWeek);
    }

    [Fact]
    public void CategorySharesAndTopApps()
    {
        HistoryEntry[] entries =
        [
            Aged(10, words: 30, category: AppCategory.Work, app: ("slack.exe", "Slack")),
            Aged(20, words: 20, category: AppCategory.Work, app: ("slack.exe", "Slack Old")),
            Aged(30, words: 25, category: AppCategory.Personal, app: ("telegram.exe", "Telegram")),
            Aged(40, words: 25, category: AppCategory.Email, app: ("olk.exe", "Outlook")),
            Aged(50, words: 0, category: AppCategory.Other, app: ("olk.exe", "Outlook")),
            Aged(60, words: 0, category: AppCategory.Other, noApp: true),
        ];
        var insights = InsightsCalculator.Compute(entries, Now, Utc, topAppCount: 2);
        Assert.Equal([AppCategory.Personal, AppCategory.Work, AppCategory.Email, AppCategory.Other], insights.CategoryShares.Select(s => s.Category));
        Assert.Equal([25, 50, 25, 0], insights.CategoryShares.Select(s => s.Words));
        Assert.Equal([0.25, 0.5, 0.25, 0], insights.CategoryShares.Select(s => s.Fraction));

        Assert.Equal(2, insights.TopApps.Count);
        Assert.Equal(new AppUsage(new AppIdentity("slack.exe", "Slack"), 50, 2), insights.TopApps[0]);
        // Outlook and Telegram tie on words; Outlook has more dictations.
        Assert.Equal("Outlook", insights.TopApps[1].App.Name);
        Assert.Equal(2, insights.TopApps[1].Dictations);
    }

    // MARK: - Daylight saving time (FixTextInsightsTests)

    [Theory]
    [InlineData("America/Santiago", 2026, 9, 5)]
    [InlineData("America/Havana", 2026, 3, 7)]
    public void StreakAcrossASkippedMidnight(string zone, int year, int month, int day)
    {
        var calendar = new DayCalendar(TimeZoneInfo.FindSystemTimeZoneById(zone), DayOfWeek.Monday);
        var first = new DateOnly(year, month, day);
        var insights = Compute([0, 1, 2], first, nowOffset: 2, calendar);
        Assert.Equal(3, insights.CurrentStreak);
        Assert.Equal(3, insights.LongestStreak);
        Assert.Equal(3, insights.DailyWords.Count(d => d.Words > 0));
    }

    [Fact]
    public void YesterdayKeepsTheStreakAliveAcrossASkippedMidnight()
    {
        var calendar = new DayCalendar(TimeZoneInfo.FindSystemTimeZoneById("America/Havana"), DayOfWeek.Monday);
        Assert.Equal(2, Compute([0, 1], new DateOnly(2026, 3, 6), nowOffset: 2, calendar).CurrentStreak);
    }

    /// <summary>Every daylight saving change of 2026 in every time zone the system knows.</summary>
    [Fact]
    public void StreaksAcrossEveryTransitionOf2026()
    {
        var checkedTransitions = 0;
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var calendar = new DayCalendar(zone, DayOfWeek.Monday);
            foreach (var transition in Transitions2026(zone))
            {
                var first = calendar.LocalDate(transition).AddDays(-2);
                var full = Compute([0, 1, 2, 3, 4], first, nowOffset: 4, calendar);
                Assert.True(full.CurrentStreak == 5 && full.LongestStreak == 5, $"{zone.Id} {transition:O}");
                var gap = Compute([0, 1, 3, 4], first, nowOffset: 4, calendar);
                Assert.True(gap.CurrentStreak == 2 && gap.LongestStreak == 2, $"{zone.Id} {transition:O}");
                Assert.True(full.DailyWords.Zip(full.DailyWords.Skip(1)).All(pair => calendar.LocalDate(pair.First.Day).AddDays(1) == calendar.LocalDate(pair.Second.Day)),
                            $"{zone.Id}: one element per calendar day");
                checkedTransitions++;
            }
        }
        Assert.True(checkedTransitions > 50, $"only {checkedTransitions} transitions");
    }

    [Theory]
    [InlineData("America/Santiago", 2026, 9, 6, 1, -3, 23)]
    [InlineData("America/Havana", 2026, 3, 8, 1, -4, 23)]
    [InlineData("Europe/Berlin", 2026, 10, 25, 0, 2, 25)]
    [InlineData("Europe/Berlin", 2026, 3, 29, 0, 1, 23)]
    [InlineData("Europe/Moscow", 2026, 9, 15, 0, 3, 24)]
    public void DaysStartAtTheirFirstInstant(string zone, int year, int month, int day, int startHour, int offsetHours, int hours)
    {
        var calendar = new DayCalendar(TimeZoneInfo.FindSystemTimeZoneById(zone), DayOfWeek.Monday);
        var date = new DateOnly(year, month, day);
        var start = calendar.StartOfDay(date);
        Assert.Equal(new DateTimeOffset(year, month, day, startHour, 0, 0, TimeSpan.FromHours(offsetHours)), start);
        Assert.Equal(TimeSpan.FromHours(hours), calendar.EndOfDay(date) - start);
        Assert.Equal(date, calendar.LocalDate(start));
        Assert.Equal(date.AddDays(-1), calendar.LocalDate(start.AddTicks(-1)));
        Assert.Equal(start, calendar.StartOfDay(start.AddHours(hours - 0.5)));
    }

    /// <summary>Insights for one dictation at noon on each listed day after <paramref name="first"/>, seen at 18:00 on day <paramref name="nowOffset"/>.</summary>
    static Insights Compute(int[] activeDayOffsets, DateOnly first, int nowOffset, DayCalendar calendar)
    {
        var entries = activeDayOffsets.Select(offset => Entry(Noon(first.AddDays(offset), calendar), words: 3, duration: 3)).ToList();
        var now = Noon(first.AddDays(nowOffset), calendar).AddHours(6);
        return InsightsCalculator.Compute(entries, now, calendar);
    }

    static DateTimeOffset Noon(DateOnly day, DayCalendar calendar)
    {
        var local = day.ToDateTime(new TimeOnly(12, 0));
        var offset = calendar.TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>The instants in 2026 where the zone's offset changes, found six hours at a time and refined to the minute.</summary>
    static IEnumerable<DateTimeOffset> Transitions2026(TimeZoneInfo zone)
    {
        var cursor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var offset = zone.GetUtcOffset(cursor);
        while (cursor < end)
        {
            var next = cursor.AddHours(6);
            var nextOffset = zone.GetUtcOffset(next);
            if (nextOffset != offset)
            {
                var low = cursor;
                while (next - low > TimeSpan.FromMinutes(1))
                {
                    var middle = low + (next - low) / 2;
                    if (zone.GetUtcOffset(middle) == offset)
                    {
                        low = middle;
                    }
                    else
                    {
                        next = middle;
                    }
                }
                yield return next;
                offset = nextOffset;
            }
            cursor = cursor.AddHours(6);
        }
    }
}
