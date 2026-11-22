import XCTest
@testable import SlovoCore

/// `DiarizationService` with a fake downloader and a fake loader in a temporary models
/// folder: nothing touches the network or the real Models folder.
@MainActor
final class DiarizationServiceTests: XCTestCase {
    final class Downloader: DiarizationModelDownloading, @unchecked Sendable {
        typealias Behavior = @Sendable (URL, @escaping @Sendable (Double) -> Void) async throws -> Void
        let behavior: DictationFakes.Locked<Behavior>
        let calls = DictationFakes.Locked(0)

        init(_ behavior: @escaping Behavior = { _, _ in }) {
            self.behavior = DictationFakes.Locked(behavior)
        }

        func download(to folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
            calls.value += 1
            try await behavior.value(folder, progress)
        }
    }

    final class Diarizer: SpeakerDiarizing {
        func diarize(fileAt url: URL, hint: SpeakerCountHint,
                     progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn] {
            [SpeakerTurn(start: 0, end: 1, cluster: 0)]
        }
    }

    struct Failure: LocalizedError {
        var errorDescription: String? { "disk on fire" }
    }

    private var root: URL!
    private var downloader: Downloader!
    private var loads = DictationFakes.Locked(0)
    private var loadGate: DictationFakes.Gate?
    private var loadError: Error?

