import Darwin
import SlovoCore
import SwiftUI

struct InsightsScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        let insights = model.insights
        PanelScroll {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(loc.t("sidebar.insights"), subtitle: loc.t("insights.subtitle"))

                if insights.dictationCount == 0 {
                    InlineNotice(symbol: "chart.bar.xaxis", text: loc.t("insights.empty"), tone: .neutral)
                }

                HStack(alignment: .top, spacing: 14) {
                    Card {
                        VStack(alignment: .leading, spacing: 10) {
                            Text(loc.t("insights.wpm"))
                                .font(theme.headlineFont)
                                .foregroundStyle(theme.textPrimary)
                            WPMGauge(wordsPerMinute: insights.wordsPerMinute)
                                .frame(maxWidth: .infinity)
                        }
                        .frame(maxHeight: .infinity, alignment: .top)
                    }
                    .frame(width: 300)
                    VStack(spacing: 14) {
                        MetricCard(title: loc.t("insights.totalWords"), value: loc.number(insights.totalWords),
                                   caption: loc.plural("plural.dictations", insights.dictationCount))
                        MetricCard(title: loc.t("insights.fixes"), value: loc.number(insights.fixesMade),
                                   caption: loc.t("insights.fixes.caption"))
                        MetricCard(title: loc.t("insights.timeSaved"), value: durationText(insights.timeSaved, locale: loc.locale),
                                   caption: loc.t("insights.timeSaved.caption"))
                    }
                }
                .fixedSize(horizontal: false, vertical: true)

                StreakCard(insights: insights)

                HStack(alignment: .top, spacing: 14) {
                    CategoryCard(shares: insights.categoryShares)
                    TopAppsCard(apps: insights.topApps)
                }
                .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 32)
            .padding(.vertical, 28)
        }
    }
}

/// "3 h 12 min" / "3 ч 12 мин", or minutes alone under an hour.
private func durationText(_ seconds: TimeInterval, locale: Locale) -> String {
    let formatter = DateComponentsFormatter()
    formatter.unitsStyle = .abbreviated
    formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : [.minute]
    formatter.zeroFormattingBehavior = .dropLeading
    var calendar = Calendar.current
    calendar.locale = locale
    formatter.calendar = calendar
    return formatter.string(from: max(0, seconds.rounded())) ?? ""
}

private struct MetricCard: View {
    @Environment(\.theme) private var theme
    let title: String
    let value: String
    let caption: String

    var body: some View {
        Card {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(title)
                        .font(theme.headlineFont)
                        .foregroundStyle(theme.textPrimary)
                    Text(caption)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 12)
                Text(value)
                    .font(theme.statNumberFont)
                    .foregroundStyle(theme.textPrimary)
                    .lineLimit(1)
            }
            .frame(maxHeight: .infinity)
        }
    }
}

