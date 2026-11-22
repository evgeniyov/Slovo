import AppKit
import Combine
import Foundation
import SlovoCore
import SwiftUI
import UniformTypeIdentifiers

/// Main window sidebar destinations.
enum Screen: String, CaseIterable, Identifiable {
    case home
    case meetings
    case insights
    case dictionary
    case snippets
    case style
    case scratchpad

    var id: String { rawValue }

    var titleKey: String { "sidebar.\(rawValue)" }

    var symbol: String {
        switch self {
        case .home: return "waveform"
        case .meetings: return "person.2.wave.2"
        case .insights: return "chart.bar.xaxis"
        case .dictionary: return "character.book.closed"
        case .snippets: return "text.badge.plus"
        case .style: return "textformat"
        case .scratchpad: return "note.text"
        }
    }
}

/// Sections of the settings sheet.
enum SettingsSection: String, CaseIterable, Identifiable {
    case general
    case recognition
    case system
    case meetings
    case integrations
    case privacy
    case about

    var id: String { rawValue }

    var titleKey: String { "settings.section.\(rawValue)" }

    var symbol: String {
        switch self {
        case .general: return "gearshape"
        case .recognition: return "waveform.badge.mic"
        case .system: return "macwindow"
        case .meetings: return "person.2"
        case .integrations: return "puzzlepiece.extension"
        case .privacy: return "lock"
        case .about: return "info.circle"
        }
    }
}

/// Tabs of the Style screen.
enum StyleTab: Hashable {
    case category(AppCategory)
    case cleanup
}

/// Banners the user can dismiss; the raw values persist in `AppSettings.dismissedBanners`.
enum BannerID: String {
    case home = "home.dictionary"
    case dictionary = "dictionary.terms"
    case snippets = "snippets.examples"
}

struct AppAlert: Identifiable {
    let id = UUID()
    let title: String
    let message: String
}

/// What the dictation key can do right now, for the sidebar status line.
enum DictationReadiness: Equatable {
    case checking
    case ready(EngineID)
    case permissionsNeeded
    case modelMissing(EngineID)
    case downloading(EngineID, Double)
}

/// A meeting recording in progress as the Meetings screen draws it in screenshots.
struct MeetingRecordingPreview {
    var meetingID: UUID
    var elapsed: TimeInterval
    var microphoneLevel: Float
    var systemLevel: Float
    var systemAudioStatus: SystemAudioStatus
    /// The microphone's Voice Isolation mode, which can drop distant voices.
    var isVoiceIsolationOn = false
    var microphoneStatus: MicrophoneStatus = .capturing
    var isMicrophoneOn = true
}

/// The result of Settings → Integrations → Check.
enum MCPCheckResult: Equatable {
    /// The server answered; the count is how many meetings the age limit leaves visible.
    case works(visibleMeetings: Int)
    case accessOff
    /// The message is already localized.
    case failed(String)
}

enum MCPCheckState: Equatable {
    case idle
    case running
    case finished(MCPCheckResult)
}

/// Fixed content that replaces live service data while `ScreenRenderer` draws
/// screenshots. Nil in the running app.
struct SnapshotContent {
    var now = Date()
    var history: [HistoryEntry] = []
    var insights: Insights = .empty
    var dictionary: [DictionaryItem] = []
    var dictionarySuggestions: [DictionarySuggestion] = []
    var snippets: [Snippet] = []
    var notes: [Note] = []
    var meetings: [MeetingRecord] = []
    var transcriptionProgress: [UUID: TranscriptionProgress] = [:]
    var meetingRecording: MeetingRecordingPreview?
    var modelStatuses: [EngineID: ModelStatus] = [:]
    var engineLoadStates: [EngineID: EngineLoadState] = [:]
    var permissions: [PermissionKind: PermissionStatus] = [:]
    var microphones: [AudioInputDevice] = []
    var appsByCategory: [AppCategory: [AppIdentity]] = [:]
    var isKeyDown = false
    var isHotkeyRunning = true
    var launchAtLogin = false
    var fnUsage: FnKeyStatus.Usage = .doNothing
    var isWisprFlowRunning = false
    /// A list row drawn in its hover state, to show row actions.
    var hoveredRowID: UUID?
    var practiceResult: String?
    var playingID: UUID?
    var speakerModelStatus: ModelStatus = .unknown
    var speakerModelLoadState: EngineLoadState = .unloaded
    var isSpeakerSeparationSupported = true
    /// The Meetings detail as it opens: tab, speaker filter and a line drawn hovered.
    var meetingDetailTab: MeetingDetailTab = .transcript
    var speakerFilter: Set<String> = []
    var hoveredTranscriptLineID: Int?
    var refusedHighlightShortcut: HotkeyShortcut?
    var refusedMicrophoneShortcut: HotkeyShortcut?
    var claudeRegistration: ClaudeRegistration?
    var mcpCheck: MCPCheckState = .idle
    var executablePath = ClaudeIntegration.installedExecutablePath
    var mcpEnabled = false
}

