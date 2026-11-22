import Foundation
import XCTest
@testable import SlovoCore

@MainActor
final class DictationEngineManagerTests: XCTestCase {
    /// Records created engines and how many prepare at the same time.
    final class LoadingEngine: TranscriptionEngine, @unchecked Sendable {
        let id: String
        let preparing = DictationFakes.Locked(0)
        let maximumPreparing: DictationFakes.Locked<Int>
        let prepareCount = DictationFakes.Locked(0)
        private let gate: DictationFakes.Gate?
        private let prepareError: Error?
        private let shared: Shared

        final class Shared: @unchecked Sendable {
            let current = DictationFakes.Locked(0)
            let maximum = DictationFakes.Locked(0)
        }

        init(id: String, shared: Shared, gate: DictationFakes.Gate?, prepareError: Error?) {
            self.id = id
            self.shared = shared
            self.gate = gate
            self.prepareError = prepareError
            self.maximumPreparing = shared.maximum
        }

        func prepare() async throws {
            prepareCount.value += 1
            shared.current.value += 1
            shared.maximum.value = max(shared.maximum.value, shared.current.value)
            if let gate {
                await gate.wait()
            }
            shared.current.value -= 1
            if let prepareError {
                throw prepareError
            }
        }

        func transcribe(fileURL: URL) async throws -> String {
            ""
        }
    }

    @MainActor
    final class Factory {
        let shared = LoadingEngine.Shared()
        var gate: DictationFakes.Gate?
        var prepareError: Error?
        private(set) var made: [(id: EngineID, mode: LanguageMode)] = []
        var engines: [LoadingEngine] = []

        func make(_ id: EngineID, _ mode: LanguageMode) throws -> any TranscriptionEngine {
            let engine = LoadingEngine(id: id.rawValue, shared: shared, gate: gate, prepareError: prepareError)
            made.append((id, mode))
            engines.append(engine)
            return engine
        }
    }

    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var models: DictationFakes.Models!
    private var uptime: DictationFakes.Uptime!
    private var factory: Factory!
    private var manager: EngineManager!

    override func setUp() async throws {
        suiteName = "slovo.tests.engines.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        models = DictationFakes.Models()
        uptime = DictationFakes.Uptime()
        factory = Factory()
        let factory = self.factory
        manager = EngineManager(settings: settings, models: models, factory: { id, mode in
            try XCTUnwrap(factory).make(id, mode)
        }, uptime: uptime)
    }

    override func tearDown() async throws {
        manager?.unloadAll()
        manager = nil
        defaults.removePersistentDomain(forName: suiteName)
    }

    private func engineIdentity(_ id: EngineID) async throws -> ObjectIdentifier {
        ObjectIdentifier(try await manager.engine(for: id))
    }

    func testEngineIDsFollowRecognitionSettings() {
        XCTAssertEqual(manager.engineID(for: .dictation(.mixed)), .gigaamRNNT)
        XCTAssertEqual(manager.engineID(for: .dictation(.russian)), .gigaamRNNT)
        XCTAssertEqual(manager.engineID(for: .dictation(.english)), .parakeet)
        XCTAssertEqual(manager.engineID(for: .meetings), .gigaamCTC)

        settings.russianEngine = .gigaamCTC
        settings.englishEngine = .appleDictation
        settings.meetingEngine = .parakeet
        XCTAssertEqual(manager.engineID(for: .dictation(.russian)), .gigaamCTC)
        XCTAssertEqual(manager.engineID(for: .dictation(.english)), .appleDictation)
        XCTAssertEqual(manager.engineID(for: .meetings), .parakeet)
    }

    func testEngineLoadsOnceAndIsReused() async throws {
        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)

        let first = try await engineIdentity(.gigaamRNNT)
        let second = try await engineIdentity(.gigaamRNNT)

