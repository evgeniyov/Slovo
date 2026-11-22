import Foundation
import XCTest
@testable import SlovoCore

/// Test doubles for the English helper of mixed dictation.
enum MixedLanguageFakes {
    /// A helper engine that takes the Dictionary's terms, like Whisper.
    final class HelperEngine: VocabularyTranscribing, @unchecked Sendable {
        let id: String
        let text: DictationFakes.Locked<String>
        let error = DictationFakes.Locked<Error?>(nil)
        /// Waits until its task is cancelled, the way whisper.cpp polls its abort flag.
        let waitsForCancellation = DictationFakes.Locked(false)
        let wasCancelled = DictationFakes.Locked(false)
        let vocabularies = DictationFakes.Locked<[[String]]>([])

        init(id: EngineID = .whisperTurbo, text: String) {
            self.id = id.rawValue
            self.text = DictationFakes.Locked(text)
        }

        var calls: Int {
            vocabularies.value.count
        }

        func prepare() async throws {}

        func transcribe(fileURL: URL) async throws -> String {
            try await transcribe(fileURL: fileURL, vocabulary: [])
        }

        func transcribe(fileURL: URL, vocabulary: [String]) async throws -> String {
            vocabularies.value.append(vocabulary)
            if waitsForCancellation.value {
                while !Task.isCancelled {
                    try? await Task.sleep(for: .milliseconds(5))
                }
                wasCancelled.value = true
                throw CancellationError()
            }
            if let error = error.value {
                throw error
            }
            return text.value
        }
    }

    /// The Russian words of the texts below, so only the garbled ones are suspect.
    static let merger = MixedLanguageMerger(lexicon: .fixed(["ru": [
        "сделай", "в", "версии", "привет", "как", "дела", "и", "всё", "работает", "под",
    ]]))

    static let garbled = "Сделай ребейс в версии Makos."
    static let helperHeard = "Сделай rebase в версии macOS."
    static let merged = "Сделай rebase в версии macOS."
    static let russianOnly = "Привет, как дела?"

    static let ready = ModelStatus.ready(diskBytes: 1)
    static let missing = ModelStatus.notDownloaded(downloadBytes: 1)
}

// MARK: - Choosing the helper

final class DictationEnglishHelperChoiceTests: XCTestCase {
    private func statuses(whisper: ModelStatus, parakeet: ModelStatus) -> (EngineID) -> ModelStatus {
        { id in
            switch id {
            case .whisperTurbo: return whisper
            case .parakeet: return parakeet
            default: return MixedLanguageFakes.ready
            }
        }
    }

    func testAutoPrefersWhisperThenParakeetThenNothing() {
        let ready = MixedLanguageFakes.ready, missing = MixedLanguageFakes.missing
        XCTAssertEqual(EnglishHelperChoice.auto.engine(status: statuses(whisper: ready, parakeet: ready)), .whisperTurbo)
        XCTAssertEqual(EnglishHelperChoice.auto.engine(status: statuses(whisper: missing, parakeet: ready)), .parakeet)
        XCTAssertEqual(EnglishHelperChoice.auto.engine(status: statuses(whisper: .downloading(progress: 0.5), parakeet: ready)),
                       .parakeet, "A model still downloading cannot help yet")
        XCTAssertNil(EnglishHelperChoice.auto.engine(status: statuses(whisper: missing, parakeet: .damaged(reason: "x"))))
    }

    func testExplicitChoicesNeedTheirOwnModel() {
        let ready = MixedLanguageFakes.ready, missing = MixedLanguageFakes.missing
        XCTAssertEqual(EnglishHelperChoice.parakeet.engine(status: statuses(whisper: ready, parakeet: ready)), .parakeet)
        XCTAssertNil(EnglishHelperChoice.parakeet.engine(status: statuses(whisper: ready, parakeet: missing)))
        XCTAssertEqual(EnglishHelperChoice.whisper.engine(status: statuses(whisper: ready, parakeet: ready)), .whisperTurbo)
        XCTAssertNil(EnglishHelperChoice.whisper.engine(status: statuses(whisper: missing, parakeet: ready)),
                     "Whisper chosen but missing does not quietly switch to Parakeet")
        XCTAssertNil(EnglishHelperChoice.off.engine(status: statuses(whisper: ready, parakeet: ready)))
    }

