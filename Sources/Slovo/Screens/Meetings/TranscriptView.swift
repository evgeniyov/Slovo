import SlovoCore
import SwiftUI

/// The transcript lines of the selected speakers, scrolled to a line picked in the list
/// of moments.
struct TranscriptView: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.isSnapshot) private var isSnapshot
    let meeting: MeetingRecord
    let lines: [TranscriptLine]
    @Binding var focusedSegment: Int?

    var body: some View {
        if lines.isEmpty {
            Text(loc.t("meetings.transcript.filtered"))
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
                .padding(18)
        } else {
            let focusedID = lineID(containing: focusedSegment)
            ScrollViewReader { proxy in
                PanelScroll {
                    rows(focusedID: focusedID)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 10)
                }
                .onAppear { scroll(proxy, to: focusedID) }
                .onChange(of: focusedSegment) { _, segment in
                    scroll(proxy, to: lineID(containing: segment))
                }
            }
        }
    }

    /// Long meetings have thousands of lines; the window builds only visible rows.
    /// Screenshots have no scroll view, so they lay out every row.
    @ViewBuilder
    private func rows(focusedID: Int?) -> some View {
        if isSnapshot {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(lines) { line in
                    TranscriptLineRow(meeting: meeting, line: line, isFocused: line.id == focusedID)
                }
            }
        } else {
            LazyVStack(alignment: .leading, spacing: 2) {
                ForEach(lines) { line in
                    TranscriptLineRow(meeting: meeting, line: line, isFocused: line.id == focusedID)
                        .id(line.id)
                }
            }
        }
    }

    private func lineID(containing segment: Int?) -> Int? {
        guard let segment else { return nil }
        return lines.first { $0.segmentIndices.contains(segment) }?.id
    }

    private func scroll(_ proxy: ScrollViewProxy, to id: Int?) {
        guard let id else { return }
        withAnimation(.easeInOut(duration: 0.25)) {
            proxy.scrollTo(id, anchor: .center)
        }
    }
}

