import Combine
import Foundation

/// The dictation state machine: trigger → record → transcribe → pipeline → insert →
/// history. Everything it talks to is a protocol, so tests use fakes.
@MainActor
public final class DictationController: ObservableObject {
    public struct Dependencies {
        public var recorder: AudioRecording
        public var hotkeys: HotkeyMonitoring
        public var inserter: TextInserting
        public var frontmostApp: FrontmostAppProviding
        public var permissions: PermissionsProviding
        public var engines: EngineProviding
        public var pipeline: TextProcessing
        public var history: HistoryStoring
        public var dictionary: DictionaryStoring
        public var snippets: SnippetStoring
        public var notes: NoteStoring
        public var categories: AppCategoryStoring
        public var meetings: MeetingActivityProviding
        public var sounds: SoundPlaying?
        public var settings: AppSettings
        public var directories: AppDirectories
        public var uptime: UptimeProviding
        /// Puts a helper's English into mixed dictation; nil keeps the engine's text as it is.
        public var mixedLanguage: MixedLanguageHelping?

        public init(recorder: AudioRecording, hotkeys: HotkeyMonitoring, inserter: TextInserting,
                    frontmostApp: FrontmostAppProviding, permissions: PermissionsProviding,
                    engines: EngineProviding, pipeline: TextProcessing, history: HistoryStoring,
                    dictionary: DictionaryStoring, snippets: SnippetStoring, notes: NoteStoring,
                    categories: AppCategoryStoring, meetings: MeetingActivityProviding, sounds: SoundPlaying?,
                    settings: AppSettings, directories: AppDirectories,
                    uptime: UptimeProviding = SystemUptime(), mixedLanguage: MixedLanguageHelping? = nil) {
            self.recorder = recorder
            self.hotkeys = hotkeys
            self.inserter = inserter
            self.frontmostApp = frontmostApp
            self.permissions = permissions
            self.engines = engines
            self.pipeline = pipeline
            self.history = history
            self.dictionary = dictionary
            self.snippets = snippets
            self.notes = notes
            self.categories = categories
            self.meetings = meetings
            self.sounds = sounds
            self.settings = settings
            self.directories = directories
            self.uptime = uptime
            self.mixedLanguage = mixedLanguage
        }
    }

    /// How long result messages stay in the Flow bar.
    public static let messageDuration: TimeInterval = 1.5
    /// The Flow bar countdown appears this long before the hands-free limit.
    public static let countdownThreshold: TimeInterval = 30
    /// How long a dictation may wait for its engine to load before it is abandoned. A
    /// first Parakeet load compiles for the Neural Engine and can take minutes.
    public static let modelLoadTimeout: TimeInterval = 300
    /// How long transcription may take, on top of the recording's own length, before the
    /// dictation is abandoned. Bundled engines need a small fraction of the audio length.
    public static let transcriptionTimeout: TimeInterval = 60
    /// Prefix of the temporary WAV files engines read; see `removeLeftoverRecordings`.
    nonisolated static let temporaryRecordingPrefix = "Slovo-"

    @Published public private(set) var state: DictationState = .idle {
        didSet {
            let isProcessing = state.isProcessing
            guard isProcessing != oldValue.isProcessing else { return }
            (dependencies.hotkeys as? ProcessingAwareHotkeys)?.syncProcessing(isProcessing)
        }
    }
    /// Smoothed input level from 0 to 1 while recording.
    @Published public private(set) var level: Float = 0
    /// Seconds left in a hands-free recording during its last 30 s, else nil.
    @Published public private(set) var handsFreeRemaining: TimeInterval?
    /// The newest remaining dictation, used by both the shortcut and the menu.
    public var lastTranscript: String? {
        guard let text = dependencies.history.entries.first?.text, !text.isEmpty else { return nil }
        return text
    }
    /// The result of a dictation whose target was `.practice`.
    @Published public private(set) var practiceResult: String?

    /// Where recordings started with the dictation key go. Onboarding sets `.practice`
    /// during its practice step, so holding the key fills the practice field instead of
    /// pasting into another app.
    public var keyTarget: DictationTarget = .frontmostApp

    /// How long result messages stay; tests shorten it.
    var messageLifetime: TimeInterval = DictationController.messageDuration
    /// The processing time limits; tests shorten them.
    var modelLoadTimeLimit: TimeInterval = DictationController.modelLoadTimeout
    var transcriptionTimeLimit: TimeInterval = DictationController.transcriptionTimeout
    /// How often a running recording checks its time limit.
    var limitCheckInterval: TimeInterval = 0.25
    /// Replaces the delays of the hold checks; tests set it so they run the checks by hand.
    var holdCheckDelay: TimeInterval?

