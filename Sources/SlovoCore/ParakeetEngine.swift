import FluidAudio
import Foundation

/// NVIDIA Parakeet TDT 0.6B v3, run through FluidAudio on Core ML.
///
/// By default the first prepare() downloads about 480 MB of Core ML files from Hugging
/// Face into modelsDirectory; after that the models load from disk without the network.
public final class ParakeetEngine: TranscriptionEngine {
    public let id: String
    private let runtime: ParakeetRuntime

    /// - Parameter allowsDownload: when false, missing files are an error instead of a
    ///   download. The app passes false and downloads only from the Download button.
    public init(id: String = "parakeet-v3",
                modelsDirectory: URL = ModelStore.root.appendingPathComponent("parakeet-v3", isDirectory: true),
                allowsDownload: Bool = true) {
        self.id = id
        self.runtime = ParakeetRuntime(files: ParakeetModelFiles(modelsDirectory: modelsDirectory,
                                                                 allowsDownload: allowsDownload))
    }

    public func prepare() async throws {
        try await runtime.prepare()
    }

    public func transcribe(fileURL: URL) async throws -> String {
        let samples = try AudioLoader.samples(from: fileURL)
        return try await runtime.transcribe(samples)
    }

    /// Downloads the model files without loading them, reporting progress from 0 to 1 on
    /// any thread. Files already on disk are kept and interrupted ones resume.
    public static func downloadModels(to modelsDirectory: URL,
                                      progress: (@Sendable (Double) -> Void)? = nil) async throws {
        let files = ParakeetModelFiles(modelsDirectory: modelsDirectory, allowsDownload: true)
        try await FluidAudioJobQueue.shared.run {
            try await files.download(progress: progress)
        }
    }

    /// True when every file the engine loads is in modelsDirectory and no download was
    /// left half-finished.
    public static func modelFilesComplete(in modelsDirectory: URL) -> Bool {
        ParakeetModelFiles(modelsDirectory: modelsDirectory, allowsDownload: false).isComplete
    }
}

/// Owns the loaded models of one engine.
///
/// Loading reads FluidAudio's process-wide offline switch, so it queues on
/// `FluidAudioJobQueue.shared` behind downloads. Transcribing a loaded model touches
/// neither the switch nor the network, so it queues only behind this engine's own
/// transcriptions: a speaker-model download holding the shared queue for minutes never
/// keeps a dictation waiting.
private actor ParakeetRuntime {
    private let files: ParakeetModelFiles
    private var manager: AsrManager?
    /// AsrManager suspends inside transcribe, and overlapping calls would interleave on its
    /// shared buffers, so this engine's transcriptions run one at a time.
    private let transcriptions = FluidAudioJobQueue()

    init(files: ParakeetModelFiles) {
        self.files = files
    }

    func prepare() async throws {
        try await FluidAudioJobQueue.shared.run {
            _ = try await self.loadedManager()
        }
    }

    func transcribe(_ samples: [Float]) async throws -> String {
        guard !samples.isEmpty else { return "" }
        if manager == nil {
            // Callers normally prepare first; loading goes through the shared queue.
            try await prepare()
        }
        return try await transcriptions.run {
            try await self.transcribeNow(samples)
        }
    }

    private func transcribeNow(_ samples: [Float]) async throws -> String {
        guard let manager else {
            throw TranscriptionError(CoreStrings.format("engines.parakeet.notDownloaded", table: .engines,
                                                        files.modelsDirectory.path))
        }

        // FluidAudio rejects clips shorter than 0.3 s; a single short word is still
        // worth decoding, so pad it with silence instead.
        var audio = samples
        let minimum = ASRConstants.minimumRequiredSamples(forSampleRate: ASRConstants.sampleRate)
        if audio.count < minimum {
            audio += [Float](repeating: 0, count: minimum - audio.count)
        }

        let layers = await manager.decoderLayerCount
        var decoderState = try TdtDecoderState(decoderLayers: layers)
        do {
            // Audio over 15 s is split by FluidAudio into overlapping windows and merged back.
            let result = try await manager.transcribe(audio, decoderState: &decoderState)
            return result.text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            try rethrowIfCancelled(error)
            throw TranscriptionError(CoreStrings.format("engines.parakeet.transcribeFailed", table: .engines,
                                                        error.localizedDescription))
        }
    }

    private func loadedManager() async throws -> AsrManager {
        if let manager {
            return manager
        }
        let models = try await files.load()
        let loaded = AsrManager(config: .default, models: models)
        manager = loaded
        return loaded
    }
}

/// Cancellation can surface as URLError.cancelled or another library error instead of
/// CancellationError; callers should see all of them as cancellation.
private func rethrowIfCancelled(_ error: Error) throws {
    if error is CancellationError || Task.isCancelled {
        throw CancellationError()
    }
}

/// The Core ML files FluidAudio needs for Parakeet v3.
///
/// FluidAudio always keeps them in a folder named after the Hugging Face repository,
/// so they end up in modelsDirectory/parakeet-tdt-0.6b-v3.
private struct ParakeetModelFiles: Sendable {
    /// The encoder FluidAudio uses by default; other precisions are never downloaded.
    private static let precision = ParakeetEncoderPrecision.int8

    let modelsDirectory: URL
    let allowsDownload: Bool

    private var repoDirectory: URL {
        modelsDirectory.appendingPathComponent(Repo.parakeetV3.folderName, isDirectory: true)
    }

    func load() async throws -> AsrModels {
        if !isComplete {
            guard allowsDownload else {
                throw TranscriptionError(CoreStrings.format("engines.parakeet.notDownloaded", table: .engines,
                                                            repoDirectory.path))
            }
            try await download(progress: nil)
        }

        // With the files present, FluidAudio would delete and re-download them if Core ML
        // fails to load; offline mode turns that into an error instead.
        let wasOffline = ModelHub.offlineMode
        ModelHub.offlineMode = true
        defer { ModelHub.offlineMode = wasOffline }
        do {
            return try await AsrModels.load(from: repoDirectory, version: .v3, encoderPrecision: Self.precision)
        } catch {
            try rethrowIfCancelled(error)
            throw TranscriptionError(CoreStrings.format("engines.parakeet.loadFailed", table: .engines,
                                                        repoDirectory.path, error.localizedDescription))
        }
    }

    /// Fetches only the four compiled models this precision needs and the vocabulary;
    /// files already on disk are kept, and interrupted ones resume.
    func download(progress: (@Sendable (Double) -> Void)?) async throws {
        var handler: ProgressHandler?
        if let progress {
            handler = { update in
                progress(update.fractionCompleted)
            }
        }
        do {
            try await ModelHub.download(.parakeetV3, to: modelsDirectory, variant: Self.precision.rawValue,
                                        progressHandler: handler)
        } catch {
            try rethrowIfCancelled(error)
            throw TranscriptionError(CoreStrings.format("engines.parakeet.downloadFailed", table: .engines,
                                                        error.localizedDescription))
        }
        guard isComplete else {
            throw TranscriptionError(CoreStrings.format("engines.parakeet.filesMissingAfterDownload",
                                                        table: .engines, repoDirectory.path))
        }
    }

    /// All required files exist and no download was left half-finished.
    var isComplete: Bool {
        guard AsrModels.modelsExist(at: repoDirectory, version: .v3, encoderPrecision: Self.precision) else {
            return false
        }
        guard let items = FileManager.default.enumerator(at: repoDirectory, includingPropertiesForKeys: nil) else {
            return false
        }
        for case let item as URL in items where item.pathExtension == "partial" {
            return false
        }
        return true
    }
}
