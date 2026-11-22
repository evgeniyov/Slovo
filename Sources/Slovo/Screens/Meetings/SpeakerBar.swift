import SlovoCore
import SwiftUI

/// «All» and one chip per speaker with its colour and talk time. Chips toggle a filter
/// shared by the transcript, Copy and the list of moments; the pencil opens the speaker's
/// name and merge controls.
struct SpeakerBar: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let meeting: MeetingRecord
    @Binding var filter: Set<String>

    var body: some View {
        let speakers = meeting.effectiveSpeakers
        let active = filter.intersection(speakers.map(\.id))
        WrapLayout(spacing: 6, lineSpacing: 6) {
            Button {
                filter.removeAll()
            } label: {
                Text(loc.t("meetings.speakers.all"))
                    .font(.system(size: 12, weight: active.isEmpty ? .semibold : .medium))
                    .foregroundStyle(active.isEmpty ? theme.primaryButtonText : theme.secondaryButtonText)
                    .padding(.horizontal, 12)
                    .frame(height: 28)
                    .background(active.isEmpty ? theme.primaryButtonBackground : theme.secondaryButtonBackground,
                                in: Capsule())
                    .contentShape(Capsule())
            }
            .buttonStyle(.plain)
            ForEach(speakers) { speaker in
                SpeakerChip(meeting: meeting, speaker: speaker, isSelected: active.contains(speaker.id)) {
                    if filter.contains(speaker.id) {
                        filter.remove(speaker.id)
                    } else {
                        filter.insert(speaker.id)
                    }
                }
            }
        }
    }
}

private struct SpeakerChip: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isEditing = false
    let meeting: MeetingRecord
    let speaker: MeetingSpeaker
    let isSelected: Bool
    let toggle: () -> Void

    var body: some View {
        let color = theme.speakerColor(speaker.colorIndex)
        HStack(spacing: 0) {
            Button(action: toggle) {
                HStack(spacing: 6) {
                    Circle()
                        .fill(color)
                        .frame(width: 8, height: 8)
                    Text(model.speakerLabel(speaker, in: meeting))
                        .font(.system(size: 12, weight: isSelected ? .semibold : .medium))
                        .foregroundStyle(theme.textPrimary)
                        .lineLimit(1)
                    Text(clockText(meeting.talkTime(of: speaker.id)))
                        .font(theme.monoCaptionFont)
                        .foregroundStyle(theme.textSecondary)
                }
                .padding(.leading, 10)
                .padding(.trailing, 4)
                .frame(height: 28)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            Button {
                isEditing = true
            } label: {
                Image(systemName: "pencil")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(theme.textSecondary)
                    .frame(width: 22, height: 28)
                    .padding(.trailing, 3)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(loc.t("meetings.speaker.edit"))
            .popover(isPresented: $isEditing, arrowEdge: .bottom) {
                AppRoot(model: model) {
                    SpeakerPopover(meeting: meeting, speaker: speaker)
                }
            }
        }
        .background(isSelected ? color.opacity(0.14) : theme.secondaryButtonBackground, in: Capsule())
        .overlay(Capsule().strokeBorder(isSelected ? color.opacity(0.7) : Color.clear, lineWidth: 1))
        .fixedSize()
    }
}
