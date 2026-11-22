import SlovoCore
import SwiftUI

struct HomeScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.dictationKey) private var dictationKeyRaw = DictationKey.fn.rawValue
    @State private var query = ""

    private var keySymbol: String {
        (DictationKey(rawValue: dictationKeyRaw) ?? .fn).symbol
    }

    var body: some View {
        let insights = model.insights
        let hasHistory = !model.historyEntries.isEmpty
        let groups = model.historyGroups(matching: query)
        PanelScroll {
            HomeStack {
                HStack(spacing: 10) {
                    Text(loc.t("home.greeting", model.firstName))
                    KeycapBadge(symbol: keySymbol, size: 17)
                    Text(loc.t("home.greetingSuffix"))
                }
                .font(theme.pageTitleFont)
                .foregroundStyle(theme.textPrimary)
                .lineLimit(1)
                .minimumScaleFactor(0.7)

                if model.isBannerVisible(.home) {
                    BannerCard(headline: loc.t("home.banner.headline"), message: loc.t("home.banner.message"),
                               buttonTitle: loc.t("home.banner.button"),
                               action: { model.selectedScreen = .dictionary },
                               onDismiss: { model.dismissBanner(.home) })
                }

                StatsCard(insights: insights)

                if hasHistory {
                    HStack(alignment: .center) {
                        Text(loc.t("home.history"))
                            .font(theme.sectionTitleFont)
                            .foregroundStyle(theme.textPrimary)
                        Spacer()
                        SearchField(placeholder: loc.t("home.search"), text: $query)
                            .frame(width: 260)
                    }
                    if groups.isEmpty {
                        EmptyStateCard(symbol: "magnifyingglass", title: loc.t("home.search.empty.title"),
                                       message: loc.t("home.search.empty.message", query))
                    }
                    ForEach(groups, id: \.day) { group in
                        VStack(alignment: .leading, spacing: 8) {
                            SectionLabel(text: loc.dayTitle(group.day, now: model.now))
                                .padding(.leading, 4)
                            VStack(spacing: 0) {
                                ForEach(Array(group.entries.enumerated()), id: \.element.id) { index, entry in
                                    if index > 0 {
                                        RowDivider().padding(.horizontal, 16)
                                    }
                                    HistoryRow(entry: entry)
                                }
                            }
                            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
                            .clipShape(RoundedRectangle(cornerRadius: theme.cardCornerRadius))
                            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius)
                                .strokeBorder(theme.panelBorder, lineWidth: 1))
                        }
                    }
                } else {
                    EmptyStateCard(symbol: "waveform", title: loc.t("home.empty.title"),
                                   message: loc.t("home.empty.message", keySymbol))
                }
            }
            .padding(.horizontal, 32)
            .padding(.vertical, 28)
        }
    }
}

private struct HomeStack<Content: View>: View {
    @Environment(\.isSnapshot) private var isSnapshot
    @ViewBuilder let content: () -> Content

    var body: some View {
        if isSnapshot {
            VStack(alignment: .leading, spacing: 22, content: content)
        } else {
            // Build the day groups as they approach the scroll viewport.
            LazyVStack(alignment: .leading, spacing: 22, content: content)
        }
    }
}

private struct StatsCard: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let insights: Insights

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            StatColumn(title: loc.t("home.stats.totalWords"), value: loc.number(insights.totalWords),
                       caption: loc.plural("plural.dictations", insights.dictationCount))
            divider
            StatColumn(title: loc.t("home.stats.wpm"), value: loc.number(Int(insights.wordsPerMinute.rounded())),
                       caption: loc.t("home.stats.wpm.caption"))
            divider
            StatColumn(title: loc.t("home.stats.streak"), value: loc.number(insights.currentStreak),
                       caption: loc.t("home.stats.longest", loc.plural("plural.days", insights.longestStreak)))
        }
        .padding(.vertical, 18)
        .frame(maxWidth: .infinity)
        .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }

    private var divider: some View {
        Rectangle().fill(theme.divider).frame(width: 1, height: 70)
    }
}