/// Owns every service and the navigation state the views share.
@MainActor
final class AppModel: ObservableObject {
    @Published var selectedScreen: Screen = .home
    @Published var isSettingsPresented = false
    @Published var settingsSection: SettingsSection = .general
    @Published var isShortcutsPresented = false
    @Published var isTroubleshootingPresented = false
    @Published var selectedMeetingID: UUID?
    @Published var selectedNoteID: UUID?
    @Published var styleTab: StyleTab = .category(.personal)
    @Published var alert: AppAlert?
    /// An error from an action taken inside Settings, shown by the Settings sheet: an alert
    /// on the window behind it would wait until the sheet closes.
    @Published var settingsAlert: AppAlert?
    @Published private(set) var dismissedBanners: Set<String>
    @Published private(set) var rerunningEntryIDs: Set<UUID> = []
    /// The mark-moment shortcut macOS refused at the last recording; Settings asks for
    /// another one while it is still selected.
    @Published var refusedHighlightShortcut: HotkeyShortcut?
    /// The same for the shortcut that switches the meeting microphone.
    @Published var refusedMicrophoneShortcut: HotkeyShortcut?
    /// Whether Slovo is listed in Claude's configuration files; nil until checked.
    @Published private(set) var claudeRegistration: ClaudeRegistration?
    @Published private(set) var mcpCheckState: MCPCheckState = .idle

    let localizer: Localizer
    let settings: AppSettings
    let directories: AppDirectories

    let permissions: Permissions
    let frontmostApp: FrontmostApp
    let hotkeys: HotkeyMonitor
    let pasteLast: PasteLastShortcut
    let inserter: TextInserter
    let recorder: MicrophoneRecorder
    let audioDevices: AudioDeviceObserver
    let sounds: DictationSounds

    let history: HistoryStore
    let dictionary: DictionaryStore
    let snippets: SnippetStore
    let notes: NoteStore
    let categories: AppCategoryStore
    let meetingStore: MeetingStore
    let retention: RetentionCleaner

    let models: ModelManager
    let diarization: DiarizationService
    let engines: EngineManager
    let pipeline: TextPipeline
    let dictation: DictationController
    let meetingRecorder: MeetingRecorder
    let meetingTranscriber: MeetingTranscriber
    let wiring: ServiceWiring
    let meetingWiring: MeetingWiring
    let inboxImporter: InboxImporter
    let focusedInput: FocusedInputWatcher
    let phoneClips: PhoneClipCollector

    let loginItem = LoginItem()
    let fnConflicts = FnKeyStatus()
    let playback = AudioPlayback()
    /// Not in the `objectWillChange` merge below: its playhead changes many times a second
    /// while playing, and only the playback bar needs to redraw for that.
    let meetingPlayback = MeetingPlayback()

    /// Replaces live data while screens are rendered to images; nil in the app.
    var snapshot: SnapshotContent?

    /// Window actions supplied by the app delegate.
    var showMainWindow: () -> Void = {}
    var showOnboardingWindow: (OnboardingStep) -> Void = { _ in }

    /// Opens onboarding at `step`, from the start unless a caller needs a later step.
    func showOnboarding(_ step: OnboardingStep = .welcome) {
        showOnboardingWindow(step)
    }

    private var cancellables: Set<AnyCancellable> = []
    private var isStarted = false
    /// Insights of the current history, kept for the day: the window redraws on every
    /// download or transcription progress step, and the history rarely changes then.
    private var cachedInsights: (day: Date, value: Insights)?
    private let log = SlovoLog.logger("AppModel")

