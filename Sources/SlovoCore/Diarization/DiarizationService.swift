import Combine
import FluidAudio
import Foundation

/// The speaker model on disk and the diarizer loaded from it.
///
/// The model is downloaded only when the user presses Download, and loading never
/// downloads: a missing or damaged model is an error that the meeting pipeline turns
/// into fallback speakers. The loaded diarizer is dropped after every meeting run,
/// because meetings are rare and Core ML state is large.
@MainActor
public final class DiarizationService: ObservableObject, DiarizationProviding, DiarizationModelManaging {
    /// The 23 files of the offline variant of FluidInference/speaker-diarization-coreml.
    nonisolated public static let downloadBytes: Int64 = 21_776_918
    /// Recorded in `MeetingDiarization.model`.
    nonisolated public static let modelIdentifier = "pyannote-community-1-coreml@1ed7a66"

    @Published public private(set) var status: ModelStatus = .unknown
    @Published public private(set) var lastError: String?
    @Published public private(set) var loadState: EngineLoadState = .unloaded

    public let modelsRoot: URL

    private let downloader: DiarizationModelDownloading
    private let factory: @MainActor (URL) async throws -> any SpeakerDiarizing
    private let systemSupported: Bool
    private let log = SlovoLog.logger("Diarization")

    private var download: Task<Void, Never>?
    private var loaded: (any SpeakerDiarizing)?
    private var load: Task<any SpeakerDiarizing, Error>?
    /// Identifies `load`, so a caller of an earlier load cannot cancel a newer one.
    private var loadID = 0
    /// Callers waiting for a load: which load, and whether the caller's task was cancelled.
    private var loadCallers: [Int: (load: Int, cancelled: Bool)] = [:]
    private var nextLoadCaller = 0
    /// Set once every caller of the running load was cancelled and the load with them.
    private var loadAbandoned = false
    /// Bumped by `release()`, so a load that finishes afterwards is not kept.
    private var loadGeneration = 0
    /// Callers inside `waitForLoad()`. The files are about to be deleted, so no new load
    /// may start meanwhile.
    private var drainingCount = 0

    /// - Parameters:
    ///   - downloader: fetches the model files into `modelFolder`; tests pass a fake.
    ///   - factory: loads a diarizer from `modelFolder`; tests pass a fake.
    public convenience init(modelsRoot: URL = ModelStore.root,
                            downloader: DiarizationModelDownloading = LiveDiarizationModelDownloader(),
                            factory: @escaping @MainActor (URL) async throws -> any SpeakerDiarizing = FluidSpeakerDiarizer.load) {
        self.init(modelsRoot: modelsRoot, downloader: downloader, factory: factory,
                  systemSupported: Self.systemSupportsSeparation)
    }

    /// Lets tests play an older macOS.
    init(modelsRoot: URL, downloader: DiarizationModelDownloading,
         factory: @escaping @MainActor (URL) async throws -> any SpeakerDiarizing, systemSupported: Bool) {
        self.modelsRoot = modelsRoot
        self.downloader = downloader
        self.factory = factory
        self.systemSupported = systemSupported
        refresh()
    }

    /// `modelsRoot/diarization`; FluidAudio keeps the files in `speaker-diarization/` inside it
    /// (the repository name without its `-coreml` suffix).
    public var modelFolder: URL {
        modelsRoot.appendingPathComponent("diarization", isDirectory: true)
    }

    public var modelStatus: ModelStatus {
        status
    }

    /// macOS 14 has a BNNS bug that crashes offline diarization; macOS 15 fixed it.
    public var isSupportedOnThisSystem: Bool {
        systemSupported
    }

    nonisolated static var systemSupportsSeparation: Bool {
        ProcessInfo.processInfo.isOperatingSystemAtLeast(
            OperatingSystemVersion(majorVersion: 15, minorVersion: 0, patchVersion: 0))
    }

    // MARK: - Files

