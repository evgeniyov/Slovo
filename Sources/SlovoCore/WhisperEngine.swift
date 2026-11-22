import Foundation
import os
import whisper

/// OpenAI Whisper large-v3 turbo (q5_0) run through whisper.cpp on Metal: the
/// primary recognizer for accurate mixed dictation or its optional English helper.
///
/// Russian is forced for mixed dictation because language detection can select
/// English and translate Russian speech. The decoder uses beam search of 5 and
/// uses the user's Dictionary terms as the initial prompt.
public final class WhisperEngine: VocabularyTranscribing {
    /// The one model file, as published on Hugging Face.
    public enum Model {
        public static let fileName = "ggml-large-v3-turbo-q5_0.bin"
        public static let size: Int64 = 574_041_195
        public static let sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"
        /// Pinned to a commit of ggerganov/whisper.cpp, so the file cannot change under its checksum.
        public static let address = "https://huggingface.co/ggerganov/whisper.cpp/resolve/"
            + "5359861c739e955e79d9a303bcbc70fb988958b1/" + fileName
    }

    /// CPU threads for the parts ggml keeps off the GPU; whisper-cli's default, as evaluated.
    static let threads: Int32 = 4
    static let beamSize: Int32 = 5
    /// Candidates sampled when a window falls back to a higher temperature, as in whisper-cli.
    static let bestOf: Int32 = 5
    static let language = "ru"
    /// Characters of Dictionary terms in the prompt. Whisper reads at most 224 prompt tokens
    /// and silently drops the oldest beyond that. The character budget keeps the starter
    /// vocabulary well below that token limit with room for the user's own terms.
    static let promptBudget = 600

    public let id: String
    private let modelFile: URL
    /// Decoding holds threads and the GPU for seconds, so it runs here instead of on the
    /// Swift concurrency pool. Being serial, the queue also keeps concurrent calls off the
    /// one whisper context.
    private let queue = DispatchQueue(label: "SlovoCore.WhisperEngine", qos: .userInitiated,
                                     autoreleaseFrequency: .workItem)
    /// `whisper_context *`; accessed only on `queue` (and in `deinit`).
    private var context: OpaquePointer?
    private var warmedUp = false
    private let decodingLanguage: String

    public init(id: String = EngineID.whisperTurbo.rawValue,
                modelFile: URL = ModelStore.root.appendingPathComponent(EngineID.whisperTurbo.modelFolder ?? "",
                                                                        isDirectory: true)
                    .appendingPathComponent(Model.fileName), languageMode: LanguageMode = .mixed) {
        self.id = id
        self.modelFile = modelFile
        decodingLanguage = languageMode == .english ? "en" : Self.language
    }

    deinit {
        if let context {
            whisper_free(context)
        }
    }

    public func prepare() async throws {
        let abort = AbortFlag()
        try await withTaskCancellationHandler {
            try await onQueue {
                guard !abort.isRaised else { throw CancellationError() }
                let context = try self.loadedContext()
                guard !self.warmedUp else { return }
                // Compile the decoder kernels before the first real dictation. Silence
                // is synthetic and its output is discarded; no user's audio is read.
                _ = try Self.decode([Float](repeating: 0, count: 16_000), context: context,
                                    prompt: nil, language: self.decodingLanguage, abort: abort)
                self.warmedUp = true
            }
        } onCancel: {
            abort.raise()
        }
    }

    public func transcribe(fileURL: URL) async throws -> String {
        try await transcribe(fileURL: fileURL, vocabulary: [])
    }

    /// Transcribes with the terms as the initial prompt. Cancelling the task stops
    /// whisper.cpp at its next check: Metal looks between command buffers, so an encoder
    /// pass already running finishes first (about a second).
    public func transcribe(fileURL: URL, vocabulary: [String]) async throws -> String {
        let abort = AbortFlag()
        let prompt = Self.prompt(for: vocabulary)
        return try await withTaskCancellationHandler {
            try await onQueue {
                guard !abort.isRaised else { throw CancellationError() }
                let context = try self.loadedContext()
                let samples = try AudioLoader.samples(from: fileURL)
                return try Self.decode(samples, context: context, prompt: prompt,
                                       language: self.decodingLanguage, abort: abort)
            }
        } onCancel: {
            abort.raise()
        }
    }

    /// The prompt separates terms with commas and ends with a full stop. The newest
    /// terms take priority when the budget is tight. A term too long to fit is skipped.
    /// Returns nil if no terms fit.
    static func prompt(for terms: [String]) -> String? {
        var seen = Set<String>()
        var kept: [String] = []
        var length = 0
        for term in terms.reversed() {
            let trimmed = term.trimmingCharacters(in: .whitespacesAndNewlines)
            let count = trimmed.count
            guard count > 0, count < promptBudget,
                  seen.insert(trimmed.lowercased()).inserted else { continue }
            let added = count + (kept.isEmpty ? 1 : 2)
            guard length + added <= promptBudget else { break }
            kept.append(trimmed)
            length += added
        }
        guard !kept.isEmpty else { return nil }
        return kept.reversed().joined(separator: ", ") + "."
    }