    func testSettingDefaultsToAutoAndAppliesOnlyToMixedGigaAMDictation() throws {
        let suite = "slovo.tests.englishHelper.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        let ready: (EngineID) -> ModelStatus = { _ in MixedLanguageFakes.ready }

        XCTAssertEqual(settings.englishHelper, .auto)
        XCTAssertEqual(AppSettings.registrationDefaults[SettingsKey.englishHelper] as? String, "auto")
        XCTAssertEqual(SettingsKey.englishHelper, "recognition.englishHelper")
        XCTAssertEqual(settings.englishHelper(for: .gigaamRNNT, languageMode: .mixed, status: ready), .whisperTurbo)
        XCTAssertEqual(settings.englishHelper(for: .gigaamCTC, languageMode: .mixed, status: ready), .whisperTurbo)
        XCTAssertNil(settings.englishHelper(for: .gigaamRNNT, languageMode: .russian, status: ready))
        XCTAssertNil(settings.englishHelper(for: .gigaamRNNT, languageMode: .english, status: ready))
        XCTAssertNil(settings.englishHelper(for: .parakeet, languageMode: .mixed, status: ready),
                     "A multilingual engine writes its own English")
        XCTAssertNil(settings.englishHelper(for: .appleDictation, languageMode: .mixed, status: ready))

        defaults.set("whisper", forKey: SettingsKey.englishHelper)
        XCTAssertEqual(settings.englishHelper, .whisper)
        settings.englishHelper = .off
        XCTAssertNil(settings.englishHelper(for: .gigaamRNNT, languageMode: .mixed, status: ready))
        defaults.set("bogus", forKey: SettingsKey.englishHelper)
        XCTAssertEqual(settings.englishHelper, .auto)
    }

    func testWhisperCanTranscribeOnItsOwnAndIsSelectedByTheAccurateToggle() {
        XCTAssertEqual(EngineID.whisperTurbo.rawValue, "whisper-large-v3-turbo")
        XCTAssertEqual(EngineID.whisperTurbo.displayName, "Whisper large-v3 turbo")
        XCTAssertEqual(EngineID.whisperTurbo.modelFolder, "whisper-v3-turbo")
        XCTAssertTrue(EngineID.transcriptionEngines.contains(.whisperTurbo))
        XCTAssertEqual(EngineID.transcriptionEngines.count, EngineID.allCases.count)
        for choices in [EngineID.russianChoices, EngineID.englishChoices, EngineID.meetingChoices] {
            XCTAssertFalse(choices.contains(.whisperTurbo))
        }
    }

    func testWhisperPromptIsTheNewestTermsThatFit() {
        XCTAssertNil(WhisperEngine.prompt(for: []))
        XCTAssertNil(WhisperEngine.prompt(for: ["  ", ""]))
        XCTAssertEqual(WhisperEngine.prompt(for: ["main", "pull request", "Main", "macOS"]), "pull request, Main, macOS.")

        let many = (0..<200).map { "Term\($0)" }
        let prompt = try? XCTUnwrap(WhisperEngine.prompt(for: many))
        XCTAssertLessThanOrEqual(prompt?.count ?? .max, WhisperEngine.promptBudget)
        XCTAssertTrue(prompt?.hasSuffix("Term199.") ?? false, "The newest terms are kept")
        XCTAssertFalse(prompt?.contains("Term0,") ?? true)
    }

