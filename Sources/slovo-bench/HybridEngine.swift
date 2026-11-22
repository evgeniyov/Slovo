import AVFoundation
import Foundation
import SlovoCore

/// GigaAM v3 RNNT with an English helper, the way mixed dictation runs in the app: the
/// app's own `MixedLanguageHelper` decides whether the helper runs, waits for it no longer
/// than a dictation would and merges its English in. Both models load in `prepare`, so the
/// times are those of a warm app; the difference to `gigaam-v3-rnnt` is the helper's cost.
final class HybridEngine: TranscriptionEngine {
    let id: String
    private let russian: GigaAMEngine
    private let helper: any TranscriptionEngine
    private let helperID: EngineID
    private let vocabulary: [String]

    init(id: String, helper: any TranscriptionEngine, helperID: EngineID, vocabulary: [String]) {
        self.id = id
        self.russian = GigaAMEngine(id: EngineID.gigaamRNNT.rawValue, variant: .transducer)
        self.helper = helper
        self.helperID = helperID
        self.vocabulary = vocabulary
    }

    func prepare() async throws {
        try await russian.prepare()
        try await helper.prepare()
    }

    func transcribe(fileURL: URL) async throws -> String {
        let text = try await russian.transcribe(fileURL: fileURL)
        let file = try AVAudioFile(forReading: fileURL)
        let duration = Double(file.length) / file.fileFormat.sampleRate
        let result = await improve(text, audio: fileURL, duration: duration)
        switch result.outcome {
        case .failed, .timedOut, .cancelled:
            FileHandle.standardError.write(Data("  \(fileURL.lastPathComponent): помощник не помог (\(result.outcome))\n".utf8))
        case .notApplicable, .notNeeded, .merged:
            break
        }
        return result.text
    }

    @MainActor
    private func improve(_ text: String, audio: URL, duration: TimeInterval) async -> MixedLanguageHelper.Result {
        // Registered values live only in memory, so the bench writes no preferences.
        let defaults = UserDefaults(suiteName: "local.slovo.bench.hybrid") ?? .standard
        let choice: EnglishHelperChoice = helperID == .whisperTurbo ? .whisper : .parakeet
        defaults.register(defaults: [SettingsKey.englishHelper: choice.rawValue])
        let helperID = self.helperID
        let mixed = MixedLanguageHelper(engines: BenchHelperEngines(helper: helper, helperID: helperID),
                                        settings: AppSettings(defaults: defaults),
                                        status: { $0 == helperID ? .ready(diskBytes: 0) : .notDownloaded(downloadBytes: 0) })
        return await mixed.run(text, audio: audio, duration: duration, engine: .gigaamRNNT, languageMode: .mixed,
                               vocabulary: vocabulary)
    }
}

/// Hands Bench/vocabulary.txt to an engine that takes expected terms, as the app hands it
/// the Dictionary.
final class PromptedEngine: TranscriptionEngine {
    let id: String
    private let engine: any VocabularyTranscribing
    private let vocabulary: [String]

    init(_ engine: any VocabularyTranscribing, vocabulary: [String]) {
        self.id = engine.id
        self.engine = engine
        self.vocabulary = vocabulary
    }

    func prepare() async throws {
        try await engine.prepare()
    }

    func transcribe(fileURL: URL) async throws -> String {
        try await engine.transcribe(fileURL: fileURL, vocabulary: vocabulary)
    }
}

/// Hands `MixedLanguageHelper` the helper the bench already loaded.
@MainActor
private final class BenchHelperEngines: EngineProviding {
    private let helper: any TranscriptionEngine
    private let helperID: EngineID

    init(helper: any TranscriptionEngine, helperID: EngineID) {
        self.helper = helper
        self.helperID = helperID
    }

    func engineID(for purpose: EnginePurpose) -> EngineID {
        .gigaamRNNT
    }

    func loadState(of id: EngineID) -> EngineLoadState {
        id == helperID ? .ready : .unloaded
    }

    func engine(for id: EngineID) async throws -> any TranscriptionEngine {
        guard id == helperID else {
            throw BenchError("Помощник \(helperID.rawValue) не просил \(id.rawValue)")
        }
        return helper
    }

    func preloadForCurrentSettings() {}

    func markUsed(_ id: EngineID) {}
}
