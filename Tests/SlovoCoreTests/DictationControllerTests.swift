import Foundation
import XCTest
@testable import SlovoCore

/// Test doubles for every service the dictation module talks to.
enum DictationFakes {
    /// A value shared with code that runs off the main actor.
    final class Locked<Value>: @unchecked Sendable {
        private let lock = NSLock()
        private var stored: Value

        init(_ value: Value) {
            stored = value
        }

        var value: Value {
            get { lock.withLock { stored } }
            set { lock.withLock { stored = newValue } }
        }
    }

    final class Uptime: UptimeProviding, @unchecked Sendable {
        private let now = Locked<TimeInterval>(1_000)

        func uptime() -> TimeInterval {
            now.value
        }

        func advance(_ seconds: TimeInterval) {
            now.value += seconds
        }
    }

    /// Suspends callers until it is opened.
    final class Gate: @unchecked Sendable {
        private let lock = NSLock()
        private var isOpen = false
        private var arrivals = 0
        private var waiters: [CheckedContinuation<Void, Never>] = []

        var arrived: Int {
            lock.withLock { arrivals }
        }

        func wait() async {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                let resumeNow = lock.withLock { () -> Bool in
                    arrivals += 1
                    if isOpen {
                        return true
                    }
                    waiters.append(continuation)
                    return false
                }
                if resumeNow {
                    continuation.resume()
                }
            }
        }

