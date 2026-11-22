import SlovoCore
import SwiftUI

/// A speaker's name, «This is me», the default name and merging with another speaker of
/// the same source.
struct SpeakerPopover: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @State private var name: String
    let meeting: MeetingRecord
    let speaker: MeetingSpeaker

    init(meeting: MeetingRecord, speaker: MeetingSpeaker) {
        self.meeting = meeting
        self.speaker = speaker
        _name = State(initialValue: speaker.displayName ?? "")
    }

    var body: some View {
        let mergeTargets = meeting.effectiveSpeakers.filter { $0.track == speaker.track && $0.id != speaker.id }
        // The microphone's speaker counts as «Я» until someone else is marked.
        let isMe = TranscriptMerger.isMe(speaker, in: meeting)
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 9) {
                Circle()
                    .fill(theme.speakerColor(speaker.colorIndex))
                    .frame(width: 10, height: 10)
                VStack(alignment: .leading, spacing: 1) {
                    Text(model.speakerLabel(speaker, in: meeting))
                        .font(theme.headlineFont)
                        .foregroundStyle(theme.textPrimary)
                        .lineLimit(1)
                    Text(sourceLine)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                // The default label is the placeholder: it shows what an empty name brings back.
                ThemedTextField(placeholder: defaultLabel, text: $name, onSubmit: saveName)
                Text(loc.t("meetings.speaker.nameHint"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            RowDivider()
            VStack(alignment: .leading, spacing: 2) {
                Button {
                    model.markAsMe(speaker.id, in: meeting.id)
                    dismiss()
                } label: {
                    PopoverActionLabel(symbol: "person.crop.circle", title: loc.t("meetings.speaker.itsMe"),
                                       isChecked: isMe)
                }
                .buttonStyle(.plain)
                .disabled(isMe)
                Button {
                    model.renameSpeaker(speaker.id, to: nil, in: meeting.id)
                    dismiss()
                } label: {
                    PopoverActionLabel(symbol: "arrow.uturn.backward", title: loc.t("meetings.speaker.reset"))
                }
                .buttonStyle(.plain)
                .disabled(speaker.displayName == nil)
                MenuButton(entries: {
                    mergeTargets.map { target in
                        MenuEntry.action(model.speakerLabel(target, in: meeting)) {
                            model.mergeSpeaker(speaker.id, into: target.id, in: meeting.id)
                            dismiss()
                        }
                    }
                }) {
                    PopoverActionLabel(symbol: "arrow.triangle.merge", title: loc.t("meetings.speaker.merge"), showsChevron: true)
                }
                .disabled(mergeTargets.isEmpty)
                .help(mergeTargets.isEmpty ? loc.t("meetings.speaker.merge.none") : "")
            }
        }
        .padding(14)
        .frame(width: 290, alignment: .leading)
        .background(theme.panelBackground)
    }

    /// The label the speaker has without a name of their own.
    private var defaultLabel: String {
        var unnamed = speaker
        unnamed.displayName = nil
        return model.speakerLabel(unnamed, in: meeting)
    }

    private var sourceLine: String {
        let talk = clockText(meeting.talkTime(of: speaker.id))
        return speaker.track == .microphone ? loc.t("meetings.speaker.source.microphone", talk)
                                            : loc.t("meetings.speaker.source.system", talk)
    }

    private func saveName() {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        model.renameSpeaker(speaker.id, to: trimmed.isEmpty ? nil : trimmed, in: meeting.id)
        dismiss()
    }
}

/// A menu-like row in a popover: icon, title, and a check mark or chevron.
struct PopoverActionLabel: View {
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var isEnabled
    @State private var isHovering = false
    let symbol: String
    let title: String
    var isChecked = false
    var showsChevron = false

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: symbol)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(theme.textSecondary)
                .frame(width: 18)
            Text(title)
                .font(theme.bodyFont)
                .foregroundStyle(theme.textPrimary)
                .lineLimit(1)
            Spacer(minLength: 8)
            if isChecked {
                Image(systemName: "checkmark")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.accentText)
            }
            if showsChevron {
                Image(systemName: "chevron.right")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(theme.textSecondary)
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 28)
        .background(isHovering && isEnabled ? theme.rowHover : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .opacity(isEnabled || isChecked ? 1 : 0.45)
        .contentShape(Rectangle())
        .onHover { isHovering = $0 }
    }
}