    private func onQueue<T>(_ work: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result(catching: work))
            }
        }
    }

    /// Loads the model on first use. Call only on `queue`.
    private func loadedContext() throws -> OpaquePointer {
        if let context {
            return context
        }
        // A truncated file is refused before ggml reads it.
        let attributes = try? FileManager.default.attributesOfItem(atPath: modelFile.path)
        guard let size = attributes?[.size] as? Int64 ?? (attributes?[.size] as? Int).map(Int64.init) else {
            throw TranscriptionError(CoreStrings.format("engines.whisper.notDownloaded", table: .engines,
                                                        modelFile.deletingLastPathComponent().path))
        }
        guard size == Model.size else {
            throw TranscriptionError(CoreStrings.format("engines.whisper.damaged", table: .engines, modelFile.path))
        }
        WhisperLog.install()
        var parameters = whisper_context_default_params()
        parameters.use_gpu = true
        parameters.flash_attn = true
        guard let created = modelFile.path.withCString({ whisper_init_from_file_with_params($0, parameters) }) else {
            throw TranscriptionError(CoreStrings.format("engines.whisper.loadFailed", table: .engines, modelFile.path))
        }
        context = created
        return created
    }

    private static func decode(_ samples: [Float], context: OpaquePointer, prompt: String?, language: String,
                               abort: AbortFlag) throws -> String {
        guard !samples.isEmpty else { return "" }
        var parameters = whisper_full_default_params(WHISPER_SAMPLING_BEAM_SEARCH)
        parameters.n_threads = threads
        parameters.translate = false
        parameters.detect_language = false
        parameters.no_timestamps = true
        // Every dictation is independent. Do not retain earlier transcripts as context.
        parameters.no_context = true
        parameters.print_progress = false
        parameters.print_realtime = false
        parameters.print_timestamps = false
        parameters.print_special = false
        parameters.beam_search.beam_size = beamSize
        parameters.greedy.best_of = bestOf
        let flag = Unmanaged.passUnretained(abort).toOpaque()
        parameters.abort_callback = { data in AbortFlag.isRaised(data) }
        parameters.abort_callback_user_data = flag
        parameters.encoder_begin_callback = { _, _, data in !AbortFlag.isRaised(data) }
        parameters.encoder_begin_callback_user_data = flag

        let status: Int32 = withExtendedLifetime(abort) {
            language.withCString { language in
                parameters.language = language
                return withOptionalCString(prompt) { prompt in
                    parameters.initial_prompt = prompt
                    return samples.withUnsafeBufferPointer { buffer in
                        whisper_full(context, parameters, buffer.baseAddress, Int32(buffer.count))
                    }
                }
            }
        }
        if abort.isRaised {
            throw CancellationError()
        }
        guard status == 0 else {
            throw TranscriptionError(CoreStrings.format("engines.whisper.transcribeFailed", table: .engines, Int(status)))
        }
        var segments: [String] = []
        for index in 0..<whisper_full_n_segments(context) {
            guard let text = whisper_full_get_segment_text(context, index) else { continue }
            segments.append(String(cString: text))
        }
        return segments.joined(separator: " ").split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }
}

/// Raised when the transcribing task is cancelled; whisper.cpp polls it between steps.
private final class AbortFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var raised = false

    var isRaised: Bool {
        lock.withLock { raised }
    }

    func raise() {
        lock.withLock { raised = true }
    }

    static func isRaised(_ pointer: UnsafeMutableRawPointer?) -> Bool {
        guard let pointer else { return false }
        return Unmanaged<AbortFlag>.fromOpaque(pointer).takeUnretainedValue().isRaised
    }
}

/// whisper.cpp and ggml print every load step to stderr; only their errors reach Slovo's log.
private enum WhisperLog {
    private static let logger = SlovoLog.logger("Whisper")
    private static let installation: Void = {
        whisper_log_set({ level, text, _ in
            guard level == GGML_LOG_LEVEL_ERROR, let text else { return }
            let message = String(cString: text).trimmingCharacters(in: .whitespacesAndNewlines)
            // The prebuilt framework looks for an optional Core ML encoder first and runs on
            // Metal without one; that miss is expected, not an error.
            guard !message.isEmpty, !message.contains("Core ML") else { return }
            WhisperLog.logger.notice("whisper.cpp: \(message, privacy: .public)")
        }, nil)
    }()

    static func install() {
        _ = installation
    }
}

private func withOptionalCString<T>(_ string: String?, _ body: (UnsafePointer<CChar>?) -> T) -> T {
    guard let string else { return body(nil) }
    return string.withCString { body($0) }
}
