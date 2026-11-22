import Foundation
import XCTest
@testable import SlovoCore

/// Deleting a model while its engine loads. ONNX Runtime aborts the whole process when a
/// model file disappears in the middle of a load, so deletion waits for the load and no
/// new load of that engine may start before the files are gone.
@MainActor
final class FixCoreModelDeletionTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!

    override func setUp() async throws {
        suiteName = "slovo.tests.fixcore.deletion.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoFixCoreDeletion-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    func testDeletionWaitsForTheLoadAndRefusesNewLoadsMeanwhile() async throws {
        let events = DictationFakes.Locked<[String]>([])
        let gate = DictationFakes.Gate()
        let models = DeletingModels(events: events)
        models.statuses[.gigaamRNNT] = .ready(diskBytes: 1)
        let manager = EngineManager(settings: settings, models: models, factory: { id, _ in
            events.value.append("make")
            return GatedEngine(id: id.rawValue, gate: gate, events: events)
        })
        defer { manager.unloadAll() }
        let wiring = ServiceWiring(settings: settings, models: models, engines: manager,
                                   pasteLast: IntegrationWiringTests.ShortcutRegistrar()) {}

        manager.preloadForCurrentSettings()
        let preparing = await DictationFakes.waitUntil { gate.arrived == 1 }
        XCTAssertTrue(preparing)
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .loading)

        let deletion = Task { try await wiring.deleteModel(.gigaamRNNT) }
        // The deletion releases the engine first, then waits for the load.
        let waiting = await DictationFakes.waitUntil { manager.loadState(of: .gigaamRNNT) == .unloaded }
        XCTAssertTrue(waiting)
        XCTAssertEqual(events.value, ["make", "prepare started"], "Nothing is deleted while the load reads the files")

        do {
            _ = try await manager.engine(for: .gigaamRNNT)
            XCTFail("No load may start while the model is being deleted")
        } catch let error as EngineUnavailableError {
            XCTAssertEqual(error.engineID, .gigaamRNNT)
        }
        manager.preloadForCurrentSettings()
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded, "Preloading skips a model being deleted")

        gate.open()
        try await deletion.value
        XCTAssertEqual(events.value, ["make", "prepare started", "prepare finished", "delete"])
        XCTAssertEqual(manager.loadState(of: .gigaamRNNT), .unloaded, "The dropped load is not kept")

        // Afterwards the engine is refused because its files are gone.
        do {
            _ = try await manager.engine(for: .gigaamRNNT)
            XCTFail("A deleted model must not load")
        } catch let error as EngineUnavailableError {
            XCTAssertEqual(error.status, .notDownloaded(downloadBytes: 0))
        }
        XCTAssertEqual(events.value.filter { $0 == "make" }.count, 1)
    }

    func testDeletionWithoutALoadRemovesTheFilesAtOnce() async throws {
        let events = DictationFakes.Locked<[String]>([])
        let models = DeletingModels(events: events)
        models.statuses[.gigaamCTC] = .ready(diskBytes: 1)
        let manager = EngineManager(settings: settings, models: models, factory: { id, _ in
            DictationFakes.Engine(id: id.rawValue, text: "")
        })
        let wiring = ServiceWiring(settings: settings, models: models, engines: manager,
                                   pasteLast: IntegrationWiringTests.ShortcutRegistrar()) {}
        _ = try await manager.engine(for: .gigaamCTC)
        XCTAssertEqual(manager.loadState(of: .gigaamCTC), .ready)

        try await wiring.deleteModel(.gigaamCTC)
        XCTAssertEqual(events.value, ["delete"])
        XCTAssertEqual(manager.loadState(of: .gigaamCTC), .unloaded)
        // Nothing stays blocked after a deletion: a model downloaded again loads normally.
        models.statuses[.gigaamCTC] = .ready(diskBytes: 1)
        _ = try await manager.engine(for: .gigaamCTC)
        XCTAssertEqual(manager.loadState(of: .gigaamCTC), .ready)
        manager.unloadAll()
    }

    /// The crash from the review: deleting the RNNT model shortly after a preload
    /// started killed the process with an uncaught `Ort::Exception` once sherpa-onnx
    /// reached `decoder.onnx`. Runs against a copy of the installed model in a
    /// temporary models folder (an APFS clone, so it costs no space) and skips when the
    /// model is not installed. The installed files are only read.
    func testDeletingARealModelWhileItLoadsKeepsTheAppAlive() async throws {
        let variant = GigaAMEngine.Variant.transducer
        let installedFolder = ModelStore.root.appendingPathComponent("gigaam-v3", isDirectory: true)
            .appendingPathComponent(variant.folderName, isDirectory: true)
        guard case .ready = ModelManager.diskStatus(of: .gigaamRNNT, modelsRoot: ModelStore.root) else {
            throw XCTSkip("The GigaAM RNNT model is not installed in \(ModelStore.root.path)")
        }
        let root = temporary.appendingPathComponent("Models", isDirectory: true)
        let folder = root.appendingPathComponent("gigaam-v3", isDirectory: true)
            .appendingPathComponent(variant.folderName, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for file in variant.files {
            try FileManager.default.copyItem(at: installedFolder.appendingPathComponent(file.name),
                                             to: folder.appendingPathComponent(file.name))
        }

        let models = ModelManager(modelsRoot: root, appleAvailability: { false })
        models.refresh()
        guard case .ready = models.status(of: .gigaamRNNT) else {
            return XCTFail("The copied model is not usable: \(models.status(of: .gigaamRNNT))")
        }
        let engines = EngineManager(settings: settings, models: models, factory: EngineManager.factory(modelsRoot: root))
        defer { engines.unloadAll() }
        let wiring = ServiceWiring(settings: settings, models: models, engines: engines,
                                   pasteLast: IntegrationWiringTests.ShortcutRegistrar()) {}

        engines.preloadForCurrentSettings()
        // Creating the recognizer takes about a second; the review's crash happened when
        // the files went away 40 to 400 ms into it.
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertEqual(engines.loadState(of: .gigaamRNNT), .loading, "The deletion must start while the model loads")

        try await wiring.deleteModel(.gigaamRNNT)

        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.path))
        XCTAssertEqual(models.status(of: .gigaamRNNT),
                       .notDownloaded(downloadBytes: ModelManager.downloadBytes(for: .gigaamRNNT)))
        XCTAssertEqual(engines.loadState(of: .gigaamRNNT), .unloaded)
        // Still alive a while later, and the engine is refused for its missing files.
        try await Task.sleep(for: .seconds(1))
        do {
            _ = try await engines.engine(for: .gigaamRNNT)
            XCTFail("A deleted model must not load")
        } catch let error as EngineUnavailableError {
            XCTAssertEqual(error.engineID, .gigaamRNNT)
        }
    }
}

