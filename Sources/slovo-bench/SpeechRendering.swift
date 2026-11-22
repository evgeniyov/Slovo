import AVFoundation
import Foundation
import SlovoCore

/// Deterministic randomness (SplitMix64), so one seed reproduces a case exactly.
struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var value = state
        value = (value ^ (value >> 30)) &* 0xBF58_476D_1CE4_E5B9
        value = (value ^ (value >> 27)) &* 0x94D0_49BB_1331_11EB
        return value ^ (value >> 31)
    }
}

/// Audio for the synthetic speaker evaluation, made with AVFoundation only: speech
/// voices, pitch and tempo changes, a far-field room, pink noise and the track files.
/// Every sample array is 16 kHz mono.
enum SpeechRendering {
    static let sampleRate = 16_000.0

    // MARK: - Voices

    static func isInstalled(voice identifier: String) -> Bool {
        AVSpeechSynthesisVoice(identifier: identifier) != nil
    }

    /// One utterance of an installed voice, trimmed of leading and trailing silence.
    static func speak(_ text: String, voice identifier: String) async throws -> [Float] {
        guard let voice = AVSpeechSynthesisVoice(identifier: identifier) else {
            throw BenchError("Голос \(identifier) не установлен")
        }
        let utterance = AVSpeechUtterance(string: text)
        utterance.voice = voice
        let synthesizer = AVSpeechSynthesizer()
        let collector = BufferCollector()
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            collector.continuation = continuation
            synthesizer.write(utterance) { buffer in
                collector.receive(buffer)
            }
            // Synthesis stalls rarely, but a stalled run would never end on its own.
            DispatchQueue.global().asyncAfter(deadline: .now() + 60) {
                collector.finish(throwing: BenchError("Голос \(identifier) не ответил за 60 с: «\(text.prefix(40))…»"))
            }
        }
        withExtendedLifetime(synthesizer) {}
        let buffers = collector.buffers
        guard let format = buffers.first?.format else {
            throw BenchError("Голос \(identifier) ничего не произнёс: «\(text.prefix(40))…»")
        }
        // AudioLoader already mixes to mono and resamples to 16 kHz.
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("slovo-bench-speech-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent("utterance.caf")
        do {
            let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: format.commonFormat,
                                       interleaved: format.isInterleaved)
            for buffer in buffers {
                try file.write(from: buffer)
            }
        }
        return trimmed(try AudioLoader.samples(from: url))
    }

    /// Collects synthesizer output and resumes its caller exactly once.
    private final class BufferCollector: @unchecked Sendable {
        private let lock = NSLock()
        private var collected: [AVAudioPCMBuffer] = []
        private var finished = false
        var continuation: CheckedContinuation<Void, Error>?

        var buffers: [AVAudioPCMBuffer] {
            lock.withLock { collected }
        }

        func receive(_ buffer: AVAudioBuffer) {
            guard let pcm = buffer as? AVAudioPCMBuffer else { return }
            if pcm.frameLength == 0 {
                finish(throwing: nil)
            } else {
                lock.withLock { collected.append(pcm) }
            }
        }

        func finish(throwing error: Error?) {
            let pending = lock.withLock { () -> CheckedContinuation<Void, Error>? in
                guard !finished else { return nil }
                finished = true
                defer { continuation = nil }
                return continuation
            }
            if let error {
                pending?.resume(throwing: error)
            } else {
                pending?.resume()
            }
        }
    }

    /// A voiced buzz on `pitchHz` with a syllable rhythm. It stands in for a voice where
    /// the harness itself is tested, and is useless for judging diarization quality.
    static func tone(seconds: Double, pitchHz: Double, generator: inout SeededGenerator) -> [Float] {
        let count = max(1, Int(seconds * sampleRate))
        let syllableHz = Double.random(in: 3.5...5, using: &generator)
        let phaseOffset = Double.random(in: 0...(2 * .pi), using: &generator)
        var samples = [Float](repeating: 0, count: count)
        for index in 0..<count {
            let time = Double(index) / sampleRate
            let vibrato = 1 + 0.02 * sin(2 * .pi * 5 * time + phaseOffset)
            var value = 0.0
            for harmonic in 1...8 {
                value += sin(2 * .pi * pitchHz * vibrato * Double(harmonic) * time) / Double(harmonic)
            }
            let syllable = pow(sin(.pi * syllableHz * time + phaseOffset), 2)
            let edge = min(1, time / 0.03, (seconds - time) / 0.03)
            samples[index] = Float(0.12 * value * syllable * max(0, edge))
        }
        return samples
    }

    // MARK: - Effects

    /// Pitch in cents and tempo, rendered offline with AVAudioUnitTimePitch.
    static func shifted(_ samples: [Float], cents: Double, rate: Double) throws -> [Float] {
        guard cents != 0 || rate != 1 else { return samples }
        let pitch = AVAudioUnitTimePitch()
        pitch.pitch = Float(cents)
        pitch.rate = Float(rate)
        let frames = Double(samples.count) / max(rate, 0.1)
        return trimmed(try render(samples, through: [pitch], frames: Int(frames) + Int(sampleRate * 0.3)))
    }

    /// A far seat in a medium room: a 7 kHz low-pass and reverb at `wetPercent`.
    static func room(_ samples: [Float], lowPassHz: Float = 7_000, wetPercent: Float = 20) throws -> [Float] {
        let equalizer = AVAudioUnitEQ(numberOfBands: 1)
        if let band = equalizer.bands.first {
            band.filterType = .lowPass
            band.frequency = lowPassHz
            band.bypass = false
        }
        let reverb = AVAudioUnitReverb()
        reverb.loadFactoryPreset(.mediumRoom)
        reverb.wetDryMix = wetPercent
        // The room's tail is cut at the track's length, as a recording would be.
        return try render(samples, through: [equalizer, reverb], frames: samples.count)
    }

    /// Plays `samples` through `effects` in an offline engine and returns `frames` samples.
    ///
    /// The graph runs in stereo: Apple's reverb and equalizer refuse a mono bus with an
    /// exception that Swift cannot catch. Both channels carry the same signal and are
    /// averaged back to mono.
    private static func render(_ samples: [Float], through effects: [AVAudioNode], frames: Int) throws -> [Float] {
        guard let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 2),
              let input = buffer(samples, format: format) else {
            throw BenchError("Не удалось подготовить звук для обработки")
        }
        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        engine.attach(player)
        var previous: AVAudioNode = player
        for effect in effects {
            engine.attach(effect)
            engine.connect(previous, to: effect, format: format)
            previous = effect
        }
        engine.connect(previous, to: engine.mainMixerNode, format: format)
        try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4_096)
        try engine.start()
        defer { engine.stop() }
        player.scheduleBuffer(input, completionHandler: nil)
        player.play()

        guard let chunk = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat,
                                           frameCapacity: engine.manualRenderingMaximumFrameCount) else {
            throw BenchError("Не удалось подготовить звук для обработки")
        }
        var output: [Float] = []
        output.reserveCapacity(frames)
        var idleRounds = 0
        while output.count < frames {
            let wanted = min(chunk.frameCapacity, AVAudioFrameCount(frames - output.count))
            switch try engine.renderOffline(wanted, to: chunk) {
            case .success:
                guard let channels = chunk.floatChannelData else {
                    throw BenchError("Обработка звука не вернула данных")
                }
                let count = Int(chunk.frameLength)
                let channelCount = Int(chunk.format.channelCount)
                for index in 0..<count {
                    var sum: Float = 0
                    for channel in 0..<channelCount {
                        sum += channels[channel][index]
                    }
                    output.append(sum / Float(max(1, channelCount)))
                }
                idleRounds = 0
            case .insufficientDataFromInputNode, .cannotDoInCurrentContext:
                idleRounds += 1
                if idleRounds > 1_000 {
                    throw BenchError("Обработка звука остановилась")
                }
            case .error:
                throw BenchError("Обработка звука завершилась с ошибкой")
            @unknown default:
                throw BenchError("Обработка звука завершилась с ошибкой")
            }
        }
        return output
    }

    // MARK: - Samples

    /// Pink noise with an RMS of 1: Paul Kellet's filter over seeded white noise.
    static func pinkNoise(count: Int, generator: inout SeededGenerator) -> [Float] {
        var b0 = 0.0, b1 = 0.0, b2 = 0.0, b3 = 0.0, b4 = 0.0, b5 = 0.0, b6 = 0.0
        var noise = [Float](repeating: 0, count: count)
        for index in 0..<count {
            let white = Double.random(in: -1...1, using: &generator)
            b0 = 0.99886 * b0 + white * 0.0555179
            b1 = 0.99332 * b1 + white * 0.0750759
            b2 = 0.96900 * b2 + white * 0.1538520
            b3 = 0.86650 * b3 + white * 0.3104856
            b4 = 0.55000 * b4 + white * 0.5329522
            b5 = -0.7616 * b5 - white * 0.0168980
            noise[index] = Float(b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362)
            b6 = white * 0.115926
        }
        let level = rms(noise[...])
        guard level > 0 else { return noise }
        return noise.map { $0 / level }
    }

    static func rms(_ samples: ArraySlice<Float>) -> Float {
        guard !samples.isEmpty else { return 0 }
        let sum = samples.reduce(0.0) { $0 + Double($1) * Double($1) }
        return Float((sum / Double(samples.count)).squareRoot())
    }

    static func gain(decibels: Double) -> Float {
        Float(pow(10, decibels / 20))
    }

    /// Drops leading and trailing 20 ms frames quieter than 5% of the loudest frame,
    /// keeping 50 ms around the speech.
    static func trimmed(_ samples: [Float]) -> [Float] {
        let frame = Int(sampleRate * 0.02)
        guard samples.count > frame * 3 else { return samples }
        let levels = stride(from: 0, to: samples.count, by: frame).map { start in
            rms(samples[start..<min(samples.count, start + frame)])
        }
        let threshold = max(0.003, (levels.max() ?? 0) * 0.05)
        guard let first = levels.firstIndex(where: { $0 > threshold }),
              let last = levels.lastIndex(where: { $0 > threshold }) else { return [] }
        let margin = Int(sampleRate * 0.05)
        let start = max(0, first * frame - margin)
        let end = min(samples.count, (last + 1) * frame + margin)
        return Array(samples[start..<end])
    }

    static func silence(seconds: Double) -> [Float] {
        [Float](repeating: 0, count: max(0, Int(seconds * sampleRate)))
    }

    /// Adds `source` scaled by `gain` into `track` starting at `offset` samples.
    static func mix(_ source: [Float], into track: inout [Float], at offset: Int, gain: Float = 1) {
        guard offset < track.count else { return }
        let count = min(source.count, track.count - max(0, offset))
        guard count > 0 else { return }
        for index in 0..<count where offset + index >= 0 {
            track[offset + index] += source[index] * gain
        }
    }

    // MARK: - Files

    /// 16 kHz mono Float32 CAF, a format the meeting track reader accepts.
    static func writeTrack(_ samples: [Float], to url: URL) throws {
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]
        try? FileManager.default.removeItem(at: url)
        let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        guard let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1) else {
            throw BenchError("Не удалось записать \(url.lastPathComponent)")
        }
        let block = Int(sampleRate)
        for start in stride(from: 0, to: samples.count, by: block) {
            guard let chunk = buffer(Array(samples[start..<min(samples.count, start + block)]), format: format) else {
                throw BenchError("Не удалось записать \(url.lastPathComponent)")
            }
            try file.write(from: chunk)
        }
    }

    /// The same samples in every channel of `format`.
    private static func buffer(_ samples: [Float], format: AVAudioFormat) -> AVAudioPCMBuffer? {
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(max(1, samples.count))),
              let channels = buffer.floatChannelData else { return nil }
        samples.withUnsafeBufferPointer { source in
            guard let base = source.baseAddress else { return }
            for channel in 0..<Int(format.channelCount) {
                channels[channel].update(from: base, count: samples.count)
            }
        }
        buffer.frameLength = AVAudioFrameCount(samples.count)
        return buffer
    }
}
