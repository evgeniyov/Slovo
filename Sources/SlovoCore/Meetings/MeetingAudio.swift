import AVFoundation
import CoreAudio
import Foundation

/// The format of every meeting track on disk and of the samples the segmenter and
/// the engines see.
enum MeetingAudioFormat {
    static let sampleRate = 16_000

    /// Float32 mono at 16 kHz, what capture converts to and what reading returns.
    static var processingFormat: AVAudioFormat? {
        AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Double(sampleRate), channels: 1, interleaved: false)
    }

    /// 16-bit PCM halves the disk use of Float32 and loses nothing that matters for
    /// speech; CAF keeps the samples readable when the app dies before the header is
    /// finalized.
    static var trackFileSettings: [String: Any] {
        [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: Double(sampleRate),
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]
    }

    /// The short WAV clips handed to engines.
    static var clipFileSettings: [String: Any] {
        [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: Double(sampleRate),
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]
    }
}

enum MeetingAudioMath {
    /// Averages every channel of the given buffers into one channel. Works for
    /// interleaved and planar Float32 lists alike.
    ///
    /// - Parameter trailingChannels: only mix the last buffers that together hold this
    ///   many channels, or everything when nil. An aggregate device lists the streams
    ///   of its sub-devices before those of its taps, so the tap is at the end.
    static func mixdown(_ list: UnsafePointer<AudioBufferList>, trailingChannels: Int? = nil) -> [Float] {
        let all = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: list))
        var selected: [AudioBuffer] = []
        var selectedChannels = 0
        for buffer in all.reversed() {
            if let trailingChannels, selectedChannels >= trailingChannels { break }
            guard buffer.mNumberChannels > 0, buffer.mData != nil else { continue }
            selected.append(buffer)
            selectedChannels += Int(buffer.mNumberChannels)
        }
        guard selectedChannels > 0 else { return [] }

        var frames = Int.max
        for buffer in selected {
            frames = min(frames, Int(buffer.mDataByteSize) / (MemoryLayout<Float>.size * Int(buffer.mNumberChannels)))
        }
        guard frames > 0, frames != .max else { return [] }

        var mono = [Float](repeating: 0, count: frames)
        mono.withUnsafeMutableBufferPointer { output in
            for buffer in selected {
                guard let data = buffer.mData else { continue }
                let channels = Int(buffer.mNumberChannels)
                let samples = data.assumingMemoryBound(to: Float.self)
                for frame in 0..<frames {
                    var sum: Float = 0
                    for channel in 0..<channels {
                        sum += samples[frame * channels + channel]
                    }
                    output[frame] += sum
                }
            }
            if selectedChannels > 1 {
                let scale = 1 / Float(selectedChannels)
                for frame in 0..<frames {
                    output[frame] *= scale
                }
            }
        }
        return mono
    }

    /// Averages the channels of a PCM buffer into one Float32 channel. Float32 is what
    /// files and the capture output deliver; 16- and 32-bit integers cover a capture
    /// device that ignores the requested format.
    static func mixdown(_ buffer: AVAudioPCMBuffer) -> [Float] {
        let frames = Int(buffer.frameLength)
        let channels = Int(buffer.format.channelCount)
        guard frames > 0, channels > 0 else { return [] }
        let interleaved = buffer.format.isInterleaved
        if let data = buffer.floatChannelData {
            if channels == 1 {
                return Array(UnsafeBufferPointer(start: data[0], count: frames))
            }
            return average(data, frames: frames, channels: channels, interleaved: interleaved, scale: 1) { $0 }
        }
        if let data = buffer.int16ChannelData {
            return average(data, frames: frames, channels: channels, interleaved: interleaved, scale: 1 / 32_768) { Float($0) }
        }
        if let data = buffer.int32ChannelData {
            return average(data, frames: frames, channels: channels, interleaved: interleaved,
                           scale: 1 / 2_147_483_648) { Float($0) }
        }
        return []
    }

    private static func average<Sample>(_ data: UnsafePointer<UnsafeMutablePointer<Sample>>, frames: Int, channels: Int,
                                        interleaved: Bool, scale: Float, value: (Sample) -> Float) -> [Float] {
        var mono = [Float](repeating: 0, count: frames)
        let factor = scale / Float(channels)
        mono.withUnsafeMutableBufferPointer { output in
            for channel in 0..<channels {
                // Interleaved samples all live behind the first pointer, one frame after another.
                let samples = data[interleaved ? 0 : channel]
                for frame in 0..<frames {
                    output[frame] += value(samples[interleaved ? frame * channels + channel : frame]) * factor
                }
            }
        }
        return mono
    }

    static func rms<C: Collection>(_ samples: C) -> Float where C.Element == Float {
        guard !samples.isEmpty else { return 0 }
        var sum: Double = 0
        for sample in samples {
            sum += Double(sample) * Double(sample)
        }
        return Float((sum / Double(samples.count)).squareRoot())
    }

    /// Maps RMS to a meter value from 0 to 1 on a decibel scale: -50 dBFS and below is
    /// empty, full scale is 1. Speech usually lands around the middle.
    static func level(rms: Float) -> Float {
        guard rms > 0 else { return 0 }
        let decibels = 20 * log10(rms)
        return min(1, max(0, (decibels + 50) / 50))
    }

    /// How much louder the meeting microphone track is made before it reaches disk:
    /// enough to lift a quiet or distant voice without making normal speech
    /// uncomfortably loud.
    static let microphoneBoostDecibels: Float = 9
    private static let microphoneBoostGain = pow(Float(10), microphoneBoostDecibels / 20)

    /// Boosts every sample by `microphoneBoostGain`, then softens whatever that boost
    /// pushes past full scale with `tanh` instead of hard-clipping it, so a raised voice
    /// compresses instead of crackling. Near silence the boost is close to linear;
    /// `tanh` only bends it as samples approach ±1.
    static func microphoneBoost(_ samples: [Float]) -> [Float] {
        samples.map { tanhf($0 * microphoneBoostGain) }
    }
}