/// An engine whose preparation waits for a gate and reports when it starts and ends.
private final class GatedEngine: TranscriptionEngine, @unchecked Sendable {
    let id: String
    private let gate: DictationFakes.Gate
    private let events: DictationFakes.Locked<[String]>

    init(id: String, gate: DictationFakes.Gate, events: DictationFakes.Locked<[String]>) {
        self.id = id
        self.gate = gate
        self.events = events
    }

    func prepare() async throws {
        events.value.append("prepare started")
        await gate.wait()
        events.value.append("prepare finished")
    }

    func transcribe(fileURL: URL) async throws -> String {
        ""
    }
}

/// Model files that report their deletion in the shared event list.
@MainActor
private final class DeletingModels: ModelManaging {
    var statuses: [EngineID: ModelStatus] = [:]
    private let events: DictationFakes.Locked<[String]>

    init(events: DictationFakes.Locked<[String]>) {
        self.events = events
    }

    func status(of id: EngineID) -> ModelStatus {
        statuses[id] ?? .unknown
    }

    func refresh() {}

    func startDownload(_ id: EngineID) {}

    func cancelDownload(_ id: EngineID) {}

    func delete(_ id: EngineID) throws {
        events.value.append("delete")
        statuses[id] = .notDownloaded(downloadBytes: 0)
    }
}
