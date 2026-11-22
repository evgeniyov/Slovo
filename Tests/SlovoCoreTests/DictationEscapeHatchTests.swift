import Foundation
import XCTest
@testable import SlovoCore

/// A dictation that cannot finish never holds the Flow bar: Esc, the × and the menu cancel
/// it, a time limit abandons it, a lost microphone keeps what it captured, and re-running
/// an old dictation uses the language it was dictated in.
@MainActor
final class DictationEscapeHatchTests: XCTestCase {
    /// A key listener that also records what it is told about processing.
    @MainActor
    final class ProcessingHotkeys: HotkeyMonitoring, ProcessingAwareHotkeys {
        var isRunning = true
        var isKeyDown = false
        var onTrigger: ((DictationTrigger) -> Void)?
        var onKeyStateChange: ((Bool) -> Void)?
        private(set) var processing: [Bool] = []
        private(set) var syncedModes: [DictationMode?] = []

        func start(configuration: HotkeyConfiguration) -> Bool { true }
        func update(configuration: HotkeyConfiguration) {}
        func syncRecording(mode: DictationMode?) { syncedModes.append(mode) }
        func syncProcessing(_ isProcessing: Bool) { processing.append(isProcessing) }
        func stop() {}
    }

    /// Engines that tell language modes apart, with an optional gate on handing one out.
    @MainActor
    final class LanguageEngines: LanguageEngineProviding {
        let base = DictationFakes.Engines()
        var loadGate: DictationFakes.Gate?
        private(set) var languages: [LanguageMode] = []

        func engineID(for purpose: EnginePurpose) -> EngineID { base.engineID(for: purpose) }
        func loadState(of id: EngineID) -> EngineLoadState { base.loadState(of: id) }
        func engine(for id: EngineID) async throws -> any TranscriptionEngine { try await base.engine(for: id) }

        func engine(for id: EngineID, languageMode: LanguageMode) async throws -> any TranscriptionEngine {
            languages.append(languageMode)
            if let loadGate {
                await loadGate.wait()
            }
            return try await base.engine(for: id)
        }

        func preloadForCurrentSettings() {}
        func markUsed(_ id: EngineID) {}
    }

    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!
    private var directories: AppDirectories!
    private var recorder: DictationFakes.Recorder!
    private var hotkeys: ProcessingHotkeys!
    private var inserter: DictationFakes.Inserter!
    private var engines: LanguageEngines!
    private var engine: DictationFakes.Engine!
    private var history: DictationFakes.History!
    private var sounds: DictationFakes.Sounds!
    private var controller: DictationController!
    private var gates: [DictationFakes.Gate] = []

    private var timedOut: FlowMessage {
        .failure(.engineFailed(CoreStrings.string("dictation.error.timedOut", table: .dictation)))
    }

    override func setUp() async throws {
        suiteName = "slovo.tests.dictation.escape.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoDictationEscapeHatchTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        recorder = DictationFakes.Recorder()
        recorder.nextAudio = DictationFakes.speech(seconds: 0.4)
        hotkeys = ProcessingHotkeys()
        inserter = DictationFakes.Inserter()
        engines = LanguageEngines()
        engine = DictationFakes.Engine(id: EngineID.gigaamRNNT.rawValue, text: "привет мир")
        engines.base.engines[.gigaamRNNT] = engine
        engines.base.loadStates[.gigaamRNNT] = .ready
        history = DictationFakes.History(directories: directories)
        sounds = DictationFakes.Sounds()
        controller = DictationController(dependencies: .init(
            recorder: recorder, hotkeys: hotkeys, inserter: inserter, frontmostApp: DictationFakes.FrontmostApp(),
            permissions: DictationFakes.Permissions(), engines: engines, pipeline: DictationFakes.Pipeline(),
            history: history, dictionary: DictationFakes.Dictionary(), snippets: DictationFakes.Snippets(),
            notes: DictationFakes.Notes(), categories: DictationFakes.Categories(), meetings: DictationFakes.Meetings(),
            sounds: sounds, settings: settings, directories: directories, uptime: DictationFakes.Uptime()))
        controller.messageLifetime = 60
        controller.limitCheckInterval = 3_600
        controller.holdCheckDelay = 3_600
    }

