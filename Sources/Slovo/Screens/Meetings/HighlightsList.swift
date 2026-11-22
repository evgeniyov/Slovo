import SlovoCore
import SwiftUI

/// A moment with the transcript line it belongs to, if any.
private struct HighlightItem: Identifiable {
    let highlight: MeetingHighlight
    let line: TranscriptLine?

    var id: UUID { highlight.id }
}

/// The meeting's important moments in time order: speaker, the start of the line, an
/// editable note and delete. A row leads to its line in the transcript.
struct HighlightsList: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var justCopied = false
    let meeting: MeetingRecord
    /// Unfiltered lines, so every moment finds its own line.
    let allLines: [TranscriptLine]
    let filter: Set<String>
    let onShow: (TranscriptLine) -> Void

    var body: some View {
        let items = meeting.highlights
            .sorted { $0.time < $1.time }
            .map { HighlightItem(highlight: $0, line: anchoredLine(of: $0, in: allLines)) }
            .filter { item in filter.isEmpty || item.line.map { filter.contains($0.speakerID) } == true }
        if meeting.highlights.isEmpty {
            VStack(spacing: 8) {
                Image(systemName: "star")
                    .font(.system(size: 22, weight: .light))
                    .foregroundStyle(theme.textSecondary)
                Text(loc.t("meetings.highlights.empty.title"))
                    .font(theme.headlineFont)
                    .foregroundStyle(theme.textPrimary)
                Text(loc.t("meetings.highlights.empty", model.settings.highlightShortcut.symbols))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: 320)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 40)
        } else {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 8) {
                    Text(loc.plural("plural.moments", items.count))
                        .font(theme.captionMediumFont)
                        .foregroundStyle(theme.textSecondary)
                    Spacer(minLength: 8)
                    Button {
                        model.copyToPasteboard(model.highlightsText(meeting, speakerFilter: filter.isEmpty ? nil : filter))
                        justCopied = true
                        Task {
                            try? await Task.sleep(for: .seconds(1.2))
                            justCopied = false
                        }
                    } label: {
                        Label(justCopied ? loc.t("common.copied") : loc.t("meetings.highlights.copy"),
                              systemImage: justCopied ? "checkmark" : "doc.on.doc")
                    }
                    .buttonStyle(SecondaryButtonStyle())
                    .disabled(items.isEmpty)
                }
                .padding(.horizontal, 18)
                .padding(.vertical, 10)
                RowDivider()
                if items.isEmpty {
                    Text(loc.t("meetings.highlights.filtered"))
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .padding(18)
                } else {
                    PanelScroll {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                                if index > 0 {
                                    RowDivider()
                                        .padding(.leading, 44)
                                }
                                HighlightRow(meeting: meeting, item: item, onShow: onShow)
                            }
                        }
                        .padding(.vertical, 4)
                    }
                }
            }
        }
    }
}

private struct HighlightRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let meeting: MeetingRecord
    let item: HighlightItem
    let onShow: (TranscriptLine) -> Void

    var body: some View {
        let speaker = item.line.flatMap { meeting.speaker(id: $0.speakerID) }
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(Image(systemName: "star.fill"))
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(theme.highlightStar)
                .frame(width: 16)
            VStack(alignment: .leading, spacing: 6) {
                Button {
                    model.seekMeetingPlayback(meeting, to: item.highlight.time)
                    if let line = item.line {
                        onShow(line)
                    }
                } label: {
                    VStack(alignment: .leading, spacing: 3) {
                        HStack(spacing: 6) {
                            Text(clockText(item.highlight.time))
                                .font(theme.monoCaptionFont)
                                .foregroundStyle(theme.textSecondary)
                            if let speaker {
                                Text(model.speakerLabel(speaker, in: meeting))
                                    .font(theme.captionMediumFont)
                                    .foregroundStyle(theme.speakerColor(speaker.colorIndex))
                            }
                            if item.line != nil {
                                Image(systemName: "arrow.up.forward")
                                    .font(.system(size: 9, weight: .semibold))
                                    .foregroundStyle(theme.textSecondary)
                                    .opacity(isHovering ? 1 : 0)
                            }
                        }
                        if let line = item.line {
                            Text(loc.t("meetings.highlight.quoted", AppModel.excerpt(line.text)))
                                .font(theme.bodyFont)
                                .foregroundStyle(theme.textPrimary)
                                .lineSpacing(2)
                                .fixedSize(horizontal: false, vertical: true)
                        } else {
                            Text(loc.t("meetings.highlight.noLine"))
                                .font(theme.bodyFont)
                                .foregroundStyle(theme.textSecondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(item.line != nil ? loc.t("meetings.highlight.show")
                                       : (meeting.hasAudio ? loc.t("meetings.playback.jump") : ""))
                HighlightNoteField(meetingID: meeting.id, highlight: item.highlight)
            }
            IconButton(symbol: "trash", help: loc.t("meetings.highlight.delete")) {
                model.deleteHighlight(item.highlight.id, in: meeting.id)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(isHovering ? theme.rowHover : Color.clear)
        .onHover { isHovering = $0 }
    }
}

/// An inline note that saves on Return and when it loses focus.
private struct HighlightNoteField: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.isSnapshot) private var isSnapshot
    @FocusState private var isFocused: Bool
    @State private var note: String
    let meetingID: UUID
    let highlight: MeetingHighlight

    init(meetingID: UUID, highlight: MeetingHighlight) {
        self.meetingID = meetingID
        self.highlight = highlight
        _note = State(initialValue: highlight.note ?? "")
    }

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "text.bubble")
                .font(.system(size: 11))
                .foregroundStyle(theme.textSecondary)
            if isSnapshot {
                Text(note.isEmpty ? loc.t("meetings.highlight.notePlaceholder") : note)
                    .foregroundStyle(note.isEmpty ? theme.textSecondary : theme.textPrimary)
                    .lineLimit(1)
                Spacer(minLength: 0)
            } else {
                TextField(loc.t("meetings.highlight.notePlaceholder"), text: $note)
                    .textFieldStyle(.plain)
                    .foregroundStyle(theme.textPrimary)
                    .focused($isFocused)
                    .onSubmit(save)
                    .onChange(of: isFocused) { _, focused in
                        if !focused {
                            save()
                        }
                    }
            }
        }
        .font(theme.captionFont)
        .padding(.horizontal, 8)
        .frame(height: 26)
        .background(theme.fieldBackground, in: RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(theme.divider, lineWidth: 1))
        .onChange(of: highlight.note) { _, newValue in
            if !isFocused {
                note = newValue ?? ""
            }
        }
    }

    private func save() {
        let value = cleanedNote(note)
        guard value != highlight.note else { return }
        model.setHighlightNote(highlight.id, note: value, in: meetingID)
    }
}
