import Foundation
import os

/// Sber GigaAM v3 with punctuation, run through sherpa-onnx.
///
/// Both variants are the end-to-end models: they write punctuation and spell
/// English terms in Latin script.
public final class GigaAMEngine: TranscriptionEngine {
    public enum Variant: String, Sendable {
        case transducer
        case ctc
    }

    /// ONNX Runtime threads per decode. On an M4 (4 performance cores) 4 was fastest
    /// for both variants: 6 spills onto efficiency cores and is about 10% slower,
    /// 2 is about 20% slower and 1 about twice as slow.
    static let threads = 4

    public let id: String
    private let variant: Variant
    private let modelsDirectory: URL
    /// Decoding holds a thread for seconds, so it runs here instead of on the Swift
    /// concurrency pool. Being serial, the queue is also what keeps concurrent calls
    /// from using the recognizer at the same time.
    private let queue = DispatchQueue(label: "SlovoCore.GigaAMEngine", qos: .utility)
    /// Accessed only on `queue`.
    private var recognizer: GigaAMRecognizer?

    public init(id: String, variant: Variant,
                modelsDirectory: URL = ModelStore.root.appendingPathComponent("gigaam-v3", isDirectory: true)) {
        self.id = id
        self.variant = variant
        self.modelsDirectory = modelsDirectory
    }

    public func prepare() async throws {
        try await onQueue { _ in _ = try self.loadedRecognizer() }
    }

    public func transcribe(fileURL: URL) async throws -> String {
        try await onQueue { checkCancellation in
            let recognizer = try self.loadedRecognizer()
            try checkCancellation()
            let samples = try AudioLoader.samples(from: fileURL)
            return Self.join(try Self.decode(samples, recognizer: recognizer, checkCancellation: checkCancellation))
        }
    }

    /// Lookahead is copied from capture on the utility queue. Cut and inference then
    /// run without holding the capture lock.
    func decodeFinalizedChunk(from capture: CaptureBuffer, window: Range<Int>) async throws -> GigaAMChunk? {
        try await onQueue(qos: .utility) { checkCancellation in
            guard let raw = capture.finalizedWindow(window) else { return nil }
            let samples = try Self.dictationSamples(raw)
            let cut = GigaAMChunker.cut(in: samples, window: samples.indices)
            let recognizer = try self.loadedRecognizer()
            try checkCancellation()
            let text = try recognizer.recognize(samples[..<cut])
            return GigaAMChunk(end: window.lowerBound + cut, text: text)
        }
    }

    func decodeTail(_ raw: ArraySlice<Float>, after prefix: [String]) async throws -> String {
        try await onQueue { checkCancellation in
            let samples = try Self.dictationSamples(Array(raw))
            let recognizer = try self.loadedRecognizer()
            return Self.join(prefix + (try Self.decode(samples, recognizer: recognizer,
                                                      checkCancellation: checkCancellation)))
        }
    }

    /// Use the same PCM16 quantization as the file consumed by the offline path.
    private static func dictationSamples(_ raw: [Float]) throws -> [Float] {
        let audio = RecordedAudio(samples: raw, sampleRate: AudioLoader.sampleRate)
        guard let pcm = WAVFile.decode(WAVFile.encode(audio)) else {
            throw TranscriptionError("Could not normalize dictation audio")
        }
        return pcm.samples
    }

    private static func decode(_ samples: [Float], recognizer: GigaAMRecognizer,
                               checkCancellation: () throws -> Void) throws -> [String] {
        try GigaAMChunker.ranges(for: samples).map {
            try checkCancellation()
            return try recognizer.recognize(samples[$0])
        }
    }

    /// Joins chunk text and collapses the double spaces CTC sometimes emits.
    static func join(_ chunks: [String]) -> String {
        chunks.joined(separator: " ").split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    private func onQueue<T>(qos: DispatchQoS = .userInitiated,
                            _ work: @escaping (() throws -> Void) throws -> T) async throws -> T {
        try Task.checkCancellation()
        let cancelled = OSAllocatedUnfairLock(initialState: false)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                queue.async(qos: qos, flags: .enforceQoS) {
                    continuation.resume(with: Result {
                        let checkCancellation = {
                            if cancelled.withLock({ $0 }) { throw CancellationError() }
                        }
                        try checkCancellation()
                        let result = try work(checkCancellation)
                        try checkCancellation()
                        return result
                    })
                }
            }
        } onCancel: {
            cancelled.withLock { $0 = true }
        }
    }

    /// Creates the recognizer on first use. Call only on `queue`.
    private func loadedRecognizer() throws -> GigaAMRecognizer {
        if let recognizer {
            return recognizer
        }

        let folder = modelsDirectory.appendingPathComponent(variant.folderName, isDirectory: true)
        // ONNX Runtime and sherpa-onnx abort the whole process on a truncated or damaged
        // file instead of returning an error, so every file must match the release size.
        var missing: [String] = []
        var damaged: [String] = []
        for file in variant.files {
            let path = folder.appendingPathComponent(file.name).resolvingSymlinksInPath().path
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: path),
                  attributes[.type] as? FileAttributeType == .typeRegular else {
                missing.append(file.name)
                continue
            }
            if attributes[.size] as? Int != file.size {
                damaged.append(file.name)
            }
        }
        var problems: [String] = []
        if !missing.isEmpty {
            problems.append(CoreStrings.format("engines.gigaam.missingFiles", table: .engines,
                                               variant.rawValue, missing.joined(separator: ", ")))
        }
        if !damaged.isEmpty {
            problems.append(CoreStrings.format("engines.gigaam.damagedFiles", table: .engines,
                                               variant.rawValue, damaged.joined(separator: ", ")))
        }
        guard problems.isEmpty else {
            let expected = CoreStrings.format("engines.gigaam.expectedFolder", table: .engines, folder.path)
            throw TranscriptionError((problems + [expected]).joined(separator: " "))
        }

        let file = { (name: String) in folder.appendingPathComponent(name) }
        let model: GigaAMRecognizer.Model
        switch variant {
        case .transducer:
            model = .transducer(encoder: file("encoder.int8.onnx"), decoder: file("decoder.onnx"),
                                joiner: file("joiner.onnx"))
        case .ctc:
            model = .ctc(model: file("model.int8.onnx"))
        }
        let created = try GigaAMRecognizer(model: model, tokens: file("tokens.txt"), threads: Self.threads)
        recognizer = created
        return created
    }
}