    override func tearDown() async throws {
        // Late engines finish into an abandoned dictation, which must change nothing.
        gates.forEach { $0.open() }
        controller?.stop()
        controller = nil
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    private func gate() -> DictationFakes.Gate {
        let gate = DictationFakes.Gate()
        gates.append(gate)
        return gate
    }

    // MARK: - Cancelling while processing

    func testCancelAbandonsADictationBeingTranscribedAndTellsTheKeyListener() async {
        let gate = gate()
        engine.gate.value = gate
        controller.startHandsFree()
        controller.finish()
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false))
        XCTAssertEqual(hotkeys.processing, [true], "Esc must cancel while the engine works")
        let arrived = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(arrived)

        controller.cancel()
        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(hotkeys.processing, [true, false])

        gate.open()
        try? await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
        XCTAssertEqual(controller.state, .idle)
    }

    func testEscapeTriggerCancelsProcessing() async {
        engine.gate.value = gate()
        controller.startHandsFree()
        controller.finish()
        controller.handle(.cancel)
        XCTAssertEqual(controller.state, .idle)
        XCTAssertEqual(hotkeys.processing, [true, false])
    }

    func testFinishedDictationTellsTheKeyListenerProcessingEnded() async {
        controller.startHandsFree()
        controller.finish()
        await controller.waitForProcessing()
        XCTAssertEqual(inserter.inserted, ["привет мир"])
        XCTAssertEqual(hotkeys.processing, [true, false])
    }

    // MARK: - Time limits

    func testTranscriptionThatTakesTooLongIsAbandoned() async {
        let gate = gate()
        engine.gate.value = gate
        controller.transcriptionTimeLimit = 0.05
        controller.startHandsFree()
        controller.finish()

        let abandoned = await DictationFakes.waitUntil { self.controller.state == .message(self.timedOut) }
        XCTAssertTrue(abandoned)
        XCTAssertEqual(hotkeys.processing, [true, false])

        // A new dictation may start at once.
        controller.startHandsFree()
        XCTAssertTrue(controller.state.isRecording)
        controller.cancel()

        gate.open()
        try? await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(inserter.inserted.isEmpty, "the late result of an abandoned dictation is dropped")
        XCTAssertTrue(history.entries.isEmpty)
    }

    func testModelLoadThatTakesTooLongIsAbandoned() async {
        engines.base.loadStates[.gigaamRNNT] = .loading
        engines.loadGate = gate()
        controller.modelLoadTimeLimit = 0.05
        controller.startHandsFree()
        controller.finish()
        XCTAssertEqual(controller.state, .processing(isLoadingModel: true))

        let abandoned = await DictationFakes.waitUntil { self.controller.state == .message(self.timedOut) }
        XCTAssertTrue(abandoned)
    }

    func testLoadedEngineGetsTheTranscriptionLimitInsteadOfTheLoadLimit() async {
        engines.base.loadStates[.gigaamRNNT] = .loading
        engine.gate.value = gate()
        controller.modelLoadTimeLimit = 0.2
        controller.transcriptionTimeLimit = 30
        controller.startHandsFree()
        controller.finish()

        let loaded = await DictationFakes.waitUntil { self.controller.state == .processing(isLoadingModel: false) }
        XCTAssertTrue(loaded)
        try? await Task.sleep(for: .milliseconds(500))
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false),
                       "the load limit no longer applies once the engine is ready")
        controller.cancel()
    }

    // MARK: - Microphone lost mid-recording

    func testMicrophoneLostMidRecordingStillTranscribesWhatWasCaptured() async {
        controller.startHandsFree()
        // The recorder lost its device for good and keeps what it recorded before.
        recorder.isRecording = false
        recorder.nextAudio = DictationFakes.speech(seconds: 1)
        controller.checkRecordingLimit()
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false))
        await controller.waitForProcessing()

        XCTAssertEqual(inserter.inserted, ["привет мир"])
        XCTAssertEqual(recorder.stopCount, 1)
        XCTAssertEqual(sounds.played, [.start], "no stop sound: the recording ended by itself")
        XCTAssertEqual(hotkeys.syncedModes.last, .some(nil))
    }

    func testMicrophoneLostWithNothingCapturedIsAnError() {
        controller.startHandsFree()
        recorder.isRecording = false
        recorder.nextAudio = .empty
        controller.checkRecordingLimit()
        let message = CoreStrings.string("dictation.error.microphoneFailed", table: .dictation)
        XCTAssertEqual(controller.state, .message(.failure(.recordingFailed(message))))
        XCTAssertTrue(engines.base.requested.isEmpty)
    }

    // MARK: - Languages

    func testRerunKeepsAudioAvailableWhenRetentionRemovesTheOriginalDuringModelLoad() async throws {
        controller.startHandsFree()
        controller.finish()
        await controller.waitForProcessing()
        let entry = try XCTUnwrap(history.entries.first)
        let originalAudio = try XCTUnwrap(history.audioURL(for: entry))
        let loadGate = gate()
        engines.loadGate = loadGate
        engine.text.value = "повторное распознавание"
        let rerun = Task { try await controller.rerun(entryID: entry.id, with: .gigaamRNNT) }
        let waiting = await DictationFakes.waitUntil { loadGate.arrived == 1 }
        XCTAssertTrue(waiting)

        // Retention deletes audio and clears its reference but keeps the text entry.
        try FileManager.default.removeItem(at: originalAudio)
        var retained = entry
        retained.audioFileName = nil
        history.update(retained)
        loadGate.open()
        try await rerun.value

        XCTAssertEqual(engine.sampleCounts.value.last, 6_400, "the rerun must read the full snapshot")
        XCTAssertEqual(history.entries.first?.text, "повторное распознавание")
        XCTAssertNil(history.entries.first?.audioFileName, "a rerun must not resurrect deleted audio")
        XCTAssertFalse(FileManager.default.fileExists(atPath: originalAudio.path))
        let snapshot = try XCTUnwrap(engine.fileURLs.value.last)
        XCTAssertNotEqual(snapshot, originalAudio)
        XCTAssertFalse(FileManager.default.fileExists(atPath: snapshot.path), "temporary audio is removed after the rerun")
    }

    func testDictationAndRerunUseTheLanguageOfTheDictation() async throws {
        settings.languageMode = .english
        engines.base.dictationEngine = .appleDictation
        engines.base.engines[.appleDictation] = DictationFakes.Engine(id: EngineID.appleDictation.rawValue, text: "hello")
        engines.base.loadStates[.appleDictation] = .ready
        controller.startHandsFree()
        controller.finish()
        await controller.waitForProcessing()
        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertEqual(entry.languageMode, .english)
        XCTAssertNotNil(entry.audioFileName)

        settings.languageMode = .russian
        try await controller.rerun(entryID: entry.id, with: .appleDictation)
        XCTAssertEqual(engines.languages, [.english, .english])
    }

    func testEngineManagerLoadsAppleDictationForTheRequestedLanguage() async throws {
        let models = DictationFakes.Models()
        models.statuses[.appleDictation] = .systemManaged(available: true)
        var made: [LanguageMode] = []
        let manager = EngineManager(settings: settings, models: models, factory: { id, mode in
            made.append(mode)
            return DictationFakes.Engine(id: id.rawValue, text: "")
        }, uptime: DictationFakes.Uptime())
        defer { manager.unloadAll() }
        settings.languageMode = .russian

        _ = try await manager.engine(for: .appleDictation, languageMode: .english)
        _ = try await manager.engine(for: .appleDictation)
        _ = try await manager.engine(for: .appleDictation, languageMode: .mixed)
        XCTAssertEqual(made, [.english, .russian], "mixed speech uses the Russian locale already loaded")
    }

    // MARK: - Leftover temporary recordings

    func testLeftoverTemporaryRecordingsAreRemoved() throws {
        let folder = temporary.appendingPathComponent("tmp", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let now = Date()
        let old = now.addingTimeInterval(-7_200)
        func file(_ name: String, modified: Date) throws -> URL {
            let url = folder.appendingPathComponent(name)
            try Data([1, 2, 3]).write(to: url)
            try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
            return url
        }
        let leftover = try file("Slovo-\(UUID().uuidString).wav", modified: old)
        let running = try file("Slovo-\(UUID().uuidString).wav", modified: now)
        let unrelated = try file("Slovo-notes.wav", modified: old)
        let other = try file("recording.wav", modified: old)
        let meeting = folder.appendingPathComponent("Slovo-Meeting-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: meeting, withIntermediateDirectories: true)

        XCTAssertEqual(DictationController.removeLeftoverRecordings(in: folder, now: now), 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: leftover.path))
        for kept in [running, unrelated, other, meeting] {
            XCTAssertTrue(FileManager.default.fileExists(atPath: kept.path), kept.lastPathComponent)
        }
    }

    func testTemporaryRecordingNamesAreExact() {
        XCTAssertTrue(DictationController.isTemporaryRecording("Slovo-\(UUID().uuidString).wav"))
        XCTAssertFalse(DictationController.isTemporaryRecording("Slovo-\(UUID().uuidString).caf"))
        XCTAssertFalse(DictationController.isTemporaryRecording("Slovo-Meeting-\(UUID().uuidString)"))
        XCTAssertFalse(DictationController.isTemporaryRecording("Slovo-.wav"))
    }
}
