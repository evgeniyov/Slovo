import AppKit
import AVFoundation
import SlovoCore
import SwiftUI

struct MeetingsScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        let meetings = model.meetingItems
        let selectedID = model.selectedMeetingID.flatMap { id in meetings.first { $0.id == id }?.id } ?? meetings.first?.id
        VStack(alignment: .leading, spacing: 18) {
            PageHeader(loc.t("sidebar.meetings"), subtitle: loc.t("meetings.subtitle")) {
                VStack(alignment: .trailing, spacing: 6) {
                    HStack(spacing: 10) {
                        ImportFileButton()
                        NewRecordingButton(recorder: model.meetingRecorder)
                    }
                    HStack(spacing: 5) {
                        Image(systemName: "person.wave.2")
                        Text(model.meetingRecorder.screenRecordingStatus == .capturing
                             ? loc.t("meetings.consent.screen") : loc.t("meetings.consent"))
                    }
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                }
            }

            RecordingPanel(recorder: model.meetingRecorder)

            if meetings.isEmpty {
                EmptyStateCard(symbol: "person.2.wave.2", title: loc.t("meetings.empty.title"),
                               message: loc.t("meetings.empty.message"))
                Spacer(minLength: 0)
            } else {
                HStack(alignment: .top, spacing: 14) {
                    MeetingList(meetings: meetings, selectedID: selectedID)
                        .frame(width: 280)
                    if let meeting = meetings.first(where: { $0.id == selectedID }) {
                        MeetingDetail(meeting: meeting, tab: model.snapshot?.meetingDetailTab ?? .transcript,
                                      speakerFilter: model.snapshot?.speakerFilter ?? [])
                            .id(meeting.id)
                    }
                }
                .frame(maxHeight: .infinity, alignment: .top)
            }
        }
        .padding(.horizontal, 32)
        .padding(.vertical, 28)
    }
}

/// The tracks a recording is writing right now, in the words the recording panel and the
/// meeting card share.
@MainActor
func tracksText(_ tracks: MeetingRecordingTracks, loc: Localizer) -> String {
    switch tracks {
    case .microphoneAndSystem: return loc.t("meetings.tracks.both")
    case .microphoneOnly: return loc.t("meetings.detail.micOnly")
    case .systemOnly: return loc.t("meetings.tracks.systemOnly")
    case .neither: return loc.t("meetings.tracks.neither")
    }
}

private struct ImportFileButton: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer

    var body: some View {
        Button {
            model.pickMeetingFileToImport()
        } label: {
            Label(loc.t("meetings.importFile"), systemImage: "square.and.arrow.down")
        }
        .buttonStyle(SecondaryButtonStyle())
        .help(loc.t("meetings.importFile.help"))
    }
}

private struct NewRecordingButton: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @ObservedObject var recorder: MeetingRecorder

    var body: some View {
        let isBusy: Bool = {
            if model.snapshot?.meetingRecording != nil { return true }
            switch recorder.state {
            case .recording, .stopping: return true
            case .idle, .failed: return false
            }
        }()
        HStack(spacing: 8) {
            Button {
                model.startMeeting()
            } label: {
                Label(loc.t("meetings.newRecording"), systemImage: "record.circle")
            }
            .buttonStyle(PrimaryButtonStyle())
            .disabled(isBusy)

            ScreenCaptureButton(isBusy: isBusy)
        }
    }
}