/// Words per minute on a half-circle scale up to 200, with a mark at typing speed.
private struct WPMGauge: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let wordsPerMinute: Double

    private let maximum = 200.0
    private let typingSpeed = 40.0
    private let lineWidth: CGFloat = 16

    var body: some View {
        VStack(spacing: 6) {
            ZStack(alignment: .bottom) {
                HalfCircle(inset: lineWidth / 2)
                    .stroke(theme.calendarEmpty, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                HalfCircle(inset: lineWidth / 2)
                    .trim(from: 0, to: min(wordsPerMinute / maximum, 1))
                    .stroke(theme.accent, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                TypingMark(fraction: typingSpeed / maximum, inset: lineWidth / 2)
                    .stroke(theme.textSecondary, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                VStack(spacing: 0) {
                    Text(loc.number(Int(wordsPerMinute.rounded())))
                        .font(theme.largeNumberFont)
                        .foregroundStyle(theme.textPrimary)
                    Text(loc.t("insights.wpm.unit"))
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                }
            }
            .frame(width: 220, height: 120)
            HStack {
                Text("0")
                Spacer()
                Text(loc.number(Int(maximum)))
            }
            .font(theme.monoCaptionFont)
            .foregroundStyle(theme.textSecondary)
            .frame(width: 236)
            HStack(spacing: 6) {
                Capsule().fill(theme.textSecondary).frame(width: 10, height: 2)
                Text(loc.t("insights.wpm.typing"))
            }
            .font(theme.captionFont)
            .foregroundStyle(theme.textSecondary)
        }
    }
}

private struct HalfCircle: Shape {
    let inset: CGFloat

    func path(in rect: CGRect) -> Path {
        let radius = min(rect.width / 2, rect.height) - inset
        var path = Path()
        path.addArc(center: CGPoint(x: rect.midX, y: rect.maxY - inset), radius: radius,
                    startAngle: .degrees(180), endAngle: .degrees(360), clockwise: false)
        return path
    }
}

/// A short radial tick across the gauge track.
private struct TypingMark: Shape {
    let fraction: Double
    let inset: CGFloat

    func path(in rect: CGRect) -> Path {
        let radius = min(rect.width / 2, rect.height) - inset
        let center = CGPoint(x: rect.midX, y: rect.maxY - inset)
        let angle = Angle.degrees(180 + 180 * fraction).radians
        let x = CGFloat(Darwin.cos(angle))
        let y = CGFloat(Darwin.sin(angle))
        let inner = radius - inset - 3
        let outer = radius + inset + 3
        var path = Path()
        path.move(to: CGPoint(x: center.x + inner * x, y: center.y + inner * y))
        path.addLine(to: CGPoint(x: center.x + outer * x, y: center.y + outer * y))
        return path
    }
}

private struct StreakCard: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let insights: Insights

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    Text(loc.t("insights.activity"))
                        .font(theme.headlineFont)
                        .foregroundStyle(theme.textPrimary)
                    Spacer()
                    Text(loc.t("insights.calendar"))
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                }
                HStack(alignment: .top, spacing: 28) {
                    VStack(alignment: .leading, spacing: 16) {
                        streakStat(title: loc.t("insights.currentStreak"), days: insights.currentStreak)
                        streakStat(title: loc.t("insights.longestStreak"), days: insights.longestStreak)
                    }
                    .frame(width: 140, alignment: .leading)
                    ActivityCalendar(days: insights.dailyWords)
                    Spacer(minLength: 0)
                }
            }
        }
    }

    private func streakStat(title: String, days: Int) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(title)
                .font(theme.captionMediumFont)
                .foregroundStyle(theme.textSecondary)
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(loc.number(days))
                    .font(theme.statNumberFont)
                    .foregroundStyle(theme.textPrimary)
                Text(loc.plural("plural.daysUnit", days))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
            }
        }
    }
}

/// GitHub-style grid: one column per week, one row per weekday.
private struct ActivityCalendar: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let days: [DailyWordCount]

    private let cell: CGFloat = 21
    private let gap: CGFloat = 4

    private struct Layout {
        var columns: [[DailyWordCount?]]
        var monthLabels: [Int: Date]
    }

    private var layout: Layout {
        let calendar = Calendar.current
        guard let first = days.first,
              let firstWeek = calendar.dateInterval(of: .weekOfYear, for: first.day)?.start else {
            return Layout(columns: Array(repeating: Array(repeating: nil, count: 7), count: 20), monthLabels: [:])
        }
        var columns: [[DailyWordCount?]] = []
        for day in days {
            let offset = calendar.dateComponents([.day], from: firstWeek, to: calendar.startOfDay(for: day.day)).day ?? 0
            let column = max(0, offset / 7)
            let row = max(0, min(6, offset % 7))
            while columns.count <= column {
                columns.append(Array(repeating: nil, count: 7))
            }
            columns[column][row] = day
        }
        var labels: [Int: Date] = [:]
        var lastMonth: Int?
        for (index, column) in columns.enumerated() {
            guard let date = column.compactMap({ $0 }).first?.day else { continue }
            let month = calendar.component(.month, from: date)
            if month != lastMonth {
                if lastMonth != nil || calendar.component(.day, from: date) <= 7 {
                    labels[index] = date
                }
                lastMonth = month
            }
        }
        return Layout(columns: columns, monthLabels: labels)
    }

    var body: some View {
        let layout = layout
        let maximum = max(1, days.map(\.words).max() ?? 1)
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: gap) {
                weekdayLabels
                ForEach(Array(layout.columns.enumerated()), id: \.offset) { index, column in
                    VStack(spacing: gap) {
                        Text(layout.monthLabels[index].map(loc.monthAbbreviation) ?? " ")
                            .font(.system(size: 10))
                            .foregroundStyle(theme.textSecondary)
                            .fixedSize()
                            .frame(width: cell, height: 12, alignment: .leading)
                        ForEach(0..<7, id: \.self) { row in
                            let day = column[row]
                            RoundedRectangle(cornerRadius: 3.5)
                                .fill(color(for: day, maximum: maximum))
                                .frame(width: cell, height: cell)
                                .help(day.map { loc.t("insights.calendar.tooltip", loc.plural("plural.words", $0.words), loc.longDate($0.day)) } ?? "")
                        }
                    }
                }
            }
            HStack(spacing: 4) {
                Spacer()
                Text(loc.t("insights.calendar.less"))
                RoundedRectangle(cornerRadius: 2.5).fill(theme.calendarEmpty).frame(width: 11, height: 11)
                ForEach(0..<theme.calendarLevels.count, id: \.self) { level in
                    RoundedRectangle(cornerRadius: 2.5).fill(theme.calendarLevels[level]).frame(width: 11, height: 11)
                }
                Text(loc.t("insights.calendar.more"))
            }
            .font(.system(size: 10))
            .foregroundStyle(theme.textSecondary)
        }
        .fixedSize()
    }

    private var weekdayLabels: some View {
        // Rows follow the grid's calendar; only the names come from the interface
        // language, whose own first weekday can differ (Sunday for English).
        let firstWeekday = Calendar.current.firstWeekday
        var named = Calendar.current
        named.locale = loc.locale
        let symbols = named.shortStandaloneWeekdaySymbols
        return VStack(alignment: .leading, spacing: gap) {
            Text(" ").font(.system(size: 10)).frame(height: 12)
            ForEach(0..<7, id: \.self) { row in
                let index = (firstWeekday - 1 + row) % 7
                Text(row % 2 == 0 ? symbols[index] : " ")
                    .font(.system(size: 10))
                    .foregroundStyle(theme.textSecondary)
                    .frame(width: 26, height: cell, alignment: .leading)
            }
        }
    }

    private func color(for day: DailyWordCount?, maximum: Int) -> Color {
        // Days outside the range (after today, before the first week) stay blank.
        guard let day else { return Color.clear }
        guard day.words > 0 else { return theme.calendarEmpty }
        let levels = theme.calendarLevels
        let ratio = Double(day.words) / Double(maximum)
        let index = min(levels.count - 1, Int((ratio * Double(levels.count)).rounded(.up)) - 1)
        return levels[max(0, index)]
    }
}