/// Turns a stream of 16 kHz samples into about 30 meter values per second.
struct LevelMeter {
    let windowLength: Int
    private var sum: Float = 0
    private var count = 0

    init(sampleRate: Int = MeetingAudioFormat.sampleRate, updatesPerSecond: Int = 30) {
        windowLength = max(1, sampleRate / max(1, updatesPerSecond))
    }

    /// Feeds samples and returns the level of the last window they completed, if any.
    mutating func consume(_ samples: [Float]) -> Float? {
        var level: Float?
        for sample in samples {
            sum += sample * sample
            count += 1
            if count == windowLength {
                level = MeetingAudioMath.level(rms: (sum / Float(count)).squareRoot())
                sum = 0
                count = 0
            }
        }
        return level
    }
}

/// Decides when a system audio tap has delivered nothing but zeros for long enough
/// that System Audio Recording is most likely denied: macOS then feeds the tap
/// silence instead of failing.
struct SilenceDetector {
    enum Event: Equatable {
        /// Only zeros since the start for `timeout` seconds.
        case silence
        /// Real audio after `.silence` was reported: permission was there after all.
        case signalAfterSilence
    }

    let timeout: TimeInterval
    private(set) var heardSignal = false
    private(set) var reportedSilence = false
    private var silentSeconds: TimeInterval = 0

    init(timeout: TimeInterval) {
        self.timeout = timeout
    }

    mutating func consume(frames: Int, sampleRate: Double, allZero: Bool) -> Event? {
        guard frames > 0, sampleRate > 0 else { return nil }
        if !allZero {
            let recovering = reportedSilence && !heardSignal
            heardSignal = true
            return recovering ? .signalAfterSilence : nil
        }
        guard !heardSignal, !reportedSilence else { return nil }
        silentSeconds += Double(frames) / sampleRate
        if silentSeconds >= timeout {
            reportedSilence = true
            return .silence
        }
        return nil
    }

    /// For when no buffers arrived at all during the first seconds.
    mutating func noAudio(forSeconds seconds: TimeInterval) -> Event? {
        guard !heardSignal, !reportedSilence, seconds >= timeout else { return nil }
        reportedSilence = true
        return .silence
    }
}

/// Streaming sample-rate conversion of mono Float32 to 16 kHz. Not thread-safe: one
/// capture callback or one reader uses it at a time.
final class MeetingResampler {
    let sourceSampleRate: Double
    private let inputFormat: AVAudioFormat
    private let outputFormat: AVAudioFormat
    private let converter: AVAudioConverter?

