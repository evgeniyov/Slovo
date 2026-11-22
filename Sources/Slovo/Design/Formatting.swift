import Foundation
import SlovoCore

/// Dates, numbers and durations in the interface language.
@MainActor
extension Localizer {
    /// "14:32" or "2:32 PM".
    func timeText(_ date: Date) -> String {
        date.formatted(Date.FormatStyle(date: .omitted, time: .shortened).locale(locale))
    }

    /// "Today", "Yesterday", or "Monday, September 8" (with the year when it differs).
    func dayTitle(_ day: Date, now: Date, calendar: Calendar = .current) -> String {
        if calendar.isDate(day, inSameDayAs: now) {
            return self.t("date.today")
        }
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: now), calendar.isDate(day, inSameDayAs: yesterday) {
            return self.t("date.yesterday")
        }
        var style = Date.FormatStyle().weekday(.wide).day().month(.wide).locale(locale)
        if calendar.component(.year, from: day) != calendar.component(.year, from: now) {
            style = style.year()
        }
        return capitalizedFirst(day.formatted(style))
    }

    /// "Sep 8, 14:32" style, for lists of meetings and notes.
    func shortDateTime(_ date: Date, now: Date, calendar: Calendar = .current) -> String {
        if calendar.isDate(date, inSameDayAs: now) {
            return self.t("date.todayAt", timeText(date))
        }
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: now), calendar.isDate(date, inSameDayAs: yesterday) {
            return self.t("date.yesterdayAt", timeText(date))
        }
        let style = Date.FormatStyle().day().month(.abbreviated).locale(locale)
        return "\(date.formatted(style)), \(timeText(date))"
    }

    /// A full date for headers: "September 15, 2026".
    func longDate(_ date: Date) -> String {
        date.formatted(Date.FormatStyle(date: .long, time: .omitted).locale(locale))
    }

    /// Month abbreviation for calendar labels.
    func monthAbbreviation(_ date: Date) -> String {
        capitalizedFirst(date.formatted(Date.FormatStyle().month(.abbreviated).locale(locale)))
    }

    /// Grouped digits: "12,480" or «12 480».
    func number(_ value: Int) -> String {
        value.formatted(IntegerFormatStyle<Int>(locale: locale))
    }

    func percent(_ fraction: Double) -> String {
        fraction.formatted(FloatingPointFormatStyle<Double>.Percent(locale: locale).precision(.fractionLength(0)))
    }

    /// "215 MB" / «215 МБ».
    func bytes(_ count: Int64) -> String {
        count.formatted(ByteCountFormatStyle(style: .file, allowedUnits: .all, spellsOutZero: false,
                                             includesActualByteCount: false, locale: locale))
    }

    private func capitalizedFirst(_ text: String) -> String {
        guard let first = text.first else { return text }
        return first.uppercased() + text.dropFirst()
    }
}

/// `m:ss`, or `h:mm:ss` from one hour on, for timers, durations and transcript stamps.
func clockText(_ seconds: TimeInterval) -> String {
    let total = max(0, Int(seconds.rounded(.down)))
    let hours = total / 3600
    let minutes = (total % 3600) / 60
    let secs = total % 60
    if hours > 0 {
        return String(format: "%d:%02d:%02d", hours, minutes, secs)
    }
    return String(format: "%d:%02d", minutes, secs)
}

/// Case-insensitive search that treats ё as е, as Russian readers expect.
func searchKey(_ text: String) -> String {
    text.lowercased().replacingOccurrences(of: "ё", with: "е")
}

func matchesSearch(_ query: String, _ fields: String...) -> Bool {
    let needle = searchKey(query.trimmingCharacters(in: .whitespacesAndNewlines))
    guard !needle.isEmpty else { return true }
    return fields.contains { searchKey($0).contains(needle) }
}