    /// Re-reads the model folder; a running download keeps its progress.
    public func refresh() {
        guard download == nil else { return }
        setStatus(DiarizationModelFiles(modelFolder: modelFolder).status)
    }

    public func startDownload() {
        guard systemSupported, download == nil else { return }
        refresh()
        if case .ready = status {
            return
        }
        lastError = nil
        setStatus(.downloading(progress: 0))
        log.info("Downloading the speaker model")
        let downloader = self.downloader
        let folder = modelFolder
        let report: @Sendable (Double) -> Void = { [weak self] fraction in
            Task { @MainActor in
                self?.updateProgress(fraction)
            }
        }
        download = Task { [weak self] in
            do {
                try await downloader.download(to: folder, progress: report)
                self?.downloadFinished(error: nil)
            } catch {
                self?.downloadFinished(error: error)
            }
        }
    }

    public func cancelDownload() {
        guard let download else { return }
        log.info("Cancelling the speaker model download")
        download.cancel()
    }

    /// Removes `modelFolder`. Refused while a download runs; callers release the
    /// diarizer and wait for a running load first.
    public func delete() throws {
        guard download == nil else {
            let message = CoreStrings.string("diarization.error.deleteWhileDownloading", table: .diarization)
            lastError = message
            throw SlovoError(message)
        }
        release()
        let fileManager = FileManager.default
        do {
            if fileManager.fileExists(atPath: modelFolder.path) {
                try fileManager.removeItem(at: modelFolder)
            }
        } catch {
            lastError = error.localizedDescription
            refresh()
            log.error("Could not delete the speaker model: \(error.localizedDescription, privacy: .public)")
            throw error
        }
        log.info("Deleted the speaker model")
        lastError = nil
        refresh()
    }

    /// Waits for a running download to end; for tests.
    func waitForDownload() async {
        await download?.value
    }

    // MARK: - Loading

    public func diarizer() async throws -> any SpeakerDiarizing {
        guard systemSupported else {
            throw SlovoError(CoreStrings.string("diarization.error.unsupportedSystem", table: .diarization))
        }
        // A load every caller gave up on ends soon with a cancellation that is not this
        // caller's, so it is waited out and the model loaded again.
        while loaded == nil, drainingCount == 0, loadAbandoned, let running = load {
            _ = try? await running.value
            try Task.checkCancellation()
        }
        if let loaded {
            return loaded
        }
        // Checked before joining a running load: a caller that joined would get models
        // whose files are deleted as soon as the load ends.
        guard drainingCount == 0 else {
            throw SlovoError(CoreStrings.string("diarization.error.modelMissing", table: .diarization))
        }
        if let load {
            return try await wait(for: load, loadID: loadID)
        }
        refresh()
        switch status {
        case .ready:
            break
        case .damaged:
            throw SlovoError(CoreStrings.string("diarization.error.modelDamaged", table: .diarization))
        default:
            throw SlovoError(CoreStrings.string("diarization.error.modelMissing", table: .diarization))
        }

        let generation = loadGeneration
        let folder = modelFolder
        let factory = self.factory
        loadState = .loading
        loadID += 1
        loadAbandoned = false
        // The task records its own outcome, so callers that join a running load and a
        // caller that stops waiting leave the same state behind.
        let task = Task { @MainActor [weak self] () throws -> any SpeakerDiarizing in
            do {
                let diarizer = try await factory(folder)
                self?.loadFinished(generation: generation, diarizer: diarizer, error: nil)
                return diarizer
            } catch {
                self?.loadFinished(generation: generation, diarizer: nil, error: error)
                throw error
            }
        }
        load = task
        return try await wait(for: task, loadID: loadID)
    }

