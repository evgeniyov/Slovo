#if DEBUG
import AppKit
import SlovoCore
import SwiftUI

/// `Slovo --render-screens <dir>`: draws every main screen, sheet, settings section,
/// onboarding step and Flow bar state with `ImageRenderer` into PNGs, in English and
/// Russian, light and dark, then exits. Uses a throwaway settings suite and data folder,
/// and fixed content instead of the services.
@MainActor
enum ScreenRenderer {
    static func requestedDirectory(in arguments: [String]) -> URL? {
        guard let index = arguments.firstIndex(of: "--render-screens"), index + 1 < arguments.count else {
            return nil
        }
        return URL(fileURLWithPath: arguments[index + 1], isDirectory: true)
    }

    /// Renders everything; false when any image failed.
    static func renderAll(to directory: URL) -> Bool {
        let log = SlovoLog.logger("ScreenRenderer")
        let suiteName = "local.slovo.Slovo.render.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else { return false }
        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoRender-\(UUID().uuidString)", isDirectory: true)
        defer {
            defaults.removePersistentDomain(forName: suiteName)
            try? FileManager.default.removeItem(at: scratch)
        }
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        } catch {
            log.error("Cannot create \(directory.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return false
        }

        var written = 0
        var failed = 0
        let fixtures = ScreenFixtures()
        for language in [AppLanguage.en, .ru] {
            for scheme in [ColorScheme.light, .dark] {
                let settings = AppSettings(defaults: defaults)
                settings.registerDefaults()
                let model = AppModel(localizer: Localizer(fixed: language), settings: settings,
                                     directories: AppDirectories(base: scratch),
                                     modelsRoot: scratch.appendingPathComponent("Models", isDirectory: true))
                var session = RenderSession(model: model, fixtures: fixtures, scheme: scheme, directory: directory,
                                            prefix: "\(language.rawValue)-\(scheme == .dark ? "dark" : "light")")
                session.renderAll()
                written += session.written
                failed += session.failed
            }
        }
        log.info("Rendered \(written) images, \(failed) failed, into \(directory.path, privacy: .public)")
        return failed == 0 && written > 0
    }
}

@MainActor
private struct RenderSession {
    let model: AppModel
    let fixtures: ScreenFixtures
    let scheme: ColorScheme
    let directory: URL
    let prefix: String
    var written = 0
    var failed = 0

    init(model: AppModel, fixtures: ScreenFixtures, scheme: ColorScheme, directory: URL, prefix: String) {
        self.model = model
        self.fixtures = fixtures
        self.scheme = scheme
        self.directory = directory
        self.prefix = prefix
    }

    private static let windowSize = CGSize(width: 1100, height: 720)

    mutating func renderAll() {
        renderScreens()
        renderSheets()
        renderSettings()
        renderOnboarding()
        renderFlowBar()
    }

    /// Resets navigation and content before each image.
    private func reset(_ content: SnapshotContent? = nil) {
        let snapshot = content ?? fixtures.full
        model.snapshot = snapshot
        model.selectedMeetingID = nil
        model.selectedNoteID = nil
        model.styleTab = .category(.personal)
        model.settings.dictationKey = .rightOption
        // Views bind these through @AppStorage, so they live in the throwaway suite.
        model.settings.mcpEnabled = snapshot.mcpEnabled
        model.settings.mcpMaxAgeDays = 0
        model.settings.speakerSeparation = true
        model.settings.expectedSpeakers = 0
        model.settings.highlightShortcut = .markMomentDefault
        model.settings.recordMicrophone = true
        model.settings.microphoneShortcut = .microphoneToggleDefault
    }