    init(sourceSampleRate: Double) throws {
        guard sourceSampleRate > 0,
              let input = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sourceSampleRate,
                                        channels: 1, interleaved: false),
              let output = MeetingAudioFormat.processingFormat else {
            throw MeetingErrors.unsupportedAudioFormat()
        }
        self.sourceSampleRate = sourceSampleRate
        inputFormat = input
        outputFormat = output
        if sourceSampleRate == output.sampleRate {
            converter = nil
        } else {
            guard let converter = AVAudioConverter(from: input, to: output) else {
                throw MeetingErrors.unsupportedAudioFormat()
            }
            self.converter = converter
        }
    }

    func process(_ samples: [Float]) -> [Float] {
        convertForCapture(samples, endOfStream: false)
    }

    /// Returns the samples the converter still holds back; call once at the end.
    func flush() -> [Float] {
        convertForCapture([], endOfStream: true)
    }

    private func convertForCapture(_ samples: [Float], endOfStream: Bool) -> [Float] {
        do {
            return try convert(samples, endOfStream: endOfStream)
        } catch {
            SlovoLog.logger("Meetings").error("Sample rate conversion failed: \(error.localizedDescription, privacy: .public)")
            return []
        }
    }

    fileprivate func convert(_ samples: [Float], endOfStream: Bool) throws -> [Float] {
        guard let converter else { return samples }
        guard !samples.isEmpty || endOfStream else { return [] }
        var input: AVAudioPCMBuffer?
        if !samples.isEmpty {
            guard let buffer = AVAudioPCMBuffer(pcmFormat: inputFormat, frameCapacity: AVAudioFrameCount(samples.count)),
                  let channel = buffer.floatChannelData?[0] else {
                throw MeetingErrors.unsupportedAudioFormat()
            }
            samples.withUnsafeBufferPointer { source in
                if let base = source.baseAddress {
                    channel.update(from: base, count: samples.count)
                }
            }
            buffer.frameLength = AVAudioFrameCount(samples.count)
            input = buffer
        }

        let ratio = outputFormat.sampleRate / inputFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(max(samples.count, 4096)) * ratio) + 256
        var result: [Float] = []
        var delivered = input == nil
        var status: AVAudioConverterOutputStatus
        repeat {
            guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else {
                throw MeetingErrors.unsupportedAudioFormat()
            }
            var error: NSError?
            status = converter.convert(to: output, error: &error) { _, inputStatus in
                if !delivered, let input {
                    delivered = true
                    inputStatus.pointee = .haveData
                    return input
                }
                // `.noDataNow` keeps the converter's filter state for the next chunk;
                // `.endOfStream` drains it.
                inputStatus.pointee = endOfStream ? .endOfStream : .noDataNow
                return nil
            }
            if status == .error {
                if let error { throw error }
                throw MeetingErrors.unsupportedAudioFormat()
            }
            if let channel = output.floatChannelData?[0], output.frameLength > 0 {
                result.append(contentsOf: UnsafeBufferPointer(start: channel, count: Int(output.frameLength)))
            }
            if output.frameLength == 0 { break }
        } while status == .haveData
        return result
    }
}

/// Appends 16 kHz mono samples to a CAF file on its own serial queue, so capture
/// callbacks never wait for the disk and nothing accumulates in memory.
///
/// All tracks of a meeting share one host-time origin. When a chunk arrives later
/// than the samples written so far account for (the device paused, the engine was
/// rebuilt after a device change), the gap is filled with silence, so both tracks
/// stay on one timeline and transcript times match the recording.
final class MeetingTrackWriter: @unchecked Sendable {
    /// Gaps shorter than this are ordinary scheduling jitter and are not filled.
    static let gapTolerance: TimeInterval = 0.1
    /// Protects the disk from a bogus timestamp.
    static let maximumGap: TimeInterval = 3600
    /// The most one sleep pad writes at once. A sleep is measured by the clocks rather than
    /// read from a buffer, so it may exceed `maximumGap`; a longer one is padded in parts.
    static let maximumSleepPad: TimeInterval = 48 * 3600
    /// Disk bytes one second of a track takes: 16 kHz of 16-bit samples.
    static let bytesPerSecond: Int64 = Int64(MeetingAudioFormat.sampleRate) * 2

    let url: URL
    let originHostTime: UInt64

    /// The first write that failed, so the recording can end instead of ticking on over
    /// a track that stopped growing. Called once, on the writer's queue; set before the
    /// first `append`.
    var onWriteFailure: ((Error) -> Void)?
    /// Writes one buffer to the file; tests replace it to reach the failure path, which
    /// a real disk only takes when it fills.
    var writeBuffer: (AVAudioFile, AVAudioPCMBuffer) throws -> Void = { try $0.write(from: $1) }