    func testWhisperPromptSkipsAnOversizedTerm() {
        let oversized = String(repeating: "x", count: WhisperEngine.promptBudget)
        XCTAssertEqual(WhisperEngine.prompt(for: ["rebase", "macOS", oversized]), "rebase, macOS.")
        XCTAssertEqual(WhisperEngine.prompt(for: ["rebase", oversized, "macOS"]), "rebase, macOS.")
        XCTAssertNil(WhisperEngine.prompt(for: [oversized]))

        let boundary = String(oversized.dropLast())
        XCTAssertEqual(WhisperEngine.prompt(for: [boundary]), boundary + ".")
    }
}

// MARK: - The helper

@MainActor
final class DictationMixedLanguageHelperTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var engines: DictationFakes.Engines!
    private var models: DictationFakes.Models!
    private var whisper: MixedLanguageFakes.HelperEngine!
    private var helper: MixedLanguageHelper!
    private let audio = URL(fileURLWithPath: "/nonexistent/Slovo-test.wav")

    override func setUp() async throws {
        suiteName = "slovo.tests.mixedHelper.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        engines = DictationFakes.Engines()
        models = DictationFakes.Models()
        models.statuses = [.whisperTurbo: MixedLanguageFakes.ready, .parakeet: MixedLanguageFakes.ready]
        whisper = MixedLanguageFakes.HelperEngine(text: MixedLanguageFakes.helperHeard)
        engines.extraEngines[.whisperTurbo] = whisper
        let models = self.models!
        helper = MixedLanguageHelper(engines: engines, settings: settings, status: { models.status(of: $0) },
                                     merger: MixedLanguageFakes.merger)
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
    }

    private func run(_ text: String = MixedLanguageFakes.garbled, engine: EngineID = .gigaamRNNT,
                     mode: LanguageMode = .mixed, vocabulary: [String] = []) async -> MixedLanguageHelper.Result {
        await helper.run(text, audio: audio, duration: 4, engine: engine, languageMode: mode, vocabulary: vocabulary)
    }

    func testHelperDoesNotRunWhenNothingIsSuspect() async {
        let result = await run(MixedLanguageFakes.russianOnly)
        XCTAssertEqual(result, .init(text: MixedLanguageFakes.russianOnly, outcome: .notNeeded))
        XCTAssertEqual(whisper.calls, 0)
        XCTAssertTrue(engines.requested.isEmpty, "Pure Russian must not even load the helper")
    }

    func testSuspectWordsTakeTheHelpersEnglish() async {
        let result = await run(vocabulary: ["rebase", "macOS"])
        XCTAssertEqual(result.text, MixedLanguageFakes.merged)
        XCTAssertEqual(result.outcome, .merged(helper: .whisperTurbo, replacements: 2))
        XCTAssertEqual(whisper.vocabularies.value, [["rebase", "macOS"]], "Whisper hears the Dictionary's terms")
        XCTAssertEqual(engines.requested, [.whisperTurbo])
        XCTAssertEqual(engines.markedUsed, [.whisperTurbo], "The helper's idle countdown restarts")
    }

    func testAutoFallsBackToParakeetWithoutWhisper() async {
        models.statuses[.whisperTurbo] = MixedLanguageFakes.missing
        let parakeet = DictationFakes.Engine(id: EngineID.parakeet.rawValue, text: MixedLanguageFakes.helperHeard)
        engines.engines[.parakeet] = parakeet

        let result = await run()
        XCTAssertEqual(result.outcome, .merged(helper: .parakeet, replacements: 2))
        XCTAssertEqual(parakeet.sampleCounts.value.count, 1)
        XCTAssertEqual(whisper.calls, 0)
    }

    func testNoHelperModelKeepsTheText() async {
        models.statuses = [:]
        let result = await run()
        XCTAssertEqual(result, .init(text: MixedLanguageFakes.garbled, outcome: .notApplicable))
        XCTAssertTrue(engines.requested.isEmpty)
    }

    func testSettingOffKeepsTheText() async {
        settings.englishHelper = .off
        let result = await run()
        XCTAssertEqual(result, .init(text: MixedLanguageFakes.garbled, outcome: .notApplicable))
        XCTAssertTrue(engines.requested.isEmpty)
    }

    func testOtherModesAndEnginesAreUntouched() async {
        for mode in [LanguageMode.russian, .english] {
            let result = await run(mode: mode)
            XCTAssertEqual(result, .init(text: MixedLanguageFakes.garbled, outcome: .notApplicable), "\(mode)")
        }
        for engine in [EngineID.parakeet, .appleDictation] {
            let result = await run(engine: engine)
            XCTAssertEqual(result.outcome, .notApplicable, "\(engine)")
        }
        XCTAssertTrue(engines.requested.isEmpty)
    }

    func testHelperFailureKeepsGigaAMsText() async {
        whisper.error.value = SlovoError("Metal went away")
        let result = await run()
        XCTAssertEqual(result, .init(text: MixedLanguageFakes.garbled, outcome: .failed(helper: .whisperTurbo)))

        engines.unavailable[.whisperTurbo] = .damaged(reason: "x")
        let unloadable = await run()
        XCTAssertEqual(unloadable.text, MixedLanguageFakes.garbled)
    }

    func testSlowHelperIsAbandonedAtTheTimeLimit() async {
        whisper.waitsForCancellation.value = true
        helper.timeLimitOverride = 0.1
        let clock = ContinuousClock()
        let started = clock.now
        let result = await run()
        XCTAssertEqual(result, .init(text: MixedLanguageFakes.garbled, outcome: .timedOut(helper: .whisperTurbo)))
        XCTAssertLessThan(started.duration(to: clock.now), .seconds(2))
        let stopped = await DictationFakes.waitUntil { self.whisper.wasCancelled.value }
        XCTAssertTrue(stopped, "An abandoned helper is told to stop")
    }

    func testHelperThatIgnoresCancellationStillCannotHoldTheDictation() async {
        // DictationFakes.Engine waits on a gate and never looks at cancellation.
        let stuck = DictationFakes.Engine(id: EngineID.parakeet.rawValue, text: MixedLanguageFakes.helperHeard)
        let gate = DictationFakes.Gate()
        stuck.gate.value = gate
        engines.engines[.parakeet] = stuck
        settings.englishHelper = .parakeet
        helper.timeLimitOverride = 0.1

        let result = await run()
        XCTAssertEqual(result.outcome, .timedOut(helper: .parakeet))
        XCTAssertEqual(result.text, MixedLanguageFakes.garbled)
        gate.open()
    }

    func testCancellingStopsTheHelper() async {
        whisper.waitsForCancellation.value = true
        let pending = Task { await self.run() }
        let started = await DictationFakes.waitUntil { self.whisper.calls == 1 }
        XCTAssertTrue(started)

        pending.cancel()
        let result = await pending.value
        XCTAssertEqual(result, .init(text: MixedLanguageFakes.garbled, outcome: .cancelled(helper: .whisperTurbo)))
        let stopped = await DictationFakes.waitUntil { self.whisper.wasCancelled.value }
        XCTAssertTrue(stopped)
    }

    func testTimeLimitIsThreeSecondsOrHalfTheRecording() {
        XCTAssertEqual(MixedLanguageHelper.timeLimit(forAudio: 1), 3)
        XCTAssertEqual(MixedLanguageHelper.timeLimit(forAudio: 6), 3)
        XCTAssertEqual(MixedLanguageHelper.timeLimit(forAudio: 30), 15)
        XCTAssertEqual(MixedLanguageHelper.timeLimit(forAudio: 360), 180)
    }
}