    private mutating func renderScreens() {
        for screen in Screen.allCases {
            reset()
            model.selectedScreen = screen
            switch screen {
            case .home: model.snapshot?.hoveredRowID = fixtures.historyIDs[1]
            case .dictionary:
                model.snapshot?.hoveredRowID = fixtures.dictionaryHoverID
                model.snapshot?.dictionarySuggestions = DictionarySuggestions.find(
                    in: [
                        "Вчера обсудил синхронизацию с гитхабом.", "Проверь интеграцию с гитхабом.", "Сверь с гитхабом.",
                        "Подключился через Proxi.", "Проверь настройки Proxi.", "После обновления Proxi заработал."
                    ],
                    dictionary: fixtures.dictionary, bundledTerms: ["GitHub", "Proxy"])
            case .snippets: model.snapshot?.hoveredRowID = fixtures.snippetHoverID
            case .meetings: model.selectedMeetingID = fixtures.transcribedMeetingID
            default: break
            }
            window("screen-\(screen.rawValue)")
        }

        reset(fixtures.empty)
        model.selectedScreen = .home
        window("screen-home-empty")

        reset()
        model.selectedScreen = .meetings
        model.snapshot?.meetings = fixtures.recordingMeetings
        model.snapshot?.meetingRecording = MeetingRecordingPreview(
            meetingID: fixtures.recordingMeetingID, elapsed: 754, microphoneLevel: 0.62, systemLevel: 0.34,
            systemAudioStatus: .capturing, isVoiceIsolationOn: true)
        model.selectedMeetingID = fixtures.transcribingMeetingID
        window("screen-meetings-recording")

        renderMeetingStates()

        reset()
        model.selectedScreen = .style
        model.styleTab = .cleanup
        window("screen-style-cleanup")

        reset(fixtures.empty)
        model.selectedScreen = .scratchpad
        window("screen-scratchpad-empty")
    }

    /// Speakers, moments, speech statistics and processing phases of the Meetings detail.
    private mutating func renderMeetingStates() {
        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.transcribedMeetingID
        model.snapshot?.speakerFilter = ["s2", "s3"]
        model.snapshot?.hoveredTranscriptLineID = 1
        window("screen-meetings-filtered")

        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.transcribedMeetingID
        model.snapshot?.meetingDetailTab = .highlights
        window("screen-meetings-moments")

        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.transcribedMeetingID
        model.snapshot?.meetingDetailTab = .statistics
        window("screen-meetings-statistics")

        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.modelMissingMeetingID
        model.snapshot?.speakerModelStatus = .notDownloaded(downloadBytes: DiarizationService.downloadBytes)
        window("screen-meetings-model-missing")

        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.modelMissingMeetingID
        let reason = CoreStrings.format("diarization.error.loadFailed", table: .diarization, language: model.localizer.language,
                                        "Core ML")
        let message = CoreStrings.format("meetings.diarization.failed", table: .meetings, language: model.localizer.language, reason)
        model.snapshot?.meetings = fixtures.meetings.map { meeting in
            var copy = meeting
            if copy.id == fixtures.modelMissingMeetingID {
                copy.diarization = MeetingDiarization(state: .failed, message: message)
            }
            return copy
        }
        window("screen-meetings-speakers-failed")

        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.transcribingMeetingID
        model.snapshot?.transcriptionProgress = [fixtures.transcribingMeetingID: TranscriptionProgress(
            completedSegments: 0, totalSegments: 0, phase: .separatingSpeakers(.microphone, fraction: 0.4),
            overallFraction: 0.08)]
        window("screen-meetings-separating")

        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.modelMissingMeetingID
        model.snapshot?.meetingDetailTab = .highlights
        model.snapshot?.transcriptionProgress = [fixtures.transcribingMeetingID: TranscriptionProgress(
            completedSegments: 0, totalSegments: 0, phase: .loadingSpeakerModel, overallFraction: 0)]
        window("screen-meetings-moments-empty")

        reset()
        model.selectedScreen = .meetings
        model.selectedMeetingID = fixtures.transcribingMeetingID
        model.snapshot?.transcriptionProgress = [fixtures.transcribingMeetingID: TranscriptionProgress(
            completedSegments: 0, totalSegments: 0, phase: .loadingSpeakerModel, overallFraction: 0)]
        window("screen-meetings-loading-model")
    }

