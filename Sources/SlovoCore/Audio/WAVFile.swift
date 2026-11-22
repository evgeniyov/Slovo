import Foundation

/// Dictation recordings on disk: 16 kHz mono WAV files kept for playback.
public enum WAVFile {
    /// Writes the samples as a 16-bit PCM mono WAV file, creating the parent folder.
    public static func write(_ audio: RecordedAudio, to url: URL) throws {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try encode(audio).write(to: url, options: .atomic)
        } catch {
            throw SlovoError(CoreStrings.format("audio.error.writeFailed", table: .audio, error.localizedDescription))
        }
    }

    /// Reads a WAV file back as mono samples at its own sample rate.
    public static func read(from url: URL) throws -> RecordedAudio {
        let data: Data
        do {
            data = try Data(contentsOf: url, options: .mappedIfSafe)
        } catch {
            throw SlovoError(CoreStrings.format("audio.error.readFailed", table: .audio,
                                                url.lastPathComponent, error.localizedDescription))
        }
        guard let audio = decode(data) else {
            throw SlovoError(CoreStrings.format("audio.error.unreadableWAV", table: .audio, url.lastPathComponent))
        }
        return audio
    }

    /// `yyyy-MM-dd/<uuid>.wav`, the path under `AppDirectories.recordingsFolder` that
    /// `HistoryEntry.audioFileName` stores.
    public static func relativePath(for id: UUID, date: Date, calendar: Calendar = .current) -> String {
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        let day = String(format: "%04d-%02d-%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0)
        return "\(day)/\(id.uuidString).wav"
    }

    /// 16-bit PCM keeps files half the size of Float32 with more precision than speech
    /// recognition can use.
    static func encode(_ audio: RecordedAudio) -> Data {
        let sampleRate = UInt32(min(1_000_000, max(1, audio.sampleRate.rounded())))
        let bytesPerSample = 2
        // RIFF sizes are 32-bit; a dictation lasts minutes, far below that limit.
        let frameCount = min(audio.samples.count, (Int(UInt32.max) - 36) / bytesPerSample)
        let dataBytes = frameCount * bytesPerSample

        var data = Data(capacity: 44 + dataBytes)
        data.appendASCII("RIFF")
        data.appendLittleEndian(UInt32(36 + dataBytes))
        data.appendASCII("WAVE")
        data.appendASCII("fmt ")
        data.appendLittleEndian(UInt32(16))
        data.appendLittleEndian(UInt16(1))                   // PCM
        data.appendLittleEndian(UInt16(1))                   // mono
        data.appendLittleEndian(sampleRate)
        data.appendLittleEndian(sampleRate * UInt32(bytesPerSample))
        data.appendLittleEndian(UInt16(bytesPerSample))
        data.appendLittleEndian(UInt16(16))
        data.appendASCII("data")
        data.appendLittleEndian(UInt32(dataBytes))

        var pcm = [Int16](repeating: 0, count: frameCount)
        for index in 0..<frameCount {
            let sample = audio.samples[index]
            let clamped = sample.isFinite ? max(-1, min(1, sample)) : 0
            pcm[index] = Int16((clamped * 32767).rounded()).littleEndian
        }
        pcm.withUnsafeBytes { data.append(contentsOf: $0) }
        return data
    }

    /// Parses PCM (8/16/24/32-bit) and IEEE float (32/64-bit) WAV data, including
    /// `WAVE_FORMAT_EXTENSIBLE`, averaging channels to mono. Nil when unreadable.
    static func decode(_ data: Data) -> RecordedAudio? {
        data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) -> RecordedAudio? in
            func uint16(_ offset: Int) -> UInt16 {
                UInt16(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: UInt16.self))
            }
            func uint32(_ offset: Int) -> UInt32 {
                UInt32(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: UInt32.self))
            }
            func fourCC(_ offset: Int) -> String {
                String(decoding: bytes[offset..<(offset + 4)], as: UTF8.self)
            }

            guard bytes.count >= 12, fourCC(0) == "RIFF", fourCC(8) == "WAVE" else { return nil }

            var format: (tag: UInt16, channels: Int, sampleRate: Double, bits: Int)?
            var payload: Range<Int>?
            var offset = 12
            while offset + 8 <= bytes.count, format == nil || payload == nil {
                let chunk = fourCC(offset)
                let size = Int(uint32(offset + 4))
                let body = offset + 8
                let end = min(bytes.count, body + size)
                if chunk == "fmt ", end - body >= 16 {
                    var tag = uint16(body)
                    // WAVE_FORMAT_EXTENSIBLE keeps the real format tag at the start of its sub-format GUID.
                    if tag == 0xFFFE, end - body >= 26 {
                        tag = uint16(body + 24)
                    }
                    format = (tag, Int(uint16(body + 2)), Double(uint32(body + 4)), Int(uint16(body + 14)))
                } else if chunk == "data" {
                    payload = body..<end
                }
                offset = body + size + (size & 1)
            }

            guard let format, let payload, format.channels > 0, format.sampleRate > 0,
                  let sampleType = SampleType(tag: format.tag, bits: format.bits) else { return nil }
            let frameBytes = sampleType.byteCount * format.channels
            let frames = payload.count / frameBytes
            var samples = [Float](repeating: 0, count: frames)
            let scale = 1 / Float(format.channels)
            for frame in 0..<frames {
                let base = payload.lowerBound + frame * frameBytes
                var sum: Float = 0
                for channel in 0..<format.channels {
                    sum += sampleType.value(in: bytes, at: base + channel * sampleType.byteCount)
                }
                samples[frame] = sum * scale
            }
            return RecordedAudio(samples: samples, sampleRate: format.sampleRate)
        }
    }

    private enum SampleType {
        case uint8, int16, int24, int32, float32, float64

        init?(tag: UInt16, bits: Int) {
            switch (tag, bits) {
            case (1, 8): self = .uint8
            case (1, 16): self = .int16
            case (1, 24): self = .int24
            case (1, 32): self = .int32
            case (3, 32): self = .float32
            case (3, 64): self = .float64
            default: return nil
            }
        }

        var byteCount: Int {
            switch self {
            case .uint8: return 1
            case .int16: return 2
            case .int24: return 3
            case .int32, .float32: return 4
            case .float64: return 8
            }
        }

        func value(in bytes: UnsafeRawBufferPointer, at offset: Int) -> Float {
            switch self {
            case .uint8:
                return (Float(bytes[offset]) - 128) / 128
            case .int16:
                return Float(Int16(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: Int16.self))) / 32_768
            case .int24:
                let value = Int32(bytes[offset]) | Int32(bytes[offset + 1]) << 8
                    | Int32(Int8(bitPattern: bytes[offset + 2])) << 16
                return Float(value) / 8_388_608
            case .int32:
                return Float(Int32(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: Int32.self))) / 2_147_483_648
            case .float32:
                return Float(bitPattern: UInt32(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: UInt32.self)))
            case .float64:
                return Float(Double(bitPattern: UInt64(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: UInt64.self))))
            }
        }
    }
}

private extension Data {
    mutating func appendASCII(_ text: String) {
        append(contentsOf: Array(text.utf8))
    }

    mutating func appendLittleEndian<T: FixedWidthInteger>(_ value: T) {
        Swift.withUnsafeBytes(of: value.littleEndian) { append(contentsOf: $0) }
    }
}
