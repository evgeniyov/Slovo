import SlovoCore
import SwiftUI

struct ScratchpadScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        let notes = model.noteItems
        let selected = model.selectedNoteID.flatMap { id in notes.first { $0.id == id } } ?? notes.first
        VStack(alignment: .leading, spacing: 18) {
            PageHeader(loc.t("sidebar.scratchpad"), subtitle: loc.t("scratchpad.subtitle")) {
                HStack(spacing: 8) {
                    DictateIntoNoteButton(dictation: model.dictation, noteID: selected?.id)
                    Button {
                        model.createNote()
                    } label: {
                        Label(loc.t("scratchpad.newNote"), systemImage: "square.and.pencil")
                    }
                    .buttonStyle(PrimaryButtonStyle())
                }
            }

            if let selected {
                HStack(alignment: .top, spacing: 14) {
                    NoteList(notes: notes, selectedID: selected.id)
                        .frame(width: 250)
                    NoteEditor(note: selected)
                        .id(selected.id)
                }
                .frame(maxHeight: .infinity, alignment: .top)
            } else {
                EmptyStateCard(symbol: "note.text", title: loc.t("scratchpad.empty.title"),
                               message: loc.t("scratchpad.empty.message"), actionTitle: loc.t("scratchpad.newNote")) {
                    model.createNote()
                }
                Spacer(minLength: 0)
            }
        }
        .padding(.horizontal, 32)
        .padding(.vertical, 28)
    }
}

private struct DictateIntoNoteButton: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @ObservedObject var dictation: DictationController
    let noteID: UUID?

    var body: some View {
        let isDictatingHere: Bool = {
            guard let noteID, case let .recording(info) = dictation.state else { return false }
            return info.target == .note(noteID)
        }()
        Button {
            if let noteID {
                model.toggleDictation(intoNote: noteID)
            } else {
                model.createNote()
                if let created = model.selectedNoteID {
                    model.toggleDictation(intoNote: created)
                }
            }
        } label: {
            if isDictatingHere {
                HStack(spacing: 7) {
                    Circle().fill(theme.accent).frame(width: 7, height: 7)
                    Text(loc.t("scratchpad.stopDictation"))
                }
            } else {
                Label(loc.t("scratchpad.dictate"), systemImage: "mic")
            }
        }
        .buttonStyle(SecondaryButtonStyle())
    }
}

private struct NoteList: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let notes: [Note]
    let selectedID: UUID

    var body: some View {
        PanelScroll {
            VStack(spacing: 2) {
                ForEach(notes) { note in
                    NoteListRow(note: note, isSelected: note.id == selectedID) {
                        model.selectedNoteID = note.id
                    }
                }
            }
            .padding(6)
        }
        .frame(maxHeight: .infinity)
        .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }
}

private struct NoteListRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let note: Note
    let isSelected: Bool
    let action: () -> Void

    private var preview: String {
        let lines = note.text.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        return lines.dropFirst().first ?? ""
    }

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 3) {
                Text(note.title.isEmpty ? loc.t("scratchpad.untitled") : note.title)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(note.title.isEmpty ? theme.textSecondary : theme.textPrimary)
                    .lineLimit(1)
                if !preview.isEmpty {
                    Text(preview)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .lineLimit(1)
                }
                Text(loc.shortDateTime(note.updatedAt, now: model.now))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 9)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(isSelected ? theme.sidebarSelection : (isHovering ? theme.rowHover : Color.clear),
                        in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
    }
}

private struct NoteEditor: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var draft: String
    @State private var isConfirmingDelete = false
    @State private var justCopied = false
    let note: Note

    init(note: Note) {
        self.note = note
        _draft = State(initialValue: note.text)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Text(loc.t("scratchpad.edited", loc.shortDateTime(note.updatedAt, now: model.now)))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                Text("·")
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                Text(loc.plural("plural.words", HistoryEntry.countWords(in: draft)))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                Spacer()
                IconButton(symbol: justCopied ? "checkmark" : "doc.on.doc", help: loc.t("home.action.copy"),
                           tint: justCopied ? theme.success : nil) {
                    model.copyToPasteboard(draft)
                    justCopied = true
                    Task {
                        try? await Task.sleep(for: .seconds(1.2))
                        justCopied = false
                    }
                }
                IconButton(symbol: "trash", help: loc.t("common.delete")) {
                    isConfirmingDelete = true
                }
            }
            .padding(.horizontal, 14)
            .frame(height: 44)

            RowDivider()

            ThemedTextEditor(placeholder: loc.t("scratchpad.editorPlaceholder"), text: $draft, font: .system(size: 14))
                .padding(.horizontal, 14)
                .padding(.vertical, 12)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
        .onChange(of: draft) { _, newValue in
            save(newValue)
        }
        .onChange(of: note.text) { _, newValue in
            // Dictation appends to the stored note; show it without losing the cursor text.
            if newValue != draft {
                draft = newValue
            }
        }
        .confirmationDialog(loc.t("scratchpad.delete.title"), isPresented: $isConfirmingDelete) {
            Button(loc.t("common.delete"), role: .destructive) {
                model.notes.delete(id: note.id)
                model.selectedNoteID = nil
            }
            Button(loc.t("common.cancel"), role: .cancel) {}
        } message: {
            Text(loc.t("scratchpad.delete.message"))
        }
    }

    private func save(_ text: String) {
        guard model.snapshot == nil, text != note.text else { return }
        var updated = note
        updated.text = text
        updated.updatedAt = Date()
        model.notes.update(updated)
    }
}
