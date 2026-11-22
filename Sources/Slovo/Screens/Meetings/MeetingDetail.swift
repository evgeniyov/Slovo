import SlovoCore
import SwiftUI

/// Tabs of a transcribed meeting.
enum MeetingDetailTab: Hashable {
    case transcript
    case highlights
    case statistics
    /// Only for a meeting Claude has written about.
    case claudeNotes
}

/// The line an important moment belongs to: the line that lists it, else the anchoring
/// rule of `TranscriptMerger`. Pass unfiltered lines, so the rule never picks a line of
/// another speaker because the right one is filtered out.
func anchoredLine(of highlight: MeetingHighlight, in lines: [TranscriptLine]) -> TranscriptLine? {
    if let listed = lines.first(where: { $0.highlightIDs.contains(highlight.id) }) {
        return listed
    }
    guard let index = TranscriptMerger.highlightLineIndex(highlight, in: lines), lines.indices.contains(index) else {
        return nil
    }
    return lines[index]
}

/// One meeting: title and actions, speaker notices, speakers, the transcript and its
/// important moments, or the processing state.
struct MeetingDetail: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.isSnapshot) private var isSnapshot
    @State private var titleDraft = ""
    @State private var isHoveringTitle = false
    @FocusState private var titleFocused: Bool
    @State private var isConfirmingDelete = false
    @State private var isReprocessing = false
    @State private var justCopied = false
    @State private var tab: MeetingDetailTab
    @State private var speakerFilter: Set<String>
    /// A segment of the line to scroll to and flash, set from the list of moments.
    @State private var focusedSegment: Int?
    /// Holds keyboard focus so the left/right arrow shortcuts reach `onKeyPress` without
    /// the user clicking anything first; the title field steals it while renaming and
    /// hands it back once that is done.
    @FocusState private var isPlaybackFocused: Bool
    let meeting: MeetingRecord

    /// How far a left/right arrow press moves the playhead.
    private static let seekStep: TimeInterval = 5

    init(meeting: MeetingRecord, tab: MeetingDetailTab = .transcript, speakerFilter: Set<String> = []) {
        self.meeting = meeting
        _tab = State(initialValue: tab)
        _speakerFilter = State(initialValue: speakerFilter)
    }

    var body: some View {
        // A merged or reassigned speaker disappears; the filter forgets it.
        let filter = speakerFilter.intersection(meeting.effectiveSpeakers.map(\.id))
        VStack(alignment: .leading, spacing: 0) {
            header(filter: filter)
            RowDivider()
            content(filter: filter)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
        .focusable()
        .focusEffectDisabled()
        .focused($isPlaybackFocused)
        .onKeyPress(.leftArrow) { seek(by: -Self.seekStep) }
        .onKeyPress(.rightArrow) { seek(by: Self.seekStep) }
        .onAppear {
            titleDraft = meeting.title
            isPlaybackFocused = true
        }
        .onChange(of: meeting.title) { _, newValue in titleDraft = newValue }
        .confirmationDialog(loc.t("meetings.delete.title"), isPresented: $isConfirmingDelete) {
            Button(loc.t("common.delete"), role: .destructive) {
                model.deleteMeeting(meeting.id)
            }
            Button(loc.t("common.cancel"), role: .cancel) {}
        } message: {
            Text(loc.t("meetings.delete.message", meeting.title))
        }
        .sheet(isPresented: $isReprocessing) {
            AppRoot(model: model) {
                ReprocessSheet(meeting: meeting, initial: reprocessDefaults)
            }
        }
    }

    // MARK: - Header

    private func header(filter: Set<String>) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 4) {
                titleField
                Spacer(minLength: 8)
                IconButton(symbol: "folder", help: loc.t("meetings.action.finder")) {
                    model.showMeetingInFinder(meeting.id)
                }
                IconButton(symbol: "trash", help: loc.t("common.delete")) {
                    isConfirmingDelete = true
                }
                .disabled(meeting.status == .recording)
            }
            Text(detailLine)
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
            if let sourceLine {
                Text(sourceLine)
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if meeting.hasAudio, meeting.status != .recording {
                PlaybackBar(playback: model.meetingPlayback, meeting: meeting)
            }
            // The file is still being written while the meeting records.
            if meeting.hasScreenRecording, meeting.status != .recording {
                ScreenRecordingPlayer(url: model.meetingStore.screenRecordingURL(for: meeting.id))
            }
            WrapLayout(spacing: 8, lineSpacing: 8) {
                copyButton(filter: filter)
                Button {
                    model.exportTranscript(meeting)
                } label: {
                    Label(loc.t("meetings.action.export"), systemImage: "square.and.arrow.up")
                }
                .disabled(meeting.segments.isEmpty)
                if meeting.status != .recording {
                    reprocessButton
                }
            }
            .buttonStyle(SecondaryButtonStyle())
            notice
        }
        .padding(18)
    }

    @ViewBuilder
    private var titleField: some View {
        if isSnapshot {
            Text(meeting.title)
                .font(.system(size: 20, weight: .semibold))
                .foregroundStyle(theme.textPrimary)
                .lineLimit(1)
        } else {
            // The title reads as plain text until it is pointed at: an always-visible field
            // would compete with the name itself. The underline is the only hint that it can
            // be typed over, and leaving the field saves too, so a new name cannot be lost by
            // clicking elsewhere instead of pressing Return.
            TextField(loc.t("meetings.titlePlaceholder"), text: $titleDraft)
                .textFieldStyle(.plain)
                .font(.system(size: 20, weight: .semibold))
                .foregroundStyle(theme.textPrimary)
                .focused($titleFocused)
                .background(alignment: .bottom) {
                    Rectangle()
                        .fill(titleFocused || isHoveringTitle ? theme.textPrimary.opacity(0.3) : .clear)
                        .frame(height: 1)
                        .offset(y: 4)
                }
                .onHover { isHoveringTitle = $0 }
                .onSubmit { model.renameMeeting(meeting.id, to: titleDraft) }
                .onChange(of: titleFocused) { _, focused in
                    if !focused {
                        model.renameMeeting(meeting.id, to: titleDraft)
                        // Hands keyboard focus back so the arrow shortcuts work again
                        // right after renaming, without an extra click.
                        isPlaybackFocused = true
                    }
                }
        }
    }

    private func copyButton(filter: Set<String>) -> some View {
        Button {
            model.copyToPasteboard(model.transcriptText(meeting, speakerFilter: filter.isEmpty ? nil : filter))
            justCopied = true
            Task {
                try? await Task.sleep(for: .seconds(1.2))
                justCopied = false
            }
        } label: {
            Label(justCopied ? loc.t("common.copied")
                             : (filter.isEmpty ? loc.t("meetings.action.copy") : loc.t("meetings.action.copyFiltered")),
                  systemImage: justCopied ? "checkmark" : "doc.on.doc")
        }
        .disabled(meeting.segments.isEmpty)
    }

    private var reprocessButton: some View {
        let isAvailable = model.canProcess(meeting, mode: .full) || model.canProcess(meeting, mode: .speakersOnly)
        // A disabled button shows no tooltip, so the reason sits on a container around it.
        return HStack {
            Button {
                isReprocessing = true
            } label: {
                Label(loc.t("meetings.reprocess"), systemImage: "arrow.triangle.2.circlepath")
            }
            .disabled(!isAvailable)
        }
        .help(meeting.hasAudio ? "" : loc.t("meetings.reprocess.noAudio"))
    }

    private var detailLine: String {
        var parts = [loc.longDate(meeting.createdAt), loc.timeText(meeting.createdAt), clockText(meeting.duration)]
        if let engine = meeting.engineID {
            parts.append(engine.displayName)
        }
        if meeting.diarization?.state == .applied {
            parts.append(loc.plural("plural.speakers", meeting.effectiveSpeakers.count))
        }
        // While a meeting records, only the recorder knows which tracks get sound: the
        // record is created before either capture starts, so its flag reads "no system
        // audio" until then, and it never says the microphone was switched off.
        if let tracks = model.liveTracks(of: meeting.id) {
            parts.append(tracksText(tracks, loc: loc))
        } else if meeting.status != .recording, !meeting.hasSystemAudio {
            parts.append(loc.t("meetings.detail.micOnly"))
        }
        if !meeting.hasAudio {
            parts.append(loc.t("meetings.detail.audioDeleted"))
        }
        return parts.joined(separator: " · ")
    }

    /// Where the audio came from, when it did not come from this Mac. A meeting recorded
    /// here says nothing: the line exists to explain the one that arrived from a phone.
    private var sourceLine: String? {
        guard let source = meeting.source, source.origin == .phone else { return nil }
        guard let device = source.device else { return loc.t("meetings.source.phone") }
        return loc.t("meetings.source.phone.device", device)
    }

    /// What speaker separation could not do for this meeting, with the way to fix it.
    @ViewBuilder
    private var notice: some View {
        if meeting.status == .transcribed, let diarization = meeting.diarization {
            switch diarization.state {
            case .modelMissing where !model.speakerModelStatus.isUsable && model.isSpeakerSeparationSupported:
                InlineNotice(symbol: "person.2.wave.2", text: loc.t("meetings.diarization.modelMissing.notice"), tone: .accent,
                             actionTitle: loc.t("meetings.diarization.openSettings")) {
                    model.openSettings(.meetings)
                }
            case .failed:
                let canRetry = model.canProcess(meeting, mode: .speakersOnly) || model.canProcess(meeting, mode: .full)
                InlineNotice(symbol: "exclamationmark.triangle",
                             text: diarization.message ?? loc.t("meetings.diarization.failedGeneric"),
                             actionTitle: canRetry ? loc.t("meetings.retry") : nil) {
                    retrySpeakers()
                }
            case .unsupportedSystem:
                InlineNotice(symbol: "info.circle",
                             text: CoreStrings.string("meetings.diarization.unsupportedSystem", table: .meetings,
                                                      language: loc.language),
                             tone: .neutral)
            case .applied, .off, .modelMissing:
                EmptyView()
            }
        }
    }

    private func retrySpeakers() {
        if model.canProcess(meeting, mode: .speakersOnly) {
            model.reprocessMeeting(meeting.id, options: MeetingProcessingOptions(
                mode: .speakersOnly, separateSpeakers: true, expectedSpeakers: meeting.diarization?.requestedSpeakers))
        } else {
            model.reprocessMeeting(meeting.id, options: .fromSettings(model.settings))
        }
    }

    /// Speakers only when possible, the people count this meeting last used, its engine.
    private var reprocessDefaults: MeetingProcessingOptions {
        let settings = model.settings
        return MeetingProcessingOptions(
            mode: model.canProcess(meeting, mode: .speakersOnly) ? .speakersOnly : .full,
            separateSpeakers: settings.speakerSeparation,
            expectedSpeakers: meeting.diarization?.requestedSpeakers ?? settings.expectedSpeakers,
            engineID: meeting.engineID ?? settings.meetingEngine)
    }

    // MARK: - Content

    @ViewBuilder
    private func content(filter: Set<String>) -> some View {
        switch meeting.status {
        case .transcribed:
            if meeting.segments.isEmpty {
                statusMessage(symbol: "text.bubble", title: loc.t("meetings.transcript.empty"), detail: nil)
            } else {
                transcribedContent(filter: filter)
            }
        case .transcribing:
            processingView
        case .recorded:
            statusMessage(symbol: "waveform", title: loc.t("meetings.recorded.title"),
                          detail: meeting.hasAudio ? loc.t("meetings.recorded.detail") : loc.t("meetings.detail.audioDeleted"),
                          actionTitle: meeting.hasAudio ? loc.t("meetings.transcribe") : nil)
        case .failed:
            VStack(alignment: .leading, spacing: 12) {
                InlineNotice(symbol: "exclamationmark.triangle.fill",
                             text: meeting.errorMessage ?? loc.t("meetings.failed.generic"), tone: .destructive)
                if meeting.hasAudio {
                    Button(loc.t("meetings.retry")) {
                        model.transcribeMeeting(meeting.id)
                    }
                    .buttonStyle(SecondaryButtonStyle())
                }
            }
            .padding(18)
        case .recording:
            statusMessage(symbol: "record.circle", title: loc.t("meetings.status.recording"), detail: nil)
        }
    }

    private func transcribedContent(filter: Set<String>) -> some View {
        let allLines = TranscriptMerger.lines(for: meeting)
        let lines = filter.isEmpty ? allLines : TranscriptMerger.lines(for: meeting, speakerFilter: filter)
        // Deleting Claude's notes takes their tab with them, so the card falls back to the
        // transcript instead of showing an empty one.
        let notes = meeting.claudeNotes
        let shown = tab == .claudeNotes && notes == nil ? .transcript : tab
        return VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                SpeakerBar(meeting: meeting, filter: $speakerFilter)
                DetailTabs(tab: $tab, highlightCount: meeting.highlights.count, hasClaudeNotes: notes != nil)
            }
            .padding(.horizontal, 18)
            .padding(.top, 14)
            RowDivider()
            switch shown {
            case .transcript:
                TranscriptView(meeting: meeting, lines: lines, focusedSegment: $focusedSegment)
            case .highlights:
                HighlightsList(meeting: meeting, allLines: allLines, filter: filter) { line in
                    show(line)
                }
            case .statistics:
                SpeechStatsView(meeting: meeting)
            case .claudeNotes:
                if let notes {
                    ClaudeNotesView(meeting: meeting, notes: notes)
                }
            }
        }
    }

    /// Switches to the transcript, scrolls to the line and lets the flash fade.
    private func show(_ line: TranscriptLine) {
        guard let segment = line.segmentIndices.first else { return }
        tab = .transcript
        focusedSegment = segment
        Task {
            try? await Task.sleep(for: .seconds(1.6))
            if focusedSegment == segment {
                focusedSegment = nil
            }
        }
    }

    /// Left/right arrow shortcut: nudges the moment already loaded, ignored otherwise so a
    /// stray arrow press elsewhere in the app cannot start playback by itself.
    private func seek(by delta: TimeInterval) -> KeyPress.Result {
        guard meeting.hasAudio else { return .ignored }
        model.nudgeMeetingPlayback(meeting, by: delta)
        return .handled
    }

    private var processingView: some View {
        let progress = model.transcriptionProgress(for: meeting.id)
        return VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(phaseTitle(progress))
                    .font(theme.headlineFont)
                    .foregroundStyle(theme.textPrimary)
                Spacer(minLength: 8)
                if let progress, !Self.titleShowsPercent(progress.phase) {
                    Text(loc.percent(progress.overallFraction))
                        .font(theme.monoCaptionFont)
                        .foregroundStyle(theme.textSecondary)
                }
            }
            ThemedProgressBar(fraction: progress?.overallFraction ?? 0)
            HStack {
                if let detail = phaseDetail(progress) {
                    Text(detail)
                }
                Spacer()
                Button(loc.t("common.cancel")) {
                    model.cancelTranscription(meeting.id)
                }
                .buttonStyle(SecondaryButtonStyle())
            }
            .font(theme.captionFont)
            .foregroundStyle(theme.textSecondary)
        }
        .padding(18)
    }

    private func phaseTitle(_ progress: TranscriptionProgress?) -> String {
        guard let progress else { return loc.t("meetings.transcribing.preparing") }
        switch progress.phase {
        case .preparing:
            return loc.t("meetings.transcribing.preparing")
        case .loadingSpeakerModel:
            return loc.t("meetings.transcribing.loadingSpeakerModel")
        case .separatingSpeakers:
            // The whole run's share, like the bar and the badge in the list; the share of
            // the current track would put two different percentages on screen.
            return loc.t("meetings.transcribing.separating", loc.percent(progress.overallFraction))
        case .transcribing:
            return progress.totalSegments > 0
                ? loc.t("meetings.transcribing.progress", progress.completedSegments, progress.totalSegments)
                : loc.t("meetings.transcribing.title")
        }
    }

    /// Whether the phase title already carries the percentage, so the corner does not repeat it.
    private static func titleShowsPercent(_ phase: MeetingProcessingPhase) -> Bool {
        if case .separatingSpeakers = phase {
            return true
        }
        return false
    }

    /// The track being separated; the other phases say everything in the title.
    private func phaseDetail(_ progress: TranscriptionProgress?) -> String? {
        guard let progress, case let .separatingSpeakers(track, _) = progress.phase else { return nil }
        return track == .microphone ? loc.t("meetings.meter.microphone") : loc.t("meetings.meter.system")
    }

    private func statusMessage(symbol: String, title: String, detail: String?, actionTitle: String? = nil) -> some View {
        VStack(spacing: 8) {
            Image(systemName: symbol)
                .font(.system(size: 24, weight: .light))
                .foregroundStyle(theme.textSecondary)
            Text(title)
                .font(theme.headlineFont)
                .foregroundStyle(theme.textPrimary)
            if let detail {
                Text(detail)
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .multilineTextAlignment(.center)
            }
            if let actionTitle {
                Button(actionTitle) {
                    model.transcribeMeeting(meeting.id)
                }
                .buttonStyle(PrimaryButtonStyle())
                .padding(.top, 4)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 40)
    }
}

/// "Transcript" and "Important moments (n)", underlined on the divider below.
private struct DetailTabs: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Binding var tab: MeetingDetailTab
    let highlightCount: Int
    /// The Claude tab appears only for a meeting it has written about.
    let hasClaudeNotes: Bool

    var body: some View {
        HStack(spacing: 20) {
            tabButton(.transcript, title: loc.t("meetings.tab.transcript"))
            tabButton(.highlights, title: loc.t("meetings.tab.highlights", highlightCount))
            tabButton(.statistics, title: loc.t("meetings.tab.statistics"))
            if hasClaudeNotes {
                tabButton(.claudeNotes, title: loc.t("meetings.tab.claudeNotes"))
            }
            Spacer(minLength: 0)
        }
    }

    private func tabButton(_ value: MeetingDetailTab, title: String) -> some View {
        let isSelected = tab == value
        return Button {
            tab = value
        } label: {
            VStack(spacing: 7) {
                Text(title)
                    .font(.system(size: 13, weight: isSelected ? .semibold : .regular))
                    .foregroundStyle(isSelected ? theme.textPrimary : theme.textSecondary)
                Capsule()
                    .fill(isSelected ? theme.accent : Color.clear)
                    .frame(height: 2)
            }
            .fixedSize()
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