private struct CategoryCard: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let shares: [CategoryShare]

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 14) {
                Text(loc.t("insights.byCategory"))
                    .font(theme.headlineFont)
                    .foregroundStyle(theme.textPrimary)
                ForEach(AppCategory.allCases) { category in
                    let share = shares.first { $0.category == category }
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(spacing: 8) {
                            Image(systemName: category.symbol)
                                .font(.system(size: 11))
                                .foregroundStyle(theme.textSecondary)
                                .frame(width: 16)
                            Text(loc.t(category.titleKey))
                                .font(theme.bodyFont)
                                .foregroundStyle(theme.textPrimary)
                            Spacer()
                            Text(loc.percent(share?.fraction ?? 0))
                                .font(theme.monoCaptionFont)
                                .foregroundStyle(theme.textSecondary)
                        }
                        ThemedProgressBar(fraction: share?.fraction ?? 0)
                    }
                }
            }
            .frame(maxHeight: .infinity, alignment: .top)
        }
    }
}

private struct TopAppsCard: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let apps: [AppUsage]

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 12) {
                Text(loc.t("insights.topApps"))
                    .font(theme.headlineFont)
                    .foregroundStyle(theme.textPrimary)
                if apps.isEmpty {
                    Text(loc.t("common.nothingYet"))
                        .font(theme.bodyFont)
                        .foregroundStyle(theme.textSecondary)
                } else {
                    let maximum = max(1, apps.map(\.words).max() ?? 1)
                    ForEach(Array(apps.enumerated()), id: \.offset) { index, usage in
                        HStack(spacing: 10) {
                            Text("\(index + 1)")
                                .font(theme.monoCaptionFont)
                                .foregroundStyle(theme.textSecondary)
                                .frame(width: 12, alignment: .leading)
                            AppIconView(bundleID: usage.app.bundleID, name: usage.app.name, size: 22)
                            VStack(alignment: .leading, spacing: 4) {
                                HStack {
                                    Text(usage.app.name)
                                        .font(theme.bodyFont)
                                        .foregroundStyle(theme.textPrimary)
                                        .lineLimit(1)
                                    Spacer()
                                    Text(loc.plural("plural.words", usage.words))
                                        .font(theme.captionFont)
                                        .foregroundStyle(theme.textSecondary)
                                }
                                ThemedProgressBar(fraction: Double(usage.words) / Double(maximum), height: 4)
                            }
                        }
                    }
                }
            }
            .frame(maxHeight: .infinity, alignment: .top)
        }
    }
}
