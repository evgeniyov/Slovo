import SlovoCore
import SwiftUI

struct DictionaryScreen: View {
    enum Tab: Hashable {
        case all
        case terms
        case replacements
    }

    enum Sort: Hashable {
        case newest
        case alphabetical
    }

    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var tab: Tab = .all
    @State private var sort: Sort = .newest
    @State private var query = ""
    @State private var draft: DictionaryDraft?
    @State private var suggestions: [DictionarySuggestion] = []

    var body: some View {
        let items = model.dictionaryItems
        let visible = visibleItems(items)
        let input = DictionarySuggestions.Input(history: model.historyEntries, dictionary: items)
        let proposed = model.snapshot?.dictionarySuggestions ?? (input.history.isEmpty ? [] : suggestions)
        PanelScroll {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(loc.t("sidebar.dictionary"), subtitle: loc.t("dictionary.subtitle")) {
                    Button {
                        draft = DictionaryDraft()
                    } label: {
                        Label(loc.t("common.addNew"), systemImage: "plus")
                    }
                    .buttonStyle(PrimaryButtonStyle())
                }

                if model.isBannerVisible(.dictionary) {
                    BannerCard(headline: loc.t("dictionary.banner.headline"), message: loc.t("dictionary.banner.message"),
                               buttonTitle: nil, onDismiss: { model.dismissBanner(.dictionary) }) {
                        HStack(spacing: 8) {
                            BannerExample(from: "кубернетес", to: "Kubernetes")
                            BannerExample(from: "гитхаб экшенс", to: "GitHub Actions")
                            BannerExample(from: "юзер дефолтс", to: "UserDefaults")
                        }
                        .padding(.top, 2)
                    }
                }

                if !proposed.isEmpty {
                    suggestionSection(proposed)
                }

                HStack(spacing: 10) {
                    ChoiceChips(options: [(Tab.all, loc.t("dictionary.tab.all")),
                                          (Tab.terms, loc.t("dictionary.tab.terms")),
                                          (Tab.replacements, loc.t("dictionary.tab.replacements"))],
                                selection: $tab,
                                counts: [.all: items.count,
                                         .terms: items.filter { $0.kind == .term }.count,
                                         .replacements: items.filter { $0.kind == .replacement }.count])
                    Spacer(minLength: 8)
                    PopupChoice(options: [(Sort.newest, loc.t("dictionary.sort.newest")),
                                          (Sort.alphabetical, loc.t("dictionary.sort.alphabetical"))],
                                selection: $sort)
                    SearchField(placeholder: loc.t("dictionary.search"), text: $query)
                        .frame(width: 210)
                }

                if items.isEmpty {
                    EmptyStateCard(symbol: "character.book.closed", title: loc.t("dictionary.empty.title"),
                                   message: loc.t("dictionary.empty.message"), actionTitle: loc.t("common.addNew")) {
                        draft = DictionaryDraft()
                    }
                } else if visible.isEmpty {
                    EmptyStateCard(symbol: "magnifyingglass", title: loc.t("common.nothingFound"),
                                   message: loc.t("dictionary.search.empty"))
                } else {
                    VStack(spacing: 0) {
                        ForEach(Array(visible.enumerated()), id: \.element.id) { index, item in
                            if index > 0 {
                                RowDivider().padding(.horizontal, 16)
                            }
                            DictionaryRow(item: item, showsKind: tab == .all) {
                                draft = DictionaryDraft(item: item)
                            }
                        }
                    }
                    .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
                    .clipShape(RoundedRectangle(cornerRadius: theme.cardCornerRadius))
                    .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
                }
            }
            .padding(.horizontal, 32)
            .padding(.vertical, 28)
        }
        .task(id: input) { await loadSuggestions(for: input) }
        .sheet(item: $draft) { draft in
            AppRoot(model: model) {
                DictionaryEditorSheet(draft: draft)
            }
        }
    }

    private func loadSuggestions(for input: DictionarySuggestions.Input) async {
        guard model.snapshot == nil else { return }
        suggestions = []
        guard !input.history.isEmpty else { return }
        let worker = Task.detached(priority: .utility) { input.suggestions() }
        let computed = await withTaskCancellationHandler {
            await worker.value
        } onCancel: {
            worker.cancel()
        }
        guard !Task.isCancelled,
              input == DictionarySuggestions.Input(history: model.historyEntries, dictionary: model.dictionaryItems)
        else { return }
        let dismissed = model.settings.dismissedDictionarySuggestions
        suggestions = computed.filter { !dismissed.contains($0.id) }
    }

    private func suggestionSection(_ proposed: [DictionarySuggestion]) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            Text(loc.t("dictionary.suggestions.title"))
                .font(theme.sectionTitleFont)
                .foregroundStyle(theme.textPrimary)
                .accessibilityAddTraits(.isHeader)
            VStack(spacing: 0) {
                ForEach(Array(proposed.enumerated()), id: \.element.id) { index, suggestion in
                    if index > 0 {
                        RowDivider().padding(.horizontal, 16)
                    }
                    suggestionRow(suggestion)
                }
            }
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
        }
    }

    private func suggestionRow(_ suggestion: DictionarySuggestion) -> some View {
        HStack(spacing: 8) {
            HStack(spacing: 8) {
                Text(suggestion.from)
                    .foregroundStyle(theme.textSecondary)
                Image(systemName: "arrow.right")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(theme.textSecondary)
                    .accessibilityHidden(true)
                Text(suggestion.to)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(theme.textPrimary)
                Text("· " + loc.plural("plural.dictionaryOccurrences", suggestion.occurrences))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize()
            }
            .font(theme.bodyFont)
            .lineLimit(1)
            .help(suggestion.examples.joined(separator: "\n"))
            .accessibilityElement(children: .combine)
            Spacer(minLength: 4)
            Button(loc.t("dictionary.suggestions.dismiss")) {
                model.settings.dismissedDictionarySuggestions.insert(suggestion.id)
                suggestions.removeAll { $0.id == suggestion.id }
            }
            .buttonStyle(SecondaryButtonStyle())
            Button(loc.t("dictionary.suggestions.add")) {
                model.dictionary.add(.replacement(from: suggestion.from, to: suggestion.to))
                suggestions.removeAll { $0.id == suggestion.id }
            }
            .buttonStyle(PrimaryButtonStyle())
        }
        .padding(.horizontal, 14)
        .frame(height: 46)
    }

    private func visibleItems(_ items: [DictionaryItem]) -> [DictionaryItem] {
        let filtered = items.filter { item in
            switch tab {
            case .all: break
            case .terms: guard item.kind == .term else { return false }
            case .replacements: guard item.kind == .replacement else { return false }
            }
            return matchesSearch(query, item.text, item.replacement)
        }
        switch sort {
        case .newest:
            return filtered.sorted { $0.createdAt > $1.createdAt }
        case .alphabetical:
            return filtered.sorted { $0.text.localizedStandardCompare($1.text) == .orderedAscending }
        }
    }
}