    override func setUp() async throws {
        root = try DiarizationFixtures.temporaryFolder("SlovoDiarizationService")
        downloader = Downloader()
        loads = DictationFakes.Locked(0)
        loadGate = nil
        loadError = nil
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func makeService(systemSupported: Bool = true) -> DiarizationService {
        DiarizationService(modelsRoot: root, downloader: downloader, factory: { [unowned self] folder in
            XCTAssertEqual(folder, self.root.appendingPathComponent("diarization", isDirectory: true))
            self.loads.value += 1
            if let gate = self.loadGate {
                await gate.wait()
            }
            if let error = self.loadError {
                throw error
            }
            return Diarizer()
        }, systemSupported: systemSupported)
    }

    private func string(_ key: String) -> String {
        CoreStrings.string(key, table: .diarization)
    }

    private var modelFolder: URL {
        root.appendingPathComponent("diarization", isDirectory: true)
    }

    // MARK: - Status

    func testStatusComesFromDiskAtStart() throws {
        XCTAssertEqual(makeService().status, .notDownloaded(downloadBytes: DiarizationService.downloadBytes))
        let bytes = try DiarizationFixtures.installModel(in: modelFolder)
        let service = makeService()
        XCTAssertEqual(service.status, .ready(diskBytes: bytes))
        XCTAssertEqual(service.modelStatus, service.status)
        XCTAssertEqual(service.modelFolder, modelFolder)
    }

    func testRefreshSeesFilesChangedOnDisk() throws {
        let service = makeService()
        try DiarizationFixtures.installModel(in: modelFolder, skipping: ["plda-parameters.json"])
        service.refresh()
        XCTAssertEqual(service.status, .damaged(reason: string("diarization.error.modelDamaged")))
    }

    // MARK: - Download

    func testDownloadReportsProgressAndCompletes() async throws {
        let gate = DictationFakes.Gate()
        downloader.behavior.value = { folder, progress in
            progress(0.25)
            progress(0.5)
            await gate.wait()
            try DiarizationFixtures.installModel(in: folder)
        }
        let service = makeService()
        service.startDownload()
        XCTAssertEqual(service.status, .downloading(progress: 0))
        await DiarizationFixtures.waitUntil { service.status == .downloading(progress: 0.5) }

        // A second press while downloading starts nothing.
        service.startDownload()
        gate.open()
        await service.waitForDownload()

        guard case .ready = service.status else {
            return XCTFail("Expected a ready model, got \(service.status)")
        }
        XCTAssertNil(service.lastError)
        XCTAssertEqual(downloader.calls.value, 1)
    }

    func testCancelledDownloadLeavesNoErrorAndReReadsTheDisk() async {
        downloader.behavior.value = { _, progress in
            progress(0.1)
            while true {
                try await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        let service = makeService()
        service.startDownload()
        await DiarizationFixtures.waitUntil { self.downloader.calls.value == 1 }
        service.cancelDownload()
        await service.waitForDownload()

        XCTAssertEqual(service.status, .notDownloaded(downloadBytes: DiarizationService.downloadBytes))
        XCTAssertNil(service.lastError)
    }

    func testFailedDownloadReportsTheError() async throws {
        downloader.behavior.value = { _, _ in throw SlovoError("already localized") }
        let service = makeService()
        service.startDownload()
        await service.waitForDownload()
        XCTAssertEqual(service.lastError, "already localized")

        downloader.behavior.value = { folder, _ in
            try DiarizationFixtures.installModel(in: folder, skipping: ["Segmentation.mlmodelc/coremldata.bin"])
            throw Failure()
        }
        service.startDownload()
        XCTAssertNil(service.lastError, "A new download clears the previous error")
        await service.waitForDownload()
        XCTAssertEqual(service.lastError, CoreStrings.format("diarization.error.downloadFailed", table: .diarization,
                                                             "disk on fire"))
        XCTAssertEqual(service.status, .damaged(reason: string("diarization.error.modelDamaged")))
    }

    func testDownloadOfAReadyModelDoesNothing() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        let service = makeService()
        service.startDownload()
        await service.waitForDownload()
        XCTAssertEqual(downloader.calls.value, 0)
    }

    func testDeleteIsRefusedWhileDownloading() async throws {
        try DiarizationFixtures.installModel(in: modelFolder, skipping: ["plda-parameters.json"])
        let gate = DictationFakes.Gate()
        downloader.behavior.value = { _, _ in await gate.wait() }
        let service = makeService()
        service.startDownload()

        XCTAssertThrowsError(try service.delete()) { error in
            XCTAssertEqual(error.localizedDescription, self.string("diarization.error.deleteWhileDownloading"))
        }
        XCTAssertEqual(service.lastError, string("diarization.error.deleteWhileDownloading"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: modelFolder.path))

        gate.open()
        await service.waitForDownload()
        try service.delete()
        XCTAssertFalse(FileManager.default.fileExists(atPath: modelFolder.path))
        XCTAssertEqual(service.status, .notDownloaded(downloadBytes: DiarizationService.downloadBytes))
        XCTAssertNil(service.lastError)
    }

    // MARK: - Loading

    func testDiarizerThrowsAndNeverDownloadsWhenTheModelIsNotReady() async throws {
        let service = makeService()
        await assertDiarizerThrows(service, string("diarization.error.modelMissing"))

        try DiarizationFixtures.installModel(in: modelFolder, skipping: ["Embedding.mlmodelc/weights/weight.bin"])
        await assertDiarizerThrows(service, string("diarization.error.modelDamaged"))

        XCTAssertEqual(downloader.calls.value, 0)
        XCTAssertEqual(loads.value, 0)
        XCTAssertEqual(service.loadState, .unloaded)
    }

    func testUnsupportedSystemNeitherDownloadsNorLoads() async throws {
        let service = makeService(systemSupported: false)
        XCTAssertFalse(service.isSupportedOnThisSystem)
        service.startDownload()
        await service.waitForDownload()
        XCTAssertEqual(downloader.calls.value, 0)
        XCTAssertEqual(service.status, .notDownloaded(downloadBytes: DiarizationService.downloadBytes))

        try DiarizationFixtures.installModel(in: modelFolder)
        service.refresh()
        await assertDiarizerThrows(service, string("diarization.error.unsupportedSystem"))
        XCTAssertEqual(loads.value, 0)
    }

    func testDiarizerLoadsOnceUntilReleased() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        let service = makeService()
        XCTAssertTrue(service.isSupportedOnThisSystem)

        let first = try await service.diarizer()
        let second = try await service.diarizer()
        XCTAssertTrue(first === second)
        XCTAssertEqual(loads.value, 1)
        XCTAssertEqual(service.loadState, .ready)

        service.release()
        XCTAssertEqual(service.loadState, .unloaded)
        let third = try await service.diarizer()
        XCTAssertFalse(first === third)
        XCTAssertEqual(loads.value, 2)
    }

    func testConcurrentCallersShareOneLoad() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        let gate = DictationFakes.Gate()
        loadGate = gate
        let service = makeService()

        let one = Task { try await service.diarizer() }
        await DiarizationFixtures.waitUntil { gate.arrived == 1 }
        XCTAssertEqual(service.loadState, .loading)
        let two = Task { try await service.diarizer() }
        try await Task.sleep(nanoseconds: 20_000_000)
        gate.open()

        let a = try await one.value
        let b = try await two.value
        XCTAssertTrue(a === b)
        XCTAssertEqual(loads.value, 1)
    }

    func testFailedLoadIsReportedAndRetried() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        loadError = SlovoError("cannot load")
        let service = makeService()
        await assertDiarizerThrows(service, "cannot load")
        XCTAssertEqual(service.loadState, .failed("cannot load"))