// MARK: - Dictation with the helper

@MainActor
final class DictationMixedLanguageControllerTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!
    private var directories: AppDirectories!
    private var recorder: DictationFakes.Recorder!
    private var hotkeys: DictationFakes.Hotkeys!
    private var inserter: DictationFakes.Inserter!
    private var engines: DictationFakes.Engines!
    private var models: DictationFakes.Models!
    private var gigaAM: DictationFakes.Engine!
    private var whisper: MixedLanguageFakes.HelperEngine!
    private var pipeline: DictationFakes.Pipeline!
    private var history: DictationFakes.History!
    private var dictionary: DictationFakes.Dictionary!
    private var helper: MixedLanguageHelper!
    private var controller: DictationController!

    override func setUp() async throws {
        suiteName = "slovo.tests.mixedDictation.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMixedDictationTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)

        recorder = DictationFakes.Recorder()
        recorder.nextAudio = DictationFakes.speech(seconds: 1)
        hotkeys = DictationFakes.Hotkeys()
        inserter = DictationFakes.Inserter()
        engines = DictationFakes.Engines()
        gigaAM = DictationFakes.Engine(id: EngineID.gigaamRNNT.rawValue, text: MixedLanguageFakes.garbled)
        engines.engines[.gigaamRNNT] = gigaAM
        engines.loadStates[.gigaamRNNT] = .ready
        whisper = MixedLanguageFakes.HelperEngine(text: MixedLanguageFakes.helperHeard)
        engines.extraEngines[.whisperTurbo] = whisper
        models = DictationFakes.Models()
        models.statuses = [.whisperTurbo: MixedLanguageFakes.ready]
        pipeline = DictationFakes.Pipeline()
        history = DictationFakes.History(directories: directories)
        dictionary = DictationFakes.Dictionary()
        dictionary.items = [.term("rebase"), .replacement(from: "слово", to: "Slovo"), .term("macOS")]
        let models = self.models!
        helper = MixedLanguageHelper(engines: engines, settings: settings, status: { models.status(of: $0) },
                                     merger: MixedLanguageFakes.merger)
        controller = DictationController(dependencies: .init(
            recorder: recorder, hotkeys: hotkeys, inserter: inserter, frontmostApp: DictationFakes.FrontmostApp(),
            permissions: DictationFakes.Permissions(), engines: engines, pipeline: pipeline, history: history,
            dictionary: dictionary, snippets: DictationFakes.Snippets(), notes: DictationFakes.Notes(),
            categories: DictationFakes.Categories(), meetings: DictationFakes.Meetings(), sounds: nil,
            settings: settings, directories: directories, uptime: DictationFakes.Uptime(), mixedLanguage: helper))
        controller.messageLifetime = 60
        controller.limitCheckInterval = 3_600
        controller.holdCheckDelay = 3_600
    }

    override func tearDown() async throws {
        controller?.stop()
        controller = nil
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    private func startDictation() {
        controller.startHandsFree()
        controller.finish()
    }

    private func dictate() async {
        startDictation()
        await controller.waitForProcessing()
    }

    func testMergedTextIsInsertedAndKeptAsTheRawText() async throws {
        await dictate()

        XCTAssertEqual(inserter.inserted, [MixedLanguageFakes.merged])
        XCTAssertEqual(pipeline.contexts.value.count, 1, "The merged text goes through the pipeline once")
        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertEqual(entry.rawText, MixedLanguageFakes.merged)
        XCTAssertEqual(entry.engineID, .gigaamRNNT, "GigaAM still wrote the dictation")
        XCTAssertEqual(entry.languageMode, .mixed)
        XCTAssertEqual(whisper.vocabularies.value, [["rebase", "macOS"]], "Only terms reach the prompt")
        XCTAssertEqual(controller.state, .idle)
    }

    func testPureRussianNeverRunsTheHelper() async {
        gigaAM.text.value = MixedLanguageFakes.russianOnly
        await dictate()
        XCTAssertEqual(inserter.inserted, [MixedLanguageFakes.russianOnly])
        XCTAssertEqual(whisper.calls, 0, "The helper may be loaded while recording, never run")
        XCTAssertTrue(engines.requested.contains(.gigaamRNNT))
    }

    func testAccurateModeUsesTheWholeWhisperTranscriptAndVocabularyOnce() async throws {
        settings.accurateMixedDictation = true
        engines.dictationEngine = .whisperTurbo
        // Different Russian wording proves there is no term-only merge with GigaAM.
        whisper.text.value = "Пожалуйста, сделай rebase для macOS."
        await dictate()
        XCTAssertEqual(inserter.inserted, [whisper.text.value])
        XCTAssertEqual(history.entries.first?.rawText, whisper.text.value)
        XCTAssertEqual(history.entries.first?.engineID, .whisperTurbo)
        XCTAssertEqual(whisper.vocabularies.value, [["rebase", "macOS"]])
        XCTAssertEqual(pipeline.contexts.value.count, 1)
        XCTAssertFalse(engines.requested.contains(.gigaamRNNT))
    }

    func testRerunningWithWhisperSuppliesVocabulary() async throws {
        settings.keepDictationAudio = .forever
        gigaAM.text.value = MixedLanguageFakes.russianOnly
        await dictate()
        let entry = try XCTUnwrap(history.entries.first)
        try await controller.rerun(entryID: entry.id, with: .whisperTurbo)
        XCTAssertEqual(history.entries.first?.rawText, whisper.text.value)
        XCTAssertEqual(history.entries.first?.engineID, .whisperTurbo)
        XCTAssertEqual(whisper.vocabularies.value, [["rebase", "macOS"]])
    }

    func testCancellingAccuratePrimaryDoesNotInsertOrSaveText() async {
        settings.accurateMixedDictation = true
        engines.dictationEngine = .whisperTurbo
        whisper.waitsForCancellation.value = true
        startDictation()
        let started = await DictationFakes.waitUntil { self.whisper.calls == 1 }
        XCTAssertTrue(started)
        controller.cancel()
        await controller.waitForProcessing()
        let stopped = await DictationFakes.waitUntil { self.whisper.wasCancelled.value }
        XCTAssertTrue(stopped)
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
    }

    func testAMixedRecordingLoadsTheHelperWhileTheUserSpeaks() async {
        controller.startHandsFree()
        for _ in 0..<100 where !engines.requested.contains(.whisperTurbo) {
            await Task.yield()
        }
        XCTAssertTrue(engines.requested.contains(.whisperTurbo), "A cold helper starts loading at the first word")
        XCTAssertEqual(whisper.calls, 0)
        controller.cancel()
    }

    func testOtherModesDoNotLoadTheHelper() async {
        settings.languageMode = .english
        controller.startHandsFree()
        for _ in 0..<20 {
            await Task.yield()
        }
        XCTAssertFalse(engines.requested.contains(.whisperTurbo))
        controller.cancel()
    }

    func testHelperFailureInsertsGigaAMsTextWithoutAnError() async {
        whisper.error.value = SlovoError("boom")
        await dictate()
        XCTAssertEqual(inserter.inserted, [MixedLanguageFakes.garbled])
        XCTAssertEqual(history.entries.first?.rawText, MixedLanguageFakes.garbled)
        XCTAssertEqual(controller.state, .idle, "No failure message for a helper that could not help")
    }

    func testSlowHelperFallsBackToGigaAMsText() async {
        whisper.waitsForCancellation.value = true
        helper.timeLimitOverride = 0.1
        await dictate()
        XCTAssertEqual(inserter.inserted, [MixedLanguageFakes.garbled])
        XCTAssertEqual(controller.state, .idle)
    }

    func testCancellingDuringTheHelperCancelsIt() async {
        whisper.waitsForCancellation.value = true
        startDictation()
        let started = await DictationFakes.waitUntil { self.whisper.calls == 1 }
        XCTAssertTrue(started)
        XCTAssertEqual(controller.state, .processing(isLoadingModel: false))

        controller.cancel()
        XCTAssertEqual(controller.state, .idle)
        await controller.waitForProcessing()
        let stopped = await DictationFakes.waitUntil { self.whisper.wasCancelled.value }
        XCTAssertTrue(stopped, "Cancelling the dictation stops the helper")
        XCTAssertTrue(inserter.inserted.isEmpty)
        XCTAssertTrue(history.entries.isEmpty)
    }

    func testOtherLanguageModesAreUntouched() async {
        for mode in [LanguageMode.russian, .english] {
            settings.languageMode = mode
            await dictate()
        }
        XCTAssertEqual(inserter.inserted, [MixedLanguageFakes.garbled, MixedLanguageFakes.garbled])
        XCTAssertEqual(whisper.calls, 0)
    }

    func testSettingOffKeepsGigaAMsText() async {
        settings.englishHelper = .off
        await dictate()
        XCTAssertEqual(inserter.inserted, [MixedLanguageFakes.garbled])
        XCTAssertEqual(whisper.calls, 0)
    }

    func testRunAgainOfAMixedEntryUsesTheHelper() async throws {
        gigaAM.text.value = MixedLanguageFakes.russianOnly
        settings.keepDictationAudio = .forever
        await dictate()
        let entry = try XCTUnwrap(history.entries.first)
        XCTAssertNotNil(entry.audioFileName)

        // Dictated in the mixed mode; the current mode no longer matters.
        settings.languageMode = .english
        gigaAM.text.value = MixedLanguageFakes.garbled
        try await controller.rerun(entryID: entry.id, with: .gigaamRNNT)
        XCTAssertEqual(history.entries.first?.rawText, MixedLanguageFakes.merged)
        XCTAssertEqual(history.entries.first?.text, MixedLanguageFakes.merged)
        XCTAssertEqual(whisper.calls, 1)
    }
}

