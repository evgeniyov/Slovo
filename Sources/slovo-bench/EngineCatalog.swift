import Foundation
import SlovoCore

enum EngineCatalog {
    struct Entry {
        let id: String
        let summary: String
    }

    static let entries: [Entry] = [
        Entry(id: "gigaam-v3-rnnt", summary: "GigaAM v3 (Сбер) с пунктуацией, transducer, через sherpa-onnx"),
        Entry(id: "gigaam-v3-ctc", summary: "GigaAM v3 (Сбер) с пунктуацией, CTC, через sherpa-onnx"),
        Entry(id: "parakeet-v3", summary: "NVIDIA Parakeet TDT 0.6B v3 через FluidAudio (Core ML)"),
        Entry(id: "apple-dictation", summary: "Apple DictationTranscriber, ru-RU; встроен в macOS 26+, ничего не скачивает"),
        Entry(id: "apple-dictation-vocab", summary: "то же с подсказками терминов из Bench/vocabulary.txt"),
        Entry(id: "whisper-large-v3-turbo",
              summary: "Whisper large-v3 turbo (q5_0) через whisper.cpp на Metal: русский, beam 5, термины из Bench/vocabulary.txt в подсказке; в приложении только помощник"),
        Entry(id: "hybrid-parakeet",
              summary: "GigaAM v3 RNNT + английские слова от Parakeet v3, как смешанная диктовка в приложении"),
        Entry(id: "hybrid-whisper",
              summary: "GigaAM v3 RNNT + английские слова от Whisper large-v3 turbo, как смешанная диктовка в приложении")
    ]

    /// Where Whisper's model file is read from: SLOVO_BENCH_WHISPER_MODEL, else the app's models folder.
    static var whisperModelFile: URL {
        if let path = ProcessInfo.processInfo.environment["SLOVO_BENCH_WHISPER_MODEL"], !path.isEmpty {
            return URL(fileURLWithPath: path)
        }
        return ModelStore.root.appendingPathComponent(EngineID.whisperTurbo.modelFolder ?? "", isDirectory: true)
            .appendingPathComponent(WhisperEngine.Model.fileName)
    }

    static func make(_ id: String, paths: BenchPaths) throws -> any TranscriptionEngine {
        switch id {
        case "gigaam-v3-rnnt":
            return GigaAMEngine(id: id, variant: .transducer)
        case "gigaam-v3-ctc":
            return GigaAMEngine(id: id, variant: .ctc)
        case "parakeet-v3":
            return ParakeetEngine(id: id)
        case "whisper-large-v3-turbo":
            return PromptedEngine(WhisperEngine(id: id, modelFile: whisperModelFile), vocabulary: try vocabulary(paths))
        case "hybrid-parakeet":
            // The app never downloads while loading; neither does the bench's helper.
            return HybridEngine(id: id, helper: ParakeetEngine(id: EngineID.parakeet.rawValue, allowsDownload: false),
                                helperID: .parakeet, vocabulary: try vocabulary(paths))
        case "hybrid-whisper":
            return HybridEngine(id: id, helper: WhisperEngine(modelFile: whisperModelFile), helperID: .whisperTurbo,
                                vocabulary: try vocabulary(paths))
        case "apple-dictation", "apple-dictation-vocab":
            guard #available(macOS 26.0, *) else {
                throw BenchError("Движку Apple нужна macOS 26 или новее")
            }
            let terms = id == "apple-dictation-vocab" ? try vocabulary(paths) : []
            return AppleDictationEngine(id: id, contextualStrings: terms)
        default:
            throw BenchError("Неизвестный движок «\(id)». Список: swift run slovo-bench engines")
        }
    }

    /// Where an engine's model files live; nil for Apple's engines, whose models macOS manages.
    static func modelDirectory(_ id: String) -> URL? {
        let gigaAM = ModelStore.root.appendingPathComponent("gigaam-v3", isDirectory: true)
        switch id {
        case "gigaam-v3-rnnt":
            return gigaAM.appendingPathComponent("sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16", isDirectory: true)
        case "gigaam-v3-ctc":
            return gigaAM.appendingPathComponent("sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16", isDirectory: true)
        case "parakeet-v3":
            return ModelStore.root.appendingPathComponent("parakeet-v3", isDirectory: true)
        default:
            return nil
        }
    }

    /// Terms from Bench/vocabulary.txt, one per line, skipping comments.
    static func vocabulary(_ paths: BenchPaths) throws -> [String] {
        try String(contentsOf: paths.vocabulary, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
    }
}