/// A small square button that opens `ScreenCapturePopover`: closed, it shows whether a
/// new recording would also capture the screen, and the chosen app's own icon once one
/// is picked — so the choice reads at a glance instead of needing a text label that has
/// to fit in this row.
private struct ScreenCaptureButton: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.recordScreen) private var recordScreen = false
    @AppStorage(SettingsKey.screenCaptureApp) private var screenCaptureAppBundleID = ""
    @State private var isPickerPresented = false
    let isBusy: Bool

    private var chosenAppIcon: NSImage? {
        guard !screenCaptureAppBundleID.isEmpty else { return nil }
        return NSWorkspace.shared.runningApplications
            .first { $0.bundleIdentifier == screenCaptureAppBundleID }?.icon
    }

    var body: some View {
        Button {
            isPickerPresented = true
        } label: {
            Group {
                if let chosenAppIcon {
                    Image(nsImage: chosenAppIcon)
                        .resizable()
                        .scaledToFit()
                        .padding(5)
                } else {
                    Image(systemName: "rectangle.inset.filled")
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(recordScreen ? theme.accent : theme.textSecondary)
                }
            }
            .frame(width: 34, height: 34)
            .background(recordScreen ? theme.accent.opacity(0.16) : theme.secondaryButtonBackground,
                        in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.buttonCornerRadius)
                .strokeBorder(recordScreen ? theme.accent.opacity(0.55) : Color.clear, lineWidth: 1))
        }
        .buttonStyle(.plain)
        .disabled(isBusy)
        .help(loc.t("meetings.recordScreen"))
        // An icon-only button: VoiceOver reads the name and whether the screen is on.
        .accessibilityLabel(loc.t("meetings.recordScreen"))
        .accessibilityValue(recordScreen ? loc.t("meetings.recordScreen.title") : "")
        .accessibilityAddTraits(recordScreen ? .isSelected : [])
        .popover(isPresented: $isPickerPresented, arrowEdge: .bottom) {
            AppRoot(model: model) {
                ScreenCapturePopover(recordScreen: $recordScreen, screenCaptureAppBundleID: $screenCaptureAppBundleID)
            }
        }
    }
}