struct GigaAMChunk {
    let end: Int
    let text: String
}

protocol GigaAMChunkDecoding: AnyObject {
    func decodeFinalizedChunk(from capture: CaptureBuffer, window: Range<Int>) async throws -> GigaAMChunk?
    func decodeTail(_ raw: ArraySlice<Float>, after prefix: [String]) async throws -> String
}

extension GigaAMEngine: GigaAMChunkDecoding {}

/// One confirmed recording's decoded prefix. Only one job may be pending, and
/// no new jobs are started after the recorder stops.
@MainActor
final class GigaAMRecordingSession {
    private let capture: CaptureBuffer
    private let makeDecoder: () async throws -> (any GigaAMChunkDecoding)?
    private var decoder: (any GigaAMChunkDecoding)?
    private var task: Task<Void, Never>?
    private var cut = 0
    private var texts: [String] = []
    private var isCapturing = true

    init(capture: CaptureBuffer, makeDecoder: @escaping () async throws -> (any GigaAMChunkDecoding)?) {
        self.capture = capture
        self.makeDecoder = makeDecoder
    }

    deinit { task?.cancel() }

    func advance() {
        guard isCapturing else { return }
        guard capture.allowsIncrementalDecode else {
            cancel()
            return
        }
        guard task == nil else { return }
        let window = cut..<(cut + GigaAMChunker.windowSamples)
        guard capture.hasFinalizedWindow(window) else { return }
        task = Task(priority: .utility) { [weak self, capture, decoder, makeDecoder] in
            defer { self?.task = nil }
            do {
                // Asking the engine cache for every chunk keeps a long dictation from
                // reaching the idle unload, and shows when the cache replaced the engine.
                let activeDecoder = try await makeDecoder()
                try Task.checkCancellation()
                guard let activeDecoder, decoder == nil || decoder === activeDecoder else {
                    self?.cancel()
                    return
                }
                guard let chunk = try await activeDecoder.decodeFinalizedChunk(from: capture, window: window),
                      !Task.isCancelled, let self else { return }
                self.cut = chunk.end
                self.texts.append(chunk.text)
                self.decoder = activeDecoder
            } catch {
                self?.cancel()
            }
        }
    }

    func stop() {
        isCapturing = false
    }

    func cancel() {
        stop()
        task?.cancel()
        task = nil
        decoder = nil
        cut = 0
        texts = []
    }

    /// Nil selects the ordinary file transcription, including for short clips.
    func finish(audio: RecordedAudio, decoder releaseDecoder: any GigaAMChunkDecoding) async throws -> String? {
        guard capture.allowsIncrementalDecode, audio.sampleRate == AudioLoader.sampleRate,
              decoder == nil || decoder === releaseDecoder else {
            cancel()
            return nil
        }
        let pending = task
        await withTaskCancellationHandler {
            await pending?.value
        } onCancel: {
            pending?.cancel()
        }
        try Task.checkCancellation()
        guard decoder === releaseDecoder, capture.allowsIncrementalDecode, cut > 0, cut < audio.samples.count else {
            return nil
        }
        do {
            return try await releaseDecoder.decodeTail(audio.samples[cut...], after: texts)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            try Task.checkCancellation()
            return nil
        }
    }
}

// Internal rather than private: ModelManager checks downloads against the same table.
extension GigaAMEngine.Variant {
    var folderName: String {
        switch self {
        case .transducer: return "sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16"
        case .ctc: return "sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16"
        }
    }

    /// Every file the recognizer reads, with its size in bytes in the release `folderName` names.
    var files: [(name: String, size: Int)] {
        switch self {
        case .transducer:
            return [("encoder.int8.onnx", 224_570_820), ("decoder.onnx", 4_600_132),
                    ("joiner.onnx", 2_712_896), ("tokens.txt", 13_354)]
        case .ctc:
            return [("model.int8.onnx", 224_893_661), ("tokens.txt", 2_007)]
        }
    }
}