        XCTAssertEqual(first, second)
        XCTAssertEqual(factory.made.count, 1)
        XCTAssertEqual(factory.engines.first?.prepareCount.value, 1)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .ready)
        XCTAssertEqual(manager.loadStates[.gigaamRNNT], .ready)
    }

    func testAccurateModeOnlyChangesMixedDictation() {
        XCTAssertFalse(settings.accurateMixedDictation)
        settings.accurateMixedDictation = true
        XCTAssertEqual(manager.engineID(for: .dictation(.mixed)), .whisperTurbo)
        XCTAssertEqual(manager.engineID(for: .dictation(.russian)), .gigaamRNNT)
        XCTAssertEqual(manager.engineID(for: .dictation(.english)), .parakeet)
        XCTAssertEqual(manager.engineID(for: .meetings), .gigaamCTC)
        settings.accurateMixedDictation = false
        XCTAssertEqual(manager.engineID(for: .dictation(.mixed)), .gigaamRNNT)
    }

    func testCompletedLoadDoesNotRetainAnUnloadedEngine() async throws {
        models.statuses[.whisperTurbo] = .ready(diskBytes: 1)
        _ = try await manager.engine(for: .whisperTurbo)
        weak var released = factory.engines.last
        factory.engines.removeAll()
        manager.unload(.whisperTurbo)
        let freed = await DictationFakes.waitUntil { released == nil }
        XCTAssertTrue(freed, "Completed loading tasks must release their model result")
    }

    func testAccuratePrimarySurvivesSettingsChangesAndReleasesOldHelpers() async throws {
        models.statuses = [.gigaamRNNT: .ready(diskBytes: 1), .parakeet: .ready(diskBytes: 1),
                           .whisperTurbo: .ready(diskBytes: 1)]
        settings.englishHelper = .parakeet
        manager.preloadForCurrentSettings()
        _ = try await manager.engine(for: .gigaamRNNT)
        _ = try await manager.engine(for: .parakeet)
        settings.accurateMixedDictation = true
        manager.settingsDidChange()
        _ = try await manager.engine(for: .whisperTurbo)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)
        XCTAssertEqual(manager.loadState(of: .parakeet), .unloaded)
        settings.englishHelper = .off
        manager.settingsDidChange()
        XCTAssertEqual(manager.loadState(of: .whisperTurbo), .ready)
        settings.accurateMixedDictation = false
        manager.settingsDidChange()
        _ = try await manager.engine(for: .gigaamRNNT)
        XCTAssertEqual(manager.loadState(of: .whisperTurbo), .unloaded)
    }

    func testUnloadKeepsTheEngineAliveOnlyUntilItsCallerFinishes() async throws {
        models.statuses[.whisperTurbo] = .ready(diskBytes: 1)
        var caller: (any TranscriptionEngine)? = try await manager.engine(for: .whisperTurbo)
        weak var released = factory.engines.last
        factory.engines.removeAll()
        manager.unload(.whisperTurbo)
        await Task.yield()
        XCTAssertNotNil(released, "An active caller must keep its context usable")
        let text = try await caller?.transcribe(fileURL: URL(fileURLWithPath: "/unused.wav"))
        XCTAssertEqual(text, "")
        caller = nil
        let freed = await DictationFakes.waitUntil { released == nil }
        XCTAssertTrue(freed)
    }

    func testWhisperHintChangesForEnglishAndIsSharedByRussianAndMixed() async throws {
        models.statuses[.whisperTurbo] = .ready(diskBytes: 1)
        let russian = try await manager.engine(for: .whisperTurbo, languageMode: .russian)
        let mixed = try await manager.engine(for: .whisperTurbo, languageMode: .mixed)
        XCTAssertTrue(russian === mixed)
        let english = try await manager.engine(for: .whisperTurbo, languageMode: .english)
        XCTAssertFalse(russian === english)
        XCTAssertEqual(factory.made.map(\.mode), [.russian, .english])
    }

    func testConcurrentRequestsShareOneLoad() async throws {
        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        let gate = DictationFakes.Gate()
        factory.gate = gate

        let first = Task { try await self.engineIdentity(.gigaamRNNT) }
        let second = Task { try await self.engineIdentity(.gigaamRNNT) }
        let started = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(started)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .loading)

        gate.open()
        let firstID = try await first.value
        let secondID = try await second.value
        XCTAssertEqual(firstID, secondID)
        XCTAssertEqual(factory.made.count, 1)
    }

    func testUnusableModelsAreNeverLoaded() async {
        let cases: [(EngineID, ModelStatus)] = [
            (.parakeet, .notDownloaded(downloadBytes: 10)),
            (.gigaamRNNT, .damaged(reason: "truncated")),
            (.gigaamCTC, .downloading(progress: 0.5)),
            (.appleDictation, .systemManaged(available: false))
        ]
        for (id, status) in cases {
            models.statuses[id] = status
            do {
                _ = try await manager.engine(for: id)
                XCTFail("\(id.rawValue) must not load with status \(status)")
            } catch let error as EngineUnavailableError {
                XCTAssertEqual(error, EngineUnavailableError(engineID: id, status: status))
                XCTAssertFalse(error.localizedDescription.isEmpty)
            } catch {
                XCTFail("Unexpected error \(error)")
            }
        }
        XCTAssertTrue(factory.made.isEmpty, "An engine with missing files could download them on prepare()")
    }

    func testUnknownStatusRefreshesModelsFirst() async throws {
        models.statusesAfterRefresh = [.gigaamCTC: .ready(diskBytes: 1)]
        _ = try await manager.engine(for: .gigaamCTC)
        XCTAssertEqual(models.refreshCount, 1)
        XCTAssertEqual(factory.made.map(\.id), [.gigaamCTC])
    }

    func testAppleDictationLoadsWhileAvailabilityIsStillBeingChecked() async throws {
        _ = try await manager.engine(for: .appleDictation)
        XCTAssertEqual(factory.made.map(\.id), [.appleDictation])
    }

    func testFailedLoadIsReportedAndRetried() async throws {
        models.statuses[.parakeet] = .ready(diskBytes: 1)
        factory.prepareError = SlovoError("Core ML refused the model")
        do {
            _ = try await manager.engine(for: .parakeet)
            XCTFail("prepare() failed, so loading must fail")
        } catch {
            XCTAssertEqual(error.localizedDescription, "Core ML refused the model")
        }
        XCTAssertEqual(manager.loadState(of: .parakeet), .failed("Core ML refused the model"))

        factory.prepareError = nil
        _ = try await manager.engine(for: .parakeet)
        XCTAssertEqual(manager.loadState(of: .parakeet), .ready)
        XCTAssertEqual(factory.made.count, 2)
    }

    func testIdleEnginesAreReleased() async throws {
        settings.idleUnload = .minutes5
        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        _ = try await manager.engine(for: .gigaamRNNT)

        uptime.advance(200)
        manager.markUsed(.gigaamRNNT)
        uptime.advance(200)
        XCTAssertEqual(manager.unloadIdleEngines(), [], "markUsed restarts the countdown")

        uptime.advance(101)
        XCTAssertEqual(manager.unloadIdleEngines(), [.gigaamRNNT])
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)

        _ = try await manager.engine(for: .gigaamRNNT)
        XCTAssertEqual(factory.made.count, 2)
    }

    func testNeverUnloadKeepsEngines() async throws {
        settings.idleUnload = .never
        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        _ = try await manager.engine(for: .gigaamRNNT)
        uptime.advance(1_000_000)
        XCTAssertEqual(manager.unloadIdleEngines(), [])
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .ready)
    }

    func testLoadsRunOneAtATime() async throws {
        models.statuses = [.gigaamRNNT: .ready(diskBytes: 1), .parakeet: .ready(diskBytes: 1)]
        let gate = DictationFakes.Gate()
        factory.gate = gate

        let russian = Task { try await self.engineIdentity(.gigaamRNNT) }
        let english = Task { try await self.engineIdentity(.parakeet) }
        let started = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(started)
        try? await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(gate.arrived, 1, "The second load must wait for the first")

        gate.open()
        _ = try await russian.value
        _ = try await english.value
        XCTAssertEqual(factory.shared.maximum.value, 1)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .ready)
        XCTAssertEqual(manager.loadState(of: .parakeet), .ready)
    }

    func testPreloadLoadsTheCurrentDictationEngine() async throws {
        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        manager.preloadForCurrentSettings()
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .loading)

        _ = try await manager.engine(for: .gigaamRNNT)
        XCTAssertEqual(factory.made.map(\.id), [.gigaamRNNT])
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .ready)
    }

    func testPreloadSkipsModelsThatAreNotDownloaded() {
        models.statuses[.gigaamRNNT] = .notDownloaded(downloadBytes: 1)
        manager.preloadForCurrentSettings()
        XCTAssertTrue(factory.made.isEmpty)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)
    }

    func testSettingsChangeSwapsThePreloadedEngine() async throws {
        models.statuses = [.gigaamRNNT: .ready(diskBytes: 1), .gigaamCTC: .ready(diskBytes: 1)]
        manager.preloadForCurrentSettings()
        _ = try await manager.engine(for: .gigaamRNNT)

        settings.russianEngine = .gigaamCTC
        manager.settingsDidChange()
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded, "The engine the old settings used is released first")
        XCTAssertEqual(manager.loadState(of: .gigaamCTC), .loading)

        _ = try await manager.engine(for: .gigaamCTC)
        XCTAssertEqual(factory.made.map(\.id), [.gigaamRNNT, .gigaamCTC])
    }

    func testSettingsChangesBeforeTheFirstPreloadLoadNothing() {
        models.statuses[.gigaamCTC] = .ready(diskBytes: 1)
        settings.russianEngine = .gigaamCTC
        manager.settingsDidChange()
        XCTAssertTrue(factory.made.isEmpty)
    }

    func testAppleDictationReloadsForAnotherLanguage() async throws {
        models.statuses[.appleDictation] = .systemManaged(available: true)
        settings.russianEngine = .appleDictation
        settings.englishEngine = .appleDictation

        let russian = try await engineIdentity(.appleDictation)
        settings.languageMode = .english
        let english = try await engineIdentity(.appleDictation)

        XCTAssertNotEqual(russian, english)
        XCTAssertEqual(factory.made.map(\.mode), [.russian, .english])
    }

    func testUnloadDuringLoadDropsTheResult() async throws {
        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        let gate = DictationFakes.Gate()
        factory.gate = gate
        let pending = Task { try await self.engineIdentity(.gigaamRNNT) }
        let started = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(started)

        manager.unload(.gigaamRNNT)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)
        gate.open()
        _ = try await pending.value
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)

        _ = try await manager.engine(for: .gigaamRNNT)
        XCTAssertEqual(factory.made.count, 2)
    }

    func testUnloadAllReleasesEverything() async throws {
        models.statuses = [.gigaamRNNT: .ready(diskBytes: 1), .parakeet: .ready(diskBytes: 1)]
        _ = try await manager.engine(for: .gigaamRNNT)
        _ = try await manager.engine(for: .parakeet)
        manager.unloadAll()
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded)
        XCTAssertEqual(manager.loadState(of: .parakeet), .unloaded)
    }

    func testBundledEnginesNeverDownloadWhenFilesAreMissing() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoEngineFactoryTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let make = EngineManager.factory(modelsRoot: root)

        let parakeet = try make(.parakeet, .mixed)
        XCTAssertEqual(parakeet.id, EngineID.parakeet.rawValue)
        do {
            try await parakeet.prepare()
            XCTFail("Parakeet without files must fail instead of downloading")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains(root.path), error.localizedDescription)
        }

        let gigaAM = try make(.gigaamCTC, .mixed)
        do {
            try await gigaAM.prepare()
            XCTFail("GigaAM without files must fail")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("model.int8.onnx"), error.localizedDescription)
        }

        let created = (try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? []
        XCTAssertTrue(created.filter { $0 != "parakeet-v3" }.isEmpty, "Nothing may be downloaded: \(created)")
        let parakeetFolder = root.appendingPathComponent("parakeet-v3").path
        XCTAssertTrue(((try? FileManager.default.contentsOfDirectory(atPath: parakeetFolder)) ?? []).isEmpty)
    }
}