/// The screen-recording on/off switch and, once on, «Whole screen» plus every other
/// regular app currently running — each with its own icon, the way a native share-screen
/// picker shows its sources — to choose what a new recording captures.
private struct ScreenCapturePopover: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @Binding var recordScreen: Bool
    @Binding var screenCaptureAppBundleID: String

    /// Built fresh every time the popover opens, so a just-launched app shows up
    /// without having to reopen it.
    private var apps: [NSRunningApplication] {
        let ownBundleID = Bundle.main.bundleIdentifier
        return NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.bundleIdentifier != nil && $0.bundleIdentifier != ownBundleID }
            .sorted { ($0.localizedName ?? "") < ($1.localizedName ?? "") }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(loc.t("meetings.recordScreen.title"))
                    .font(theme.headlineFont)
                    .foregroundStyle(theme.textPrimary)
                Spacer(minLength: 12)
                ThemedToggle(isOn: $recordScreen, label: loc.t("meetings.recordScreen.title"))
                    .onChange(of: recordScreen) { _, isOn in
                        if isOn { model.requestScreenRecordingAccess() }
                    }
            }
            if recordScreen {
                RowDivider()
                ScrollView {
                    VStack(alignment: .leading, spacing: 2) {
                        Button {
                            screenCaptureAppBundleID = ""
                            dismiss()
                        } label: {
                            PopoverActionLabel(symbol: "rectangle.inset.filled",
                                               title: loc.t("meetings.recordScreen.wholeScreen"),
                                               isChecked: screenCaptureAppBundleID.isEmpty)
                        }
                        .buttonStyle(.plain)
                        ForEach(apps, id: \.bundleIdentifier) { app in
                            Button {
                                screenCaptureAppBundleID = app.bundleIdentifier ?? ""
                                dismiss()
                            } label: {
                                AppTargetRow(icon: app.icon, title: app.localizedName ?? app.bundleIdentifier ?? "",
                                            isChecked: screenCaptureAppBundleID == app.bundleIdentifier)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
                .frame(maxHeight: 240)
            } else {
                Text(loc.t("meetings.recordScreen.help"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(14)
        .frame(width: 280, alignment: .leading)
        .background(theme.panelBackground)
    }
}

/// Like `PopoverActionLabel`, but with an app's own icon instead of a symbol — a share
/// target should look like the thing it captures.
private struct AppTargetRow: View {
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let icon: NSImage?
    let title: String
    let isChecked: Bool

    var body: some View {
        HStack(spacing: 8) {
            if let icon {
                Image(nsImage: icon)
                    .resizable()
                    .scaledToFit()
                    .frame(width: 18, height: 18)
            } else {
                Image(systemName: "app.dashed")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(theme.textSecondary)
                    .frame(width: 18)
            }
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
        }
        .padding(.horizontal, 8)
        .frame(height: 30)
        .background(isHovering ? theme.rowHover : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
        .onHover { isHovering = $0 }
    }
}

/// Timer, the tracks being written, level meters, Stop, the microphone switch and «Mark
/// moment» while a meeting records.
private struct RecordingPanel: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @ObservedObject var recorder: MeetingRecorder

    var body: some View {
        if let preview = model.snapshot?.meetingRecording {
            panel(meetingID: preview.meetingID, elapsed: { preview.elapsed }, microphone: preview.microphoneLevel,
                  system: preview.systemLevel, systemStatus: preview.systemAudioStatus, screenStatus: .off,
                  screenCaptureAppName: nil, microphoneStatus: preview.microphoneStatus,
                  isMicrophoneOn: preview.isMicrophoneOn, isStopping: false)
        } else {
            switch recorder.state {
            case let .recording(meetingID, _):
                // The recorder's own value, which counts a sleep that uptime does not.
                panel(meetingID: meetingID, elapsed: { recorder.elapsed },
                      microphone: recorder.microphoneLevel, system: recorder.systemLevel,
                      systemStatus: recorder.systemAudioStatus, screenStatus: recorder.screenRecordingStatus,
                      screenCaptureAppName: recorder.screenCaptureAppName, microphoneStatus: recorder.microphoneStatus,
                      isMicrophoneOn: recorder.isMicrophoneOn, isStopping: false)
            case .stopping:
                // The capture is already gone, so there is nothing true to say about tracks;
                // no status lines rather than the "turned off in Settings" one.
                panel(meetingID: nil, elapsed: nil, microphone: 0, system: 0, systemStatus: .off, screenStatus: .off,
                      screenCaptureAppName: nil, microphoneStatus: .capturing, isMicrophoneOn: recorder.isMicrophoneOn,
                      isStopping: true)
            case let .failed(message):
                InlineNotice(symbol: "exclamationmark.triangle.fill", text: message, tone: .destructive)
            case .idle:
                EmptyView()
            }
        }
    }

    private func panel(meetingID: UUID?, elapsed: (() -> TimeInterval)?, microphone: Float, system: Float,
                       systemStatus: SystemAudioStatus, screenStatus: ScreenRecordingStatus,
                       screenCaptureAppName: String?, microphoneStatus: MicrophoneStatus, isMicrophoneOn: Bool,
                       isStopping: Bool) -> some View {
        let meeting = meetingID.flatMap { id in model.meetingItems.first { $0.id == id } }
        let title = meeting?.title
        let tracks = MeetingRecordingTracks(microphoneOn: isMicrophoneOn, microphoneStatus: microphoneStatus,
                                            systemAudioStatus: systemStatus)
        return VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: 28) {
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 7) {
                        Circle().fill(theme.accent).frame(width: 8, height: 8)
                        Text(isStopping ? loc.t("meetings.recording.stopping") : loc.t("meetings.recording.title"))
                            .font(theme.captionMediumFont)
                            .foregroundStyle(theme.accentText)
                    }
                    if let elapsed {
                        TimelineView(.periodic(from: .now, by: 1)) { _ in
                            Text(clockText(elapsed()))
                                .font(theme.largeNumberFont.monospacedDigit())
                                .foregroundStyle(theme.textPrimary)
                        }
                    }
                    if let title {
                        Text(title)
                            .font(theme.captionFont)
                            .foregroundStyle(theme.textSecondary)
                            .lineLimit(1)
                    }
                    if !isStopping {
                        Text(tracksText(tracks, loc: loc))
                            .font(theme.captionMediumFont)
                            .foregroundStyle(tracks == .neither ? theme.warning : theme.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .frame(width: 190, alignment: .leading)

                VStack(alignment: .leading, spacing: 10) {
                    meterRow(title: loc.t("meetings.meter.microphone"), level: microphone, tint: theme.accent)
                    if !isStopping {
                        microphoneStatusLine(microphoneStatus)
                    }
                    meterRow(title: screenCaptureAppName ?? loc.t("meetings.meter.system"), level: system,
                            tint: theme.secondaryAccent)
                    if !isStopping {
                        systemStatusLine(systemStatus)
                        screenStatusLine(screenStatus)
                    }
                }
                .frame(maxWidth: .infinity)

                VStack(alignment: .trailing, spacing: 8) {
                    Button {
                        model.stopMeeting()
                    } label: {
                        Label(loc.t("meetings.stop"), systemImage: "stop.fill")
                    }
                    .buttonStyle(PrimaryButtonStyle())
                    .disabled(isStopping)
                    if meetingID != nil {
                        microphoneButton(isOn: isMicrophoneOn)
                        markMomentButton(count: meeting?.highlights.count ?? 0)
                    }
                }
                .fixedSize()
            }
            if meetingID != nil {
                voiceIsolationNotice
            }
        }
        .padding(20)
        .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.accent.opacity(0.55), lineWidth: 1.5))
    }

    /// Says whether the microphone is being recorded, and switches it for this meeting.
    /// Off reads differently at a glance: a struck-through microphone in the warning color.
    private func microphoneButton(isOn: Bool) -> some View {
        Button {
            model.toggleMeetingMicrophone()
        } label: {
            HStack(spacing: 6) {
                Image(systemName: isOn ? "mic.fill" : "mic.slash.fill")
                    .font(.system(size: 12, weight: .medium))
                Text(isOn ? loc.t("meetings.recording.micOn") : loc.t("meetings.recording.micOff"))
                Text(model.settings.microphoneShortcut.symbols)
                    .foregroundStyle(theme.textSecondary)
            }
            .foregroundStyle(isOn ? theme.secondaryButtonText : theme.warning)
        }
        .buttonStyle(SecondaryButtonStyle())
        .help(isOn ? loc.t("meetings.recording.micOn.help") : loc.t("meetings.recording.micOff.help"))
    }

    private func markMomentButton(count: Int) -> some View {
        VStack(alignment: .trailing, spacing: 5) {
            Button {
                model.markMoment()
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "star")
                        .font(.system(size: 12, weight: .medium))
                    Text(loc.t("meetings.recording.mark"))
                    Text(model.settings.highlightShortcut.symbols)
                        .foregroundStyle(theme.textSecondary)
                }
            }
            .buttonStyle(SecondaryButtonStyle())
            if count > 0 {
                Text(loc.t("meetings.recording.marked", loc.plural("plural.moments", count)))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
            }
        }
    }

    /// Voice Isolation keeps the nearest voice and can erase the rest of the room;
    /// only the user can switch it off, in Control Center.
    @ViewBuilder
    private var voiceIsolationNotice: some View {
        if let preview = model.snapshot?.meetingRecording {
            if preview.isVoiceIsolationOn {
                voiceIsolationText
            }
        } else {
            TimelineView(.periodic(from: .now, by: 2)) { _ in
                if AVCaptureDevice.activeMicrophoneMode == .voiceIsolation {
                    voiceIsolationText
                }
            }
        }
    }

    private var voiceIsolationText: some View {
        InlineNotice(symbol: "waveform.badge.exclamationmark", text: loc.t("meetings.recording.voiceIsolation"))
            .padding(.top, 14)
    }

    private func meterRow(title: String, level: Float, tint: Color) -> some View {
        HStack(spacing: 12) {
            Text(title)
                .font(theme.captionMediumFont)
                .foregroundStyle(theme.textSecondary)
                .frame(width: 104, alignment: .leading)
            LevelMeter(level: level, tint: tint)
        }
    }

    /// A lost microphone does not stop the meeting, so it is said here rather than as the
    /// failure notice that replaces the whole panel.
    @ViewBuilder
    private func microphoneStatusLine(_ status: MicrophoneStatus) -> some View {
        switch status {
        case .capturing:
            EmptyView()
        case .lost:
            Text(loc.t("meetings.microphone.lost"))
                .font(theme.captionFont)
                .foregroundStyle(theme.warning)
        }
    }

    @ViewBuilder
    private func systemStatusLine(_ status: SystemAudioStatus) -> some View {
        switch status {
        case .capturing:
            EmptyView()
        case .off:
            Text(loc.t("meetings.system.off"))
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
        case .silentPermissionLikelyDenied:
            Text(loc.t("meetings.system.silent"))
                .font(theme.captionFont)
                .foregroundStyle(theme.warning)
        case let .unavailable(message):
            Text(message)
                .font(theme.captionFont)
                .foregroundStyle(theme.warning)
        }
    }

    /// Screen capture is fully optional, so `.off` (never asked for) says nothing; only
    /// a state the user might want to notice gets a line.
    @ViewBuilder
    private func screenStatusLine(_ status: ScreenRecordingStatus) -> some View {
        switch status {
        case .off, .capturing:
            EmptyView()
        case .pausedScreenLocked:
            Text(loc.t("meetings.screen.locked"))
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
        case .lost:
            Text(loc.t("meetings.screen.lost"))
                .font(theme.captionFont)
                .foregroundStyle(theme.warning)
        case let .unavailable(message):
            Text(message)
                .font(theme.captionFont)
                .foregroundStyle(theme.warning)
        }
    }
}

