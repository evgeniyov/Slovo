import Foundation

/// Derives Home and Insights statistics from history. Pure.
public enum InsightsCalculator {
    /// - Parameters:
    ///   - weeks: length of the activity calendar.
    ///   - topAppCount: how many apps `Insights.topApps` lists.
    ///
    /// `dailyWords` starts on the calendar's first weekday `weeks - 1` weeks before the
    /// current week and ends today, so element `i` sits in grid column `i / 7`, row
    /// `i % 7`; the last column is filled up to today only.
    public static func compute(entries: [HistoryEntry], now: Date, calendar: Calendar = .current,
                               weeks: Int = 20, topAppCount: Int = 5) -> Insights {
        guard !entries.isEmpty else {
            return Insights(dailyWords: dailyWords(wordsByDay: [:], now: now, calendar: calendar, weeks: weeks),
                            categoryShares: categoryShares(entries: [], totalWords: 0))
        }
        let totalWords = entries.reduce(0) { $0 + max(0, $1.wordCount) }
        var wordsByDay: [Date: Int] = [:]
        for entry in entries {
            wordsByDay[calendar.startOfDay(for: entry.date), default: 0] += max(0, entry.wordCount)
        }
        let activeDays = Set(wordsByDay.keys)

        return Insights(
            totalWords: totalWords,
            dictationCount: entries.count,
            wordsPerMinute: wordsPerMinute(entries),
            fixesMade: entries.reduce(0) { $0 + max(0, $1.fixesCount) },
            timeSaved: timeSaved(entries),
            currentStreak: currentStreak(activeDays: activeDays, now: now, calendar: calendar),
            longestStreak: longestStreak(activeDays: activeDays, calendar: calendar),
            dailyWords: dailyWords(wordsByDay: wordsByDay, now: now, calendar: calendar, weeks: weeks),
            categoryShares: categoryShares(entries: entries, totalWords: totalWords),
            topApps: topApps(entries: entries, count: topAppCount)
        )
    }

    /// Words over recorded minutes, counting only dictations with a duration.
    static func wordsPerMinute(_ entries: [HistoryEntry]) -> Double {
        let timed = entries.filter { $0.duration > 0 }
        let seconds = timed.reduce(0) { $0 + $1.duration }
        guard seconds > 0 else { return 0 }
        let words = timed.reduce(0) { $0 + max(0, $1.wordCount) }
        return Double(words) / (seconds / 60)
    }

    /// Consecutive active days ending today, or yesterday when today has none yet.
    /// Typing the dictated words at 40 per minute, minus the time spent speaking them.
    /// Only dictations with a duration count, so speech time is never underestimated.
    static func timeSaved(_ entries: [HistoryEntry]) -> TimeInterval {
        let timed = entries.filter { $0.duration > 0 && $0.duration.isFinite }
        let words = timed.reduce(0) { $0 + max(0, $1.wordCount) }
        let spoken = timed.reduce(0) { $0 + $1.duration }
        let typing = Double(words) / Insights.typingWordsPerMinute * 60
        return max(0, typing - spoken)
    }

    static func currentStreak(activeDays: Set<Date>, now: Date, calendar: Calendar) -> Int {
        let today = calendar.startOfDay(for: now)
        var day: Date
        if activeDays.contains(today) {
            day = today
        } else if let yesterday = startOfDay(1, before: today, calendar: calendar), activeDays.contains(yesterday) {
            day = yesterday
        } else {
            return 0
        }
        var streak = 0
        while activeDays.contains(day) {
            streak += 1
            guard let previous = startOfDay(1, before: day, calendar: calendar) else { break }
            day = previous
        }
        return streak
    }

    static func longestStreak(activeDays: Set<Date>, calendar: Calendar) -> Int {
        var longest = 0
        var run = 0
        var previous: Date?
        for day in activeDays.sorted() {
            if let previous, startOfDay(-1, before: previous, calendar: calendar) == day {
                run += 1
            } else {
                run = 1
            }
            longest = max(longest, run)
            previous = day
        }
        return longest
    }

    /// The start of the day `days` days before `day` (after it when negative). Adding
    /// days to a start of day keeps its clock time, which is not midnight when a DST
    /// change skipped midnight, so the step goes from midday.
    static func startOfDay(_ days: Int, before day: Date, calendar: Calendar) -> Date? {
        calendar.date(byAdding: .day, value: -days, to: day.addingTimeInterval(12 * 3600)).map(calendar.startOfDay)
    }

    static func dailyWords(wordsByDay: [Date: Int], now: Date, calendar: Calendar, weeks: Int) -> [DailyWordCount] {
        guard weeks > 0 else { return [] }
        let today = calendar.startOfDay(for: now)
        let weekStart = calendar.dateInterval(of: .weekOfYear, for: today)?.start ?? today
        guard let first = calendar.date(byAdding: .weekOfYear, value: -(weeks - 1), to: weekStart) else { return [] }
        var result: [DailyWordCount] = []
        var day = calendar.startOfDay(for: first)
        while day <= today {
            result.append(DailyWordCount(day: day, words: wordsByDay[day] ?? 0))
            guard let next = calendar.date(byAdding: .day, value: 1, to: day) else { break }
            day = calendar.startOfDay(for: next)
        }
        return result
    }

    static func categoryShares(entries: [HistoryEntry], totalWords: Int) -> [CategoryShare] {
        var words: [AppCategory: Int] = [:]
        for entry in entries {
            words[entry.category, default: 0] += max(0, entry.wordCount)
        }
        return AppCategory.allCases.map { category in
            let count = words[category] ?? 0
            let fraction = totalWords > 0 ? Double(count) / Double(totalWords) : 0
            return CategoryShare(category: category, words: count, fraction: fraction)
        }
    }

    /// Apps by words dictated, then by dictation count, then by name. The name is the
    /// one the newest entry recorded.
    static func topApps(entries: [HistoryEntry], count: Int) -> [AppUsage] {
        guard count > 0 else { return [] }
        struct Tally {
            var name: String
            var newest: Date
            var words = 0
            var dictations = 0
        }
        var tallies: [String: Tally] = [:]
        for entry in entries {
            guard let bundleID = entry.appBundleID, !bundleID.isEmpty else { continue }
            let name = entry.appName.flatMap { $0.isEmpty ? nil : $0 } ?? bundleID
            var tally = tallies[bundleID] ?? Tally(name: name, newest: entry.date)
            if entry.date > tally.newest {
                tally.name = name
                tally.newest = entry.date
            }
            tally.words += max(0, entry.wordCount)
            tally.dictations += 1
            tallies[bundleID] = tally
        }
        return tallies
            .map { AppUsage(app: AppIdentity(bundleID: $0.key, name: $0.value.name),
                            words: $0.value.words, dictations: $0.value.dictations) }
            .sorted { lhs, rhs in
                if lhs.words != rhs.words { return lhs.words > rhs.words }
                if lhs.dictations != rhs.dictations { return lhs.dictations > rhs.dictations }
                return lhs.app.name.localizedStandardCompare(rhs.app.name) == .orderedAscending
            }
            .prefix(count)
            .map { $0 }
    }
}
