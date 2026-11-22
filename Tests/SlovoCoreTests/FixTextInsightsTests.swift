import XCTest
@testable import SlovoCore

/// Streaks count calendar days, so days that start at 01:00 or last 23 or 25 hours
/// after a daylight saving change do not break them.
final class FixTextInsightsTests: XCTestCase {
    func testStreakAcrossASkippedMidnight() throws {
        for (zone, start) in [("America/Santiago", (2026, 9, 5)), ("America/Havana", (2026, 3, 7))] {
            let calendar = try calendar(zone)
            let first = try XCTUnwrap(calendar.date(from: DateComponents(year: start.0, month: start.1, day: start.2)))
            let insights = try compute(activeDayOffsets: [0, 1, 2], from: first, nowOffset: 2, calendar: calendar)
            XCTAssertEqual(insights.currentStreak, 3, zone)
            XCTAssertEqual(insights.longestStreak, 3, zone)
            XCTAssertEqual(insights.dailyWords.filter { $0.words > 0 }.count, 3, zone)
        }
    }

    /// Every daylight saving change of 2026 in every time zone the system knows.
    func testStreaksAcrossEveryTransitionOf2026() throws {
        var checked = 0
        for identifier in TimeZone.knownTimeZoneIdentifiers {
            let zone = try XCTUnwrap(TimeZone(identifier: identifier))
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = zone
            guard let yearStart = calendar.date(from: DateComponents(year: 2026, month: 1, day: 1)),
                  let yearEnd = calendar.date(from: DateComponents(year: 2027, month: 1, day: 1)) else { continue }
            var cursor = yearStart
            while let transition = zone.nextDaylightSavingTimeTransition(after: cursor), transition < yearEnd {
                cursor = transition
                let day = calendar.startOfDay(for: transition)
                guard let first = calendar.date(byAdding: .day, value: -2, to: day.addingTimeInterval(12 * 3600)) else { continue }
                let firstDay = calendar.startOfDay(for: first)
                let full = try compute(activeDayOffsets: [0, 1, 2, 3, 4], from: firstDay, nowOffset: 4, calendar: calendar)
                XCTAssertEqual(full.currentStreak, 5, "\(identifier) \(transition)")
                XCTAssertEqual(full.longestStreak, 5, "\(identifier) \(transition)")
                let gap = try compute(activeDayOffsets: [0, 1, 3, 4], from: firstDay, nowOffset: 4, calendar: calendar)
                XCTAssertEqual(gap.currentStreak, 2, "\(identifier) \(transition)")
                XCTAssertEqual(gap.longestStreak, 2, "\(identifier) \(transition)")
                checked += 1
            }
        }
        XCTAssertGreaterThan(checked, 50)
    }

    func testYesterdayKeepsTheStreakAliveAcrossASkippedMidnight() throws {
        let calendar = try calendar("America/Havana")
        let first = try XCTUnwrap(calendar.date(from: DateComponents(year: 2026, month: 3, day: 6)))
        let insights = try compute(activeDayOffsets: [0, 1], from: first, nowOffset: 2, calendar: calendar)
        XCTAssertEqual(insights.currentStreak, 2)
    }

    // MARK: - Helpers

    private func calendar(_ identifier: String) throws -> Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: identifier))
        return calendar
    }

    /// Insights for one dictation at noon on each listed day after `first`, seen at 18:00
    /// on day `nowOffset`.
    private func compute(activeDayOffsets: [Int], from first: Date, nowOffset: Int, calendar: Calendar) throws -> Insights {
        let entries = try activeDayOffsets.map { offset -> HistoryEntry in
            let date = try XCTUnwrap(noon(offset, after: first, calendar: calendar))
            return HistoryEntry(date: date, text: "раз два три", rawText: "раз два три", engineID: .gigaamRNNT,
                                languageMode: .mixed, appBundleID: nil, appName: nil, category: .other,
                                duration: 3, wordCount: 3, fixesCount: 0, audioFileName: nil)
        }
        let now = try XCTUnwrap(noon(nowOffset, after: first, calendar: calendar)).addingTimeInterval(6 * 3600)
        return InsightsCalculator.compute(entries: entries, now: now, calendar: calendar)
    }

    private func noon(_ days: Int, after first: Date, calendar: Calendar) -> Date? {
        guard let shifted = calendar.date(byAdding: .day, value: days, to: first.addingTimeInterval(12 * 3600)) else { return nil }
        var components = calendar.dateComponents([.year, .month, .day], from: shifted)
        components.hour = 12
        return calendar.date(from: components)
    }
}

final class InsightsTimeSavedTests: XCTestCase {
    private func entry(words: Int, duration: TimeInterval) -> HistoryEntry {
        HistoryEntry(date: Date(timeIntervalSinceReferenceDate: 800_000_000), text: "", rawText: "", engineID: .gigaamRNNT,
                     languageMode: .mixed, appBundleID: nil, appName: nil, category: .other,
                     duration: duration, wordCount: words, fixesCount: 0, audioFileName: nil)
    }

    func testTimeSavedIsTypingTimeMinusSpeakingTime() {
        // 120 words typed at 40 per minute take 180 s; they were spoken in 60 s.
        let insights = InsightsCalculator.compute(entries: [entry(words: 120, duration: 60)],
                                                  now: Date(timeIntervalSinceReferenceDate: 800_000_000))
        XCTAssertEqual(insights.timeSaved, 120, accuracy: 0.001)
    }

    func testTimeSavedIsNeverNegativeAndIgnoresUntimedEntries() {
        let slow = InsightsCalculator.timeSaved([entry(words: 10, duration: 600)])
        XCTAssertEqual(slow, 0)
        XCTAssertEqual(InsightsCalculator.timeSaved([entry(words: 500, duration: 0)]), 0)
    }
}