    private let queue: DispatchQueue
    private let format: AVAudioFormat
    private let muteLock = NSLock()
    private var muted = false
    /// Accessed only on `queue`.
    private var file: AVAudioFile?
    private var frames: Int64 = 0
    /// Silence added for intervals the host clock did not count, chiefly a system sleep.
    /// A position is `frame(at:origin:)` plus this, rather than the origin moved back: a
    /// pad longer than the machine's awake uptime would wrap the origin below zero and
    /// leave every later buffer at frame 0, with the gap filling dead for the rest of the
    /// meeting.
    private var paddedFrames: Int64 = 0
    /// Set by the first failed write; nothing is written afterwards.
    private var writeFailed = false
    /// Whether any non-zero sample reached the file, so a track that stopped early can be
    /// told apart from one that only ever held silence.
    private var wroteSound = false

    init(url: URL, originHostTime: UInt64 = AudioGetCurrentHostTime()) throws {
        guard let format = MeetingAudioFormat.processingFormat else {
            throw MeetingErrors.unsupportedAudioFormat()
        }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            file = try AVAudioFile(forWriting: url, settings: MeetingAudioFormat.trackFileSettings,
                                   commonFormat: .pcmFormatFloat32, interleaved: false)
        } catch {
            throw MeetingErrors.fileFailed(error)
        }
        self.url = url
        self.originHostTime = originHostTime
        self.format = format
        queue = DispatchQueue(label: "local.slovo.Slovo.MeetingTrackWriter", qos: .userInitiated)
    }

    /// Whether appended samples are written as silence. Each buffer keeps its length and
    /// its capture time, so a muted stretch holds its place on the timeline: gap filling,
    /// sleep padding, marked moments and the other track stay aligned exactly as if the
    /// sound were there, and the capture behind the writer never has to stop. Read when a
    /// buffer is appended, so a change applies from the next buffer delivered.
    var isMuted: Bool {
        get { muteLock.withLock { muted } }
        set { muteLock.withLock { muted = newValue } }
    }

    /// Queues samples for writing. `hostTime` is the capture time of the first sample,
    /// when the source knows it.
    func append(_ samples: [Float], hostTime: UInt64? = nil) {
        guard !samples.isEmpty else { return }
        let written = isMuted ? [Float](repeating: 0, count: samples.count) : samples
        queue.async {
            self.write(written, hostTime: hostTime)
        }
    }

    /// Frames written so far, including silence inserted for gaps.
    var frameCount: Int64 {
        queue.sync { frames }
    }

    /// Whether the file holds any sound at all, after the writes queued so far.
    var hasSound: Bool {
        queue.sync { wroteSound }
    }

    /// Waits for queued writes and closes the file; later appends are ignored.
    @discardableResult
    func finish() -> Int64 {
        queue.sync {
            if let file, #available(macOS 15.0, *) {
                file.close()
            }
            file = nil
            return frames
        }
    }

    /// Fills an interval the host clock never counted, which a system sleep is: both
    /// `mach_absolute_time` and `ProcessInfo.systemUptime` stand still while the Mac
    /// sleeps, so without this the track would butt-join the two sides of the sleep and
    /// every later timestamp would be early by the slept time. What was added is counted
    /// in `paddedFrames`, and every later buffer is placed that much further along, so
    /// they keep landing at their true position and ordinary gap filling still works.
    /// Returns the frames added.
    @discardableResult
    func padSilence(seconds: TimeInterval) -> Int64 {
        let capped = Self.padSeconds(for: seconds)
        guard capped > 0 else { return 0 }
        return queue.sync {
            pad(seconds: capped)
        }
    }

    /// The same pad, queued behind the writes already waiting instead of waited for, so a
    /// long sleep never holds the caller while hours of silence reach the disk. Buffers
    /// appended afterwards land after it, at their true position.
    func enqueueSilence(seconds: TimeInterval) {
        let capped = min(max(0, seconds), Self.maximumSleepPad)
        guard capped > 0 else { return }
        queue.async {
            self.pad(seconds: capped)
        }
    }

    /// Runs on `queue`. Returns the frames added.
    private func pad(seconds: TimeInterval) -> Int64 {
        guard file != nil, !writeFailed else { return 0 }
        let before = frames
        writeSilence(Int64(seconds * Double(MeetingAudioFormat.sampleRate)))
        // Only what reached the file counts: a pad cut short by a failed write must
        // not push later buffers past the end of the track.
        paddedFrames += frames - before
        return frames - before
    }

    /// What an interval is worth as padding: never negative, and never more than the gap
    /// safeguard allows to be written at once.
    static func padSeconds(for seconds: TimeInterval) -> TimeInterval {
        min(max(0, seconds), maximumGap)
    }

    /// Where a host time falls on the 16 kHz timeline that starts at `origin`.
    static func frame(at hostTime: UInt64, origin: UInt64) -> Int64 {
        guard hostTime > origin else { return 0 }
        let nanoseconds = AudioConvertHostTimeToNanos(hostTime) &- AudioConvertHostTimeToNanos(origin)
        return Int64(Double(nanoseconds) * Double(MeetingAudioFormat.sampleRate) / 1_000_000_000)
    }

    private func write(_ samples: [Float], hostTime: UInt64?) {
        guard file != nil, !writeFailed else { return }
        if let hostTime {
            let rate = Double(MeetingAudioFormat.sampleRate)
            let gap = Self.frame(at: hostTime, origin: originHostTime) + paddedFrames - frames
            if gap > Int64(Self.gapTolerance * rate) {
                writeSilence(min(gap, Int64(Self.maximumGap * rate)))
            }
        }
        writeSamples(samples)
    }

    private func writeSilence(_ count: Int64) {
        var remaining = count
        let chunk = [Float](repeating: 0, count: MeetingAudioFormat.sampleRate)
        while remaining > 0, !writeFailed {
            let length = Int(min(remaining, Int64(chunk.count)))
            writeSamples(length == chunk.count ? chunk : Array(chunk.prefix(length)), isSilence: true)
            remaining -= Int64(length)
        }
    }

    private func writeSamples(_ samples: [Float], isSilence: Bool = false) {
        guard let file, !writeFailed,
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count)),
              let channel = buffer.floatChannelData?[0] else {
            return
        }
        samples.withUnsafeBufferPointer { source in
            if let base = source.baseAddress {
                channel.update(from: base, count: samples.count)
            }
        }
        buffer.frameLength = AVAudioFrameCount(samples.count)
        do {
            try writeBuffer(file, buffer)
            frames += Int64(samples.count)
            if !isSilence, !wroteSound, samples.contains(where: { $0 != 0 }) {
                wroteSound = true
            }
        } catch {
            // A full disk or a file that went away: the track cannot grow any further.
            // Writing stops here, so the computed gap does not widen with every later
            // buffer and send the silence fill into ever more work for nothing.
            writeFailed = true
            SlovoLog.logger("Meetings").error(
                "Writing \(self.url.lastPathComponent, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            onWriteFailure?(error)
        }
    }
}