        func open() {
            let waiting = lock.withLock { () -> [CheckedContinuation<Void, Never>] in
                isOpen = true
                let current = waiters
                waiters = []
                return current
            }
            waiting.forEach { $0.resume() }
        }
    }

    final class Engine: TranscriptionEngine, @unchecked Sendable {
        let id: String
        let text: Locked<String>
        let error = Locked<Error?>(nil)
        let gate = Locked<Gate?>(nil)
        /// Samples in each file the engine was asked to transcribe; -1 when unreadable.
        let sampleCounts = Locked<[Int]>([])
        let fileURLs = Locked<[URL]>([])

        init(id: String, text: String) {
            self.id = id
            self.text = Locked(text)
        }

        func prepare() async throws {}

        func transcribe(fileURL: URL) async throws -> String {
            fileURLs.value.append(fileURL)
            let count = (try? WAVFile.read(from: fileURL))?.samples.count ?? -1
            sampleCounts.value.append(count)
            if let gate = gate.value {
                await gate.wait()
            }
            if let error = error.value {
                throw error
            }
            return text.value
        }
    }

    @MainActor
    final class Recorder: AudioRecording {
        var isRecording = false
        var onLevel: ((Float) -> Void)?
        var nextAudio = RecordedAudio.empty
        var startError: Error?
        private(set) var startedDeviceUIDs: [String?] = []
        private(set) var stopCount = 0
        private(set) var cancelCount = 0

        func start(deviceUID: String?) throws {
            if let startError {
                throw startError
            }
            startedDeviceUIDs.append(deviceUID)
            isRecording = true
        }

        func stop() -> RecordedAudio {
            stopCount += 1
            isRecording = false
            return nextAudio
        }

        func cancel() {
            cancelCount += 1
            isRecording = false
        }
    }

    @MainActor
    final class Hotkeys: HotkeyMonitoring {
        var isRunning = false
        var isKeyDown = false
        var onTrigger: ((DictationTrigger) -> Void)?
        var onKeyStateChange: ((Bool) -> Void)?
        var allowsStart = true
        private(set) var startedConfigurations: [HotkeyConfiguration] = []
        private(set) var updatedConfigurations: [HotkeyConfiguration] = []
        private(set) var syncedModes: [DictationMode?] = []
        private(set) var stopCount = 0

        func start(configuration: HotkeyConfiguration) -> Bool {
            startedConfigurations.append(configuration)
            isRunning = allowsStart
            return allowsStart
        }

        func update(configuration: HotkeyConfiguration) {
            updatedConfigurations.append(configuration)
        }

        func syncRecording(mode: DictationMode?) {
            syncedModes.append(mode)
        }

        func stop() {
            stopCount += 1
            isRunning = false
        }
    }

    @MainActor
    final class Inserter: TextInserting {
        var result: InsertionResult = .pasted
        var focusedTarget = TextInsertionTarget(processID: 1)
        private(set) var inserted: [String] = []
        private(set) var insertionTargets: [TextInsertionTarget?] = []

        func captureTarget() -> TextInsertionTarget { focusedTarget }

        func insert(_ text: String, into target: TextInsertionTarget?) async -> InsertionResult {
            inserted.append(text)
            insertionTargets.append(target)
            return text.isEmpty ? .empty : result
        }

        func copyToPasteboard(_ text: String) {}
    }

    @MainActor
    final class FrontmostApp: FrontmostAppProviding {
        var app: AppIdentity? = AppIdentity(bundleID: "com.tinyspeck.slackmacgap", name: "Slack")

        func currentApp() -> AppIdentity? {
            app
        }
    }

    @MainActor
    final class Permissions: PermissionsProviding {
        var statuses: [PermissionKind: PermissionStatus] = [.microphone: .granted, .accessibility: .granted]
        var isSecureInputActive = false

        func status(of kind: PermissionKind) -> PermissionStatus {
            statuses[kind] ?? .unknown
        }

        func request(_ kind: PermissionKind) async -> PermissionStatus {
            status(of: kind)
        }

        func openSystemSettings(for kind: PermissionKind) {}
    }

    @MainActor
    final class Engines: EngineProviding {
        var dictationEngine: EngineID = .gigaamRNNT
        var engines: [EngineID: Engine] = [:]
        /// Engines of other kinds, such as the English helper of mixed dictation.
        var extraEngines: [EngineID: any TranscriptionEngine] = [:]
        var unavailable: [EngineID: ModelStatus] = [:]
        var loadStates: [EngineID: EngineLoadState] = [:]
        private(set) var requested: [EngineID] = []
        private(set) var markedUsed: [EngineID] = []

        func engineID(for purpose: EnginePurpose) -> EngineID {
            purpose == .meetings ? .parakeet : dictationEngine
        }

        func loadState(of id: EngineID) -> EngineLoadState {
            loadStates[id] ?? .unloaded
        }

        func engine(for id: EngineID) async throws -> any TranscriptionEngine {
            requested.append(id)
            if let status = unavailable[id] {
                throw EngineUnavailableError(engineID: id, status: status)
            }
            guard let engine = engines[id] ?? extraEngines[id] else {
                throw SlovoError("No fake engine for \(id.rawValue)")
            }
            return engine
        }

        func preloadForCurrentSettings() {}

        func markUsed(_ id: EngineID) {
            markedUsed.append(id)
        }
    }

    final class Pipeline: TextProcessing, @unchecked Sendable {
        let contexts = Locked<[TextPipelineContext]>([])
        let transform = Locked<@Sendable (String) -> TextPipelineResult>({ TextPipelineResult(text: $0) })

        func process(_ raw: String, context: TextPipelineContext) -> TextPipelineResult {
            contexts.value.append(context)
            return transform.value(raw)
        }
    }

    @MainActor
    final class History: HistoryStoring {
        var entries: [HistoryEntry] = []
        let directories: AppDirectories

        init(directories: AppDirectories) {
            self.directories = directories
        }

        func add(_ entry: HistoryEntry) {
            entries.insert(entry, at: 0)
        }

        func update(_ entry: HistoryEntry) {
            guard let index = entries.firstIndex(where: { $0.id == entry.id }) else { return }
            entries[index] = entry
        }

        func delete(id: UUID) {
            entries.removeAll { $0.id == id }
        }

        func deleteAll() {
            entries = []
        }

        func audioURL(for entry: HistoryEntry) -> URL? {
            guard let name = entry.audioFileName else { return nil }
            let url = directories.recordingURL(relativePath: name)
            return FileManager.default.fileExists(atPath: url.path) ? url : nil
        }

        func removeAudio(for id: UUID) {}

        func flush() throws {}
    }

    @MainActor
    final class Dictionary: DictionaryStoring {
        var items: [DictionaryItem] = []

        func add(_ item: DictionaryItem) {
            items.append(item)
        }

        func update(_ item: DictionaryItem) {}

        func delete(id: UUID) {}

        func flush() throws {}
    }

    @MainActor
    final class Snippets: SnippetStoring {
        var snippets: [Snippet] = []

        func add(_ snippet: Snippet) {
            snippets.append(snippet)
        }

        func update(_ snippet: Snippet) {}

        func delete(id: UUID) {}

        func flush() throws {}
    }

    @MainActor
    final class Notes: NoteStoring {
        var notes: [Note] = []
        private(set) var appended: [(text: String, noteID: UUID)] = []

        func create(text: String) -> Note {
            let note = Note(text: text)
            notes.insert(note, at: 0)
            return note
        }

        func update(_ note: Note) {}

        func append(_ text: String, toNote id: UUID) {
            appended.append((text, id))
        }

        func delete(id: UUID) {}

        func flush() throws {}
    }

    @MainActor
    final class Categories: AppCategoryStoring {
        var assignments: [AppCategoryAssignment] = []
        var mapping: [String: AppCategory] = [:]

        func category(forBundleID bundleID: String?) -> AppCategory {
            bundleID.flatMap { mapping[$0] } ?? .other
        }

        func setCategory(_ category: AppCategory, for app: AppIdentity) {
            mapping[app.bundleID] = category
        }

        func flush() throws {}
    }

    @MainActor
    final class Meetings: MeetingActivityProviding {
        var isMeetingRecording = false
    }

    @MainActor
    final class Sounds: SoundPlaying {
        private(set) var played: [SoundCue] = []

        func play(_ cue: SoundCue) {
            played.append(cue)
        }
    }

    @MainActor
    final class Models: ModelManaging {
        var statuses: [EngineID: ModelStatus] = [:]
        /// Replaces `statuses` on the next refresh, like files appearing on disk.
        var statusesAfterRefresh: [EngineID: ModelStatus]?
        private(set) var refreshCount = 0

        func status(of id: EngineID) -> ModelStatus {
            statuses[id] ?? .unknown
        }

        func refresh() {
            refreshCount += 1
            if let statusesAfterRefresh {
                statuses = statusesAfterRefresh
            }
        }

        func startDownload(_ id: EngineID) {}

        func cancelDownload(_ id: EngineID) {}

        func delete(_ id: EngineID) throws {}
    }

    /// One second of a tone loud enough to count as speech.
    static func speech(seconds: Double, amplitude: Float = 0.2) -> RecordedAudio {
        let count = Int(seconds * 16_000)
        return RecordedAudio(samples: (0..<count).map { Float(sin(Double($0) * 2 * .pi * 220 / 16_000)) * amplitude })
    }

    /// Polls a condition on the main actor until it holds or the timeout passes.
    @MainActor
    static func waitUntil(timeout: Duration = .seconds(3), _ condition: () -> Bool) async -> Bool {
        let deadline = ContinuousClock.now + timeout
        while !condition() {
            if ContinuousClock.now > deadline {
                return false
            }
            try? await Task.sleep(for: .milliseconds(5))
        }
        return true
    }
}

