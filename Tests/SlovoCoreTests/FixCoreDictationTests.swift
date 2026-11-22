import Foundation
import XCTest
@testable import SlovoCore

/// Taps of the dictation key, shortcuts made with it, a microphone that fails to open,
/// results that are only a filler, and where a recording lives while it is transcribed.
@MainActor
final class FixCoreDictationTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!
    private var directories: AppDirectories!

    override func setUp() async throws {
        suiteName = "slovo.tests.fixcore.dictation.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoFixCoreDictation-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    private struct Harness {
        let controller: DictationController
        let recorder: DictationFakes.Recorder
        let hotkeys: DictationFakes.Hotkeys
        let inserter: DictationFakes.Inserter
        let engine: DictationFakes.Engine
        let engines: DictationFakes.Engines
        let history: HistoryStore
        let sounds: DictationFakes.Sounds
        let meetings: DictationFakes.Meetings
    }

    /// The real text pipeline and stores; fakes for audio, keys, engines and insertion.
    private func makeHarness(transcript: String = "да я перезвоню", pipeline: TextProcessing = TextPipeline()) -> Harness {
        let recorder = DictationFakes.Recorder()
        recorder.nextAudio = DictationFakes.speech(seconds: 1)
        let hotkeys = DictationFakes.Hotkeys()
        let inserter = DictationFakes.Inserter()
        let engine = DictationFakes.Engine(id: EngineID.gigaamRNNT.rawValue, text: transcript)
        let engines = DictationFakes.Engines()
        engines.engines[.gigaamRNNT] = engine
        engines.loadStates[.gigaamRNNT] = .ready
        let history = HistoryStore(directories: directories)
        let sounds = DictationFakes.Sounds()
        let meetings = DictationFakes.Meetings()
        let controller = DictationController(dependencies: .init(
            recorder: recorder, hotkeys: hotkeys, inserter: inserter,
            frontmostApp: DictationFakes.FrontmostApp(), permissions: DictationFakes.Permissions(), engines: engines,
            pipeline: pipeline, history: history,
            dictionary: DictionaryStore(directories: directories), snippets: SnippetStore(directories: directories),
            notes: NoteStore(directories: directories),
            categories: AppCategoryStore(directories: directories, installedNameLookup: { _ in nil }),
            meetings: meetings, sounds: sounds, settings: settings, directories: directories,
            uptime: DictationFakes.Uptime()))
        // Hold checks run by hand, in the order real time would run them.
        controller.holdCheckDelay = 3_600
        controller.messageLifetime = 60
        controller.limitCheckInterval = 3_600
        return Harness(controller: controller, recorder: recorder, hotkeys: hotkeys, inserter: inserter, engine: engine,
                       engines: engines, history: history, sounds: sounds, meetings: meetings)
    }

    /// Feeds key events through a real state machine into the controller, the way
    /// `HotkeyMonitor` does, and records the state after each step.
    private struct KeyFeed {
        var machine: HotkeyStateMachine
        let harness: Harness
        private(set) var states: [DictationState] = []

        init(harness: Harness, configuration: HotkeyConfiguration) {
            self.harness = harness
            machine = HotkeyStateMachine(configuration: configuration)
        }

        @MainActor
        mutating func send(_ input: HotkeyInput) {
            let triggers = machine.handle(input)
            harness.hotkeys.isKeyDown = machine.isKeyDown
            for trigger in triggers {
                harness.controller.handle(trigger)
            }
            states.append(harness.controller.state)
        }

        @MainActor
        mutating func holdCheck() {
            harness.controller.holdCheckDue()
            states.append(harness.controller.state)
        }
    }

    private static let fnDown: UInt64 = 0x100 | KeyFlagMask.function
    private static let fnUp: UInt64 = 0x100

    private func fn(down: Bool, at time: TimeInterval) -> HotkeyInput {
        .key(RawKeyEvent(kind: .flagsChanged, keyCode: 63, flags: down ? Self.fnDown : Self.fnUp, timestamp: time))
    }

    private func recordedFiles() -> [String] {
        ((try? FileManager.default.subpathsOfDirectory(atPath: directories.recordingsFolder.path)) ?? [])
            .filter { $0.hasSuffix(".wav") }
    }

    // MARK: - Taps

    /// A plain Fn tap, which switches the input source when AppleFnUsageType is 1.
    func testFnTapPlaysNoSoundAndShowsNothing() {
        let harness = makeHarness()
        var keys = KeyFeed(harness: harness, configuration: settings.hotkeyConfiguration)
        keys.send(fn(down: true, at: 0))
        keys.send(fn(down: false, at: 0.1))
        keys.holdCheck()
        keys.send(.deadline(0.5))
        keys.holdCheck()

        XCTAssertEqual(keys.states, [.idle, .idle, .idle, .idle, .idle])
        XCTAssertEqual(harness.sounds.played, [], "a Fn tap played a sound")
        // Capture starts at key down so the first syllable of a real dictation is kept.
        XCTAssertEqual(harness.recorder.startedDeviceUIDs.count, 1)
        XCTAssertEqual(harness.recorder.cancelCount, 1)
        XCTAssertEqual(harness.recorder.stopCount, 0)
        XCTAssertFalse(harness.recorder.isRecording, "the microphone is off again")
        XCTAssertTrue(harness.engines.requested.isEmpty)
    }

    func testHoldPastTheMinimumShowsTheRecordingAndDictates() async throws {
        let harness = makeHarness()
        var keys = KeyFeed(harness: harness, configuration: settings.hotkeyConfiguration)
        keys.send(fn(down: true, at: 0))
        XCTAssertEqual(harness.controller.state, .idle)
        keys.holdCheck()
        XCTAssertTrue(harness.controller.state.isRecording)
        XCTAssertEqual(harness.sounds.played, [.start])
        keys.send(fn(down: false, at: 1.2))
        await harness.controller.waitForProcessing()

        XCTAssertEqual(harness.sounds.played, [.start, .stop])
        XCTAssertEqual(harness.inserter.inserted.count, 1)
        XCTAssertEqual(harness.history.entries.map(\.text), harness.inserter.inserted)
    }

    func testDoubleTapShowsNothingUntilItIsPromoted() {
        let harness = makeHarness()
        var keys = KeyFeed(harness: harness, configuration: settings.hotkeyConfiguration)
        keys.send(fn(down: true, at: 0))
        keys.send(fn(down: false, at: 0.08))
        keys.holdCheck()
        XCTAssertEqual(keys.states, [.idle, .idle, .idle])
        XCTAssertTrue(harness.sounds.played.isEmpty)

        keys.send(fn(down: true, at: 0.3))
        guard case let .recording(info) = harness.controller.state else {
            return XCTFail("a double tap starts hands-free recording, got \(harness.controller.state)")
        }
        XCTAssertEqual(info.mode, .handsFree)
        XCTAssertEqual(harness.sounds.played, [.start])
        XCTAssertEqual(harness.recorder.startedDeviceUIDs.count, 1, "the capture of the first tap goes on")
    }

    func testTapNobodyResolvesIsStoppedAtTheSecondCheck() {
        let harness = makeHarness()
        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.holdCheckDue()
        XCTAssertEqual(harness.recorder.cancelCount, 0, "the double-tap window is still open")
        harness.controller.holdCheckDue()

        XCTAssertEqual(harness.recorder.cancelCount, 1)
        XCTAssertEqual(harness.controller.state, .idle)
        XCTAssertTrue(harness.sounds.played.isEmpty)
        XCTAssertEqual(harness.hotkeys.syncedModes, [nil], "the key state machine stops waiting for the tap")
    }

    func testTapLeavesAMessageOnScreen() async {
        let harness = makeHarness()
        harness.recorder.nextAudio = .empty
        harness.controller.startHandsFree()
        harness.controller.finish()
        XCTAssertEqual(harness.controller.state, .message(.nothingHeard))

        harness.controller.handle(.begin(.pushToTalk))
        XCTAssertEqual(harness.controller.state, .message(.nothingHeard))
        harness.controller.handle(.discard)
        XCTAssertEqual(harness.controller.state, .message(.nothingHeard))

        harness.controller.handle(.begin(.pushToTalk))
        harness.hotkeys.isKeyDown = true
        harness.controller.holdCheckDue()
        XCTAssertTrue(harness.controller.state.isRecording)
    }

    func testRefusedTapShowsNothingButARefusedHoldExplainsWhy() {
        let harness = makeHarness()
        harness.meetings.isMeetingRecording = true
        var keys = KeyFeed(harness: harness, configuration: settings.hotkeyConfiguration)
        keys.send(fn(down: true, at: 0))
        keys.send(fn(down: false, at: 0.1))
        keys.holdCheck()
        XCTAssertEqual(keys.states, [.idle, .idle, .idle])
        XCTAssertTrue(harness.recorder.startedDeviceUIDs.isEmpty)
        XCTAssertEqual(harness.hotkeys.syncedModes, [nil])

        keys.send(fn(down: true, at: 1))
        keys.holdCheck()
        XCTAssertEqual(harness.controller.state, .message(.blockedByMeeting))
        keys.send(fn(down: false, at: 2))
        XCTAssertEqual(harness.controller.state, .message(.blockedByMeeting))
    }

    // MARK: - Shortcuts with the dictation key

    /// Fn+Down (Page Down), with Fn held a moment before the arrow.
    func testFnNavigationChordIsNotADictation() async throws {
        for confirmsHoldFirst in [false, true] {
            let harness = makeHarness()
            var keys = KeyFeed(harness: harness, configuration: settings.hotkeyConfiguration)
            XCTAssertEqual(keys.machine.configuration.key, .fn)
            keys.send(fn(down: true, at: 0))
            if confirmsHoldFirst {
                // Real time reaches the minimum hold before the arrow at 0.4 s.
                keys.holdCheck()
            }
            let arrow = RawKeyEvent(kind: .keyDown, keyCode: 125, flags: Self.fnDown | 0x20_0000, timestamp: 0.4)
            keys.send(.key(arrow))
            keys.send(.key(RawKeyEvent(kind: .keyDown, keyCode: 125, flags: Self.fnDown | 0x20_0000, timestamp: 0.6)))
            keys.send(fn(down: false, at: 0.9))
            await harness.controller.waitForProcessing()

            XCTAssertEqual(harness.inserter.inserted, [], "Fn+arrow pasted a dictation into the focused app")
            XCTAssertTrue(harness.history.entries.isEmpty)
            XCTAssertTrue(harness.engines.requested.isEmpty)
            XCTAssertEqual(harness.recorder.stopCount, 0)
            XCTAssertEqual(harness.recorder.cancelCount, 1)
            XCTAssertEqual(harness.controller.state, .idle)
            XCTAssertEqual(harness.sounds.played, confirmsHoldFirst ? [.start] : [],
                           "no stop sound: nothing was dictated")
        }
    }

    // MARK: - Microphone failures

    func testMicrophoneThatFailsToOpenIsReportedWhenTheHoldIsConfirmed() {
        let harness = makeHarness()
        harness.controller.handle(.begin(.pushToTalk))
        // The recorder opens the device in the background and gave up, with nothing captured.
        harness.recorder.isRecording = false
        harness.recorder.nextAudio = .empty
        harness.hotkeys.isKeyDown = true
        harness.controller.holdCheckDue()

        let message = CoreStrings.string("dictation.error.microphoneFailed", table: .dictation)
        XCTAssertEqual(harness.controller.state, .message(.failure(.recordingFailed(message))))
        XCTAssertTrue(harness.sounds.played.isEmpty)
        XCTAssertEqual(harness.hotkeys.syncedModes, [nil])
        // The recorder is asked for what it kept, which after a failed open is nothing.
        XCTAssertEqual(harness.recorder.stopCount, 1)
        XCTAssertEqual(harness.recorder.cancelCount, 0)
    }

    func testMicrophoneThatFailsWhileShownIsReportedInsteadOfNothingHeard() async {
        let message = CoreStrings.string("dictation.error.microphoneFailed", table: .dictation)

        let shown = makeHarness()
        shown.controller.startHandsFree()
        shown.recorder.isRecording = false
        shown.recorder.nextAudio = .empty
        shown.controller.checkRecordingLimit()
        XCTAssertEqual(shown.controller.state, .message(.failure(.recordingFailed(message))))

        let finished = makeHarness()
        finished.controller.startHandsFree()
        finished.recorder.isRecording = false
        finished.recorder.nextAudio = .empty
        finished.controller.finish()
        await finished.controller.waitForProcessing()
        XCTAssertEqual(finished.controller.state, .message(.failure(.recordingFailed(message))))
        XCTAssertTrue(finished.engines.requested.isEmpty)
        XCTAssertEqual(finished.sounds.played, [.start], "no stop sound for a recording that captured nothing")
    }

    func testMicrophoneFailureMessageIsTranslated() {
        let english = CoreStrings.string("dictation.error.microphoneFailed", table: .dictation, language: .en)
        let russian = CoreStrings.string("dictation.error.microphoneFailed", table: .dictation, language: .ru)
        XCTAssertNotEqual(english, "dictation.error.microphoneFailed")
        XCTAssertNotEqual(russian, english)
        XCTAssertLessThanOrEqual(russian.count, 48, "the Flow bar shows longer messages as a generic line")
    }

    // MARK: - Filler-only results

    func testFillerOnlyDictationMustNotInsertPunctuation() async throws {
        for raw in ["Эм.", "Um.", "Hmm?", "Э...", "э, ээ, эм."] {
            let harness = makeHarness(transcript: raw)
            harness.controller.handle(.begin(.pushToTalk))
            harness.controller.handle(.finish)
            await harness.controller.waitForProcessing()
            XCTAssertEqual(harness.inserter.inserted, [], "raw \(raw)")
            XCTAssertEqual(harness.history.entries.map(\.text), [], "raw \(raw)")
            XCTAssertEqual(harness.controller.state, .message(.nothingHeard), "raw \(raw)")
            XCTAssertEqual(recordedFiles(), [], "raw \(raw)")
        }
    }

    func testRerunThatOnlyHearsAFillerKeepsTheEntry() async throws {
        let harness = makeHarness(transcript: "созвонимся завтра")
        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.handle(.finish)
        await harness.controller.waitForProcessing()
        let entry = try XCTUnwrap(harness.history.entries.first)

        harness.engines.engines[.parakeet] = DictationFakes.Engine(id: EngineID.parakeet.rawValue, text: "Hmm?")
        do {
            try await harness.controller.rerun(entryID: entry.id, with: .parakeet)
            XCTFail("a filler is not a new text")
        } catch {
            XCTAssertEqual(error.localizedDescription, CoreStrings.string("dictation.error.rerunEmpty", table: .dictation))
        }
        XCTAssertEqual(harness.history.entries.first?.text, entry.text)
    }

    func testContentIsLettersDigitsOrSymbols() {
        XCTAssertFalse(DictationController.hasContent(""))
        XCTAssertFalse(DictationController.hasContent(". ? ... … — «»"))
        XCTAssertTrue(DictationController.hasContent("да"))
        XCTAssertTrue(DictationController.hasContent("42"))
        XCTAssertTrue(DictationController.hasContent("👍"), "a snippet may expand to an emoji")
        XCTAssertTrue(DictationController.hasContent("€"))
    }

    // MARK: - Recordings in progress

    /// A recording stays out of Recordings/ until its entry exists, so retention can
    /// never delete the audio of a dictation that is still being transcribed.
    func testRecordingEntersRecordingsOnlyWithItsEntry() async throws {
        settings.keepDictationAudio = .hours24
        let harness = makeHarness(transcript: "проверка связи")
        let gate = DictationFakes.Gate()
        harness.engine.gate.value = gate
        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.handle(.finish)
        let transcribing = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(transcribing)
        XCTAssertEqual(harness.engine.sampleCounts.value, [16_000], "the engine read the recording")
        XCTAssertEqual(recordedFiles(), [])

        // A retention pass far in the future finds nothing to remove meanwhile.
        settings.keepDictationAudio = .never
        let cleaner = RetentionCleaner(history: harness.history, meetings: MeetingStore(directories: directories),
                                       settings: settings)
        XCTAssertEqual(cleaner.run(now: Date().addingTimeInterval(3 * 24 * 3600)).removedDictationAudio, 0)
        settings.keepDictationAudio = .hours24

        gate.open()
        await harness.controller.waitForProcessing()
        let entry = try XCTUnwrap(harness.history.entries.first)
        let relativePath = try XCTUnwrap(entry.audioFileName)
        XCTAssertEqual(recordedFiles(), [relativePath])
        XCTAssertNotNil(harness.history.audioURL(for: entry))
        let temporaryFile = FileManager.default.temporaryDirectory.appendingPathComponent("Slovo-\(entry.id.uuidString).wav")
        XCTAssertFalse(FileManager.default.fileExists(atPath: temporaryFile.path))
    }
}