// MARK: - Engines and the helper model

@MainActor
final class DictationWhisperModelTests: XCTestCase {
    private var root: URL!

    override func setUp() async throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoWhisperModelTests-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: root)
    }

    private var folder: URL {
        root.appendingPathComponent("whisper-v3-turbo", isDirectory: true)
    }

    /// A sparse file: the right length without the disk space.
    private func makeFile(_ name: String, size: Int64) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(name)
        XCTAssertTrue(FileManager.default.createFile(atPath: url.path, contents: nil))
        let handle = try FileHandle(forWritingTo: url)
        try handle.truncate(atOffset: UInt64(size))
        try handle.close()
    }

    func testStatusFollowsTheFileOnDisk() throws {
        XCTAssertEqual(ModelManager.diskStatus(of: .whisperTurbo, modelsRoot: root),
                       .notDownloaded(downloadBytes: 574_041_195))

        try makeFile("ggml-large-v3-turbo-q5_0.bin.partial", size: 1_000)
        guard case .damaged = ModelManager.diskStatus(of: .whisperTurbo, modelsRoot: root) else {
            return XCTFail("An interrupted download can be continued, so it shows as unfinished")
        }

        try FileManager.default.removeItem(at: folder)
        try makeFile(WhisperEngine.Model.fileName, size: 1_000)
        guard case .damaged = ModelManager.diskStatus(of: .whisperTurbo, modelsRoot: root) else {
            return XCTFail("A short model file is damaged")
        }

        try makeFile(WhisperEngine.Model.fileName, size: WhisperEngine.Model.size)
        XCTAssertEqual(ModelManager.diskStatus(of: .whisperTurbo, modelsRoot: root).isUsable, true)
    }

    func testModelManagerDeletesTheWholeFolder() throws {
        try makeFile(WhisperEngine.Model.fileName, size: WhisperEngine.Model.size)
        let manager = ModelManager(modelsRoot: root, downloader: NoDownloads(), appleAvailability: { false })
        manager.refresh()
        XCTAssertTrue(manager.status(of: .whisperTurbo).isUsable)

        try manager.delete(.whisperTurbo)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: root.path), "The models folder itself stays")
        XCTAssertEqual(manager.status(of: .whisperTurbo), .notDownloaded(downloadBytes: WhisperEngine.Model.size))
    }

    func testEngineWithoutItsFileFailsWithoutDownloading() async throws {
        let engine = try EngineManager.factory(modelsRoot: root)(.whisperTurbo, .mixed)
        XCTAssertEqual(engine.id, "whisper-large-v3-turbo")
        do {
            try await engine.prepare()
            XCTFail("Whisper without its file must fail")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains(folder.path), error.localizedDescription)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.path))

        try makeFile(WhisperEngine.Model.fileName, size: 10)
        do {
            try await engine.prepare()
            XCTFail("A truncated file is refused before whisper.cpp reads it")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains(WhisperEngine.Model.fileName), error.localizedDescription)
        }
    }

    func testChangingTheSettingReleasesALoadedWhisper() async throws {
        let suite = "slovo.tests.whisperRelease.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        let models = DictationFakes.Models()
        models.statuses = [.whisperTurbo: MixedLanguageFakes.ready, .parakeet: MixedLanguageFakes.ready]
        let manager = EngineManager(settings: settings, models: models, factory: { id, _ in
            MixedLanguageFakes.HelperEngine(id: id, text: "")
        })
        defer { manager.unloadAll() }

        _ = try await manager.engine(for: .whisperTurbo)
        _ = try await manager.engine(for: .parakeet)
        manager.settingsDidChange()
        XCTAssertEqual(manager.loadState(of: .whisperTurbo), .ready, "Auto still uses Whisper")

        settings.englishHelper = .parakeet
        manager.settingsDidChange()
        XCTAssertEqual(manager.loadState(of: .whisperTurbo), .unloaded)
        XCTAssertEqual(manager.loadState(of: .parakeet), .ready, "Parakeet may still serve English dictation")
    }

    private struct NoDownloads: ModelDownloading {
        func download(_ id: EngineID, modelsRoot: URL, progress: @escaping @Sendable (Double) -> Void) async throws {}
    }
}