private struct MeetingList: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.theme) private var theme
    let meetings: [MeetingRecord]
    let selectedID: UUID?

    var body: some View {
        PanelScroll {
            VStack(spacing: 2) {
                ForEach(meetings) { meeting in
                    MeetingListRow(meeting: meeting, isSelected: meeting.id == selectedID) {
                        model.selectedMeetingID = meeting.id
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

private struct MeetingListRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let meeting: MeetingRecord
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    Text(meeting.title)
                        .font(theme.bodyMediumFont)
                        .foregroundStyle(theme.textPrimary)
                        .lineLimit(1)
                    Spacer(minLength: 0)
                    MeetingStatusBadge(meeting: meeting)
                }
                subtitle
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 10)
            .padding(.vertical, 9)
            .background(isSelected ? theme.sidebarSelection : (isHovering ? theme.rowHover : Color.clear),
                        in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
    }

    /// Date and duration. The stored duration is written when recording stops, so a
    /// meeting still recording counts up from the recorder's start instead.
    @ViewBuilder
    private var subtitle: some View {
        let date = loc.shortDateTime(meeting.createdAt, now: model.now)
        if meeting.status != .recording {
            Text("\(date) · \(clockText(meeting.duration))")
        } else if let elapsed = liveElapsed {
            TimelineView(.periodic(from: .now, by: 1)) { _ in
                Text("\(date) · \(clockText(elapsed()))")
            }
        } else {
            Text(date)
        }
    }

    private var liveElapsed: (() -> TimeInterval)? {
        if let preview = model.snapshot?.meetingRecording {
            return preview.meetingID == meeting.id ? { preview.elapsed } : nil
        }
        if case let .recording(meetingID, _) = model.meetingRecorder.state, meetingID == meeting.id {
            // The same sleep-aware value the recording panel shows.
            return { model.meetingRecorder.elapsed }
        }
        return nil
    }
}

private struct MeetingStatusBadge: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    let meeting: MeetingRecord

    var body: some View {
        switch meeting.status {
        case .transcribed:
            EmptyView()
        case .recording:
            StatusBadge(text: loc.t("meetings.status.recording"), tone: .accent)
        case .recorded:
            StatusBadge(text: loc.t("meetings.status.recorded"), tone: .neutral)
        case .transcribing:
            let fraction = model.transcriptionProgress(for: meeting.id)?.overallFraction ?? 0
            StatusBadge(text: loc.percent(fraction), tone: .accent)
        case .failed:
            StatusBadge(text: loc.t("meetings.status.failed"), tone: .destructive)
        }
    }
}