private struct StatColumn: View {
    @Environment(\.theme) private var theme
    let title: String
    let value: String
    let caption: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(theme.captionMediumFont)
                .foregroundStyle(theme.textSecondary)
            Text(value)
                .font(theme.statNumberFont)
                .foregroundStyle(theme.textPrimary)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            Text(caption)
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
                .lineLimit(1)
        }
        .padding(.horizontal, 20)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct HistoryRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    @State private var justCopied = false
    let entry: HistoryEntry

    private var showsActions: Bool {
        isHovering || model.snapshot?.hoveredRowID == entry.id
    }

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            Text(loc.timeText(entry.date))
                .font(theme.monoCaptionFont)
                .foregroundStyle(theme.textSecondary)
                .frame(width: 58, alignment: .leading)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 6) {
                Text(entry.text)
                    .font(.system(size: 13.5))
                    .foregroundStyle(theme.textPrimary)
                    .lineSpacing(2.5)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
                metaLine
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            actions
                .frame(width: 118, alignment: .topTrailing)
                .opacity(showsActions ? 1 : 0)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(showsActions ? theme.rowHover : Color.clear)
        .contentShape(Rectangle())
        .onHover { isHovering = $0 }
        // The action bar appears only under the pointer; VoiceOver and keyboard users get
        // the same actions on the row itself.
        .accessibilityElement(children: .combine)
        .accessibilityActions {
            if entry.audioFileName != nil {
                Button(model.playingEntryID == entry.id ? loc.t("home.action.stop") : loc.t("home.action.play")) {
                    model.togglePlayback(entry)
                }
            }
            Button(loc.t("home.action.copy")) {
                model.copyToPasteboard(entry.text)
            }
            if entry.audioFileName != nil, !model.rerunningEntryIDs.contains(entry.id) {
                ForEach(model.readyEngines(excluding: entry.engineID), id: \.self) { engine in
                    Button(loc.t("home.action.rerunWith", engine.displayName)) {
                        model.rerun(entry, with: engine)
                    }
                }
            }
            Button(loc.t("home.action.delete")) {
                model.deleteHistoryEntry(entry.id)
            }
        }
    }

    private var metaLine: some View {
        HStack(spacing: 6) {
            if let appName = entry.appName {
                AppIconView(bundleID: entry.appBundleID, name: appName, size: 14)
                Text(appName)
                separator
            }
            Text(entry.engineID.displayName)
            separator
            Text(clockText(entry.duration))
            if entry.fixesCount > 0 {
                separator
                Text(loc.plural("plural.fixes", entry.fixesCount))
                    .foregroundStyle(theme.accentText)
            }
            if model.rerunningEntryIDs.contains(entry.id) {
                separator
                Text(loc.t("home.rerun.running"))
                    .foregroundStyle(theme.accentText)
            }
        }
        .font(theme.captionFont)
        .foregroundStyle(theme.textSecondary)
        .lineLimit(1)
    }

    private var separator: some View {
        Text("·")
    }

    private var actions: some View {
        HoverActionBar {
            if entry.audioFileName != nil {
                let isPlaying = model.playingEntryID == entry.id
                IconButton(symbol: isPlaying ? "stop.fill" : "play.fill",
                           help: isPlaying ? loc.t("home.action.stop") : loc.t("home.action.play")) {
                    model.togglePlayback(entry)
                }
            }
            IconButton(symbol: justCopied ? "checkmark" : "doc.on.doc",
                       help: loc.t("home.action.copy"), tint: justCopied ? theme.success : nil) {
                model.copyToPasteboard(entry.text)
                justCopied = true
                Task {
                    try? await Task.sleep(for: .seconds(1.2))
                    justCopied = false
                }
            }
            MenuButton(entries: rerunEntries) {
                Image(systemName: "arrow.triangle.2.circlepath")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(theme.textSecondary)
                    .frame(width: 26, height: 26)
            }
            .help(loc.t("home.action.rerun"))
            .accessibilityLabel(loc.t("home.action.rerun"))
            .disabled(model.rerunningEntryIDs.contains(entry.id))
            IconButton(symbol: "trash", help: loc.t("home.action.delete")) {
                model.deleteHistoryEntry(entry.id)
            }
        }
    }

    private func rerunEntries() -> [MenuEntry] {
        var entries: [MenuEntry] = [.header(loc.t("home.rerun.header"))]
        for engine in EngineID.transcriptionEngines {
            if engine == entry.engineID {
                entries.append(.action(engine.displayName, isChecked: true, isEnabled: false) {})
            } else if model.modelStatus(engine).isUsable {
                entries.append(.action(engine.displayName) { model.rerun(entry, with: engine) })
            } else {
                entries.append(.action(loc.t("home.rerun.unavailable", engine.displayName), isEnabled: false) {})
            }
        }
        if entry.audioFileName == nil {
            // Without the recording there is nothing to transcribe again.
            return [.header(loc.t("home.rerun.noAudio"))]
        }
        return entries
    }
}
