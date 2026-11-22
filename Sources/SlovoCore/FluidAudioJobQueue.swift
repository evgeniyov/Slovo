import FluidAudio
import Foundation

/// Runs FluidAudio jobs one at a time, in arrival order.
///
/// `ModelHub.offlineMode` is a process-wide switch: a load turns it on so a Core ML
/// failure is an error instead of a re-download, and a download needs it off for every
/// request it makes, since FluidAudio checks the switch per request. Every job that reads
/// or flips it therefore queues on `shared`: Parakeet and speaker model loads and
/// downloads. A download holds `shared` until its last request, so loads wait for it.
///
/// Work that never touches the switch stays off `shared`, so a download cannot hold it
/// up: a loaded Parakeet engine serializes its transcriptions on a private instance
/// (AsrManager suspends inside transcribe and overlapping calls would interleave on its
/// shared buffers), and one diarizer serializes its speaker separation runs the same way.
actor FluidAudioJobQueue {
    static let shared = FluidAudioJobQueue()

    private struct Waiter {
        let id: Int
        let continuation: CheckedContinuation<Void, Error>
    }

    private var isBusy = false
    private var nextWaiterID = 0
    private var waiters: [Waiter] = []

    /// Callers queued behind the running job; for tests.
    var waitingCount: Int {
        waiters.count
    }

    func run<T: Sendable>(_ job: @Sendable () async throws -> T) async throws -> T {
        try await acquire()
        defer { release() }
        try Task.checkCancellation()
        return try await job()
    }

    /// Runs `job` with FluidAudio's offline switch on and restores the previous value
    /// afterwards, also when `job` throws, so nothing in the job can reach the network.
    func runOffline<T: Sendable>(_ job: @Sendable () async throws -> T) async throws -> T {
        try await run {
            let wasOffline = ModelHub.offlineMode
            ModelHub.offlineMode = true
            defer { ModelHub.offlineMode = wasOffline }
            return try await job()
        }
    }

    /// A caller cancelled while waiting leaves at once instead of sitting behind
    /// a long recording or a model download.
    private func acquire() async throws {
        guard isBusy else {
            isBusy = true
            return
        }
        let id = nextWaiterID
        nextWaiterID += 1
        try await withTaskCancellationHandler(operation: {
            try await self.wait(as: id)
        }, onCancel: {
            Task { await self.cancelWaiter(id) }
        })
    }

    private func wait(as id: Int) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            if Task.isCancelled {
                continuation.resume(throwing: CancellationError())
            } else {
                waiters.append(Waiter(id: id, continuation: continuation))
            }
        }
    }

    /// Hands the queue straight to the next waiter, so no new caller can slip in between.
    private func release() {
        if waiters.isEmpty {
            isBusy = false
        } else {
            waiters.removeFirst().continuation.resume()
        }
    }

    private func cancelWaiter(_ id: Int) {
        guard let index = waiters.firstIndex(where: { $0.id == id }) else { return }
        waiters.remove(at: index).continuation.resume(throwing: CancellationError())
    }
}