    private struct GigaAMRecording {
        let session: GigaAMRecordingSession
        let engineID: EngineID
        let languageMode: LanguageMode
        let deviceUID: String?
    }

    private struct FinishedRecording {
        let audio: RecordedAudio
        let hasCaptureGap: Bool
        let info: RecordingInfo
        let app: AppIdentity?
        let insertionTarget: TextInsertionTarget?
        let gigaam: GigaAMRecordingSession?
    }

    private let dependencies: Dependencies
    private let log = SlovoLog.logger("Dictation")
    private var isStarted = false
    /// The app in front when the running recording began; nil for notes and practice.
    private var recordingApp: AppIdentity?
    private var recordingInsertionTarget: TextInsertionTarget?
    private var gigaamRecording: GigaAMRecording?
    /// A push-to-talk recording started by the key whose hold is not confirmed yet.
    /// Capture runs from key down so the first syllable is kept, but the Flow bar and
    /// the start sound wait: a tap shorter than the minimum hold is not a dictation.
    private var unconfirmedRecording: RecordingInfo?
    /// Why a key press could not start recording, shown only if the key is still held
    /// at the hold check, so tapping the key (Fn switches input sources) flashes nothing.
    private var unconfirmedRefusal: FlowMessage?
    /// True once a hold check found the key released and waits for the tap to resolve.
    private var isWaitingForTapDecision = false
    private var holdCheckTask: Task<Void, Never>?
    private var processingTask: Task<Void, Never>?
    /// Abandons a dictation whose engine takes too long, so a load stuck behind a download
    /// or a hung engine cannot keep the Flow bar busy and refuse every new dictation.
    private var processingWatchdog: Task<Void, Never>?
    /// Bumped whenever a dictation in progress is abandoned, so its late results are dropped.
    private var processingNumber = 0
    private var messageTask: Task<Void, Never>?
    private var limitTask: Task<Void, Never>?
    private var defaultsObserver: NSObjectProtocol?
    private var appliedHotkeyConfiguration: HotkeyConfiguration?

    public init(dependencies: Dependencies) {
        self.dependencies = dependencies
    }

    // MARK: - Lifecycle