        loadError = nil
        _ = try await service.diarizer()
        XCTAssertEqual(service.loadState, .ready)
        XCTAssertEqual(loads.value, 2)
    }

    func testDeleteAfterReleaseWaitsForARunningLoad() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        let gate = DictationFakes.Gate()
        loadGate = gate
        let service = makeService()

        let pipeline = Task { try await service.diarizer() }
        await DiarizationFixtures.waitUntil { gate.arrived == 1 }

        // What the integration's delete does: release, wait for the load, delete.
        let deleted = DictationFakes.Locked(false)
        let deletion = Task { @MainActor in
            service.release()
            await service.waitForLoad()
            try service.delete()
            deleted.value = true
        }
        try await Task.sleep(nanoseconds: 30_000_000)
        XCTAssertFalse(deleted.value, "Delete must wait while a load reads the files")
        XCTAssertTrue(FileManager.default.fileExists(atPath: modelFolder.path))
        await assertDiarizerThrows(service, string("diarization.error.modelMissing"),
                                   "No new load may start while the files are about to go")
        XCTAssertEqual(loads.value, 1)

        gate.open()
        _ = try await pipeline.value
        try await deletion.value
        XCTAssertTrue(deleted.value)
        XCTAssertFalse(FileManager.default.fileExists(atPath: modelFolder.path))
        XCTAssertEqual(service.loadState, .unloaded, "A load released while running is not kept")
        await assertDiarizerThrows(service, string("diarization.error.modelMissing"))
    }

    // MARK: - Cancellation

    /// A loader that waits at `gate` and then honours cancellation, as a load waiting for the
    /// FluidAudio queue does.
    private func makeCancellableService(gate: DictationFakes.Gate) -> DiarizationService {
        let loads = self.loads
        return DiarizationService(modelsRoot: root, downloader: downloader, factory: { _ in
            loads.value += 1
            await gate.wait()
            try Task.checkCancellation()
            return Diarizer()
        }, systemSupported: true)
    }

    func testCancellingTheOnlyCallerCancelsALoadWaitingForTheFluidAudioQueue() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        // Another job, such as a Parakeet model download, holds the queue the load needs.
        let queue = FluidAudioJobQueue()
        let gate = DictationFakes.Gate()
        let holder = Task { try await queue.run { await gate.wait() } }
        await DiarizationFixtures.waitUntil { gate.arrived == 1 }
        // Frees the queue later anyway, so a load that ignores the cancellation fails the
        // timing below instead of hanging the test.
        let fallbackOpener = Task {
            try await Task.sleep(nanoseconds: 5_000_000_000)
            gate.open()
        }
        defer { fallbackOpener.cancel() }
        let service = DiarizationService(modelsRoot: root, downloader: downloader, factory: { _ in
            try await queue.run { Diarizer() }
        }, systemSupported: true)

        let caller = Task { try await service.diarizer() }
        await DiarizationFixtures.waitUntil { await queue.waitingCount == 1 }
        XCTAssertEqual(service.loadState, .loading)
        let cancelledAt = ContinuousClock.now
        caller.cancel()
        do {
            _ = try await caller.value
            XCTFail("A cancelled caller received a diarizer")
        } catch is CancellationError {
        }

        XCTAssertLessThan(ContinuousClock.now - cancelledAt, .seconds(2), "the load waited for the queue")
        let waiting = await queue.waitingCount
        XCTAssertEqual(waiting, 0)
        XCTAssertEqual(service.loadState, .unloaded)

        gate.open()
        try await holder.value
        _ = try await service.diarizer()
        XCTAssertEqual(service.loadState, .ready)
    }

    func testALoadGoesOnWhileAnotherCallerStillWaitsForIt() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        let gate = DictationFakes.Gate()
        let service = makeCancellableService(gate: gate)

        let cancelled = Task { try await service.diarizer() }
        await DiarizationFixtures.waitUntil { gate.arrived == 1 }
        let waiting = Task { try await service.diarizer() }
        try await Task.sleep(nanoseconds: 20_000_000)
        cancelled.cancel()
        try await Task.sleep(nanoseconds: 30_000_000)
        gate.open()

        _ = try await waiting.value
        _ = try? await cancelled.value
        XCTAssertEqual(loads.value, 1)
        XCTAssertEqual(service.loadState, .ready)
    }

    func testACallerArrivingAfterEveryCallerGaveUpLoadsAgain() async throws {
        try DiarizationFixtures.installModel(in: modelFolder)
        let gate = DictationFakes.Gate()
        let service = makeCancellableService(gate: gate)

        let abandoned = Task { try await service.diarizer() }
        await DiarizationFixtures.waitUntil { gate.arrived == 1 }
        abandoned.cancel()
        try await Task.sleep(nanoseconds: 30_000_000)
        // The cancelled load is still running when the next caller arrives.
        let later = Task { try await service.diarizer() }
        try await Task.sleep(nanoseconds: 30_000_000)
        gate.open()

        do {
            _ = try await abandoned.value
            XCTFail("The abandoned load returned a diarizer")
        } catch is CancellationError {
        }
        _ = try await later.value
        XCTAssertEqual(loads.value, 2)
        XCTAssertEqual(service.loadState, .ready)
    }

    func testWaitForLoadReturnsAtOnceWithoutALoad() async {
        let service = makeService()
        await service.waitForLoad()
        service.release()
        XCTAssertEqual(service.loadState, .unloaded)
    }

    private func assertDiarizerThrows(_ service: DiarizationService, _ message: String, _ note: String = "",
                                      file: StaticString = #filePath, line: UInt = #line) async {
        do {
            _ = try await service.diarizer()
            XCTFail("Expected an error. \(note)", file: file, line: line)
        } catch {
            XCTAssertEqual(error.localizedDescription, message, note, file: file, line: line)
        }
    }
}
