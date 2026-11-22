import Foundation
import XCTest
@testable import SlovoCore

/// Wiring that no single module owns: the dictation controller over the real stores,
/// text pipeline and category lookup, retention over what dictation saved, and
/// `ServiceWiring` (paste-last shortcut, preloading, model deletion).
@MainActor
final class IntegrationWiringTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!
    private var directories: AppDirectories!

    override func setUp() async throws {
        suiteName = "slovo.tests.integration.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoIntegrationTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Dictation over real stores

    func testKeyDictationUsesStoresPipelineAndAppCategory() async throws {
        let raw = "разверни кибернетес в проде"
        let harness = makeHarness(transcript: raw)
        harness.dictionary.add(.term("Kubernetes"))
        // Spark has no fixed bundle id; the category store matches it by name.
        harness.frontmost.app = AppIdentity(bundleID: "com.example.SparkBeta", name: "Spark")
        harness.controller.start()
        defer { harness.controller.stop() }

        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.handle(.finish)
        await harness.controller.waitForProcessing()

        let expected = TextPipeline().process(raw, context: TextPipelineContext(
            dictionary: harness.dictionary.items, cleanup: settings.cleanup, style: .formal, languageMode: .mixed))
        XCTAssertTrue(expected.text.contains("Kubernetes"), expected.text)
        XCTAssertTrue(expected.text.hasSuffix("."), expected.text)
        XCTAssertEqual(harness.inserter.inserted, [expected.text])
        XCTAssertEqual(harness.controller.lastTranscript, expected.text)

        let entry = try XCTUnwrap(harness.history.entries.first)
        XCTAssertEqual(entry.text, expected.text)
        XCTAssertEqual(entry.rawText, raw)
        XCTAssertEqual(entry.category, .email)
        XCTAssertEqual(entry.appName, "Spark")
        XCTAssertEqual(entry.appBundleID, "com.example.SparkBeta")
        XCTAssertEqual(entry.engineID, .gigaamRNNT)
        XCTAssertEqual(entry.fixesCount, 1)
        let audioURL = try XCTUnwrap(harness.history.audioURL(for: entry))
        XCTAssertTrue(audioURL.path.hasPrefix(directories.recordingsFolder.path))
        XCTAssertEqual(try WAVFile.read(from: audioURL).samples.count, harness.recorder.nextAudio.samples.count)

        // The entry reaches disk: a store opened afterwards sees it.
        try harness.history.flush()
        XCTAssertEqual(HistoryStore(directories: directories).entries.first?.id, entry.id)
    }

    func testStyleFollowsTheCategorySetting() async throws {
        let harness = makeHarness(transcript: "Созвонимся завтра.")
        harness.frontmost.app = AppIdentity(bundleID: "ru.keepcoder.Telegram", name: "Telegram")
        settings.setStyle(.casual, for: .personal)

        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.handle(.finish)
        await harness.controller.waitForProcessing()

        XCTAssertEqual(harness.inserter.inserted, ["Созвонимся завтра"])
        XCTAssertEqual(harness.history.entries.first?.category, .personal)
    }

    func testHandsFreeDictationIntoNoteAppendsToStoredNote() async throws {
        let harness = makeHarness(transcript: "купить молоко")
        let note = harness.notes.create(text: "Список:")

        harness.controller.startHandsFree(target: .note(note.id))
        harness.controller.finish()
        await harness.controller.waitForProcessing()

        let stored = try XCTUnwrap(harness.notes.notes.first { $0.id == note.id })
        XCTAssertTrue(stored.text.hasPrefix("Список: "), stored.text)
        XCTAssertTrue(stored.text.lowercased().contains("купить молоко"), stored.text)
        XCTAssertTrue(harness.inserter.inserted.isEmpty)
        XCTAssertEqual(harness.history.entries.first?.category, .other)
        XCTAssertNil(harness.history.entries.first?.appBundleID)
    }

    func testRetentionRemovesAudioThatDictationKept() async throws {
        let harness = makeHarness(transcript: "проверка связи")
        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.handle(.finish)
        await harness.controller.waitForProcessing()
        let entry = try XCTUnwrap(harness.history.entries.first)
        let audioURL = try XCTUnwrap(harness.history.audioURL(for: entry))

        settings.keepDictationAudio = .hours24
        let cleaner = RetentionCleaner(history: harness.history, meetings: MeetingStore(directories: directories),
                                       settings: settings)
        XCTAssertEqual(cleaner.run(now: entry.date.addingTimeInterval(3600)), RetentionReport())
        XCTAssertEqual(cleaner.run(now: entry.date.addingTimeInterval(25 * 3600)).removedDictationAudio, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: audioURL.path))
        XCTAssertEqual(harness.history.entries.count, 1)
        XCTAssertNil(harness.history.entries.first?.audioFileName)
    }

    func testAudioRetentionNeverLeavesNoRecording() async throws {
        settings.keepDictationAudio = .never
        let harness = makeHarness(transcript: "без записи")
        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.handle(.finish)
        await harness.controller.waitForProcessing()

        let entry = try XCTUnwrap(harness.history.entries.first)
        XCTAssertNil(entry.audioFileName)
        let recordings = try FileManager.default.subpathsOfDirectory(atPath: directories.recordingsFolder.path)
        XCTAssertTrue(recordings.filter { $0.hasSuffix(".wav") }.isEmpty, "\(recordings)")
    }

    // MARK: - ServiceWiring

    func testPasteLastShortcutFollowsSettingAndPastesLastTranscript() async throws {
        let harness = makeHarness(transcript: "привет")
        harness.controller.handle(.begin(.pushToTalk))
        harness.controller.handle(.finish)
        await harness.controller.waitForProcessing()
        XCTAssertEqual(harness.inserter.inserted.count, 1)

        let registrar = ShortcutRegistrar()
        let controller = harness.controller
        let wiring = ServiceWiring(settings: settings, models: DictationFakes.Models(), engines: harness.engines,
                                   pasteLast: registrar) {
            Task { await controller.pasteLastTranscript() }
        }
        wiring.start()
        defer { wiring.stop() }
        XCTAssertEqual(registrar.registrations, [.pasteLastDefault])
        XCTAssertTrue(wiring.isPasteLastRegistered)

        registrar.fire()
        let pastedAgain = await DictationFakes.waitUntil { harness.inserter.inserted.count == 2 }
        XCTAssertTrue(pastedAgain)
        XCTAssertEqual(harness.inserter.inserted.last, controller.lastTranscript)

        // Unrelated settings leave the registration alone; a new shortcut replaces it.
        settings.soundsEnabled = false
        XCTAssertTrue(wiring.applySettings())
        XCTAssertEqual(registrar.registrations.count, 1)
        let custom = HotkeyShortcut(keyCode: 9, modifiers: [.option, .command])
        settings.pasteLastShortcut = custom
        let followed = await DictationFakes.waitUntil { registrar.registrations.last == custom }
        XCTAssertTrue(followed)
        XCTAssertEqual(registrar.shortcut, custom)
    }

    func testRefusedShortcutIsNotRetriedUntilTheSettingChanges() {
        let registrar = ShortcutRegistrar()
        registrar.refuses = true
        let wiring = ServiceWiring(settings: settings, models: DictationFakes.Models(), engines: DictationFakes.Engines(),
                                   pasteLast: registrar) {}
        XCTAssertFalse(wiring.applySettings())
        XCTAssertFalse(wiring.applySettings())
        XCTAssertEqual(registrar.registrations.count, 1)

        settings.pasteLastShortcut = HotkeyShortcut(keyCode: 11, modifiers: [.control, .option])
        XCTAssertFalse(wiring.applySettings())
        XCTAssertEqual(registrar.registrations.count, 2)
    }

    func testDictationEngineIsPreloadedOnceItsModelBecomesUsable() {
        let log = EventLog()
        let models = LoggingModels(log: log)
        models.statuses = [.gigaamRNNT: .downloading(progress: 0.5), .parakeet: .ready(diskBytes: 1)]
        let engines = LoggingEngines(log: log)
        let wiring = ServiceWiring(settings: settings, models: models, engines: engines, pasteLast: ShortcutRegistrar()) {}
        wiring.start()
        defer { wiring.stop() }

        models.statuses[.gigaamRNNT] = .downloading(progress: 0.9)
        wiring.modelStatusesDidChange()
        XCTAssertEqual(log.events, [])

        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        wiring.modelStatusesDidChange()
        wiring.modelStatusesDidChange()
        XCTAssertEqual(log.events, ["preload"])

        // Apple Dictation's check finishing does not concern the chosen GigaAM engine.
        models.statuses[.appleDictation] = .systemManaged(available: true)
        wiring.modelStatusesDidChange()
        XCTAssertEqual(log.events, ["preload"])
    }

    func testAppleDictationIsPreloadedWhenItsCheckArrivesAfterLaunch() {
        let log = EventLog()
        let models = LoggingModels(log: log)
        let engines = LoggingEngines(log: log)
        engines.dictationEngine = .appleDictation
        let wiring = ServiceWiring(settings: settings, models: models, engines: engines, pasteLast: ShortcutRegistrar()) {}
        wiring.start()
        defer { wiring.stop() }

        models.statuses[.appleDictation] = .systemManaged(available: false)
        wiring.modelStatusesDidChange()
        XCTAssertEqual(log.events, [])
        models.statuses[.appleDictation] = .systemManaged(available: true)
        wiring.modelStatusesDidChange()
        XCTAssertEqual(log.events, ["preload"])
    }

    func testDeletingAModelReleasesItsEngineFirst() async throws {
        let log = EventLog()
        let models = LoggingModels(log: log)
        models.statuses = [.parakeet: .ready(diskBytes: 1)]
        let engines = LoggingEngines(log: log)
        engines.dictationEngine = .parakeet
        let wiring = ServiceWiring(settings: settings, models: models, engines: engines, pasteLast: ShortcutRegistrar()) {}
        wiring.start()
        defer { wiring.stop() }

        try await wiring.deleteModel(.parakeet)
        XCTAssertEqual(log.events, ["unload:parakeet-v3", "wait:parakeet-v3", "delete:parakeet-v3"])

        // Downloading it again counts as becoming usable.
        models.statuses[.parakeet] = .ready(diskBytes: 1)
        wiring.modelStatusesDidChange()
        XCTAssertEqual(log.events.last, "preload")
    }

    func testFailedDeleteIsReported() async {
        let log = EventLog()
        let models = LoggingModels(log: log)
        models.deleteError = SlovoError("busy")
        let wiring = ServiceWiring(settings: settings, models: models, engines: LoggingEngines(log: log),
                                   pasteLast: ShortcutRegistrar()) {}
        // XCTAssertThrowsError takes a synchronous autoclosure, so the async call is checked by hand.
        do {
            try await wiring.deleteModel(.gigaamCTC)
            XCTFail("A refused delete must be reported")
        } catch {
            XCTAssertEqual(error.localizedDescription, "busy")
        }
        XCTAssertEqual(log.events, ["unload:gigaam-v3-ctc", "wait:gigaam-v3-ctc", "delete:gigaam-v3-ctc"])
    }

    /// `unload` has a default no-op for providers without engines; `EngineManager` must
    /// still be reached through the protocol.
    func testEngineManagerUnloadIsReachedThroughTheProtocol() async throws {
        let models = DictationFakes.Models()
        models.statuses = [.gigaamRNNT: .ready(diskBytes: 1)]
        let manager = EngineManager(settings: settings, models: models, factory: { id, _ in
            DictationFakes.Engine(id: id.rawValue, text: "")
        })
        _ = try await manager.engine(for: .gigaamRNNT)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .ready)

        let provider: EngineProviding = manager
        provider.unload(.gigaamRNNT)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)
    }

    /// The same for the name-aware category lookup of `AppCategoryStore`.
    func testCategoryStoreNameLookupIsReachedThroughTheProtocol() {
        let store: AppCategoryStoring = AppCategoryStore(directories: directories, installedNameLookup: { _ in nil })
        XCTAssertEqual(store.category(for: AppIdentity(bundleID: "com.example.Bitrix", name: "Bitrix24")), .work)
        XCTAssertEqual(store.category(forBundleID: "com.example.Bitrix"), .other)
    }

    // MARK: - Helpers

    private struct Harness {
        let controller: DictationController
        let recorder: DictationFakes.Recorder
        let inserter: DictationFakes.Inserter
        let frontmost: DictationFakes.FrontmostApp
        let engines: DictationFakes.Engines
        let history: HistoryStore
        let dictionary: DictionaryStore
        let notes: NoteStore
    }

    private func makeHarness(transcript: String) -> Harness {
        let recorder = DictationFakes.Recorder()
        recorder.nextAudio = DictationFakes.speech(seconds: 1)
        let inserter = DictationFakes.Inserter()
        let frontmost = DictationFakes.FrontmostApp()
        let engines = DictationFakes.Engines()
        engines.engines[.gigaamRNNT] = DictationFakes.Engine(id: EngineID.gigaamRNNT.rawValue, text: transcript)
        engines.loadStates[.gigaamRNNT] = .ready
        let history = HistoryStore(directories: directories)
        let dictionary = DictionaryStore(directories: directories)
        let notes = NoteStore(directories: directories)
        let controller = DictationController(dependencies: .init(
            recorder: recorder, hotkeys: DictationFakes.Hotkeys(), inserter: inserter, frontmostApp: frontmost,
            permissions: DictationFakes.Permissions(), engines: engines, pipeline: TextPipeline(), history: history,
            dictionary: dictionary, snippets: SnippetStore(directories: directories), notes: notes,
            categories: AppCategoryStore(directories: directories, installedNameLookup: { _ in nil }),
            meetings: DictationFakes.Meetings(), sounds: nil, settings: settings, directories: directories,
            uptime: DictationFakes.Uptime()))
        return Harness(controller: controller, recorder: recorder, inserter: inserter, frontmost: frontmost,
                       engines: engines, history: history, dictionary: dictionary, notes: notes)
    }

    @MainActor
    final class ShortcutRegistrar: GlobalShortcutRegistering {
        var shortcut: HotkeyShortcut?
        var refuses = false
        private(set) var registrations: [HotkeyShortcut] = []
        private var action: (@MainActor () -> Void)?

        func register(_ shortcut: HotkeyShortcut, action: @escaping @MainActor () -> Void) -> Bool {
            registrations.append(shortcut)
            guard !refuses else {
                self.shortcut = nil
                self.action = nil
                return false
            }
            self.shortcut = shortcut
            self.action = action
            return true
        }

        func unregister() {
            shortcut = nil
            action = nil
        }

        func fire() {
            action?()
        }
    }

    @MainActor
    final class EventLog {
        var events: [String] = []
    }

    @MainActor
    final class LoggingEngines: EngineProviding {
        let log: EventLog
        var dictationEngine: EngineID = .gigaamRNNT

        init(log: EventLog) {
            self.log = log
        }

        func engineID(for purpose: EnginePurpose) -> EngineID {
            purpose == .meetings ? .parakeet : dictationEngine
        }

        func loadState(of id: EngineID) -> EngineLoadState {
            .unloaded
        }

        func engine(for id: EngineID) async throws -> any TranscriptionEngine {
            throw SlovoError("Not used by these tests")
        }

        func preloadForCurrentSettings() {
            log.events.append("preload")
        }

        func markUsed(_ id: EngineID) {}

        func unload(_ id: EngineID) {
            log.events.append("unload:\(id.rawValue)")
        }

        func waitForLoad(_ id: EngineID) async {
            log.events.append("wait:\(id.rawValue)")
        }
    }

    @MainActor
    final class LoggingModels: ModelManaging {
        let log: EventLog
        var statuses: [EngineID: ModelStatus] = [:]
        var deleteError: Error?

        init(log: EventLog) {
            self.log = log
        }

        func status(of id: EngineID) -> ModelStatus {
            statuses[id] ?? .unknown
        }

        func refresh() {}

        func startDownload(_ id: EngineID) {}

        func cancelDownload(_ id: EngineID) {}

        func delete(_ id: EngineID) throws {
            log.events.append("delete:\(id.rawValue)")
            if let deleteError {
                throw deleteError
            }
            statuses[id] = .notDownloaded(downloadBytes: 0)
        }
    }
}