    /// Waits for a load on behalf of one caller. The load runs in its own task, which a
    /// caller's cancellation does not reach, so once every caller of a load is cancelled
    /// the load is cancelled too. A meeting run cancelled while the model waits for the
    /// shared FluidAudio queue, behind a model download for example, then ends at once.
    private func wait(for task: Task<any SpeakerDiarizing, Error>, loadID: Int) async throws -> any SpeakerDiarizing {
        let caller = nextLoadCaller
        nextLoadCaller += 1
        loadCallers[caller] = (loadID, false)
        defer { loadCallers[caller] = nil }
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.loadCallerCancelled(caller)
            }
        }
    }

    private func loadCallerCancelled(_ caller: Int) {
        guard let entry = loadCallers[caller], entry.load == loadID, let load else { return }
        loadCallers[caller]?.cancelled = true
        let stillWaiting = loadCallers.values.contains { $0.load == loadID && !$0.cancelled }
        guard !stillWaiting else { return }
        log.info("Cancelling the speaker model load: nobody waits for it")
        loadAbandoned = true
        load.cancel()
    }

    public func release() {
        loadGeneration += 1
        loaded = nil
        if load == nil, loadState != .unloaded {
            loadState = .unloaded
        }
    }

    public func waitForLoad() async {
        drainingCount += 1
        defer { drainingCount -= 1 }
        while let load {
            _ = try? await load.value
        }
    }

    // MARK: - Private

    private func loadFinished(generation: Int, diarizer: (any SpeakerDiarizing)?, error: Error?) {
        load = nil
        guard generation == loadGeneration else {
            loadState = .unloaded
            return
        }
        if let diarizer {
            loaded = diarizer
            loadState = .ready
        } else if let error {
            if Self.isCancellation(error) {
                loadState = .unloaded
            } else {
                loadState = .failed(error.localizedDescription)
                log.error("Loading the speaker model failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    private func updateProgress(_ fraction: Double) {
        // Late updates can arrive after the download finished.
        guard download != nil, case let .downloading(current) = status else { return }
        let rounded = (min(1, max(0, fraction)) * 100).rounded() / 100
        if rounded > current {
            status = .downloading(progress: rounded)
        }
    }

    private func downloadFinished(error: Error?) {
        download = nil
        if let error {
            if Self.isCancellation(error) {
                log.info("The speaker model download was cancelled")
            } else {
                lastError = (error as? SlovoError)?.message
                    ?? CoreStrings.format("diarization.error.downloadFailed", table: .diarization, error.localizedDescription)
                log.error("Downloading the speaker model failed: \(error.localizedDescription, privacy: .public)")
            }
        } else {
            log.info("Downloaded the speaker model")
        }
        refresh()
    }

    private func setStatus(_ newStatus: ModelStatus) {
        if status != newStatus {
            status = newStatus
        }
    }

    private static func isCancellation(_ error: Error) -> Bool {
        error is CancellationError || (error as? URLError)?.code == .cancelled
    }
}

/// Fetches the speaker model files.
public protocol DiarizationModelDownloading: Sendable {
    /// Downloads into `folder`, reporting progress from 0 to 1 on any thread.
    func download(to folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws
}

/// Downloads through FluidAudio's `ModelHub` inside the shared FluidAudio job queue,
/// then checks that every file arrived.
public struct LiveDiarizationModelDownloader: DiarizationModelDownloading {
    /// A repository download reports its own part as 0…0.5 and leaves the rest to model
    /// compilation, which only a load does.
    private static let downloadShareOfReportedProgress = 0.5

    public init() {}

    public func download(to folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
        try await FluidAudioJobQueue.shared.run {
            do {
                try await ModelHub.download(.diarizer, to: folder, variant: "offline", progressHandler: { update in
                    progress(min(1, max(0, update.fractionCompleted / Self.downloadShareOfReportedProgress)))
                })
            } catch {
                if error is CancellationError || (error as? URLError)?.code == .cancelled || Task.isCancelled {
                    throw CancellationError()
                }
                throw SlovoError(CoreStrings.format("diarization.error.downloadFailed", table: .diarization,
                                                    error.localizedDescription))
            }
            guard DiarizationModelFiles(modelFolder: folder).isComplete else {
                throw SlovoError(CoreStrings.string("diarization.error.filesMissingAfterDownload", table: .diarization))
            }
        }
    }
}
