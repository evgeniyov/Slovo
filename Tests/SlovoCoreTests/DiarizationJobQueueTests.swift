import FluidAudio
import XCTest
@testable import SlovoCore

/// The shared FluidAudio queue that Parakeet and the speaker model use.
final class DiarizationJobQueueTests: XCTestCase {
    struct Failure: Error {}

    private var savedOfflineMode = false

    override func setUp() {
        savedOfflineMode = ModelHub.offlineMode
    }

    override func tearDown() {
        ModelHub.offlineMode = savedOfflineMode
    }

    func testJobsRunOneAtATimeInArrivalOrder() async throws {
        let queue = FluidAudioJobQueue()
        let gate = DictationFakes.Gate()
        let order = DictationFakes.Locked<[Int]>([])
        let running = DictationFakes.Locked(0)
        let overlap = DictationFakes.Locked(false)

        let first = Task {
            try await queue.run {
                await gate.wait()
                order.value.append(0)
            }
        }
        await waitUntil { gate.arrived == 1 }

        var later: [Task<Void, Error>] = []
        for index in 1...4 {
            later.append(Task {
                try await queue.run {
                    running.value += 1
                    if running.value > 1 {
                        overlap.value = true
                    }
                    try await Task.sleep(nanoseconds: 2_000_000)
                    order.value.append(index)
                    running.value -= 1
                }
            })
            await waitUntil { await queue.waitingCount == index }
        }
        gate.open()
        try await first.value
        for task in later {
            try await task.value
        }
        XCTAssertEqual(order.value, [0, 1, 2, 3, 4])
        XCTAssertFalse(overlap.value)
    }

    func testCallerCancelledWhileWaitingLeavesAtOnce() async throws {
        let queue = FluidAudioJobQueue()
        let gate = DictationFakes.Gate()
        let ran = DictationFakes.Locked<[String]>([])

        let holder = Task {
            try await queue.run {
                await gate.wait()
                ran.value.append("holder")
            }
        }
        await waitUntil { gate.arrived == 1 }
        let cancelled = Task {
            try await queue.run { ran.value.append("cancelled") }
        }
        await waitUntil { await queue.waitingCount == 1 }
        let next = Task {
            try await queue.run { ran.value.append("next") }
        }
        await waitUntil { await queue.waitingCount == 2 }

        cancelled.cancel()
        switch await cancelled.result {
        case .success:
            XCTFail("A cancelled waiter must not run")
        case let .failure(error):
            XCTAssertTrue(error is CancellationError)
        }
        let waiting = await queue.waitingCount
        XCTAssertEqual(waiting, 1)
        XCTAssertEqual(ran.value, [], "The holder is still running")

        gate.open()
        try await holder.value
        try await next.value
        XCTAssertEqual(ran.value, ["holder", "next"])
    }

    func testOfflineSwitchIsOnDuringTheJobAndRestoredAfterAFailure() async {
        for initial in [false, true] {
            ModelHub.offlineMode = initial
            do {
                _ = try await FluidAudioJobQueue().runOffline { () -> Int in
                    XCTAssertTrue(ModelHub.offlineMode)
                    throw Failure()
                }
                XCTFail("The job's error must reach the caller")
            } catch {
                XCTAssertTrue(error is Failure)
            }
            XCTAssertEqual(ModelHub.offlineMode, initial)
        }
    }

    func testFailedModelLoadRestoresTheSwitchAndNeverDownloads() async throws {
        let folder = try DiarizationFixtures.temporaryFolder("SlovoDiarizationLoad")
        defer { try? FileManager.default.removeItem(at: folder) }
        ModelHub.offlineMode = false

        do {
            _ = try await FluidSpeakerDiarizer.load(modelFolder: folder)
            XCTFail("An empty folder cannot load")
        } catch {
            let prefix = CoreStrings.format("diarization.error.loadFailed", table: .diarization, "")
            XCTAssertTrue(error.localizedDescription.hasPrefix(prefix), error.localizedDescription)
        }
        XCTAssertFalse(ModelHub.offlineMode)
        XCTAssertFalse(DiarizationModelFiles(modelFolder: folder).isComplete)
        let files = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey])?
            .compactMap { ($0 as? URL).flatMap { try? $0.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile } }
            .filter { $0 == true } ?? []
        XCTAssertTrue(files.isEmpty, "Loading must not fetch any file")
    }

    private func waitUntil(timeout: TimeInterval = 5, file: StaticString = #filePath, line: UInt = #line,
                           _ condition: () async -> Bool) async {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while ProcessInfo.processInfo.systemUptime < deadline {
            if await condition() {
                return
            }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
        XCTFail("Condition not met within \(timeout) s", file: file, line: line)
    }
}