    private mutating func renderSheets() {
        reset()
        render("sheet-dictionary-term", width: 480, height: nil) {
            DictionaryEditorSheet(draft: DictionaryDraft(kind: .term, text: "Tailscale"))
        }
        let replacement = fixtures.dictionary.first { $0.kind == .replacement } ?? .replacement(from: "гитхаб", to: "GitHub")
        render("sheet-dictionary-replacement", width: 480, height: nil) {
            DictionaryEditorSheet(draft: DictionaryDraft(item: replacement))
        }
        render("sheet-snippet", width: 480, height: nil) {
            SnippetEditorSheet(draft: SnippetDraft(trigger: "подпись", expansion: "С уважением,\nКоманда проекта"))
        }
        render("sheet-shortcuts", width: 500, height: nil) {
            ShortcutsSheet()
        }

        let cowork = fixtures.cowork
        render("sheet-reprocess", width: 480, height: nil) {
            ReprocessSheet(meeting: cowork, initial: MeetingProcessingOptions(
                mode: .speakersOnly, separateSpeakers: true, expectedSpeakers: 3, engineID: .parakeet))
        }
        reset()
        model.snapshot?.speakerModelStatus = .notDownloaded(downloadBytes: DiarizationService.downloadBytes)
        let call = fixtures.meetings.first { $0.id == fixtures.modelMissingMeetingID } ?? cowork
        render("sheet-reprocess-full", width: 480, height: nil) {
            ReprocessSheet(meeting: call, initial: MeetingProcessingOptions(
                mode: .full, separateSpeakers: true, expectedSpeakers: nil, engineID: .parakeet))
        }
        reset()
        if let speaker = cowork.speakers.first(where: { $0.id == "s2" }) {
            render("popover-speaker", width: 290, height: nil) {
                SpeakerPopover(meeting: cowork, speaker: speaker)
            }
        }
        let line = TranscriptLine(id: 6, start: 758, end: 777, speakerID: "s2", track: .microphone,
                                  text: cowork.segments[6].text, segmentIndices: [6])
        render("popover-note", width: 300, height: nil) {
            HighlightNoteEditor(meetingID: cowork.id, line: line, note: "срок пробной экскурсии")
        }
    }

    private mutating func renderSettings() {
        for section in SettingsSection.allCases {
            reset()
            model.settings.dictationKey = section == .general ? .fn : .rightOption
            model.settingsSection = section
            render("settings-\(section.rawValue)", width: SettingsSheet.size.width, height: SettingsSheet.size.height) {
                SettingsSheet()
            }
        }

        // The sheet clips long sections; these show them whole.
        for section in [SettingsSection.recognition, .meetings, .integrations, .privacy] {
            reset()
            render("settings-\(section.rawValue)-content", width: SettingsSheet.size.width - 215, height: nil) {
                SettingsContentPreview(section: section)
            }
        }

        reset()
        model.snapshot?.speakerModelStatus = .downloading(progress: 0.37)
        model.snapshot?.refusedHighlightShortcut = .markMomentDefault
        render("settings-meetings-content-downloading", width: SettingsSheet.size.width - 215, height: nil) {
            SettingsContentPreview(section: .meetings)
        }

        reset()
        model.snapshot?.isSpeakerSeparationSupported = false
        model.snapshot?.speakerModelStatus = .notDownloaded(downloadBytes: DiarizationService.downloadBytes)
        render("settings-meetings-content-unsupported", width: SettingsSheet.size.width - 215, height: nil) {
            SettingsContentPreview(section: .meetings)
        }

        var development = fixtures.full
        development.mcpEnabled = false
        development.executablePath = "/Users/me/Projects/Slovo/.build/debug/Slovo"
        development.claudeRegistration = nil
        development.mcpCheck = .finished(.failed(model.localizer.t("settings.integrations.check.timeout")))
        reset(development)
        render("settings-integrations-content-dev", width: SettingsSheet.size.width - 215, height: nil) {
            SettingsContentPreview(section: .integrations)
        }
    }

    private mutating func renderOnboarding() {
        for step in OnboardingStep.allCases {
            reset(step == .permissions ? fixtures.empty : nil)
            if step == .key {
                model.settings.dictationKey = .fn
            }
            render("onboarding-\(step.rawValue + 1)", width: OnboardingView.size.width, height: OnboardingView.size.height) {
                OnboardingView(initialStep: step)
            }
        }
    }

