import AVFoundation
import Foundation

public enum AudioLoader {
    /// The sample rate every bundled engine expects.
    public static let sampleRate: Double = 16_000

    /// Reads an audio file, averages its channels to mono and resamples it to 16 kHz Float32.
    public static func samples(from url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let source = file.processingFormat
        let frameCount = AVAudioFrameCount(file.length)
        guard frameCount > 0 else { return [] }

        guard let input = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: frameCount),
              let monoFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: source.sampleRate,
                                             channels: 1, interleaved: false),
              let mono = AVAudioPCMBuffer(pcmFormat: monoFormat, frameCapacity: frameCount),
              let channels = input.floatChannelData,
              let monoChannel = mono.floatChannelData?[0] else {
            throw TranscriptionError(CoreStrings.format("engines.audioLoader.bufferFailed", table: .engines,
                                                        url.lastPathComponent))
        }
        try file.read(into: input)

        let length = Int(input.frameLength)
        let channelCount = Int(source.channelCount)
        for frame in 0..<length {
            var sum: Float = 0
            for channel in 0..<channelCount {
                sum += channels[channel][frame]
            }
            monoChannel[frame] = sum / Float(channelCount)
        }
        mono.frameLength = input.frameLength

        if source.sampleRate == sampleRate {
            return Array(UnsafeBufferPointer(start: monoChannel, count: length))
        }
        return try resample(mono, from: monoFormat)
    }

    private static func resample(_ buffer: AVAudioPCMBuffer, from format: AVAudioFormat) throws -> [Float] {
        guard let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate,
                                         channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: format, to: target) else {
            throw TranscriptionError(CoreStrings.string("engines.audioLoader.converterFailed", table: .engines))
        }
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * sampleRate / format.sampleRate) + 1024
        guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else {
            throw TranscriptionError(CoreStrings.string("engines.audioLoader.conversionBufferFailed", table: .engines))
        }

        var delivered = false
        var conversionError: NSError?
        let status = converter.convert(to: output, error: &conversionError) { _, inputStatus in
            if delivered {
                inputStatus.pointee = .endOfStream
                return nil
            }
            delivered = true
            inputStatus.pointee = .haveData
            return buffer
        }
        if status == .error {
            throw conversionError
                ?? TranscriptionError(CoreStrings.string("engines.audioLoader.conversionFailed", table: .engines))
        }
        guard let channel = output.floatChannelData?[0] else {
            throw TranscriptionError(CoreStrings.string("engines.audioLoader.noData", table: .engines))
        }
        return Array(UnsafeBufferPointer(start: channel, count: Int(output.frameLength)))
    }
}