    /// Connects the hotkey monitor and starts listening when permissions allow.
    public func start() {
        guard !isStarted else { return }
        isStarted = true
        dependencies.hotkeys.onTrigger = { [weak self] trigger in
            self?.handle(trigger)
        }
        dependencies.recorder.onLevel = { [weak self] value in
            self?.recordingLevelChanged(value)
        }
        refreshHotkeys()
        defaultsObserver = NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: dependencies.settings.defaults, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.settingsDidChange()
            }
        }
        // A crash while an engine read a recording leaves its temporary copy behind, even
        // when dictation audio is never kept.
        let folder = FileManager.default.temporaryDirectory
        Task.detached(priority: .utility) {
            Self.removeLeftoverRecordings(in: folder, now: Date())
        }
    }

    /// Deletes temporary dictation recordings (`Slovo-<uuid>.wav`) older than `minimumAge`,
    /// so a recording another running copy of Slovo is still transcribing is left alone.
    /// Returns how many files were removed.
    @discardableResult
    nonisolated static func removeLeftoverRecordings(in folder: URL, now: Date,
                                                     minimumAge: TimeInterval = 3600) -> Int {
        let fileManager = FileManager.default
        let keys: [URLResourceKey] = [.isRegularFileKey, .contentModificationDateKey]
        guard let items = try? fileManager.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys) else {
            return 0
        }
        var removed = 0
        for item in items where isTemporaryRecording(item.lastPathComponent) {
            guard let values = try? item.resourceValues(forKeys: Set(keys)), values.isRegularFile == true,
                  let modified = values.contentModificationDate, now.timeIntervalSince(modified) >= minimumAge else {
                continue
            }
            if (try? fileManager.removeItem(at: item)) != nil {
                removed += 1
            }
        }
        return removed
    }

    /// `Slovo-<uuid>.wav` exactly; meeting work folders share the prefix but not the shape.
    nonisolated static func isTemporaryRecording(_ name: String) -> Bool {
        guard name.hasPrefix(temporaryRecordingPrefix), name.hasSuffix(".wav") else { return false }
        let middle = name.dropFirst(temporaryRecordingPrefix.count).dropLast(".wav".count)
        return UUID(uuidString: String(middle)) != nil
    }

    public func stop() {
        guard isStarted else { return }
        isStarted = false
        if let defaultsObserver {
            NotificationCenter.default.removeObserver(defaultsObserver)
        }
        defaultsObserver = nil
        _ = stopRecording(sound: nil, keepAudio: false, fromTrigger: true)
        clearUnconfirmedRecording()
        abandonProcessing()
        if case .processing = state {
            state = .idle
        }
        dependencies.hotkeys.onTrigger = nil
        dependencies.hotkeys.stop()
        dependencies.recorder.onLevel = nil
        appliedHotkeyConfiguration = nil
    }

    /// Starts the key listener if it is not running, for example after Input Monitoring
    /// was granted, and applies the current key settings.
    public func refreshHotkeys() {
        guard isStarted else { return }
        let configuration = dependencies.settings.hotkeyConfiguration
        let hotkeys = dependencies.hotkeys
        if !hotkeys.isRunning {
            appliedHotkeyConfiguration = configuration
            if !hotkeys.start(configuration: configuration) {
                log.notice("The dictation key listener is not running; Input Monitoring is probably off")
            }
        } else if configuration != appliedHotkeyConfiguration {
            appliedHotkeyConfiguration = configuration
            hotkeys.update(configuration: configuration)
        }
    }

    func settingsDidChange() {
        invalidateGigaAMIfSettingsChanged()
        let configuration = dependencies.settings.hotkeyConfiguration
        guard dependencies.hotkeys.isRunning, configuration != appliedHotkeyConfiguration else { return }
        appliedHotkeyConfiguration = configuration
        dependencies.hotkeys.update(configuration: configuration)
    }

    // MARK: - Commands

    /// Applies a trigger from the hotkey state machine.
    public func handle(_ trigger: DictationTrigger) {
        switch trigger {
        case let .begin(mode):
            beginRecording(mode: mode, target: keyTarget, fromTrigger: true)
        case .promoteToHandsFree:
            promoteToHandsFree()
        case .finish:
            finishRecording(fromTrigger: true)
        case .discard:
            _ = stopRecording(sound: nil, keepAudio: false, fromTrigger: true)
        case .cancel:
            cancelDictation(fromTrigger: true)
        }
    }

    /// Starts a hands-free recording from the Flow bar, Scratchpad or onboarding.
    public func startHandsFree(target: DictationTarget = .frontmostApp) {
        beginRecording(mode: .handsFree, target: target, fromTrigger: false)
    }

    /// Stops the current recording and transcribes it.
    public func finish() {
        finishRecording(fromTrigger: false)
    }

    /// Stops the current recording without transcribing, or abandons a dictation that
    /// is still being transcribed.
    public func cancel() {
        cancelDictation(fromTrigger: false)
    }

    /// Inserts `lastTranscript` again.
    public func pasteLastTranscript() async {
        guard let text = lastTranscript, !text.isEmpty else { return }
        let result = await dependencies.inserter.insert(text)
        guard case let .copied(reason) = result else { return }
        switch state {
        case .idle, .message:
            showMessage(.copied(reason))
        case .recording, .processing:
            break
        }
    }

    /// Transcribes a history entry's audio with another engine and replaces its text.
    public func rerun(entryID: UUID, with engineID: EngineID) async throws {
        guard let entry = dependencies.history.entries.first(where: { $0.id == entryID }) else {
            throw SlovoError(CoreStrings.string("dictation.error.entryMissing", table: .dictation))
        }
        guard let audioURL = dependencies.history.audioURL(for: entry) else {
            throw SlovoError(CoreStrings.string("dictation.error.audioMissing", table: .dictation))
        }
        // Retention or a user removing the original audio can run while the model loads.
        // Snapshot before the first suspension, and keep that file until both recognizers
        // have finished, without changing the user's retention policy or history metadata.
        let snapshotURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(Self.temporaryRecordingPrefix)\(UUID().uuidString).wav")
        defer { try? FileManager.default.removeItem(at: snapshotURL) }
        try FileManager.default.copyItem(at: audioURL, to: snapshotURL)
        // Copying preserves the old timestamp; crash cleanup must see an active snapshot
        // as new even when the original recording was made weeks ago.
        try FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: snapshotURL.path)
        // Apple Dictation's locale follows the language the entry was dictated in.
        let engine = try await engine(engineID, languageMode: entry.languageMode)
        var rawText = try await transcribe(engine, fileURL: snapshotURL)
        dependencies.engines.markUsed(engineID)
        rawText = await withEnglishHelp(rawText, audio: snapshotURL, duration: entry.duration, engine: engineID,
                                        languageMode: entry.languageMode)

        let result = dependencies.pipeline.process(rawText, context: pipelineContext(category: entry.category,
                                                                                     languageMode: entry.languageMode))
        let text = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Self.hasContent(text) else {
            throw SlovoError(CoreStrings.string("dictation.error.rerunEmpty", table: .dictation))
        }
        // The entry may have been edited or deleted while the engine worked.
        guard var updated = dependencies.history.entries.first(where: { $0.id == entryID }) else {
            throw SlovoError(CoreStrings.string("dictation.error.entryMissing", table: .dictation))
        }
        updated.text = text
        updated.rawText = rawText
        updated.engineID = engineID
        updated.wordCount = HistoryEntry.countWords(in: text)
        updated.fixesCount = result.fixesCount
        dependencies.history.update(updated)
    }

    /// Clears a result message before its timeout, for example when it is clicked.
    public func dismissMessage() {
        guard case .message = state else { return }
        messageTask?.cancel()
        messageTask = nil
        state = .idle
    }

    // MARK: - Recording

    private func beginRecording(mode: DictationMode, target: DictationTarget, fromTrigger: Bool) {
        if var info = unconfirmedRecording {
            if fromTrigger {
                dependencies.hotkeys.syncRecording(mode: info.mode)
            } else {
                // The Flow bar was clicked while the key is held: keep the capture going hands-free.
                info.mode = .handsFree
                dependencies.hotkeys.syncRecording(mode: .handsFree)
                showRecording(info)
            }
            return
        }
        // A key press may still turn out to be a tap, which must change nothing on screen.
        let waitsForHold = fromTrigger && mode == .pushToTalk
        unconfirmedRefusal = nil
        switch state {
        case let .recording(info):
            // Already recording: keep the key state machine in step with what really runs.
            if fromTrigger {
                dependencies.hotkeys.syncRecording(mode: info.mode)
            }
            return
        case .processing:
            // One dictation at a time; the previous one is still being transcribed.
            if fromTrigger {
                dependencies.hotkeys.syncRecording(mode: nil)
            }
            return
        case .message:
            if !waitsForHold {
                messageTask?.cancel()
                messageTask = nil
                state = .idle
            }
        case .idle:
            break
        }

        if dependencies.meetings.isMeetingRecording {
            refuse(.blockedByMeeting, fromTrigger: fromTrigger, waitsForHold: waitsForHold)
            return
        }
        if dependencies.permissions.status(of: .microphone) == .denied {
            refuse(.failure(.microphoneDenied), fromTrigger: fromTrigger, waitsForHold: waitsForHold)
            return
        }
        let app = target == .frontmostApp ? dependencies.frontmostApp.currentApp() : nil
        do {
            try dependencies.recorder.start(deviceUID: dependencies.settings.microphoneUID)
        } catch {
            log.error("Could not start recording: \(error.localizedDescription, privacy: .public)")
            refuse(.failure(.recordingFailed(error.localizedDescription)), fromTrigger: fromTrigger,
                   waitsForHold: waitsForHold)
            return
        }

        recordingApp = app
        recordingInsertionTarget = target == .frontmostApp ? dependencies.inserter.captureTarget() : nil
        level = 0
        handsFreeRemaining = nil
        let info = RecordingInfo(mode: mode, startedAt: dependencies.uptime.uptime(), target: target)
        if waitsForHold {
            unconfirmedRecording = info
            scheduleHoldCheck(after: holdCheckDelay ?? dependencies.settings.hotkeyConfiguration.minimumHold)
            return
        }
        if !fromTrigger {
            dependencies.hotkeys.syncRecording(mode: mode)
        }
        showRecording(info)
    }

    /// Makes a running recording visible: the Flow bar pill, the start sound, the time limit.
    private func showRecording(_ info: RecordingInfo) {
        // The recorder opens the device in the background and stops by itself when that fails.
        guard dependencies.recorder.isRecording else {
            microphoneFailed()
            return
        }
        clearUnconfirmedRecording()
        messageTask?.cancel()
        messageTask = nil
        state = .recording(info)
        dependencies.sounds?.play(.start)
        startLimitTimer()
        startGigaAMRecording()
        warmEnglishHelper()
    }

    private func startGigaAMRecording() {
        let languageMode = dependencies.settings.languageMode
        let engineID = dependencies.engines.engineID(for: .dictation(languageMode))
        guard engineID.writesOnlyRussian,
              let buffer = (dependencies.recorder as? IncrementalAudioRecording)?.captureBuffer else { return }
        let session = GigaAMRecordingSession(capture: buffer) { [weak self] in
            try await self?.engine(engineID, languageMode: languageMode) as? GigaAMChunkDecoding
        }
        gigaamRecording = GigaAMRecording(session: session, engineID: engineID, languageMode: languageMode,
                                        deviceUID: dependencies.settings.microphoneUID)
    }

    private func invalidateGigaAMIfSettingsChanged() {
        guard let active = gigaamRecording else { return }
        let languageMode = dependencies.settings.languageMode
        if active.languageMode != languageMode || active.deviceUID != dependencies.settings.microphoneUID
            || active.engineID != dependencies.engines.engineID(for: .dictation(languageMode)) {
            active.session.cancel()
            gigaamRecording = nil
        }
    }

    /// Loads the English helper of a mixed dictation while the user speaks, so a cold model is
    /// ready when GigaAM's text asks for it.
    private func warmEnglishHelper() {
        guard let helper = dependencies.mixedLanguage else { return }
        let languageMode = dependencies.settings.languageMode
        helper.warmUp(for: dependencies.engines.engineID(for: .dictation(languageMode)), languageMode: languageMode)
    }

    private func scheduleHoldCheck(after delay: TimeInterval) {
        holdCheckTask?.cancel()
        holdCheckTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self else { return }
            self.holdCheckTask = nil
            self.holdCheckDue()
        }
    }

    /// The minimum hold has passed since the key went down. A key still held is a
    /// dictation. A released key was a tap, which the key state machine promotes to
    /// hands-free or discards within the double-tap window; a second check after that
    /// window stops a tap nobody resolved, so the microphone never runs unseen.
    func holdCheckDue() {
        if let refusal = unconfirmedRefusal {
            unconfirmedRefusal = nil
            if dependencies.hotkeys.isKeyDown {
                showMessage(refusal)
            }
            return
        }
        guard let info = unconfirmedRecording else { return }
        if dependencies.hotkeys.isKeyDown {
            showRecording(info)
        } else if !isWaitingForTapDecision {
            isWaitingForTapDecision = true
            scheduleHoldCheck(after: holdCheckDelay ?? dependencies.settings.hotkeyConfiguration.doubleTapWindow + 0.1)
        } else {
            log.notice("A tap of the dictation key was never resolved; stopping its capture")
            _ = stopRecording(sound: nil, keepAudio: false, fromTrigger: false)
        }
    }

    private func clearUnconfirmedRecording() {
        unconfirmedRecording = nil
        unconfirmedRefusal = nil
        isWaitingForTapDecision = false
        holdCheckTask?.cancel()
        holdCheckTask = nil
    }

    private func refuse(_ message: FlowMessage, fromTrigger: Bool, waitsForHold: Bool) {
        // The key state machine already believes a recording started.
        if fromTrigger {
            dependencies.hotkeys.syncRecording(mode: nil)
        }
        guard waitsForHold else {
            showMessage(message)
            return
        }
        unconfirmedRefusal = message
        scheduleHoldCheck(after: holdCheckDelay ?? dependencies.settings.hotkeyConfiguration.minimumHold)
    }

    /// The recorder stopped by itself: the microphone could not be opened, or it was lost
    /// mid-recording and no other input took over. What it captured before that is still
    /// a dictation; a recording that kept no speech ends with an error, not "Nothing heard".
    private func microphoneFailed() {
        log.error("The microphone stopped before the recording ended")
        if state.isRecording || unconfirmedRecording != nil {
            finishRecording(fromTrigger: false)
            return
        }
        // Shown before anything could be captured, such as a Flow bar start whose device
        // failed at once.
        dependencies.recorder.cancel()
        recordingApp = nil
        recordingInsertionTarget = nil
        level = 0
        handsFreeRemaining = nil
        dependencies.hotkeys.syncRecording(mode: nil)
        showMicrophoneFailure()
    }

    private func showMicrophoneFailure() {
        showMessage(.failure(.recordingFailed(CoreStrings.string("dictation.error.microphoneFailed", table: .dictation))))
    }

    private func promoteToHandsFree() {
        if var info = unconfirmedRecording {
            info.mode = .handsFree
            showRecording(info)
            checkRecordingLimit()
            return
        }
        guard case var .recording(info) = state, info.mode == .pushToTalk else { return }
        info.mode = .handsFree
        state = .recording(info)
        checkRecordingLimit()
    }

    /// Stops capture and resets the recording state; nil when nothing was recording.
    private func stopRecording(sound: SoundCue?, keepAudio: Bool, fromTrigger: Bool) -> FinishedRecording? {
        let info: RecordingInfo
        let wasVisible: Bool
        if case let .recording(current) = state {
            info = current
            wasVisible = true
        } else if let unconfirmed = unconfirmedRecording {
            info = unconfirmed
            wasVisible = false
        } else {
            return nil
        }
        clearUnconfirmedRecording()
        limitTask?.cancel()
        limitTask = nil
        invalidateGigaAMIfSettingsChanged()
        let gigaam = gigaamRecording?.session
        gigaamRecording = nil
        let audio: RecordedAudio
        let hasCaptureGap: Bool
        if keepAudio {
            gigaam?.stop()
            audio = dependencies.recorder.stop()
            hasCaptureGap = (dependencies.recorder as? CaptureGapReporting)?.hasCaptureGap == true
        } else {
            gigaam?.cancel()
            dependencies.recorder.cancel()
            audio = .empty
            hasCaptureGap = false
        }
        let app = recordingApp
        let insertionTarget = recordingInsertionTarget
        recordingApp = nil
        recordingInsertionTarget = nil
        level = 0
        handsFreeRemaining = nil
        if wasVisible {
            state = .idle
        }
        // A capture nobody saw ends silently, even when it is kept as a dictation: a stop
        // cue with no start cue before it sounds like a glitch.
        if let sound, wasVisible {
            dependencies.sounds?.play(sound)
        }
        if !fromTrigger {
            dependencies.hotkeys.syncRecording(mode: nil)
        }
        return FinishedRecording(audio: audio, hasCaptureGap: hasCaptureGap, info: info, app: app, insertionTarget: insertionTarget,
                                 gigaam: keepAudio ? gigaam : nil)
    }

    private func cancelDictation(fromTrigger: Bool) {
        if unconfirmedRecording != nil {
            _ = stopRecording(sound: .cancel, keepAudio: false, fromTrigger: fromTrigger)
            return
        }
        switch state {
        case .recording:
            _ = stopRecording(sound: .cancel, keepAudio: false, fromTrigger: fromTrigger)
        case .processing:
            abandonProcessing()
            state = .idle
        case .idle, .message:
            break
        }
    }

    private func finishRecording(fromTrigger: Bool) {
        guard state.isRecording || unconfirmedRecording != nil else { return }
        // False when the recorder stopped by itself; `stop()` still returns what it kept.
        let microphoneWorked = dependencies.recorder.isRecording
        guard let recording = stopRecording(sound: microphoneWorked ? .stop : nil, keepAudio: true,
                                            fromTrigger: fromTrigger) else { return }
        if !microphoneWorked || recording.hasCaptureGap { recording.gigaam?.cancel() }
        guard !SpeechEnergy.isEffectivelySilent(recording.audio) else {
            recording.gigaam?.cancel()
            if recording.hasCaptureGap {
                showPartialRecording()
            } else if microphoneWorked {
                showMessage(.nothingHeard)
            } else {
                showMicrophoneFailure()
            }
            return
        }
        if !microphoneWorked {
            log.notice("Transcribing what was captured before the microphone stopped")
        }
        let languageMode = dependencies.settings.languageMode
        let engineID = dependencies.engines.engineID(for: .dictation(languageMode))
        let isLoadingModel = dependencies.engines.loadState(of: engineID) != .ready
        state = .processing(isLoadingModel: isLoadingModel)
        processingNumber += 1
        let number = processingNumber
        startProcessingWatchdog(number, limit: isLoadingModel ? modelLoadTimeLimit
                                                              : transcriptionTimeLimit + recording.audio.duration)
        processingTask = Task { [weak self] in
            await self?.transcribe(recording, engineID: engineID, languageMode: languageMode, number: number)
            self?.stopProcessingWatchdog(number)
        }
    }

    private func showPartialRecording(copied: Bool = false) {
        let key = copied ? "dictation.error.partialRecordingCopied" : "dictation.error.partialRecording"
        showMessage(.failure(.recordingFailed(CoreStrings.string(key, table: .dictation))))
    }

    /// Abandons dictation `number` with an error once `limit` seconds pass. The engine may
    /// still finish in the background; its result is dropped like a cancelled one.
    private func startProcessingWatchdog(_ number: Int, limit: TimeInterval) {
        processingWatchdog?.cancel()
        processingWatchdog = Task { [weak self] in
            try? await Task.sleep(for: .seconds(limit))
            guard !Task.isCancelled, let self, self.isCurrent(number) else { return }
            self.log.error("Dictation \(number) took longer than \(Int(limit)) s; abandoning it")
            self.processingWatchdog = nil
            self.abandonProcessing()
            self.showMessage(.failure(.engineFailed(CoreStrings.string("dictation.error.timedOut", table: .dictation))))
        }
    }

    private func stopProcessingWatchdog(_ number: Int) {
        guard number == processingNumber else { return }
        processingWatchdog?.cancel()
        processingWatchdog = nil
    }

    /// The microphone the last recording used; the Flow bar names it when nothing was heard,
    /// which is how a silent headset input shows up.
    public var lastInputDeviceName: String? {
        dependencies.recorder.lastDeviceName
    }

    private func recordingLevelChanged(_ value: Float) {
        guard state.isRecording else { return }
        level = value
    }

    private func startLimitTimer() {
        limitTask?.cancel()
        let interval = limitCheckInterval
        limitTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(interval))
                guard !Task.isCancelled, let self else { return }
                self.checkRecordingLimit()
            }
        }
    }

    /// Updates the countdown and finishes a recording that reached the maximum length.
    /// Push-to-talk has the same limit, so a key stuck down cannot record forever. Also
    /// notices a microphone that could not be opened while the recording is shown.
    func checkRecordingLimit() {
        guard case let .recording(info) = state else { return }
        guard dependencies.recorder.isRecording else {
            microphoneFailed()
            return
        }
        let elapsed = dependencies.uptime.uptime() - info.startedAt
        let remaining = dependencies.settings.maxHandsFreeSeconds - elapsed
        guard remaining > 0 else {
            log.info("The recording reached its time limit")
            finishRecording(fromTrigger: false)
            return
        }
        invalidateGigaAMIfSettingsChanged()
        gigaamRecording?.session.advance()
        let countdown: TimeInterval? = info.mode == .handsFree && remaining <= Self.countdownThreshold
            ? remaining.rounded(.up) : nil
        if countdown != handsFreeRemaining {
            handsFreeRemaining = countdown
        }
    }

    // MARK: - Transcription and delivery

    private func transcribe(_ recording: FinishedRecording, engineID: EngineID, languageMode: LanguageMode,
                            number: Int) async {
        defer { recording.gigaam?.cancel() }
        let entryID = UUID()
        let date = Date()
        let target = recording.info.target
        let keepsAudio = target != .practice && dependencies.settings.keepDictationAudio != .never
        // Engines read files, so the audio is written to a temporary file first. It moves
        // into Recordings/ only together with its history entry, so retention never
        // finds the file of a dictation that is still being transcribed.
        let fileURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(Self.temporaryRecordingPrefix)\(entryID.uuidString).wav")
        defer {
            try? FileManager.default.removeItem(at: fileURL)
        }

        do {
            try WAVFile.write(recording.audio, to: fileURL)
        } catch {
            guard isCurrent(number) else { return }
            log.error("Could not write the recording: \(error.localizedDescription, privacy: .public)")
            showMessage(.failure(.recordingFailed(error.localizedDescription)))
            return
        }

        var rawText: String
        do {
            let engine = try await engine(engineID, languageMode: languageMode)
            guard isCurrent(number) else { return }
            if case .processing(isLoadingModel: true) = state {
                // The load is over; transcription gets its own, shorter limit.
                startProcessingWatchdog(number, limit: transcriptionTimeLimit + recording.audio.duration)
            }
            state = .processing(isLoadingModel: false)
            if let decoder = engine as? GigaAMChunkDecoding,
               let prepared = try await recording.gigaam?.finish(audio: recording.audio, decoder: decoder) {
                rawText = prepared
            } else {
                recording.gigaam?.cancel()
                rawText = try await transcribe(engine, fileURL: fileURL)
            }
            dependencies.engines.markUsed(engineID)
        } catch {
            guard isCurrent(number) else { return }
            log.error("Transcription with \(engineID.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            if let unavailable = error as? EngineUnavailableError {
                showMessage(.failure(.modelMissing(unavailable.engineID)))
            } else {
                showMessage(.failure(.engineFailed(error.localizedDescription)))
            }
            return
        }
        guard isCurrent(number) else { return }
        // The helper has its own, shorter limit and never fails the dictation; cancelling
        // the dictation cancels it.
        rawText = await withEnglishHelp(rawText, audio: fileURL, duration: recording.audio.duration, engine: engineID,
                                        languageMode: languageMode)
        guard isCurrent(number) else { return }
        // The engine is done; delivering the text must not be cut short.
        stopProcessingWatchdog(number)

        let category = target == .frontmostApp
            ? recording.app.map { dependencies.categories.category(for: $0) } ?? .other
            : .other
        let result = dependencies.pipeline.process(rawText, context: pipelineContext(category: category,
                                                                                    languageMode: languageMode))
        let text = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Self.hasContent(text) else {
            if recording.hasCaptureGap { showPartialRecording() } else { showMessage(.nothingHeard) }
            return
        }

        switch target {
        case .frontmostApp:
            let insertion = await dependencies.inserter.insert(text, into: recording.insertionTarget)
            // Cancelling while the paste runs cannot take the text back, so history still records it.
            let isStillCurrent = isCurrent(number)
            switch insertion {
            case .pasted:
                if isStillCurrent {
                    if recording.hasCaptureGap { showPartialRecording() } else { state = .idle }
                }
            case let .copied(reason):
                log.info("Copied instead of pasting: \(reason.rawValue, privacy: .public)")
                if isStillCurrent {
                    if recording.hasCaptureGap { showPartialRecording(copied: true) } else { showMessage(.copied(reason)) }
                }
            case .empty:
                if isStillCurrent {
                    if recording.hasCaptureGap { showPartialRecording() } else { showMessage(.nothingHeard) }
                }
                return
            }
        case let .note(noteID):
            dependencies.notes.append(text, toNote: noteID)
            if recording.hasCaptureGap { showPartialRecording() } else { state = .idle }
        case .practice:
            practiceResult = text
            if recording.hasCaptureGap { showPartialRecording() } else { state = .idle }
            return
        }

        let audioFileName = keepsAudio ? keepRecording(fileURL, id: entryID, date: date) : nil
        dependencies.history.add(HistoryEntry(
            id: entryID, date: date, text: text, rawText: rawText, engineID: engineID, languageMode: languageMode,
            appBundleID: recording.app?.bundleID, appName: recording.app?.name, category: category,
            duration: recording.audio.duration, wordCount: HistoryEntry.countWords(in: text),
            fixesCount: result.fixesCount, audioFileName: audioFileName))
    }

    /// Moves a transcribed recording to `Recordings/<day>/<id>.wav` and returns that
    /// relative path; nil when the move fails, and the entry is kept without audio.
    private func keepRecording(_ fileURL: URL, id: UUID, date: Date) -> String? {
        let relativePath = WAVFile.relativePath(for: id, date: date)
        let destination = dependencies.directories.recordingURL(relativePath: relativePath)
        do {
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try FileManager.default.moveItem(at: fileURL, to: destination)
            return relativePath
        } catch {
            log.error("Could not keep the recording: \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    /// Expected terms go to the primary recognizer too, not only to its helper.
    private func transcribe(_ engine: any TranscriptionEngine, fileURL: URL) async throws -> String {
        if let hinted = engine as? VocabularyTranscribing {
            let terms = dependencies.dictionary.items.filter { $0.kind == .term }.map(\.text)
            return try await hinted.transcribe(fileURL: fileURL, vocabulary: terms)
        }
        return try await engine.transcribe(fileURL: fileURL)
    }

    /// The engine's text with English words from the helper when the dictation is mixed and
    /// GigaAM was unsure of a word; the merged text is what the pipeline and history see.
    private func withEnglishHelp(_ text: String, audio: URL, duration: TimeInterval, engine: EngineID,
                                 languageMode: LanguageMode) async -> String {
        guard let helper = dependencies.mixedLanguage else { return text }
        let vocabulary = dependencies.dictionary.items.filter { $0.kind == .term }.map(\.text)
        return await helper.improve(text, audio: audio, duration: duration, engine: engine,
                                    languageMode: languageMode, vocabulary: vocabulary)
    }

    private func pipelineContext(category: AppCategory, languageMode: LanguageMode) -> TextPipelineContext {
        TextPipelineContext(dictionary: dependencies.dictionary.items,
                            snippets: dependencies.snippets.snippets,
                            cleanup: dependencies.settings.cleanup,
                            style: dependencies.settings.style(for: category),
                            languageMode: languageMode)
    }

    /// Whether a result is worth delivering. Cleanup turns a lone filler ("Эм.", "Hmm?",
    /// "Э...") into bare punctuation, which is not. Letters and digits count, and so do
    /// symbols, so a snippet that expands to an emoji still arrives.
    static func hasContent(_ text: String) -> Bool {
        text.contains { $0.isLetter || $0.isNumber || $0.isSymbol }
    }

    private func isCurrent(_ number: Int) -> Bool {
        guard number == processingNumber, case .processing = state else { return false }
        return true
    }

    private func abandonProcessing() {
        processingNumber += 1
        processingTask?.cancel()
        processingTask = nil
        processingWatchdog?.cancel()
        processingWatchdog = nil
    }

    /// The engine for a language mode when the provider tells modes apart (Apple Dictation's
    /// locale), otherwise the one current settings use.
    private func engine(_ id: EngineID, languageMode: LanguageMode) async throws -> any TranscriptionEngine {
        if let engines = dependencies.engines as? LanguageEngineProviding {
            return try await engines.engine(for: id, languageMode: languageMode)
        }
        return try await dependencies.engines.engine(for: id)
    }

    /// Waits for the dictation being transcribed; for tests.
    func waitForProcessing() async {
        await processingTask?.value
    }

    private func showMessage(_ message: FlowMessage) {
        messageTask?.cancel()
        state = .message(message)
        let lifetime = messageLifetime
        messageTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(lifetime))
            guard !Task.isCancelled, let self, self.state == .message(message) else { return }
            self.state = .idle
            self.messageTask = nil
        }
    }
}

private extension DictationState {
    var isProcessing: Bool {
        if case .processing = self { return true }
        return false
    }
}
