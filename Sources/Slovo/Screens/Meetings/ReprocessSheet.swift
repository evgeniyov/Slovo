import SlovoCore
import SwiftUI

/// «Process again…»: speakers only or speakers and text, the number of people and the
/// engine. Choices that cannot run say why.
struct ReprocessSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @State private var mode: MeetingProcessingOptions.Mode
    @State private var separateSpeakers: Bool
    @State private var expectedSpeakers: Int
    @State private var engine: EngineID
    let meeting: MeetingRecord

    init(meeting: MeetingRecord, initial: MeetingProcessingOptions) {
        self.meeting = meeting
        _mode = State(initialValue: initial.mode)
        _separateSpeakers = State(initialValue: initial.separateSpeakers)
        _expectedSpeakers = State(initialValue: AppSettings.normalizedExpectedSpeakers(initial.expectedSpeakers ?? 0))
        _engine = State(initialValue: initial.engineID ?? .gigaamCTC)
    }

    var body: some View {
        let speakersOnlyReason = unavailableReason(for: .speakersOnly)
        let fullReason = unavailableReason(for: .full)
        let modelReason = speakerModelReason
        let separates = mode == .speakersOnly || (separateSpeakers && modelReason == nil)
        SheetContainer(title: loc.t("meetings.reprocess.title")) {
            VStack(alignment: .leading, spacing: 14) {
                Text(meeting.title)
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .lineLimit(1)
                    .padding(.top, -10)
                VStack(spacing: 8) {
                    ModeOption(symbol: "person.2.wave.2", title: loc.t("meetings.reprocess.mode.speakers"),
                               detail: loc.t("meetings.reprocess.mode.speakers.detail"),
                               isSelected: mode == .speakersOnly, reason: speakersOnlyReason) {
                        mode = .speakersOnly
                    }
                    ModeOption(symbol: "text.bubble", title: loc.t("meetings.reprocess.mode.full"),
                               detail: loc.t("meetings.reprocess.mode.full.detail"),
                               isSelected: mode == .full, reason: fullReason) {
                        mode = .full
                    }
                }
                VStack(alignment: .leading, spacing: 0) {
                    if mode == .full {
                        SettingsRow(loc.t("settings.meetings.separate"), detail: modelReason) {
                            // Without the model the run cannot separate, so the switch shows off.
                            ThemedToggle(isOn: Binding(get: { separateSpeakers && modelReason == nil },
                                                       set: { separateSpeakers = $0 }))
                                .disabled(modelReason != nil)
                        }
                        RowDivider()
                    }
                    SettingsRow(loc.t("settings.meetings.people"), detail: loc.t("settings.meetings.people.detail")) {
                        PopupChoice(options: AppSettings.expectedSpeakerChoices.map { ($0, expectedSpeakersTitle($0, loc: loc)) },
                                    selection: $expectedSpeakers)
                    }
                    .disabled(!separates)
                    if mode == .full {
                        RowDivider()
                        SettingsRow(loc.t("settings.meetings.engine")) {
                            PopupChoice(options: EngineID.meetingChoices.map { ($0, engineChoiceTitle($0, model: model, loc: loc)) },
                                        selection: $engine)
                        }
                    }
                }
                .padding(.horizontal, 14)
                .background(theme.insetBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
                .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.divider, lineWidth: 1))
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Image(systemName: "checkmark.circle")
                    Text(loc.t("meetings.reprocess.keep"))
                        .fixedSize(horizontal: false, vertical: true)
                }
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
            }
        } footer: {
            Spacer()
            Button(loc.t("common.cancel")) {
                dismiss()
            }
            .buttonStyle(SecondaryButtonStyle())
            Button(loc.t("meetings.reprocess.start"), action: start)
                .buttonStyle(PrimaryButtonStyle())
                .disabled(unavailableReason(for: mode) != nil || !model.canProcess(meeting, mode: mode))
        }
        .onAppear {
            // Open on a mode that can run.
            let other: MeetingProcessingOptions.Mode = mode == .full ? .speakersOnly : .full
            if unavailableReason(for: mode) != nil, unavailableReason(for: other) == nil {
                mode = other
            }
        }
    }

    private func start() {
        let options = MeetingProcessingOptions(
            mode: mode,
            separateSpeakers: mode == .speakersOnly || separateSpeakers,
            expectedSpeakers: expectedSpeakers == 0 ? nil : expectedSpeakers,
            engineID: mode == .full ? engine : nil)
        model.reprocessMeeting(meeting.id, options: options)
        dismiss()
    }

    /// Why a mode cannot run now; nil when it can.
    private func unavailableReason(for mode: MeetingProcessingOptions.Mode) -> String? {
        guard !model.canProcess(meeting, mode: mode) else { return nil }
        if !meeting.hasAudio {
            return loc.t("meetings.reprocess.noAudio")
        }
        if meeting.status == .recording || meeting.status == .transcribing || model.transcriptionProgress(for: meeting.id) != nil {
            return loc.t("meetings.reprocess.busy")
        }
        guard mode == .speakersOnly else { return nil }
        return speakerModelReason
            ?? CoreStrings.string("meetings.error.speakersOnlyUnavailable", table: .meetings, language: loc.language)
    }

    /// Why speakers cannot be separated on this Mac now.
    private var speakerModelReason: String? {
        if !model.isSpeakerSeparationSupported {
            return CoreStrings.string("meetings.diarization.unsupportedSystem", table: .meetings, language: loc.language)
        }
        return model.speakerModelStatus.isUsable ? nil : loc.t("meetings.reprocess.needsModel")
    }
}

/// A selectable card for one processing mode.
private struct ModeOption: View {
    @Environment(\.theme) private var theme
    let symbol: String
    let title: String
    let detail: String
    let isSelected: Bool
    /// Why the mode is unavailable; shown instead of the detail.
    let reason: String?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                    .font(.system(size: 14))
                    .foregroundStyle(isSelected ? theme.accent : theme.textSecondary)
                    .padding(.top, 1)
                    .opacity(reason == nil ? 1 : 0.5)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Image(systemName: symbol)
                            .font(.system(size: 11, weight: .medium))
                            .foregroundStyle(theme.textSecondary)
                        Text(title)
                            .font(theme.bodyMediumFont)
                            .foregroundStyle(theme.textPrimary)
                    }
                    .opacity(reason == nil ? 1 : 0.5)
                    Text(reason ?? detail)
                        .font(theme.captionFont)
                        .foregroundStyle(reason == nil ? theme.textSecondary : theme.warning)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(isSelected ? theme.accentSoft : theme.insetBackground,
                        in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.rowCornerRadius)
                .strokeBorder(isSelected ? theme.accent : theme.divider, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(reason != nil)
    }
}