/// Reads a meeting track forward as 16 kHz mono Float32, a block at a time, whatever
/// the file's own rate and channel count.
final class MeetingTrackReader: @unchecked Sendable {
    private static let blockFrames: AVAudioFrameCount = 16_000

    private let file: AVAudioFile
    private let resampler: MeetingResampler?
    private var pending: [Float] = []
    private var pendingStart = 0
    private var fileFinished = false
    /// 16 kHz frames handed out or skipped so far.
    private(set) var position = 0

    init(url: URL) throws {
        do {
            file = try AVAudioFile(forReading: url)
        } catch {
            throw MeetingErrors.readFailed(error)
        }
        let format = file.processingFormat
        guard format.channelCount > 0, format.sampleRate > 0, format.commonFormat == .pcmFormatFloat32 else {
            throw MeetingErrors.unsupportedAudioFormat()
        }
        resampler = format.sampleRate == Double(MeetingAudioFormat.sampleRate)
            ? nil
            : try MeetingResampler(sourceSampleRate: format.sampleRate)
    }

    /// Converts a source file to a new CAF with bounded working memory.
    static func convertToMeetingTrack(from source: URL, to target: URL) throws -> TimeInterval {
        try Task.checkCancellation()
        let reader = try MeetingTrackReader(url: source)
        guard let format = MeetingAudioFormat.processingFormat,
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: blockFrames),
              let channel = buffer.floatChannelData?[0] else {
            throw MeetingErrors.unsupportedAudioFormat()
        }
        let file = try AVAudioFile(forWriting: target, settings: MeetingAudioFormat.trackFileSettings,
                                   commonFormat: .pcmFormatFloat32, interleaved: false)
        while true {
            try Task.checkCancellation()
            let samples = try reader.read(Int(blockFrames))
            if samples.isEmpty { break }
            samples.withUnsafeBufferPointer { source in
                if let base = source.baseAddress {
                    channel.update(from: base, count: source.count)
                }
            }
            buffer.frameLength = AVAudioFrameCount(samples.count)
            try file.write(from: buffer)
        }
        if #available(macOS 15.0, *) {
            file.close()
        }
        return Double(reader.position) / Double(MeetingAudioFormat.sampleRate)
    }

    /// Up to `count` next samples; fewer only at the end of the file.
    func read(_ count: Int) throws -> [Float] {
        guard count > 0 else { return [] }
        try fill(count)
        let available = min(count, pending.count - pendingStart)
        let samples = Array(pending[pendingStart..<(pendingStart + available)])
        pendingStart += available
        position += available
        compact()
        return samples
    }

    /// The samples of a range on the 16 kHz timeline. The reader only moves forward,
    /// so ranges must come in order; an overlap with the previous range is clipped.
    func samples(in range: Range<Int>) throws -> [Float] {
        if range.lowerBound > position {
            try skip(range.lowerBound - position)
        }
        let start = max(range.lowerBound, position)
        guard range.upperBound > start else { return [] }
        return try read(range.upperBound - start)
    }

    private func skip(_ count: Int) throws {
        let fromPending = min(count, pending.count - pendingStart)
        pendingStart += fromPending
        position += fromPending
        compact()
        var remaining = count - fromPending
        guard remaining > 0 else { return }

        if resampler == nil {
            // Same rate as the timeline: seek instead of decoding.
            let target = min(file.length, file.framePosition + Int64(remaining))
            position += Int(target - file.framePosition)
            file.framePosition = target
            return
        }
        while remaining > 0 {
            let chunk = try read(min(remaining, Int(Self.blockFrames)))
            if chunk.isEmpty { return }
            remaining -= chunk.count
        }
    }

    private func fill(_ count: Int) throws {
        while pending.count - pendingStart < count, !fileFinished {
            try Task.checkCancellation()
            guard file.framePosition < file.length else {
                fileFinished = true
                if let resampler {
                    pending.append(contentsOf: try resampler.convert([], endOfStream: true))
                }
                return
            }
            let capacity = resampler == nil
                ? AVAudioFrameCount(max(count - (pending.count - pendingStart), 1))
                : Self.blockFrames
            guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: capacity) else {
                throw MeetingErrors.unsupportedAudioFormat()
            }
            do {
                try file.read(into: buffer, frameCount: capacity)
            } catch {
                throw MeetingErrors.readFailed(error)
            }
            if buffer.frameLength == 0 {
                fileFinished = true
                if let resampler {
                    pending.append(contentsOf: try resampler.convert([], endOfStream: true))
                }
                return
            }
            let mono = MeetingAudioMath.mixdown(buffer)
            pending.append(contentsOf: try resampler?.convert(mono, endOfStream: false) ?? mono)
        }
    }

    private func compact() {
        if pendingStart > 0, pendingStart >= pending.count / 2 {
            pending.removeFirst(pendingStart)
            pendingStart = 0
        }
    }
}

