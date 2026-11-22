using Slovo.Core.Model;

namespace Slovo.Core.Storage;

/// <summary>Derives Home and Insights statistics from history (port of the macOS <c>InsightsCalculator</c>). Pure.</summary>
public static class InsightsCalculator
{
    /// <param name="calendar">Days and weeks; the machine's time zone and first weekday by default.</param>
    /// <param name="weeks">Length of the activity calendar.</param>
    /// <param name="topAppCount">How many apps <see cref="Insights.TopApps"/> lists.</param>
    /// <remarks>
    /// DailyWords starts on the calendar's first weekday <paramref name="weeks"/> − 1 weeks
    /// before the current week and ends today, so element i sits in grid column i / 7, row
    /// i % 7; the last column is filled up to today only.
    /// </remarks>
    public static Insights Compute(IReadOnlyList<HistoryEntry> entries, DateTimeOffset now, DayCalendar? calendar = null,
                                   int weeks = 20, int topAppCount = 5)
    {
        calendar ??= DayCalendar.Current;
        if (entries.Count == 0)
        {
            return new Insights
            {
                DailyWords = DailyWords(new Dictionary<DateOnly, int>(), now, calendar, weeks),
                CategoryShares = CategoryShares([], 0),
            };
        }
        var totalWords = entries.Sum(entry => Math.Max(0, entry.WordCount));
        var wordsByDay = new Dictionary<DateOnly, int>();
        foreach (var entry in entries)
        {
            var day = calendar.LocalDate(entry.Date);
            wordsByDay[day] = wordsByDay.GetValueOrDefault(day) + Math.Max(0, entry.WordCount);
        }
        var activeDays = wordsByDay.Keys.ToHashSet();

        return new Insights
        {
            TotalWords = totalWords,
            DictationCount = entries.Count,
            WordsPerMinute = WordsPerMinute(entries),
            FixesMade = entries.Sum(entry => Math.Max(0, entry.FixesCount)),
            TimeSaved = TimeSaved(entries),
            CurrentStreak = CurrentStreak(activeDays, now, calendar),
            LongestStreak = LongestStreak(activeDays),
            DailyWords = DailyWords(wordsByDay, now, calendar, weeks),
            CategoryShares = CategoryShares(entries, totalWords),
            TopApps = TopApps(entries, topAppCount),
        };
    }

    /// <summary>Typing the dictated words at 40 words per minute, minus the time spent speaking them.</summary>
    internal static double TimeSaved(IReadOnlyList<HistoryEntry> entries)
    {
        var timed = entries.Where(entry => entry.Duration > 0 && double.IsFinite(entry.Duration)).ToList();
        var words = timed.Sum(entry => (double)Math.Max(0, entry.WordCount));
        var spoken = timed.Sum(entry => entry.Duration);
        return Math.Max(0, words / Insights.TypingWordsPerMinute * 60 - spoken);
    }

    /// <summary>Words over recorded minutes, counting only dictations with a duration.</summary>
    internal static double WordsPerMinute(IReadOnlyList<HistoryEntry> entries)
    {
        var timed = entries.Where(entry => entry.Duration > 0).ToList();
        var seconds = timed.Sum(entry => entry.Duration);
        if (seconds <= 0)
        {
            return 0;
        }
        var words = timed.Sum(entry => Math.Max(0, entry.WordCount));
        return words / (seconds / 60);
    }

    /// <summary>
    /// Consecutive active days ending today, or yesterday when today has none yet. Days are
    /// calendar dates, so a day of 23 or 25 hours, or one that starts at 01:00 after a
    /// daylight saving change, is still one day.
    /// </summary>
    internal static int CurrentStreak(IReadOnlySet<DateOnly> activeDays, DateTimeOffset now, DayCalendar calendar)
    {
        var today = calendar.LocalDate(now);
        DateOnly day;
        if (activeDays.Contains(today))
        {
            day = today;
        }
        else if (activeDays.Contains(today.AddDays(-1)))
        {
            day = today.AddDays(-1);
        }
        else
        {
            return 0;
        }
        var streak = 0;
        while (activeDays.Contains(day))
        {
            streak += 1;
            day = day.AddDays(-1);
        }
        return streak;
    }

    internal static int LongestStreak(IReadOnlySet<DateOnly> activeDays)
    {
        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in activeDays.Order())
        {
            run = previous is { } last && last.AddDays(1) == day ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }
        return longest;
    }

    internal static IReadOnlyList<DailyWordCount> DailyWords(IReadOnlyDictionary<DateOnly, int> wordsByDay, DateTimeOffset now, DayCalendar calendar, int weeks)
    {
        if (weeks <= 0)
        {
            return [];
        }
        var today = calendar.LocalDate(now);
        var first = calendar.StartOfWeek(today).AddDays(-7 * (weeks - 1));
        var result = new List<DailyWordCount>();
        for (var day = first; day <= today; day = day.AddDays(1))
        {
            result.Add(new DailyWordCount(calendar.StartOfDay(day), wordsByDay.GetValueOrDefault(day)));
        }
        return result;
    }

    internal static IReadOnlyList<CategoryShare> CategoryShares(IReadOnlyList<HistoryEntry> entries, int totalWords)
    {
        var words = new Dictionary<AppCategory, int>();
        foreach (var entry in entries)
        {
            words[entry.Category] = words.GetValueOrDefault(entry.Category) + Math.Max(0, entry.WordCount);
        }
        return CategoryDefaults.All
            .Select(category =>
            {
                var count = words.GetValueOrDefault(category);
                return new CategoryShare(category, count, totalWords > 0 ? (double)count / totalWords : 0);
            })
            .ToList();
    }

    /// <summary>Apps by words dictated, then by dictation count, then by name. The name is the one the newest entry recorded.</summary>
    internal static IReadOnlyList<AppUsage> TopApps(IReadOnlyList<HistoryEntry> entries, int count)
    {
        if (count <= 0)
        {
            return [];
        }
        var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.AppBundleId))
            {
                continue;
            }
            var name = string.IsNullOrEmpty(entry.AppName) ? entry.AppBundleId : entry.AppName;
            if (!tallies.TryGetValue(entry.AppBundleId, out var tally))
            {
                tally = new Tally(name, entry.Date);
                tallies[entry.AppBundleId] = tally;
            }
            if (entry.Date > tally.Newest)
            {
                tally.Name = name;
                tally.Newest = entry.Date;
            }
            tally.Words += Math.Max(0, entry.WordCount);
            tally.Dictations += 1;
        }
        return tallies
            .Select(pair => new AppUsage(new AppIdentity(pair.Key, pair.Value.Name), pair.Value.Words, pair.Value.Dictations))
            .Order(Comparer<AppUsage>.Create((lhs, rhs) =>
            {
                if (lhs.Words != rhs.Words) return rhs.Words.CompareTo(lhs.Words);
                if (lhs.Dictations != rhs.Dictations) return rhs.Dictations.CompareTo(lhs.Dictations);
                return AppCategoryStore.LocalizedStandardCompare(lhs.App.Name, rhs.App.Name);
            }))
            .Take(count)
            .ToList();
    }

    sealed class Tally(string name, DateTimeOffset newest)
    {
        public string Name { get; set; } = name;
        public DateTimeOffset Newest { get; set; } = newest;
        public int Words { get; set; }
        public int Dictations { get; set; }
    }
}
