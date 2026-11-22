import FluidAudio
import Foundation

/// FluidAudio's offline pyannote community-1 pipeline behind `SpeakerDiarizing`.
///
/// Models load once with FluidAudio's offline switch on, so a Core ML load failure is an
/// error instead of a re-download. Every run builds its own manager, because a manager's
/// configuration is fixed when it is created, and hands it the loaded models before
/// processing: a manager without models would fetch and purge files on its own.
public final class FluidSpeakerDiarizer: SpeakerDiarizing, @unchecked Sendable {
    private static let log = SlovoLog.logger("Diarization")

    private let models: OfflineDiarizerModels
    /// One run at a time on this instance. A run of an hour holds about 230 MB of
    /// samples on disk and its embeddings in memory, and a second one gains nothing.
    private let runs = FluidAudioJobQueue()

    init(models: OfflineDiarizerModels) {
        self.models = models
    }

    /// Loads the Core ML models from `DiarizationService.modelFolder`. Never downloads.
    public static func load(modelFolder: URL) async throws -> FluidSpeakerDiarizer {
        do {
            // Loading reads the offline switch, so it waits for Parakeet jobs and downloads.
            let models = try await FluidAudioJobQueue.shared.runOffline {
                try await OfflineDiarizerModels.load(from: modelFolder)
            }
            log.info("Loaded the speaker model in \(String(format: "%.2f", models.compilationDuration), privacy: .public) s")
            return FluidSpeakerDiarizer(models: models)
        } catch {
            if isCancellation(error) {
                throw CancellationError()
            }
            log.error("Loading the speaker model failed: \(error.localizedDescription, privacy: .public)")
            throw SlovoError(CoreStrings.format("diarization.error.loadFailed", table: .diarization,
                                                error.localizedDescription))
        }
    }

    /// Diarizes with `DiarizationTuning.standard`.
    public func diarize(fileAt url: URL, hint: SpeakerCountHint,
                        progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn] {
        try await run(fileAt: url, tuning: .standard, hint: hint, progress: progress).turns
    }

    /// The evaluation's run: turns for a chosen tuning plus per-cluster centroids and
    /// stage timings. Centroids exist only in the returned value.
    public func diagnose(fileAt url: URL, config: DiarizationTuning,
                         hint: SpeakerCountHint = .automatic) async throws -> DiarizationDiagnosis {
        try await run(fileAt: url, tuning: config, hint: hint, progress: { _ in })
    }

    // MARK: - Running

    private func run(fileAt url: URL, tuning: DiarizationTuning, hint: SpeakerCountHint,
                     progress: @escaping @Sendable (Double) -> Void) async throws -> DiarizationDiagnosis {
        try Task.checkCancellation()
        let models = self.models
        // The run never touches the offline switch, so it stays off the shared queue.
        let diagnosis = try await runs.run {
            try await Self.process(fileAt: url, models: models, tuning: tuning, hint: hint, progress: progress)
        }
        try Task.checkCancellation()
        return diagnosis
    }

    private static func process(fileAt url: URL, models: OfflineDiarizerModels, tuning: DiarizationTuning,
                                hint: SpeakerCountHint,
                                progress: @escaping @Sendable (Double) -> Void) async throws -> DiarizationDiagnosis {
        let config = try tuning.offlineConfig(for: hint)
        let manager = OfflineDiarizerManager(config: config)
        manager.initialize(models: models)

        let started = ProcessInfo.processInfo.systemUptime
        let result: DiarizationResult
        do {
            result = try await manager.process(url, progressCallback: { processed, total in
                progress(min(1, max(0, Double(processed) / Double(max(total, 1)))))
            })
        } catch OfflineDiarizationError.noSpeechDetected {
            // Silence is a valid track: it simply has no speakers.
            progress(1)
            return DiarizationDiagnosis(turns: [], centroids: [:],
                                        timings: ["wall": ProcessInfo.processInfo.systemUptime - started])
        } catch {
            if isCancellation(error) {
                throw CancellationError()
            }
            throw error
        }
        progress(1)

        var centroids: [Int: [Float]] = [:]
        for (speakerID, embedding) in result.speakerDatabase ?? [:] {
            if let cluster = cluster(forSpeakerID: speakerID) {
                centroids[cluster] = embedding
            }
        }
        return DiarizationDiagnosis(turns: turns(from: result.segments), centroids: centroids,
                                    timings: timings(result.timings, models: models,
                                                     wall: ProcessInfo.processInfo.systemUptime - started))
    }

    // MARK: - Mapping

    /// `"S<k>"` becomes cluster `k − 1`; segments with other ids or empty spans are
    /// skipped. Sorted by start.
    static func turns(from segments: [TimedSpeakerSegment]) -> [SpeakerTurn] {
        var turns: [SpeakerTurn] = []
        var unknownIDs = 0
        for segment in segments {
            guard let cluster = cluster(forSpeakerID: segment.speakerId) else {
                unknownIDs += 1
                continue
            }
            let start = Double(segment.startTimeSeconds)
            let end = Double(segment.endTimeSeconds)
            guard start.isFinite, end.isFinite, end > start else { continue }
            turns.append(SpeakerTurn(start: start, end: end, cluster: cluster))
        }
        if unknownIDs > 0 {
            log.error("Skipped \(unknownIDs, privacy: .public) diarization segments with an unexpected speaker id")
        }
        return turns.sorted { ($0.start, $0.end, $0.cluster) < ($1.start, $1.end, $1.cluster) }
    }

    static func cluster(forSpeakerID id: String) -> Int? {
        let digits = id.dropFirst()
        guard id.first == "S", !digits.isEmpty, digits.allSatisfy({ $0.isASCII && $0.isNumber }),
              let number = Int(digits), number >= 1 else { return nil }
        return number - 1
    }

    private static func timings(_ pipeline: PipelineTimings?, models: OfflineDiarizerModels,
                                wall: TimeInterval) -> [String: TimeInterval] {
        var values: [String: TimeInterval] = ["wall": wall, "modelLoad": models.compilationDuration]
        if let pipeline {
            values["audioLoading"] = pipeline.audioLoadingSeconds
            values["segmentation"] = pipeline.segmentationSeconds
            values["embedding"] = pipeline.embeddingExtractionSeconds
            values["clustering"] = pipeline.speakerClusteringSeconds
            values["postProcessing"] = pipeline.postProcessingSeconds
        }
        return values
    }

    /// Cancellation can surface as URLError.cancelled or a library error instead of
    /// CancellationError; callers should see all of them as cancellation.
    private static func isCancellation(_ error: Error) -> Bool {
        error is CancellationError || (error as? URLError)?.code == .cancelled || Task.isCancelled
    }
}

/// What `FluidSpeakerDiarizer.diagnose` measured on one file.
public struct DiarizationDiagnosis: Sendable {
    public var turns: [SpeakerTurn]
    /// Mean embedding of each cluster, keyed by `SpeakerTurn.cluster`.
    public var centroids: [Int: [Float]]
    /// Wall-clock seconds per pipeline stage, keyed by stage name: `wall` for the whole
    /// run, `modelLoad` for the diarizer's load, and FluidAudio's `audioLoading`,
    /// `segmentation`, `embedding`, `clustering` and `postProcessing`.
    public var timings: [String: TimeInterval]

    public init(turns: [SpeakerTurn], centroids: [Int: [Float]], timings: [String: TimeInterval]) {
        self.turns = turns
        self.centroids = centroids
        self.timings = timings
    }
}