    private mutating func renderFlowBar() {
        reset()
        let states: [(name: String, state: DictationState, remaining: TimeInterval?, hovering: Bool)] = [
            ("idle", .idle, nil, false),
            ("hover", .idle, nil, true),
            ("recording", .recording(RecordingInfo(mode: .pushToTalk, startedAt: 0, target: .frontmostApp)), nil, false),
            ("handsfree", .recording(RecordingInfo(mode: .handsFree, startedAt: 0, target: .frontmostApp)), nil, false),
            ("countdown", .recording(RecordingInfo(mode: .handsFree, startedAt: 0, target: .frontmostApp)), 24, false),
            ("processing", .processing(isLoadingModel: false), nil, false),
            ("loading-model", .processing(isLoadingModel: true), nil, false),
            ("copied", .message(.copied(.pasteFailed)), nil, false),
            ("nothing-heard", .message(.nothingHeard), nil, false),
            ("meeting", .message(.blockedByMeeting), nil, false),
            ("error-microphone", .message(.failure(.microphoneDenied)), nil, false),
            ("error-model", .message(.failure(.modelMissing(.gigaamRNNT))), nil, false),
            ("error-generic", .message(.failure(.engineFailed(""))), nil, false)
        ]
        for item in states {
            render("flowbar-\(item.name)", width: FlowBarController.panelSize.width,
                   height: FlowBarController.panelSize.height) {
                FlowBarPreview(state: item.state, remaining: item.remaining, isHovering: item.hovering)
            }
        }
        render("flowbar-moment", width: FlowBarController.panelSize.width, height: FlowBarController.panelSize.height) {
            FlowBarPreview(state: .message(.blockedByMeeting), remaining: nil, isHovering: false, markedMoment: 760)
        }
        // The pointer has just arrived: the idle pill nudges before it opens.
        render("flowbar-pointer", width: FlowBarController.panelSize.width, height: FlowBarController.panelSize.height) {
            FlowBarPreview(state: .idle, remaining: nil, isHovering: false, isPointerInside: true)
        }
    }

    private mutating func window(_ name: String) {
        render(name, width: Self.windowSize.width, height: Self.windowSize.height) {
            MainWindowView()
        }
    }

    private mutating func render<Content: View>(_ name: String, width: CGFloat, height: CGFloat?,
                                                @ViewBuilder content: () -> Content) {
        let view = AppRoot(model: model) { content() }
            .environment(\.colorScheme, scheme)
            .environment(\.isSnapshot, true)
            .frame(width: width, height: height)
        let renderer = ImageRenderer(content: view)
        renderer.proposedSize = ProposedViewSize(width: width, height: height)
        renderer.scale = 1
        let url = directory.appendingPathComponent("\(prefix)-\(name).png")
        guard let image = renderer.cgImage,
              let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
            failed += 1
            return
        }
        do {
            try png.write(to: url)
            written += 1
        } catch {
            failed += 1
        }
    }
}

/// The Flow bar over the window background, as it would sit above a desktop.
private struct FlowBarPreview: View {
    @Environment(\.theme) private var theme
    let state: DictationState
    let remaining: TimeInterval?
    let isHovering: Bool
    var markedMoment: TimeInterval?
    var isPointerInside = false

    var body: some View {
        FlowBarContent(state: state, level: 0.6, remaining: remaining, keySymbol: DictationKey.rightOption.symbol,
                       showWhenIdle: true, isHovering: isHovering, isPointerInside: isPointerInside,
                       markedMoment: markedMoment)
            .background(theme.windowBackground)
    }
}

/// A settings section at the sheet's content width and its full height.
private struct SettingsContentPreview: View {
    @Environment(\.theme) private var theme
    let section: SettingsSection

    var body: some View {
        SettingsSectionView(section: section)
            .padding(.horizontal, 28)
            .padding(.vertical, 24)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .background(theme.insetBackground)
    }
}
#endif