/// Time, the coloured speaker, the text and notes of marked moments. The star in the
/// gutter marks the line; the context menu holds every line action.
private struct TranscriptLineRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    @State private var isEditingSpeaker = false
    @State private var isEditingNote = false
    let meeting: MeetingRecord
    let line: TranscriptLine
    let isFocused: Bool

    var body: some View {
        let speaker = meeting.speaker(id: line.speakerID) ?? MeetingSpeaker.fallback(for: line.track)
        let highlights = meeting.highlights.filter { line.highlightIDs.contains($0.id) }
        let isMarked = !highlights.isEmpty
        let showsHover = isHovering || model.snapshot?.hoveredTranscriptLineID == line.id
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Button {
                model.toggleHighlight(for: line, in: meeting.id)
            } label: {
                Text(Image(systemName: isMarked ? "star.fill" : "star"))
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(isMarked ? theme.highlightStar : theme.textSecondary)
                    .frame(width: 16)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .opacity(isMarked || showsHover ? 1 : 0)
            .help(isMarked ? loc.t("meetings.highlight.remove") : loc.t("meetings.highlight.add"))
            .accessibilityLabel(isMarked ? loc.t("meetings.highlight.remove") : loc.t("meetings.highlight.add"))

            Button {
                model.seekMeetingPlayback(meeting, to: line.start)
            } label: {
                Text(clockText(line.start))
                    .font(theme.monoCaptionFont)
                    .foregroundStyle(theme.textSecondary)
                    .frame(width: 40, alignment: .leading)
            }
            .buttonStyle(.plain)
            .disabled(!meeting.hasAudio)
            .help(meeting.hasAudio ? loc.t("meetings.playback.jump") : "")

            VStack(alignment: .leading, spacing: 3) {
                Button {
                    isEditingSpeaker = true
                } label: {
                    Text(model.speakerLabel(speaker, in: meeting))
                        .font(theme.captionMediumFont)
                        .foregroundStyle(theme.speakerColor(speaker.colorIndex))
                }
                .buttonStyle(.plain)
                .help(loc.t("meetings.speaker.rename"))
                .popover(isPresented: $isEditingSpeaker, arrowEdge: .bottom) {
                    AppRoot(model: model) {
                        SpeakerPopover(meeting: meeting, speaker: speaker)
                    }
                }
                Button {
                    model.seekMeetingPlayback(meeting, to: line.start)
                } label: {
                    Text(line.text)
                        .font(theme.bodyFont)
                        .foregroundStyle(theme.textPrimary)
                        .lineSpacing(2.5)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(!meeting.hasAudio)
                .help(meeting.hasAudio ? loc.t("meetings.playback.jump") : "")
                ForEach(highlights.filter { !($0.note ?? "").isEmpty }) { highlight in
                    HStack(alignment: .firstTextBaseline, spacing: 5) {
                        Text(Image(systemName: "text.bubble"))
                            .foregroundStyle(theme.highlightStar)
                        Text(highlight.note ?? "")
                            .foregroundStyle(theme.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .font(theme.captionFont)
                    .padding(.top, 1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 7)
        .background(isFocused ? theme.accentSoft : (showsHover ? theme.rowHover : Color.clear),
                    in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
        .contentShape(Rectangle())
        .onHover { isHovering = $0 }
        .contextMenu {
            lineMenu(speaker: speaker, isMarked: isMarked)
        }
        .popover(isPresented: $isEditingNote, arrowEdge: .bottom) {
            AppRoot(model: model) {
                HighlightNoteEditor(meetingID: meeting.id, line: line, note: highlights.first?.note ?? "")
            }
        }
    }

    @ViewBuilder
    private func lineMenu(speaker: MeetingSpeaker, isMarked: Bool) -> some View {
        let targets = meeting.effectiveSpeakers.filter { $0.track == line.track && $0.id != line.speakerID }
        Button(isMarked ? loc.t("meetings.highlight.remove") : loc.t("meetings.highlight.add")) {
            model.toggleHighlight(for: line, in: meeting.id)
        }
        Button(loc.t("meetings.highlight.note")) {
            isEditingNote = true
        }
        Button(loc.t("meetings.line.copy")) {
            model.copyToPasteboard(lineText(speaker: speaker, isMarked: isMarked))
        }
        Divider()
        Menu(loc.t("meetings.line.reassign")) {
            ForEach(targets) { target in
                Button(model.speakerLabel(target, in: meeting)) {
                    model.reassignLine(line, to: target.id, in: meeting.id)
                }
            }
        }
        .disabled(targets.isEmpty)
        Button(loc.t("meetings.speaker.rename")) {
            isEditingSpeaker = true
        }
        Button(loc.t("meetings.speaker.itsMe")) {
            model.markAsMe(line.speakerID, in: meeting.id)
        }
        .disabled(TranscriptMerger.isMe(speaker, in: meeting))
    }

    /// The line as `transcript.md` writes it: `[12:38] ★ Аня: text`.
    private func lineText(speaker: MeetingSpeaker, isMarked: Bool) -> String {
        "[\(TranscriptMerger.timestamp(line.start))] \(isMarked ? "★ " : "")\(model.speakerLabel(speaker, in: meeting)): \(line.text)"
    }
}

/// A note for the moment on a line; saving marks the line first when it has no moment.
struct HighlightNoteEditor: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @State private var note: String
    let meetingID: UUID
    let line: TranscriptLine

    init(meetingID: UUID, line: TranscriptLine, note: String) {
        self.meetingID = meetingID
        self.line = line
        _note = State(initialValue: note)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ThemedTextField(placeholder: loc.t("meetings.highlight.notePlaceholder"), text: $note, onSubmit: save)
            HStack(spacing: 8) {
                Spacer()
                Button(loc.t("common.cancel")) {
                    dismiss()
                }
                .buttonStyle(SecondaryButtonStyle())
                Button(loc.t("common.save"), action: save)
                    .buttonStyle(PrimaryButtonStyle())
            }
        }
        .padding(14)
        .frame(width: 300)
        .background(theme.panelBackground)
    }

    private func save() {
        if let id = model.highlightForNote(on: line, in: meetingID) {
            model.setHighlightNote(id, note: cleanedNote(note), in: meetingID)
        }
        dismiss()
    }
}

/// One line of at most 500 characters, or nil when nothing is left.
func cleanedNote(_ text: String) -> String? {
    let oneLine = text.split(whereSeparator: \.isNewline).joined(separator: " ").trimmingCharacters(in: .whitespaces)
    return oneLine.isEmpty ? nil : String(oneLine.prefix(500))
}