@MainActor
final class DictationControllerTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!
    private var directories: AppDirectories!

    private var recorder: DictationFakes.Recorder!
    private var hotkeys: DictationFakes.Hotkeys!
    private var inserter: DictationFakes.Inserter!
    private var frontmostApp: DictationFakes.FrontmostApp!
    private var permissions: DictationFakes.Permissions!
    private var engines: DictationFakes.Engines!
    private var engine: DictationFakes.Engine!
    private var pipeline: DictationFakes.Pipeline!
    private var history: DictationFakes.History!
    private var dictionary: DictationFakes.Dictionary!
    private var snippets: DictationFakes.Snippets!
    private var notes: DictationFakes.Notes!
    private var categories: DictationFakes.Categories!
    private var meetings: DictationFakes.Meetings!
    private var sounds: DictationFakes.Sounds!
    private var uptime: DictationFakes.Uptime!
    private var controller: DictationController!

    override func setUp() async throws {
        suiteName = "slovo.tests.dictation.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoDictationControllerTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)

        recorder = DictationFakes.Recorder()
        recorder.nextAudio = DictationFakes.speech(seconds: 1)
        hotkeys = DictationFakes.Hotkeys()
        inserter = DictationFakes.Inserter()
        frontmostApp = DictationFakes.FrontmostApp()
        permissions = DictationFakes.Permissions()
        engines = DictationFakes.Engines()
        engine = DictationFakes.Engine(id: EngineID.gigaamRNNT.rawValue, text: "привет мир")
        engines.engines[.gigaamRNNT] = engine
        engines.loadStates[.gigaamRNNT] = .ready
        pipeline = DictationFakes.Pipeline()
        history = DictationFakes.History(directories: directories)
        dictionary = DictationFakes.Dictionary()
        snippets = DictationFakes.Snippets()
        notes = DictationFakes.Notes()
        categories = DictationFakes.Categories()
        meetings = DictationFakes.Meetings()
        sounds = DictationFakes.Sounds()
        uptime = DictationFakes.Uptime()
        controller = makeController()
    }

    override func tearDown() async throws {
        controller?.stop()
        controller = nil
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    private func makeController() -> DictationController {
        let controller = DictationController(dependencies: .init(
            recorder: recorder, hotkeys: hotkeys, inserter: inserter, frontmostApp: frontmostApp,
            permissions: permissions, engines: engines, pipeline: pipeline, history: history,
            dictionary: dictionary, snippets: snippets, notes: notes, categories: categories,
            meetings: meetings, sounds: sounds, settings: settings, directories: directories, uptime: uptime))
        // Messages stay until a test shortens them; the time limit and the hold are checked by hand.
        controller.messageLifetime = 60
        controller.limitCheckInterval = 3_600
        controller.holdCheckDelay = 3_600
        return controller
    }

    private func recordedFiles() -> [URL] {
        guard let items = FileManager.default.enumerator(at: directories.recordingsFolder, includingPropertiesForKeys: nil) else {
            return []
        }
        return items.compactMap { $0 as? URL }.filter { $0.pathExtension == "wav" }
    }

    /// The key is still held when the minimum hold has passed.
    private func confirmHold() {
        hotkeys.isKeyDown = true
        controller.holdCheckDue()
    }

    private func dictate(_ trigger: DictationTrigger = .begin(.pushToTalk)) async {
        controller.handle(trigger)
        confirmHold()
        hotkeys.isKeyDown = false
        controller.handle(.finish)
        await controller.waitForProcessing()
    }

    // MARK: - Recording

    func testBeginStartsPushToTalkRecording() {
        settings.microphoneUID = "USB-MIC"
        controller.handle(.begin(.pushToTalk))
        // Capture starts at once, but nothing is shown or heard until the hold is confirmed.
        XCTAssertEqual(recorder.startedDeviceUIDs, ["USB-MIC"])
        XCTAssertEqual(controller.state, .idle)
        XCTAssertTrue(sounds.played.isEmpty)

        uptime.advance(0.25)
        confirmHold()
        XCTAssertEqual(controller.state, .recording(RecordingInfo(mode: .pushToTalk, startedAt: 1_000, target: .frontmostApp)))
        XCTAssertEqual(recorder.startedDeviceUIDs, ["USB-MIC"])
        XCTAssertEqual(sounds.played, [.start])
        XCTAssertTrue(hotkeys.syncedModes.isEmpty, "Trigger-driven starts must not be echoed back to the key state machine")
    }

    func testPromoteToHandsFreeKeepsRecording() {
        controller.handle(.begin(.pushToTalk))
        controller.handle(.promoteToHandsFree)

        XCTAssertEqual(controller.state, .recording(RecordingInfo(mode: .handsFree, startedAt: 1_000, target: .frontmostApp)))
        XCTAssertEqual(recorder.startedDeviceUIDs.count, 1)
    }

    func testMeetingBlocksDictation() {
        meetings.isMeetingRecording = true
        controller.handle(.begin(.pushToTalk))
        XCTAssertEqual(controller.state, .idle, "A press that may still be a tap shows nothing yet")
        confirmHold()

        XCTAssertEqual(controller.state, .message(.blockedByMeeting))
        XCTAssertTrue(recorder.startedDeviceUIDs.isEmpty)
        XCTAssertEqual(hotkeys.syncedModes, [nil])

        controller.dismissMessage()
        controller.startHandsFree()
        XCTAssertEqual(controller.state, .message(.blockedByMeeting))
        XCTAssertEqual(hotkeys.syncedModes, [nil], "A refused Flow bar start was never announced to the key state machine")
    }

    func testDeniedMicrophoneShowsFailure() {
        permissions.statuses[.microphone] = .denied
        controller.handle(.begin(.pushToTalk))
        confirmHold()

        XCTAssertEqual(controller.state, .message(.failure(.microphoneDenied)))
        XCTAssertTrue(recorder.startedDeviceUIDs.isEmpty)
        XCTAssertEqual(hotkeys.syncedModes, [nil])
    }

    func testRecorderFailureShowsFailure() {
        recorder.startError = SlovoError("no input")
        controller.handle(.begin(.pushToTalk))
        confirmHold()

        XCTAssertEqual(controller.state, .message(.failure(.recordingFailed("no input"))))
        XCTAssertEqual(hotkeys.syncedModes, [nil])
        XCTAssertTrue(sounds.played.isEmpty)
    }

    func testEscapeCancelsWithoutTranscribing() {
        controller.handle(.begin(.pushToTalk))
        confirmHold()
        controller.handle(.cancel)

        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(recorder.cancelCount, 1)
        XCTAssertEqual(recorder.stopCount, 0)
        XCTAssertTrue(engines.requested.isEmpty)
        XCTAssertEqual(sounds.played, [.start, .cancel])
        XCTAssertTrue(hotkeys.syncedModes.isEmpty)
    }

    func testFlowBarStartAndCancelSyncTheKeyStateMachine() {
        controller.startHandsFree()
        XCTAssertEqual(controller.state, .recording(RecordingInfo(mode: .handsFree, startedAt: 1_000, target: .frontmostApp)))
        XCTAssertEqual(hotkeys.syncedModes, [.handsFree])

        controller.cancel()
        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(hotkeys.syncedModes, [.handsFree, nil])
    }

    func testDiscardedTapIsSilent() {
        controller.handle(.begin(.pushToTalk))
        controller.handle(.discard)

        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(recorder.cancelCount, 1)
        XCTAssertTrue(sounds.played.isEmpty)
        XCTAssertTrue(engines.requested.isEmpty)
    }

    func testLevelIsPublishedOnlyWhileRecording() {
        controller.start()
        recorder.onLevel?(0.4)
        XCTAssertEqual(controller.level, 0)

        controller.handle(.begin(.pushToTalk))
        recorder.onLevel?(0.4)
        XCTAssertEqual(controller.level, 0, "A hold that is not confirmed yet is not shown")
        confirmHold()
        recorder.onLevel?(0.4)
        XCTAssertEqual(controller.level, 0.4)

        controller.handle(.cancel)
        XCTAssertEqual(controller.level, 0)
    }

    func testHandsFreeCountdownAndTimeLimit() async {
        settings.maxHandsFreeSeconds = 60
        controller.startHandsFree()

        uptime.advance(20)
        controller.checkRecordingLimit()
        XCTAssertNil(controller.handsFreeRemaining)

        uptime.advance(15)
        controller.checkRecordingLimit()
        XCTAssertEqual(controller.handsFreeRemaining, 25)

        uptime.advance(24.5)
        controller.checkRecordingLimit()
        XCTAssertEqual(controller.handsFreeRemaining, 1)

        uptime.advance(1)
        controller.checkRecordingLimit()
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false))
        XCTAssertNil(controller.handsFreeRemaining)
        XCTAssertEqual(hotkeys.syncedModes, [.handsFree, nil])

        await controller.waitForProcessing()
        XCTAssertEqual(history.entries.count, 1)
        XCTAssertEqual(inserter.inserted, ["привет мир"])
    }

    func testPushToTalkHasNoCountdown() {
        settings.maxHandsFreeSeconds = 60
        controller.handle(.begin(.pushToTalk))
        confirmHold()
        uptime.advance(50)
        controller.checkRecordingLimit()
        XCTAssertNil(controller.handsFreeRemaining)
        XCTAssertTrue(controller.state.isRecording)
    }

    // MARK: - Transcription and insertion

    func testFinishTranscribesInsertsAndSavesHistory() async throws {
        categories.mapping["com.tinyspeck.slackmacgap"] = .work
        settings.setStyle(.veryCasual, for: .work)
        dictionary.items = [.term("Kubernetes"), .replacement(from: "гитхаб", to: "GitHub")]
        snippets.snippets = [Snippet(trigger: "мой адрес", expansion: "Москва")]
        pipeline.transform.value = { raw in
            TextPipelineResult(text: raw + ".", replacementsApplied: 1, termFixesApplied: 2)
        }

        controller.handle(.begin(.pushToTalk))
        confirmHold()
        controller.handle(.finish)
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false))
        await controller.waitForProcessing()

        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(inserter.inserted, ["привет мир."])
        XCTAssertEqual(controller.lastTranscript, "привет мир.")
        XCTAssertEqual(sounds.played, [.start, .stop])
        XCTAssertEqual(engines.markedUsed, [.gigaamRNNT])
        XCTAssertEqual(engine.sampleCounts.value, [16_000], "The engine reads the recording from a real WAV file")

        let context = try XCTUnwrap(pipeline.contexts.value.last)
        XCTAssertEqual(context.style, .veryCasual)
        XCTAssertEqual(context.dictionary, dictionary.items)
        XCTAssertEqual(context.snippets, snippets.snippets)
        XCTAssertEqual(context.cleanup, settings.cleanup)
        XCTAssertEqual(context.languageMode, .mixed)

        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertEqual(history.entries.count, 1)
        XCTAssertEqual(entry.text, "привет мир.")
        XCTAssertEqual(entry.rawText, "привет мир")
        XCTAssertEqual(entry.engineID, .gigaamRNNT)
        XCTAssertEqual(entry.languageMode, .mixed)
        XCTAssertEqual(entry.appBundleID, "com.tinyspeck.slackmacgap")
        XCTAssertEqual(entry.appName, "Slack")
        XCTAssertEqual(entry.category, .work)
        XCTAssertEqual(entry.duration, 1, accuracy: 0.001)
        XCTAssertEqual(entry.wordCount, 2)
        XCTAssertEqual(entry.fixesCount, 3)
        XCTAssertEqual(entry.audioFileName, WAVFile.relativePath(for: entry.id, date: entry.date))

        let audioURL = try XCTUnwrap(history.audioURL(for: entry))
        XCTAssertEqual(try WAVFile.read(from: audioURL).samples.count, 16_000)
        XCTAssertEqual(recordedFiles().count, 1)
    }

    func testEnglishModeUsesItsEngineAndMode() async throws {
        settings.languageMode = .english
        engines.dictationEngine = .parakeet
        engines.engines[.parakeet] = DictationFakes.Engine(id: EngineID.parakeet.rawValue, text: "hello")
        await dictate()

        XCTAssertEqual(engines.requested, [.parakeet])
        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertEqual(entry.engineID, .parakeet)
        XCTAssertEqual(entry.languageMode, .english)
    }

    func testTooShortRecordingIsNothingHeard() async {
        recorder.nextAudio = DictationFakes.speech(seconds: 0.25)
        await dictate()

        XCTAssertEqual(controller.state, .message(.nothingHeard))
        XCTAssertTrue(engines.requested.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertTrue(recordedFiles().isEmpty)
    }

    func testSilentRecordingIsNothingHeard() async {
        recorder.nextAudio = RecordedAudio(samples: (0..<16_000).map { $0 % 2 == 0 ? 0.001 : -0.001 })
        await dictate()

        XCTAssertEqual(controller.state, .message(.nothingHeard))
        XCTAssertTrue(engines.requested.isEmpty)
    }

    func testEmptyTranscriptIsNothingHeardAndDropsAudio() async {
        engine.text.value = "  "
        await dictate()

        XCTAssertEqual(controller.state, .message(.nothingHeard))
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertTrue(recordedFiles().isEmpty)
    }

    func testCopyFallbackShowsCopiedAndKeepsHistory() async {
        inserter.result = .copied(.secureInput)
        await dictate()

        XCTAssertEqual(controller.state, .message(.copied(.secureInput)))
        XCTAssertEqual(history.entries.count, 1)
        XCTAssertEqual(controller.lastTranscript, "привет мир")
    }

    func testDictationKeepsTheTargetCapturedAtTheStart() async {
        let original = inserter.focusedTarget
        controller.handle(.begin(.pushToTalk))
        confirmHold()
        inserter.focusedTarget = TextInsertionTarget(processID: 2)
        inserter.result = .copied(.focusChanged)
        hotkeys.isKeyDown = false
        controller.handle(.finish)
        await controller.waitForProcessing()

        XCTAssertEqual(inserter.insertionTargets, [original])
        XCTAssertEqual(controller.state, .message(.copied(.focusChanged)))
        XCTAssertEqual(history.entries.count, 1)
    }

    func testMissingModelShowsModelFailure() async {
        engines.unavailable[.gigaamRNNT] = .notDownloaded(downloadBytes: 1)
        await dictate()

        XCTAssertEqual(controller.state, .message(.failure(.modelMissing(.gigaamRNNT))))
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertTrue(recordedFiles().isEmpty)
    }

    func testEngineErrorShowsEngineFailure() async {
        engine.error.value = SlovoError("decoder failed")
        await dictate()

        XCTAssertEqual(controller.state, .message(.failure(.engineFailed("decoder failed"))))
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertTrue(recordedFiles().isEmpty)
    }

    func testLoadingModelIsShownUntilTheEngineIsReady() async {
        engines.loadStates[.gigaamRNNT] = .unloaded
        let gate = DictationFakes.Gate()
        engine.gate.value = gate

        controller.handle(.begin(.pushToTalk))
        controller.handle(.finish)
        XCTAssertEqual(controller.state, .processing(isLoadingModel: true))

        let reachedEngine = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(reachedEngine)
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false))

        gate.open()
        await controller.waitForProcessing()
        XCTAssertEqual(controller.state, .idle)
    }

    func testCancelWhileTranscribingInsertsNothing() async {
        let gate = DictationFakes.Gate()
        engine.gate.value = gate
        controller.handle(.begin(.pushToTalk))
        controller.handle(.finish)
        let reachedEngine = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(reachedEngine)

        controller.cancel()
        XCTAssertEqual(controller.state, .idle)
        gate.open()
        await controller.waitForProcessing()
        // The abandoned task has nothing left to wait for; give it a moment to finish.
        try? await Task.sleep(for: .milliseconds(50))

        XCTAssertEqual(controller.state, .idle)
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertTrue(recordedFiles().isEmpty)
    }

    func testKeyPressWhileTranscribingIsRefused() async {
        let gate = DictationFakes.Gate()
        engine.gate.value = gate
        controller.handle(.begin(.pushToTalk))
        controller.handle(.finish)
        let reachedEngine = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(reachedEngine)

        controller.handle(.begin(.pushToTalk))
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false))
        XCTAssertEqual(recorder.startedDeviceUIDs.count, 1)
        XCTAssertEqual(hotkeys.syncedModes, [nil])

        gate.open()
        await controller.waitForProcessing()
        XCTAssertEqual(history.entries.count, 1)
    }

    func testMessagesExpire() async {
        controller.messageLifetime = 0.05
        recorder.nextAudio = .empty
        await dictate()
        XCTAssertEqual(controller.state, .message(.nothingHeard))

        let expired = await DictationFakes.waitUntil { self.controller.state == .idle }
        XCTAssertTrue(expired)
    }

    func testDismissMessage() async {
        recorder.nextAudio = .empty
        await dictate()
        controller.dismissMessage()
        XCTAssertEqual(controller.state, .idle)
    }

    func testNewDictationReplacesMessage() async {
        recorder.nextAudio = .empty
        await dictate()
        XCTAssertEqual(controller.state, .message(.nothingHeard))

        controller.handle(.begin(.pushToTalk))
        XCTAssertEqual(controller.state, .message(.nothingHeard), "A press that may still be a tap leaves the message alone")
        confirmHold()
        XCTAssertTrue(controller.state.isRecording)
    }

    // MARK: - Targets and retention

    func testNoteTargetAppendsToNote() async throws {
        let noteID = UUID()
        controller.startHandsFree(target: .note(noteID))
        controller.finish()
        await controller.waitForProcessing()

        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(notes.appended.map(\.text), ["привет мир"])
        XCTAssertEqual(notes.appended.map(\.noteID), [noteID])
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertEqual(pipeline.contexts.value.last?.style, settings.style(for: .other))
        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertNil(entry.appBundleID)
        XCTAssertEqual(entry.category, .other)
        XCTAssertEqual(hotkeys.syncedModes, [.handsFree, nil])
    }

    func testPracticeTargetFillsPracticeResultOnly() async {
        controller.keyTarget = .practice
        await dictate()

        XCTAssertEqual(controller.practiceResult, "привет мир")
        XCTAssertEqual(controller.state, .idle)
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertTrue(recordedFiles().isEmpty)
    }

    func testAudioIsNotKeptWhenRetentionIsNever() async throws {
        settings.keepDictationAudio = .never
        await dictate()

        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertNil(entry.audioFileName)
        XCTAssertTrue(recordedFiles().isEmpty)
        XCTAssertEqual(engine.sampleCounts.value, [16_000])
        let temporaryFile = FileManager.default.temporaryDirectory.appendingPathComponent("Slovo-\(entry.id.uuidString).wav")
        XCTAssertFalse(FileManager.default.fileExists(atPath: temporaryFile.path))
    }

    // MARK: - Paste last and re-run

    func testPasteLastTranscriptUsesHistoryAfterRelaunch() async {
        history.entries = [HistoryEntry(date: Date(), text: "из истории", rawText: "из истории", engineID: .gigaamRNNT,
                                        languageMode: .mixed, appBundleID: nil, appName: nil, category: .other,
                                        duration: 1, wordCount: 2, fixesCount: 0, audioFileName: nil)]
        controller = makeController()
        XCTAssertEqual(controller.lastTranscript, "из истории")

        await controller.pasteLastTranscript()
        XCTAssertEqual(inserter.inserted, ["из истории"])
        XCTAssertEqual(inserter.insertionTargets, [nil])
        XCTAssertEqual(controller.state, .idle)

        inserter.result = .copied(.accessibilityMissing)
        await controller.pasteLastTranscript()
        XCTAssertEqual(controller.state, .message(.copied(.accessibilityMissing)))
    }

    func testPasteLastTranscriptWithoutTextDoesNothing() async {
        await controller.pasteLastTranscript()
        XCTAssertTrue(inserter.inserted.isEmpty)
    }

    func testRerunReplacesEntryText() async throws {
        let id = UUID()
        let date = Date()
        let relativePath = WAVFile.relativePath(for: id, date: date)
        try WAVFile.write(DictationFakes.speech(seconds: 1), to: directories.recordingURL(relativePath: relativePath))
        history.entries = [HistoryEntry(id: id, date: date, text: "кибернетес", rawText: "кибернетес",
                                        engineID: .gigaamRNNT, languageMode: .mixed, appBundleID: "com.apple.mail",
                                        appName: "Mail", category: .email, duration: 1, wordCount: 1, fixesCount: 0,
                                        audioFileName: relativePath)]
        controller = makeController()
        settings.setStyle(.casual, for: .email)
        engines.engines[.parakeet] = DictationFakes.Engine(id: EngineID.parakeet.rawValue, text: "Kubernetes is up")
        pipeline.transform.value = { TextPipelineResult(text: $0, termFixesApplied: 1) }

        try await controller.rerun(entryID: id, with: .parakeet)

        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertEqual(entry.text, "Kubernetes is up")
        XCTAssertEqual(entry.rawText, "Kubernetes is up")
        XCTAssertEqual(entry.engineID, .parakeet)
        XCTAssertEqual(entry.wordCount, 3)
        XCTAssertEqual(entry.fixesCount, 1)
        XCTAssertEqual(entry.audioFileName, relativePath)
        XCTAssertEqual(pipeline.contexts.value.last?.style, .casual)
        XCTAssertEqual(engines.markedUsed, [.parakeet])
        XCTAssertEqual(controller.lastTranscript, "Kubernetes is up")
    }

    func testRerunWithoutAudioThrows() async {
        let id = UUID()
        history.entries = [HistoryEntry(id: id, date: Date(), text: "текст", rawText: "текст", engineID: .gigaamRNNT,
                                        languageMode: .mixed, appBundleID: nil, appName: nil, category: .other,
                                        duration: 1, wordCount: 1, fixesCount: 0, audioFileName: nil)]
        do {
            try await controller.rerun(entryID: id, with: .parakeet)
            XCTFail("Re-running an entry without audio must fail")
        } catch {
            XCTAssertEqual(error.localizedDescription,
                           CoreStrings.string("dictation.error.audioMissing", table: .dictation))
        }
        XCTAssertTrue(engines.requested.isEmpty)
    }

    func testRerunOfUnknownEntryThrows() async {
        do {
            try await controller.rerun(entryID: UUID(), with: .parakeet)
            XCTFail("Re-running a missing entry must fail")
        } catch {
            XCTAssertEqual(error.localizedDescription,
                           CoreStrings.string("dictation.error.entryMissing", table: .dictation))
        }
    }

    // MARK: - Lifecycle

    func testStartWiresHotkeysAndStopReleasesThem() {
        controller.start()
        XCTAssertEqual(hotkeys.startedConfigurations, [settings.hotkeyConfiguration])

        hotkeys.onTrigger?(.begin(.pushToTalk))
        confirmHold()
        XCTAssertTrue(controller.state.isRecording)

        controller.stop()
        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(recorder.cancelCount, 1)
        XCTAssertEqual(hotkeys.stopCount, 1)
        XCTAssertNil(hotkeys.onTrigger)
        XCTAssertNil(recorder.onLevel)
    }

    func testKeySettingsReachTheRunningMonitor() {
        controller.start()
        settings.dictationKey = .fn
        settings.handsFreeEnabled = false
        // UserDefaults may already have delivered its change notifications; this covers the case it has not.
        controller.settingsDidChange()

        XCTAssertEqual(hotkeys.updatedConfigurations.last?.key, .fn)
        XCTAssertEqual(hotkeys.updatedConfigurations.last?.handsFreeEnabled, false)

        let pushed = hotkeys.updatedConfigurations.count
        XCTAssertGreaterThan(pushed, 0)
        controller.settingsDidChange()
        settings.soundsEnabled = false
        XCTAssertEqual(hotkeys.updatedConfigurations.count, pushed, "An unchanged configuration is not pushed again")
    }

    func testRefreshHotkeysRetriesAMonitorThatCouldNotStart() {
        hotkeys.allowsStart = false
        controller.start()
        XCTAssertFalse(hotkeys.isRunning)

        hotkeys.allowsStart = true
        controller.refreshHotkeys()
        XCTAssertTrue(hotkeys.isRunning)
        XCTAssertEqual(hotkeys.startedConfigurations.count, 2)
    }
}