    init(localizer: Localizer, settings: AppSettings, directories: AppDirectories, modelsRoot: URL) {
        self.localizer = localizer
        self.settings = settings
        self.directories = directories
        dismissedBanners = settings.dismissedBanners

        permissions = Permissions()
        frontmostApp = FrontmostApp()
        hotkeys = HotkeyMonitor()
        pasteLast = PasteLastShortcut()
        inserter = TextInserter(permissions: permissions)
        recorder = MicrophoneRecorder()
        audioDevices = AudioDeviceObserver()
        sounds = DictationSounds(settings: settings)

        history = HistoryStore(directories: directories)
        dictionary = DictionaryStore(directories: directories)
        snippets = SnippetStore(directories: directories)
        notes = NoteStore(directories: directories)
        categories = AppCategoryStore(directories: directories)
        meetingStore = MeetingStore(directories: directories)
        retention = RetentionCleaner(history: history, meetings: meetingStore, settings: settings)

        models = ModelManager(modelsRoot: modelsRoot)
        diarization = DiarizationService(modelsRoot: modelsRoot)
        engines = EngineManager(settings: settings, models: models, factory: EngineManager.factory(modelsRoot: modelsRoot))
        pipeline = TextPipeline()
        // With sounds, a microphone lost mid-meeting is audible with the window closed.
        meetingRecorder = MeetingRecorder(store: meetingStore, settings: settings, sounds: sounds)
        // With settings, "delete after transcription" removes meeting audio as soon as
        // the transcript is saved; with the diarization service, speakers are separated
        // once the speaker model is downloaded.
        meetingTranscriber = MeetingTranscriber(store: meetingStore, engines: engines, settings: settings,
                                                diarization: diarization)
        let dictation = DictationController(dependencies: .init(
            recorder: recorder, hotkeys: hotkeys, inserter: inserter, frontmostApp: frontmostApp,
            permissions: permissions, engines: engines, pipeline: pipeline, history: history,
            dictionary: dictionary, snippets: snippets, notes: notes, categories: categories,
            meetings: meetingRecorder, sounds: sounds, settings: settings, directories: directories,
            mixedLanguage: MixedLanguageHelper(engines: engines, models: models, settings: settings)
        ))
        self.dictation = dictation
        wiring = ServiceWiring(settings: settings, models: models, engines: engines, pasteLast: pasteLast) {
            Task { await dictation.pasteLastTranscript() }
        }
        // Registrars of their own: one registrar holds one hot key, and paste-last keeps its
        // shortcut while the mark-moment and microphone ones come and go with recordings.
        meetingWiring = MeetingWiring(settings: settings, recorder: meetingRecorder, shortcut: PasteLastShortcut(),
                                      microphoneShortcut: PasteLastShortcut(), diarization: diarization)
        focusedInput = FocusedInputWatcher(defaults: settings.defaults,
                                           logURL: directories.base.appendingPathComponent("focus-debug.log"))
        inboxImporter = InboxImporter(directories: directories, store: meetingStore,
                                      transcriber: meetingTranscriber, settings: settings)
        // A transport beside the importer rather than part of it: clips pulled off a cable
        // land in the same folder as everything else and are imported by the same code.
        phoneClips = PhoneClipCollector(directories: directories, settings: settings)

        // Screens observe the model only, so data changes of the stores and slow-moving
        // services are forwarded. Level meters and dictation state change many times a
        // second; the views that show them observe those objects directly.
        let sources: [ObservableObjectPublisher] = [
            history.objectWillChange, dictionary.objectWillChange, snippets.objectWillChange,
            notes.objectWillChange, categories.objectWillChange, meetingStore.objectWillChange,
            models.objectWillChange, engines.objectWillChange, permissions.objectWillChange,
            audioDevices.objectWillChange, meetingTranscriber.objectWillChange, loginItem.objectWillChange,
            fnConflicts.objectWillChange, playback.objectWillChange, diarization.objectWillChange
        ]
        Publishers.MergeMany(sources)
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)
        // Which tracks a recording writes changes rarely, and the meeting card says it.
        Publishers.MergeMany(
            meetingRecorder.$isMicrophoneOn.map { _ in () }.eraseToAnyPublisher(),
            meetingRecorder.$microphoneStatus.map { _ in () }.eraseToAnyPublisher(),
            meetingRecorder.$systemAudioStatus.map { _ in () }.eraseToAnyPublisher()
        )
        .sink { [weak self] in self?.objectWillChange.send() }
        .store(in: &cancellables)
        // `@Published` sends the new value before storing it, so the value itself is copied.
        meetingWiring.$refusedShortcut
            .dropFirst()
            .sink { [weak self] shortcut in self?.refusedHighlightShortcut = shortcut }
            .store(in: &cancellables)
        meetingWiring.$refusedMicrophoneShortcut
            .dropFirst()
            .sink { [weak self] shortcut in self?.refusedMicrophoneShortcut = shortcut }
            .store(in: &cancellables)
        meetingRecorder.$lastFailure
            .compactMap { $0 }
            .sink { [weak self] message in self?.reportMeetingFailure(message) }
            .store(in: &cancellables)
        // A recording that ended by itself is saved; it is transcribed like one the owner
        // stopped, instead of waiting unnoticed for a Transcribe click.
        meetingRecorder.$autoStoppedMeeting
            .compactMap { $0 }
            .filter(\.hasAudio)
            .sink { [weak self] meeting in self?.transcribeMeeting(meeting.id) }
            .store(in: &cancellables)
        // Sent before the entries change, so no redraw can cache the old ones after it.
        history.objectWillChange
            .sink { [weak self] _ in self?.cachedInsights = nil }
            .store(in: &cancellables)
    }

    /// The real app: standard defaults and `~/Library/Application Support/Slovo`.
    static func live(localizer: Localizer) -> AppModel {
        AppModel(localizer: localizer, settings: AppSettings(defaults: .standard),
                 directories: .standard, modelsRoot: ModelStore.root)
    }

    /// Starts background work: folders, seeding, retention, permission and model
    /// checks, engine preloading, the dictation key, the paste-last shortcut and the
    /// mark-moment shortcut that follows meeting recordings.
    func start() {
        guard !isStarted else { return }
        isStarted = true
        do {
            try directories.createFolders()
        } catch {
            log.error("Could not create data folders: \(error.localizedDescription, privacy: .public)")
        }
        reportDamagedFiles()
        observeSaveFailures()
        dictionary.seedIfNeeded(settings: settings)
        retention.start()
        permissions.refresh()
        models.refresh()
        diarization.refresh()
        audioDevices.start()
        frontmostApp.startObserving()
        loginItem.refresh()
        fnConflicts.refresh()
        // The engine for the current language mode loads in the background now; one whose
        // model becomes usable later is preloaded by the wiring.
        engines.preloadForCurrentSettings()
        dictation.start()
        wiring.start()
        meetingWiring.start()
        inboxImporter.start()
        phoneClips.start()
        // `$statuses` fires before the value is stored, so the check runs a turn later.
        models.$statuses
            .dropFirst()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.wiring.modelStatusesDidChange() }
            .store(in: &cancellables)
    }

    /// Finishes a meeting recording and writes pending data before the app quits.
    func prepareForTermination() {
        guard isStarted else { return }
        meetingRecorder.stopImmediately()
        dictation.stop()
        frontmostApp.stopObserving()
        wiring.stop()
        meetingWiring.stop()
        inboxImporter.stop()
        focusedInput.stop()
        phoneClips.stop()
        retention.stop()
        playback.stop()
        meetingPlayback.stop()
        // Stores also save on their own when the app terminates; saving here as well
        // reports failures in the log.
        for flush in [history.flush, dictionary.flush, snippets.flush, notes.flush, categories.flush, meetingStore.flush] {
            do {
                try flush()
            } catch {
                log.error("Could not save before quitting: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    func openSettings(_ section: SettingsSection? = nil) {
        if let section {
            settingsSection = section
        }
        showMainWindow()
        isSettingsPresented = true
    }

    func showShortcuts() {
        showMainWindow()
        isShortcutsPresented = true
    }

    func showTroubleshooting() {
        showMainWindow()
        isTroubleshootingPresented = true
    }

    /// Shows an error where it will be seen: inside Settings while that sheet is open,
    /// otherwise on the main window, which opens and comes forward, since an action
    /// started from the menu bar has no window of its own.
    func presentAlert(_ alert: AppAlert) {
        guard snapshot == nil else { return }
        if isSettingsPresented {
            settingsAlert = alert
        } else {
            self.alert = alert
            showMainWindow()
        }
    }

    /// Re-reads what another process may have changed while Slovo was in the background:
    /// System Settings, and the notes Claude writes into a meeting.
    func applicationDidBecomeActive() {
        guard snapshot == nil else { return }
        permissions.refresh()
        fnConflicts.refresh()
        loginItem.refresh()
        models.refresh()
        diarization.refresh()
        // Input Monitoring granted while Slovo was running: the key listener can start now.
        dictation.refreshHotkeys()
        // The MCP server writes Claude's notes from its own process and tells the app
        // nothing, so the card learns about them here.
        meetingStore.refreshClaudeNotes()
    }

    /// While onboarding's practice step is visible, the dictation key fills its field
    /// instead of pasting into the app that was in front before.
    func setPracticeActive(_ isActive: Bool) {
        guard snapshot == nil else { return }
        dictation.keyTarget = isActive ? .practice : .frontmostApp
    }

    /// The user's first name for the Home greeting.
    var firstName: String {
        // Screenshots must not show the account name of the Mac that renders them.
        if snapshot != nil { return localizer.languageCode == "ru" ? "Алекс" : "Alex" }
        return NSFullUserName().split(separator: " ").first.map(String.init) ?? NSUserName()
    }

    // MARK: - Data the screens read

    var now: Date { snapshot?.now ?? Date() }

    var historyEntries: [HistoryEntry] { snapshot?.history ?? history.entries }

    var insights: Insights {
        if let snapshot {
            return snapshot.insights
        }
        let now = Date()
        let day = Calendar.current.startOfDay(for: now)
        if let cachedInsights, cachedInsights.day == day {
            return cachedInsights.value
        }
        let value = InsightsCalculator.compute(entries: history.entries, now: now)
        cachedInsights = (day, value)
        return value
    }

    var dictionaryItems: [DictionaryItem] { snapshot?.dictionary ?? dictionary.items }

    var snippetItems: [Snippet] { snapshot?.snippets ?? snippets.snippets }

    var noteItems: [Note] { snapshot?.notes ?? notes.notes }

    var meetingItems: [MeetingRecord] { snapshot?.meetings ?? meetingStore.meetings }

    var microphones: [AudioInputDevice] { snapshot?.microphones ?? audioDevices.devices }

    var isLaunchAtLoginEnabled: Bool { snapshot?.launchAtLogin ?? loginItem.isEnabled }

    var fnUsage: FnKeyStatus.Usage { snapshot?.fnUsage ?? fnConflicts.usage }

    var isWisprFlowRunning: Bool { snapshot?.isWisprFlowRunning ?? fnConflicts.isWisprFlowRunning }

    var playingEntryID: UUID? { snapshot?.playingID ?? playback.playingID }

    func transcriptionProgress(for id: UUID) -> TranscriptionProgress? {
        if let snapshot {
            return snapshot.transcriptionProgress[id]
        }
        return meetingTranscriber.progress[id]
    }

    func modelStatus(_ id: EngineID) -> ModelStatus {
        if let snapshot {
            return snapshot.modelStatuses[id] ?? .unknown
        }
        return models.status(of: id)
    }

    /// Whether the engine is in memory, loading or failed; model rows keep file actions
    /// off while it loads, because the load reads the files they would replace or remove.
    func engineLoadState(_ id: EngineID) -> EngineLoadState {
        if let snapshot {
            return snapshot.engineLoadStates[id] ?? .unloaded
        }
        return engines.loadState(of: id)
    }

    func permissionStatus(_ kind: PermissionKind) -> PermissionStatus {
        if let snapshot {
            return snapshot.permissions[kind] ?? .unknown
        }
        return permissions.status(of: kind)
    }

    func apps(in category: AppCategory) -> [AppIdentity] {
        if let snapshot {
            return snapshot.appsByCategory[category] ?? []
        }
        return categories.apps(in: category, seenIn: history.entries)
    }

    /// History of one search, grouped by day, newest first.
    func historyGroups(matching query: String) -> [HistoryDayGroup] {
        HistoryStore.groupedByDay(historyEntries.filter { matchesSearch(query, $0.text, $0.appName ?? "") })
    }

    /// Engines other than `engine` whose models can run now, for "re-run with".
    func readyEngines(excluding engine: EngineID) -> [EngineID] {
        EngineID.transcriptionEngines.filter { $0 != engine && modelStatus($0).isUsable }
    }

    var readiness: DictationReadiness {
        let required: [PermissionKind] = [.microphone, .accessibility, .inputMonitoring]
        if required.contains(where: { [.denied, .notDetermined].contains(permissionStatus($0)) }) {
            return .permissionsNeeded
        }
        let engine = settings.engine(for: .dictation(settings.languageMode))
        switch modelStatus(engine) {
        case .unknown: return .checking
        case .ready: return .ready(engine)
        case let .systemManaged(available): return available ? .ready(engine) : .modelMissing(engine)
        case let .downloading(progress): return .downloading(engine, progress)
        case .notDownloaded, .damaged: return .modelMissing(engine)
        }
    }

    // MARK: - Banners

    func isBannerVisible(_ id: BannerID) -> Bool {
        !dismissedBanners.contains(id.rawValue)
    }

    func dismissBanner(_ id: BannerID) {
        dismissedBanners.insert(id.rawValue)
        if snapshot == nil {
            settings.dismissedBanners = dismissedBanners
        }
    }

    // MARK: - History actions

    func copyToPasteboard(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }

    func togglePlayback(_ entry: HistoryEntry) {
        guard let url = history.audioURL(for: entry) else {
            alert = AppAlert(title: localizer.t("home.play.failed"), message: localizer.t("home.play.missing"))
            return
        }
        do {
            try playback.toggle(id: entry.id, url: url)
        } catch {
            alert = AppAlert(title: localizer.t("home.play.failed"), message: error.localizedDescription)
        }
    }

    func rerun(_ entry: HistoryEntry, with engine: EngineID) {
        guard !rerunningEntryIDs.contains(entry.id) else { return }
        rerunningEntryIDs.insert(entry.id)
        Task {
            do {
                try await dictation.rerun(entryID: entry.id, with: engine)
            } catch {
                alert = AppAlert(title: localizer.t("home.rerun.failed"), message: error.localizedDescription)
            }
            rerunningEntryIDs.remove(entry.id)
        }
    }

    func deleteHistoryEntry(_ id: UUID) {
        if playback.playingID == id {
            playback.stop()
        }
        if !history.deleteEntry(id: id) {
            showHistoryDeletionFailure()
        }
    }

    func deleteAllHistory() {
        playback.stop()
        if !history.clearHistory() {
            showHistoryDeletionFailure()
        }
    }

    private func showHistoryDeletionFailure() {
        presentAlert(AppAlert(title: localizer.t("data.historyDeletionFailed.title"),
                              message: localizer.t("data.historyDeletionFailed.message")))
    }

    // MARK: - Meetings

    /// Starts a meeting recording and shows it.
    func startMeeting() {
        Task {
            do {
                let meeting = try await meetingRecorder.start()
                selectedMeetingID = meeting.id
                selectedScreen = .meetings
                // The recording carries on with the microphone alone, so without this the
                // only sign that the screen was not captured is a caption on the panel.
                if case let .unavailable(message) = meetingRecorder.screenRecordingStatus {
                    presentAlert(AppAlert(title: localizer.t("meetings.error.screenRecording"), message: message))
                }
            } catch {
                presentAlert(AppAlert(title: localizer.t("meetings.error.start"), message: error.localizedDescription))
            }
        }
    }

    /// Asks macOS for Screen Recording access the moment screen recording is switched on,
    /// so the system prompt comes from that choice and not from a meeting that has already
    /// begun without the screen.
    func requestScreenRecordingAccess() {
        guard permissions.status(of: .screenRecording) != .granted else { return }
        Task { _ = await permissions.request(.screenRecording) }
    }

    /// A recording that stopped by itself. The Meetings screen's inline notice is behind a
    /// closed window for the whole meeting, so the window comes forward with the alert and
    /// the cancel sound carries the news on its own.
    private func reportMeetingFailure(_ message: String) {
        guard snapshot == nil else { return }
        log.error("The meeting recording stopped by itself: \(message, privacy: .public)")
        sounds.play(.cancel)
        presentAlert(AppAlert(title: localizer.t("meetings.error.recording"), message: message))
    }

    /// Stops the recording and transcribes it right away. `MeetingRecorder` only
    /// records, so the model starts the transcriber.
    func stopMeeting() {
        Task {
            guard let meeting = await meetingRecorder.stop() else { return }
            selectedMeetingID = meeting.id
            transcribeMeeting(meeting.id)
        }
    }

    func transcribeMeeting(_ id: UUID) {
        guard !meetingTranscriber.isTranscribing(id) else { return }
        Task {
            do {
                _ = try await meetingTranscriber.transcribe(meetingID: id)
            } catch is CancellationError {
                // The user cancelled; the store records the status.
            } catch {
                presentAlert(AppAlert(title: localizer.t("meetings.error.transcribe"), message: error.localizedDescription))
            }
        }
    }

    func cancelTranscription(_ id: UUID) {
        meetingTranscriber.cancel(meetingID: id)
    }

    func renameMeeting(_ id: UUID, to title: String) {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, var meeting = meetingStore.meetings.first(where: { $0.id == id }),
              meeting.title != trimmed else { return }
        meeting.title = trimmed
        meetingStore.update(meeting)
    }

    func deleteMeeting(_ id: UUID) {
        if meetingTranscriber.isTranscribing(id) {
            meetingTranscriber.cancel(meetingID: id)
        }
        if meetingPlayback.meetingID == id {
            meetingPlayback.stop()
        }
        meetingStore.delete(id: id)
        if selectedMeetingID == id {
            selectedMeetingID = nil
        }
    }

    /// Lets the user pick an audio or video file from anywhere on disk and import it as a
    /// meeting — the same result as dropping it into the Inbox folder, without needing that
    /// setting or a trip to Finder.
    func pickMeetingFileToImport() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = InboxImporter.audioExtensions.compactMap { UTType(filenameExtension: $0) }
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        guard panel.runModal() == .OK else { return }
        let urls = panel.urls
        Task {
            for url in urls {
                await importMeetingFile(at: url)
            }
        }
    }

    /// Mirrors `stopMeeting()`: creates the meeting and its audio, then transcribes it the
    /// same way a fresh recording would be.
    private func importMeetingFile(at url: URL) async {
        do {
            let meeting = try await inboxImporter.importPickedFile(at: url)
            selectedMeetingID = meeting.id
            selectedScreen = .meetings
            transcribeMeeting(meeting.id)
        } catch {
            alert = AppAlert(title: localizer.t("meetings.error.import"), message: error.localizedDescription)
        }
    }

    // MARK: - Meeting playback

    /// Plays or pauses a meeting's audio; switches to it first if a different meeting (or
    /// none) is currently loaded.
    func toggleMeetingPlayback(_ meeting: MeetingRecord) {
        guard meeting.hasAudio else { return }
        if meetingPlayback.meetingID == meeting.id {
            meetingPlayback.isPlaying ? meetingPlayback.pause() : meetingPlayback.play()
            return
        }
        loadMeetingPlayback(meeting) {
            meetingPlayback.play()
        }
    }

    /// Jumps to a moment and plays from there — a transcript timestamp or a marked moment,
    /// clicked to listen instead of only read.
    func seekMeetingPlayback(_ meeting: MeetingRecord, to time: TimeInterval) {
        guard meeting.hasAudio else { return }
        if meetingPlayback.meetingID == meeting.id {
            meetingPlayback.seek(to: time)
            meetingPlayback.play()
            return
        }
        loadMeetingPlayback(meeting) {
            meetingPlayback.seek(to: time)
            meetingPlayback.play()
        }
    }

    func setMeetingPlaybackRate(_ rate: Float) {
        meetingPlayback.setRate(rate)
    }

    /// Nudges playback by a relative offset without changing whether it is playing — for
    /// the left/right arrow shortcuts, unlike a transcript click which always starts
    /// playing from there. A meeting that is not already loaded ignores this: arrows are
    /// for fine-tuning a moment you are already listening to, not for starting playback.
    func nudgeMeetingPlayback(_ meeting: MeetingRecord, by delta: TimeInterval) {
        guard meetingPlayback.meetingID == meeting.id else { return }
        meetingPlayback.seek(to: meetingPlayback.currentTime() + delta)
    }

    private func loadMeetingPlayback(_ meeting: MeetingRecord, then action: () -> Void) {
        do {
            try meetingPlayback.load(meeting: meeting, store: meetingStore)
            action()
        } catch {
            let message = error is MeetingPlaybackError
                ? localizer.t("meetings.error.playback.noAudio") : error.localizedDescription
            alert = AppAlert(title: localizer.t("meetings.error.playback"), message: message)
        }
    }

    func showMeetingInFinder(_ id: UUID) {
        let folder = meetingStore.folder(for: id)
        if FileManager.default.fileExists(atPath: folder.path) {
            NSWorkspace.shared.activateFileViewerSelecting([folder])
        } else {
            NSWorkspace.shared.open(directories.meetingsFolder)
        }
    }

    /// Transcript lines as plain text, `[mm:ss] Me: …` with ★ on marked lines, limited to
    /// the selected speakers when a filter is on.
    func transcriptText(_ meeting: MeetingRecord, speakerFilter: Set<String>? = nil) -> String {
        TranscriptMerger.plainText(meeting: meeting, language: localizer.language, speakerFilter: speakerFilter)
    }

    /// A speaker's name as the meeting's transcript shows it, in the interface language.
    func speakerLabel(_ speaker: MeetingSpeaker, in meeting: MeetingRecord) -> String {
        TranscriptMerger.speakerLabel(speaker, in: meeting, language: localizer.language)
    }

    /// Important moments in the `transcript.md` list format:
    /// `- [12:40] Аня: «first 120 characters…» — note`. Moments with no line keep time and note.
    func highlightsText(_ meeting: MeetingRecord, speakerFilter: Set<String>? = nil) -> String {
        let lines = TranscriptMerger.lines(for: meeting)
        return meeting.highlights.sorted { $0.time < $1.time }.compactMap { highlight -> String? in
            let line = anchoredLine(of: highlight, in: lines)
            if let speakerFilter, !speakerFilter.isEmpty {
                guard let line, speakerFilter.contains(line.speakerID) else { return nil }
            }
            var text = "- [\(TranscriptMerger.timestamp(highlight.time))]"
            if let line {
                let name = meeting.speaker(id: line.speakerID).map { speakerLabel($0, in: meeting) } ?? ""
                text += " \(name): «\(Self.excerpt(line.text))»"
            }
            if let note = highlight.note?.trimmingCharacters(in: .whitespacesAndNewlines), !note.isEmpty {
                text += " — \(note)"
            }
            return text
        }.joined(separator: "\n")
    }

    /// The first 120 characters of a line, with an ellipsis when it was cut.
    static func excerpt(_ text: String, limit: Int = 120) -> String {
        guard text.count > limit else { return text }
        return String(text.prefix(limit)).trimmingCharacters(in: .whitespaces) + "…"
    }

    func exportTranscript(_ meeting: MeetingRecord) {
        let panel = NSSavePanel()
        let safeTitle = meeting.title.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        panel.nameFieldStringValue = "\(safeTitle).md"
        panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        // Export always writes the whole document, whatever the speaker filter.
        let text = TranscriptMerger.markdown(meeting: meeting, language: localizer.language)
        do {
            try text.write(to: url, atomically: true, encoding: .utf8)
        } catch {
            alert = AppAlert(title: localizer.t("meetings.error.export"), message: error.localizedDescription)
        }
    }

    // MARK: - Speakers and important moments

    var speakerModelStatus: ModelStatus { snapshot?.speakerModelStatus ?? diarization.status }

    var speakerModelLoadState: EngineLoadState { snapshot?.speakerModelLoadState ?? diarization.loadState }

    var isSpeakerSeparationSupported: Bool {
        snapshot?.isSpeakerSeparationSupported ?? diarization.isSupportedOnThisSystem
    }

    /// Whether "Process again…" can run in this mode now.
    func canProcess(_ meeting: MeetingRecord, mode: MeetingProcessingOptions.Mode) -> Bool {
        guard snapshot != nil else {
            return meetingTranscriber.canProcess(meeting, mode: mode)
        }
        guard meeting.hasAudio, meeting.status != .recording, meeting.status != .transcribing else { return false }
        switch mode {
        case .full:
            return true
        case .speakersOnly:
            return meeting.formatVersion >= 2 && !meeting.segments.isEmpty && isSpeakerSeparationSupported
                && speakerModelStatus.isUsable
        }
    }

    func renameSpeaker(_ speakerID: String, to name: String?, in meetingID: UUID) {
        editMeeting(meetingID) { MeetingEdits.renameSpeaker(speakerID, to: name, in: $0) }
    }

    func markAsMe(_ speakerID: String, in meetingID: UUID) {
        editMeeting(meetingID) { MeetingEdits.markAsMe(speakerID, in: $0) }
    }

    func mergeSpeaker(_ source: String, into target: String, in meetingID: UUID) {
        editMeeting(meetingID) { try MeetingEdits.mergeSpeaker(source, into: target, in: $0) }
    }

    func reassignLine(_ line: TranscriptLine, to speakerID: String, in meetingID: UUID) {
        editMeeting(meetingID) { try MeetingEdits.reassignLine(line, to: speakerID, in: $0) }
    }

    func toggleHighlight(for line: TranscriptLine, in meetingID: UUID) {
        editMeeting(meetingID) { MeetingEdits.toggleHighlight(for: line, in: $0, now: Date()) }
    }

    func setHighlightNote(_ highlightID: UUID, note: String?, in meetingID: UUID) {
        editMeeting(meetingID) { MeetingEdits.setHighlightNote(highlightID, note: note, in: $0) }
    }

    func deleteHighlight(_ highlightID: UUID, in meetingID: UUID) {
        editMeeting(meetingID) { MeetingEdits.deleteHighlight(highlightID, in: $0) }
    }

    /// Removes what Claude wrote about a meeting, in one action. The recording, the
    /// transcript and the marked moments stay as they were.
    func deleteClaudeNotes(in meetingID: UUID) {
        guard snapshot == nil else { return }
        meetingStore.setClaudeNotes(nil, for: meetingID)
    }

    /// The highlight a note on this line goes to: the first one anchored to it, or a new
    /// one at the line's start. Nil when no highlight could be added.
    func highlightForNote(on line: TranscriptLine, in meetingID: UUID) -> UUID? {
        if let existing = line.highlightIDs.first {
            return existing
        }
        guard snapshot == nil, let before = meetingStore.meetings.first(where: { $0.id == meetingID }) else { return nil }
        let known = Set(before.highlights.map(\.id))
        toggleHighlight(for: line, in: meetingID)
        return meetingStore.meetings.first(where: { $0.id == meetingID })?.highlights.first { !known.contains($0.id) }?.id
    }

    /// Runs speaker separation, and speech recognition in full mode, again. Names and
    /// important moments survive the run.
    func reprocessMeeting(_ id: UUID, options: MeetingProcessingOptions) {
        guard snapshot == nil, !meetingTranscriber.isTranscribing(id) else { return }
        Task {
            do {
                _ = try await meetingTranscriber.transcribe(meetingID: id, options: options)
            } catch is CancellationError {
                // The user cancelled; the transcriber restores or marks the meeting.
            } catch {
                alert = AppAlert(title: localizer.t("meetings.error.reprocess"), message: error.localizedDescription)
            }
        }
    }

    /// Marks an important moment in the meeting being recorded.
    func markMoment() {
        guard snapshot == nil else { return }
        meetingRecorder.markMoment()
    }

    /// Switches the microphone of the meeting being recorded, for that meeting only.
    func toggleMeetingMicrophone() {
        guard snapshot == nil else { return }
        meetingRecorder.toggleMicrophone()
    }

    /// Whether the meeting being recorded writes the microphone; true when none records.
    var isMeetingMicrophoneOn: Bool {
        snapshot?.meetingRecording?.isMicrophoneOn ?? meetingRecorder.isMicrophoneOn
    }

    /// The tracks a meeting is recording right now; nil unless it is the one recording.
    func liveTracks(of meetingID: UUID) -> MeetingRecordingTracks? {
        if let snapshot {
            guard let preview = snapshot.meetingRecording, preview.meetingID == meetingID else { return nil }
            return MeetingRecordingTracks(microphoneOn: preview.isMicrophoneOn, microphoneStatus: preview.microphoneStatus,
                                          systemAudioStatus: preview.systemAudioStatus)
        }
        guard meetingRecorder.currentMeetingID == meetingID else { return nil }
        return meetingRecorder.recordingTracks
    }

    /// Unloads the speaker model and waits for a load in flight before its files go.
    func deleteSpeakerModel() {
        guard snapshot == nil else { return }
        Task {
            do {
                try await meetingWiring.deleteDiarizationModel()
            } catch {
                presentAlert(AppAlert(title: localizer.t("settings.meetings.speakerModel.deleteFailed"),
                                      message: error.localizedDescription))
            }
        }
    }

    /// Applies a pure edit to the stored meeting and saves it when anything changed.
    private func editMeeting(_ id: UUID, _ edit: (MeetingRecord) throws -> MeetingRecord) {
        guard snapshot == nil, let meeting = meetingStore.meetings.first(where: { $0.id == id }) else { return }
        do {
            let edited = try edit(meeting)
            if edited != meeting {
                meetingStore.update(edited)
            }
        } catch {
            alert = AppAlert(title: localizer.t("meetings.error.edit"), message: error.localizedDescription)
        }
    }

    // MARK: - Claude

    /// The executable Claude should start; screenshots use the installed path.
    var executablePath: String { snapshot?.executablePath ?? ClaudeIntegration.currentExecutablePath }

    var claudeRegistrationStatus: ClaudeRegistration? { snapshot?.claudeRegistration ?? claudeRegistration }

    var mcpCheck: MCPCheckState { snapshot?.mcpCheck ?? mcpCheckState }

    /// Reads Claude's configuration files for a `slovo` entry. Only the two booleans are
    /// kept; the files are never written.
    func refreshClaudeRegistration() {
        guard snapshot == nil else { return }
        Task {
            claudeRegistration = await ClaudeIntegration.detectRegistration()
        }
    }

    /// Starts `Slovo --mcp` as Claude would and asks for one meeting.
    func runMCPCheck() {
        guard snapshot == nil, mcpCheckState != .running else { return }
        mcpCheckState = .running
        let executable = URL(fileURLWithPath: executablePath)
        Task {
            let outcome = await MCPSelfCheck.run(executable: executable)
            mcpCheckState = .finished(checkResult(outcome))
            claudeRegistration = await ClaudeIntegration.detectRegistration()
        }
    }

    private func checkResult(_ outcome: MCPSelfCheck.Outcome) -> MCPCheckResult {
        switch outcome {
        case let .answered(isError, message):
            guard isError else { return .works(visibleMeetings: meetingsVisibleToClaude) }
            // The server reads the same preferences; with access off it answers every call
            // with the "access is off" message.
            if !settings.mcpEnabled {
                return .accessOff
            }
            return .failed(message ?? localizer.t("settings.integrations.check.noAnswer"))
        case .timedOut:
            return .failed(localizer.t("settings.integrations.check.timeout"))
        case .exitedWithoutAnswer:
            return .failed(localizer.t("settings.integrations.check.exited"))
        case let .couldNotStart(message):
            return .failed(message)
        }
    }

    /// Meetings inside Claude's age limit, as the server lists them.
    private var meetingsVisibleToClaude: Int {
        let days = settings.mcpMaxAgeDays
        guard days > 0 else { return meetingStore.meetings.count }
        let cutoff = Date().addingTimeInterval(-Double(days) * 86_400)
        return meetingStore.meetings.filter { $0.createdAt >= cutoff }.count
    }

    // MARK: - Models

    func deleteModel(_ id: EngineID) {
        Task {
            do {
                try await wiring.deleteModel(id)
            } catch {
                presentAlert(AppAlert(title: localizer.t("engine.delete.failed"), message: error.localizedDescription))
            }
        }
    }

    // MARK: - Scratchpad

    /// Creates an empty note and selects it.
    func createNote() {
        let note = notes.create(text: "")
        selectedNoteID = note.id
    }

    /// Starts hands-free dictation into a note, or finishes the one already running.
    func toggleDictation(intoNote id: UUID) {
        if case let .recording(info) = dictation.state, info.target == .note(id) {
            dictation.finish()
        } else {
            dictation.startHandsFree(target: .note(id))
        }
    }

    // MARK: - System

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            try loginItem.setEnabled(enabled)
        } catch {
            presentAlert(AppAlert(title: localizer.t("settings.system.launchAtLogin.failed"),
                                  message: error.localizedDescription))
        }
    }

    func openModelsFolder() {
        let root = models.modelsRoot
        NSWorkspace.shared.open(FileManager.default.fileExists(atPath: root.path) ? root : root.deletingLastPathComponent())
    }

    func openDataFolder() {
        try? directories.createFolders()
        NSWorkspace.shared.open(directories.base)
    }

    /// Slovo logs through `os.Logger`; Console shows them under subsystem local.slovo.Slovo.
    func openLogs() {
        let console = URL(fileURLWithPath: "/System/Applications/Utilities/Console.app")
        NSWorkspace.shared.openApplication(at: console, configuration: NSWorkspace.OpenConfiguration())
    }

    /// A store whose file could not be written keeps the change in memory and retries;
    /// tell the user once so a full disk is not mistaken for a saved change.
    private func observeSaveFailures() {
        NotificationCenter.default.publisher(for: StoreFileEvents.saveFailed)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] note in
                guard let self else { return }
                let file = note.userInfo?["file"] as? String ?? ""
                let reason = note.userInfo?["message"] as? String ?? ""
                self.presentAlert(AppAlert(title: self.localizer.t("data.saveFailed.title"),
                                           message: self.localizer.t("data.saveFailed.message", file, reason)))
            }
            .store(in: &cancellables)
    }

    /// Stores that found a damaged file moved it aside and started empty; say so once.
    private func reportDamagedFiles() {
        let warnings = [history.loadWarning, dictionary.loadWarning, snippets.loadWarning, notes.loadWarning,
                        categories.loadWarning].compactMap { $0 }
        guard !warnings.isEmpty else { return }
        for warning in warnings {
            log.error("\(warning, privacy: .public)")
        }
        alert = AppAlert(title: localizer.t("data.damaged.title"), message: warnings.joined(separator: "\n"))
    }
}
