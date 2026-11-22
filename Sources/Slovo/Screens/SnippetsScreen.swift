import SlovoCore
import SwiftUI

struct SnippetsScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var query = ""
    @State private var draft: SnippetDraft?

    var body: some View {
        let snippets = model.snippetItems
        let visible = snippets
            .filter { matchesSearch(query, $0.trigger, $0.expansion) }
            .sorted { $0.updatedAt > $1.updatedAt }
        PanelScroll {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(loc.t("sidebar.snippets"), subtitle: loc.t("snippets.subtitle")) {
                    Button {
                        draft = SnippetDraft()
                    } label: {
                        Label(loc.t("common.addNew"), systemImage: "plus")
                    }
                    .buttonStyle(PrimaryButtonStyle())
                }

                if model.isBannerVisible(.snippets) {
                    BannerCard(headline: loc.t("snippets.banner.headline"), message: loc.t("snippets.banner.message"),
                               buttonTitle: nil, onDismiss: { model.dismissBanner(.snippets) }) {
                        HStack(spacing: 8) {
                            BannerExample(from: loc.t("snippets.example1.trigger"), to: loc.t("snippets.example1.text"))
                            BannerExample(from: loc.t("snippets.example2.trigger"), to: loc.t("snippets.example2.text"))
                            BannerExample(from: loc.t("snippets.example3.trigger"), to: loc.t("snippets.example3.text"))
                        }
                        .padding(.top, 2)
                    }
                }

                if !snippets.isEmpty {
                    SearchField(placeholder: loc.t("snippets.search"), text: $query)
                }

                if snippets.isEmpty {
                    EmptyStateCard(symbol: "text.badge.plus", title: loc.t("snippets.empty.title"),
                                   message: loc.t("snippets.empty.message"), actionTitle: loc.t("common.addNew")) {
                        draft = SnippetDraft()
                    }
                } else if visible.isEmpty {
                    EmptyStateCard(symbol: "magnifyingglass", title: loc.t("common.nothingFound"),
                                   message: loc.t("snippets.search.empty"))
                } else {
                    VStack(spacing: 0) {
                        ForEach(Array(visible.enumerated()), id: \.element.id) { index, snippet in
                            if index > 0 {
                                RowDivider().padding(.horizontal, 16)
                            }
                            SnippetRow(snippet: snippet) {
                                draft = SnippetDraft(snippet: snippet)
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
        .sheet(item: $draft) { draft in
            AppRoot(model: model) {
                SnippetEditorSheet(draft: draft)
            }
        }
    }
}

private struct SnippetRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let snippet: Snippet
    let onEdit: () -> Void

    private var showsActions: Bool {
        isHovering || model.snapshot?.hoveredRowID == snippet.id
    }

    var body: some View {
        HStack(alignment: .center, spacing: 14) {
            Text(loc.t("snippets.quoted", snippet.trigger))
                .font(theme.bodyMediumFont)
                .foregroundStyle(theme.textPrimary)
                .lineLimit(1)
                .frame(width: 190, alignment: .leading)
            Image(systemName: "arrow.right")
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(theme.textSecondary)
            Text(snippet.expansion.replacingOccurrences(of: "\n", with: " ⏎ "))
                .font(theme.bodyFont)
                .foregroundStyle(theme.textSecondary)
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 0) {
                IconButton(symbol: "pencil", help: loc.t("common.edit"), action: onEdit)
                IconButton(symbol: "trash", help: loc.t("common.delete")) {
                    model.snippets.delete(id: snippet.id)
                }
            }
            .opacity(showsActions ? 1 : 0)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .frame(minHeight: 50)
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
                model.snippets.delete(id: snippet.id)
            }
        }
    }
}

struct SnippetDraft: Identifiable {
    let id: UUID
    let isNew: Bool
    var trigger: String
    var expansion: String
    let createdAt: Date

    init(trigger: String = "", expansion: String = "") {
        id = UUID()
        isNew = true
        self.trigger = trigger
        self.expansion = expansion
        createdAt = Date()
    }

    init(snippet: Snippet) {
        id = snippet.id
        isNew = false
        trigger = snippet.trigger
        expansion = snippet.expansion
        createdAt = snippet.createdAt
    }

    var isValid: Bool {
        !trigger.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !expansion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var snippet: Snippet {
        Snippet(id: id, trigger: trigger.trimmingCharacters(in: .whitespacesAndNewlines),
                expansion: expansion.trimmingCharacters(in: .whitespacesAndNewlines),
                createdAt: createdAt, updatedAt: Date())
    }
}

struct SnippetEditorSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @State private var draft: SnippetDraft

    init(draft: SnippetDraft) {
        _draft = State(initialValue: draft)
    }

    var body: some View {
        SheetContainer(title: draft.isNew ? loc.t("snippets.editor.newTitle") : loc.t("snippets.editor.editTitle")) {
            VStack(alignment: .leading, spacing: 8) {
                FieldLabel(text: loc.t("snippets.editor.trigger"))
                ThemedTextField(placeholder: loc.t("snippets.example1.trigger"), text: $draft.trigger)
                Text(loc.t("snippets.editor.trigger.hint"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.bottom, 8)
                FieldLabel(text: loc.t("snippets.editor.text"))
                ThemedTextArea(placeholder: loc.t("snippets.editor.text.placeholder"), text: $draft.expansion)
                    .frame(height: 130)
            }
        } footer: {
            if !draft.isNew {
                Button(loc.t("common.delete")) {
                    model.snippets.delete(id: draft.id)
                    dismiss()
                }
                .buttonStyle(SecondaryButtonStyle(isDestructive: true))
            }
            Spacer()
            Button(loc.t("common.cancel")) { dismiss() }
                .buttonStyle(SecondaryButtonStyle())
            Button(loc.t("common.save")) {
                if draft.isNew {
                    model.snippets.add(draft.snippet)
                } else {
                    model.snippets.update(draft.snippet)
                }
                dismiss()
            }
            .buttonStyle(PrimaryButtonStyle())
            .disabled(!draft.isValid)
        }
    }
}