/// Short WAV files handed to `TranscriptionEngine.transcribe(fileURL:)`.
enum MeetingClip {
    static func write(_ samples: [Float], to url: URL) throws {
        guard let format = MeetingAudioFormat.processingFormat else {
            throw MeetingErrors.unsupportedAudioFormat()
        }
        do {
            let file = try AVAudioFile(forWriting: url, settings: MeetingAudioFormat.clipFileSettings,
                                       commonFormat: .pcmFormatFloat32, interleaved: false)
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(max(samples.count, 1))),
                  let channel = buffer.floatChannelData?[0] else {
                throw MeetingErrors.unsupportedAudioFormat()
            }
            samples.withUnsafeBufferPointer { source in
                if let base = source.baseAddress {
                    channel.update(from: base, count: samples.count)
                }
            }
            buffer.frameLength = AVAudioFrameCount(samples.count)
            try file.write(from: buffer)
            if #available(macOS 15.0, *) {
                file.close()
            }
        } catch let error as SlovoError {
            throw error
        } catch {
            throw MeetingErrors.fileFailed(error)
        }
    }

    /// Length of an audio file in seconds, or zero when it cannot be read.
    static func duration(of url: URL) -> TimeInterval {
        guard FileManager.default.fileExists(atPath: url.path),
              let file = try? AVAudioFile(forReading: url),
              file.fileFormat.sampleRate > 0 else {
            return 0
        }
        return Double(file.length) / file.fileFormat.sampleRate
    }
}
