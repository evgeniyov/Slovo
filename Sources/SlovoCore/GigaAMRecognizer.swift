import Foundation
import SherpaOnnxC

/// One GigaAM v3 model loaded into a sherpa-onnx offline recognizer.
///
/// Uses the C API directly: the Swift wrapper calls fatalError when the recognizer
/// cannot be created, and that has to reach the user as an error instead.
/// Not thread-safe; the owner serializes every call.
final class GigaAMRecognizer {
    enum Model {
        case transducer(encoder: URL, decoder: URL, joiner: URL)
        case ctc(model: URL)
    }

    /// Shorter input is padded with silence: with less than one feature frame ONNX
    /// Runtime throws inside sherpa-onnx and the uncaught exception aborts the process.
    static let minimumSamples = Int(AudioLoader.sampleRate)

    private let handle: OpaquePointer

    init(model: Model, tokens: URL, threads: Int) throws {
        let strings = CStringPool()
        // Zeroed like the C examples; sherpa-onnx fills in defaults for every zero field.
        var config = SherpaOnnxOfflineRecognizerConfig()
        // GigaAM needs 64 mel bins; sherpa-onnx also forces this from the model metadata.
        config.feat_config.sample_rate = Int32(AudioLoader.sampleRate)
        config.feat_config.feature_dim = 64
        config.model_config.tokens = strings.copy(tokens.path)
        config.model_config.num_threads = Int32(threads)
        // ONNX Runtime prepacks the INT8 weights into a second, larger copy: about 330 MiB
        // more while loaded. Without it decoding is ~4-5% slower, which the decode during
        // recording hides. sherpa-onnx reads "cpu:<file>" as session options.
        let options = CoreStrings.resourceBundle.url(forResource: "gigaam-cpu", withExtension: "conf")
        config.model_config.provider = strings.copy(options.map { "cpu:" + $0.path } ?? "cpu")
        // Beam search was no more accurate on the bench and dropped words.
        config.decoding_method = strings.copy("greedy_search")

        switch model {
        case let .transducer(encoder, decoder, joiner):
            // The generic "transducer" type expects Zipformer metadata and fails with
            // "'vocab_size' does not exist in the metadata".
            config.model_config.model_type = strings.copy("nemo_transducer")
            config.model_config.transducer.encoder = strings.copy(encoder.path)
            config.model_config.transducer.decoder = strings.copy(decoder.path)
            config.model_config.transducer.joiner = strings.copy(joiner.path)
        case let .ctc(model):
            config.model_config.model_type = strings.copy("nemo_ctc")
            config.model_config.nemo_ctc.model = strings.copy(model.path)
        }

        // sherpa-onnx copies the strings while creating the recognizer.
        let created = withExtendedLifetime(strings) { SherpaOnnxCreateOfflineRecognizer(&config) }
        guard let created else {
            throw TranscriptionError(CoreStrings.format("engines.gigaam.recognizerFailed", table: .engines,
                                                        tokens.deletingLastPathComponent().path))
        }
        handle = created
    }

    deinit {
        SherpaOnnxDestroyOfflineRecognizer(handle)
    }

    /// Recognizes 16 kHz mono samples in one pass.
    func recognize(_ samples: ArraySlice<Float>) throws -> String {
        guard let stream = SherpaOnnxCreateOfflineStream(handle) else {
            throw TranscriptionError(CoreStrings.string("engines.gigaam.streamFailed", table: .engines))
        }
        defer { SherpaOnnxDestroyOfflineStream(stream) }

        let input = samples.count < Self.minimumSamples
            ? ArraySlice(Array(samples) + [Float](repeating: 0, count: Self.minimumSamples - samples.count))
            : samples
        input.withUnsafeBufferPointer { buffer in
            SherpaOnnxAcceptWaveformOffline(stream, Int32(AudioLoader.sampleRate), buffer.baseAddress, Int32(buffer.count))
        }
        SherpaOnnxDecodeOfflineStream(handle, stream)

        guard let result = SherpaOnnxGetOfflineStreamResult(stream) else {
            throw TranscriptionError(CoreStrings.string("engines.gigaam.noResult", table: .engines))
        }
        defer { SherpaOnnxDestroyOfflineRecognizerResult(result) }
        return result.pointee.text.map { String(cString: $0) } ?? ""
    }
}

/// C strings that stay valid until the pool is released.
private final class CStringPool {
    private var pointers: [UnsafeMutablePointer<CChar>] = []

    func copy(_ string: String) -> UnsafePointer<CChar>? {
        guard let pointer = strdup(string) else { return nil }
        pointers.append(pointer)
        return UnsafePointer(pointer)
    }

    deinit {
        pointers.forEach { free($0) }
    }
}