private struct DictionaryRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let item: DictionaryItem
    let showsKind: Bool
    let onEdit: () -> Void

    private var showsActions: Bool {
        isHovering || model.snapshot?.hoveredRowID == item.id
    }

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: item.kind == .term ? "textformat.abc" : "arrow.left.arrow.right")
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(item.kind == .term ? theme.accentText : theme.secondaryAccent)
                .frame(width: 26, height: 26)
                .background(item.kind == .term ? theme.accentSoft : theme.insetBackground,
                            in: RoundedRectangle(cornerRadius: 7))
            if item.kind == .replacement {
                Text(item.text)
                    .font(theme.bodyFont)
                    .foregroundStyle(theme.textSecondary)
                    .lineLimit(1)
                Image(systemName: "arrow.right")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(theme.textSecondary)
                Text(item.replacement)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(theme.textPrimary)
                    .lineLimit(1)
            } else {
                Text(item.text)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(theme.textPrimary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if showsKind, !showsActions {
                Text(item.kind == .term ? loc.t("dictionary.kind.term") : loc.t("dictionary.kind.replacement"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
            }
            if showsActions {
                HStack(spacing: 0) {
                    IconButton(symbol: "pencil", help: loc.t("common.edit"), action: onEdit)
                    IconButton(symbol: "trash", help: loc.t("common.delete")) {
                        model.dictionary.delete(id: item.id)
                    }
                }
            }
        }
        .padding(.horizontal, 14)
        .frame(height: 46)
        .background(showsActions ? theme.rowHover : Color.clear)
        .contentShape(Rectangle())
        .onHover { isHovering = $0 }
        .onTapGesture(count: 2, perform: onEdit)
        // Edit and Delete show only under the pointer; VoiceOver and keyboard users get
        // them as actions of the row.
        .accessibilityElement(children: .combine)
        .accessibilityActions {
            Button(loc.t("common.edit"), action: onEdit)
            Button(loc.t("common.delete")) {
                model.dictionary.delete(id: item.id)
            }
        }
    }
}

/// The Add/Edit sheet's working copy.
struct DictionaryDraft: Identifiable {
    let id: UUID
    let isNew: Bool
    var kind: DictionaryItem.Kind
    var text: String
    var replacement: String
    let createdAt: Date

    init(kind: DictionaryItem.Kind = .term, text: String = "", replacement: String = "") {
        id = UUID()
        isNew = true
        self.kind = kind
        self.text = text
        self.replacement = replacement
        createdAt = Date()
    }

    init(item: DictionaryItem) {
        id = item.id
        isNew = false
        kind = item.kind
        text = item.text
        replacement = item.replacement
        createdAt = item.createdAt
    }

    var trimmedText: String { text.trimmingCharacters(in: .whitespacesAndNewlines) }
    var trimmedReplacement: String { replacement.trimmingCharacters(in: .whitespacesAndNewlines) }

    var isValid: Bool {
        !trimmedText.isEmpty && (kind == .term || !trimmedReplacement.isEmpty)
    }

    var item: DictionaryItem {
        DictionaryItem(id: id, kind: kind, text: trimmedText,
                       replacement: kind == .replacement ? trimmedReplacement : "", createdAt: createdAt)
    }
}

struct DictionaryEditorSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @State private var draft: DictionaryDraft

    init(draft: DictionaryDraft) {
        _draft = State(initialValue: draft)
    }

    private var isDuplicate: Bool {
        let key = searchKey(draft.trimmedText)
        return !key.isEmpty && model.dictionaryItems.contains {
            $0.id != draft.id && $0.kind == draft.kind && searchKey($0.text) == key
        }
    }

    var body: some View {
        SheetContainer(title: draft.isNew ? loc.t("dictionary.editor.newTitle") : loc.t("dictionary.editor.editTitle")) {
            VStack(alignment: .leading, spacing: 14) {
                ChoiceChips(options: [(DictionaryItem.Kind.term, loc.t("dictionary.kind.term")),
                                      (DictionaryItem.Kind.replacement, loc.t("dictionary.kind.replacement"))],
                            selection: $draft.kind)
                if draft.kind == .term {
                    FieldLabel(text: loc.t("dictionary.editor.term"))
                    ThemedTextField(placeholder: "Kubernetes", text: $draft.text, onSubmit: save)
                    Text(loc.t("dictionary.editor.term.hint"))
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    HStack(alignment: .bottom, spacing: 10) {
                        VStack(alignment: .leading, spacing: 6) {
                            FieldLabel(text: loc.t("dictionary.editor.from"))
                            ThemedTextField(placeholder: "гитхаб", text: $draft.text, onSubmit: save)
                        }
                        Image(systemName: "arrow.right")
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(theme.textSecondary)
                            .frame(height: 32)
                        VStack(alignment: .leading, spacing: 6) {
                            FieldLabel(text: loc.t("dictionary.editor.to"))
                            ThemedTextField(placeholder: "GitHub", text: $draft.replacement, onSubmit: save)
                        }
                    }
                    Text(loc.t("dictionary.editor.replacement.hint"))
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if isDuplicate {
                    InlineNotice(symbol: "exclamationmark.circle", text: loc.t("dictionary.editor.duplicate"))
                }
            }
        } footer: {
            if !draft.isNew {
                Button(loc.t("common.delete")) {
                    model.dictionary.delete(id: draft.id)
                    dismiss()
                }
                .buttonStyle(SecondaryButtonStyle(isDestructive: true))
            }
            Spacer()
            Button(loc.t("common.cancel")) { dismiss() }
                .buttonStyle(SecondaryButtonStyle())
            Button(loc.t("common.save"), action: save)
                .buttonStyle(PrimaryButtonStyle())
                .disabled(!draft.isValid)
        }
    }

    private func save() {
        guard draft.isValid else { return }
        if draft.isNew {
            model.dictionary.add(draft.item)
        } else {
            model.dictionary.update(draft.item)
        }
        dismiss()
    }
}

/// A caption above a field in a sheet.
struct FieldLabel: View {
    @Environment(\.theme) private var theme
    let text: String

    var body: some View {
        Text(text)
            .font(theme.captionMediumFont)
            .foregroundStyle(theme.textSecondary)
    }
}

/// The frame shared by editor sheets: a title, content and a row of buttons.
struct SheetContainer<Content: View, Footer: View>: View {
    @Environment(\.theme) private var theme
    let title: String
    let content: Content
    let footer: Footer

    init(title: String, @ViewBuilder content: () -> Content, @ViewBuilder footer: () -> Footer) {
        self.title = title
        self.content = content()
        self.footer = footer()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(title)
                .font(theme.sectionTitleFont)
                .foregroundStyle(theme.textPrimary)
            content
            HStack(spacing: 8) {
                footer
            }
            .padding(.top, 4)
        }
        .padding(24)
        .frame(width: 480, alignment: .topLeading)
        .background(theme.panelBackground)
    }
}