/// whisper.cpp itself, with the real model. Needs SLOVO_WHISPER_MODEL pointing at
/// `ggml-large-v3-turbo-q5_0.bin`; skipped otherwise.
final class DictationWhisperEngineTests: XCTestCase {
    private static let audio = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("Shared/fixtures/audio", isDirectory: true)

    private func engine() throws -> WhisperEngine {
        guard let path = ProcessInfo.processInfo.environment["SLOVO_WHISPER_MODEL"] else {
            throw XCTSkip("Set SLOVO_WHISPER_MODEL to ggml-large-v3-turbo-q5_0.bin to run this test")
        }
        return WhisperEngine(modelFile: URL(fileURLWithPath: path))
    }

    private static func words(_ text: String) -> [String] {
        text.lowercased().split(whereSeparator: { !$0.isLetter }).map(String.init)
    }

    func testTranscribesRussianWithThePrompt() async throws {
        let whisper = try engine()
        try await whisper.prepare()
        let text = try await whisper.transcribe(fileURL: Self.audio.appendingPathComponent("ru-hello.wav"),
                                                vocabulary: ["GigaAM", "macOS"])
        let expected = try String(contentsOf: Self.audio.appendingPathComponent("ru-hello.txt"), encoding: .utf8)
        XCTAssertEqual(Self.words(text), Self.words(expected), text)
    }

    func testCancellingStopsTheDecoder() async throws {
        let whisper = try engine()
        try await whisper.prepare()
        let file = Self.audio.appendingPathComponent("two-voices.wav")
        let pending = Task { try await whisper.transcribe(fileURL: file, vocabulary: []) }
        try await Task.sleep(for: .milliseconds(150))
        let clock = ContinuousClock()
        let cancelled = clock.now
        pending.cancel()
        do {
            _ = try await pending.value
            XCTFail("A cancelled transcription must not return text")
        } catch {
            XCTAssertTrue(error is CancellationError, "\(error)")
        }
        // Metal checks the flag between command buffers, so an encoder pass that already
        // started runs to its end (about a second on a busy M4).
        XCTAssertLessThan(cancelled.duration(to: clock.now), .seconds(3), "The decoder stops at its next check")

        // The context is still usable afterwards.
        let text = try await whisper.transcribe(fileURL: Self.audio.appendingPathComponent("ru-hello.wav"), vocabulary: [])
        XCTAssertFalse(text.isEmpty)
    }
}
