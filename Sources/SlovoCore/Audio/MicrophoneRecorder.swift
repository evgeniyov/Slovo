import AVFoundation
import CoreMedia
import Foundation
import os

@MainActor
protocol IncrementalAudioRecording: AudioRecording {
    var captureBuffer: CaptureBuffer? { get }
}

@MainActor
protocol CaptureGapReporting: AudioRecording {
    /// Whether audio was dropped during the most recent capture.
    var hasCaptureGap: Bool { get }
}

/// Dictation capture through `AVCaptureSession`, converted to 16 kHz mono Float32 and
/// kept in memory until `stop()`.
///
/// `AVAudioEngine` is not used here: choosing a non-default input on its input node posts
/// a configuration change that stops the engine at random, so a dictation sometimes
/// captured nothing, or silence from the previous device. A capture session binds to one
/// device, starts in well under 0.1 s and converts the format itself.
///
/// The device is opened on a serial queue of this recorder, never on the main actor:
/// starting input can block, most of all while a Bluetooth headset switches profiles, and
/// the dictation key is timed on the main thread. So `start` returns at once and throws
/// only when no microphone exists; when opening fails later, `isRecording` turns false by
/// itself. A device that disconnects mid-recording is replaced by the system default;
/// when no other input takes over, `isRecording` turns false too and `stop()` still
/// returns what was captured before the loss.
@MainActor
public final class MicrophoneRecorder: IncrementalAudioRecording, CaptureGapReporting {
    public private(set) var isRecording = false
    public var onLevel: ((Float) -> Void)?
    /// Name of the microphone the last recording used, shown when nothing was heard.
    public private(set) var lastDeviceName: String?

    /// The rate every bundled engine expects.
    public nonisolated static let sampleRate: Double = 16_000
    /// About 30 level updates per second.
    nonisolated static let meterInterval: Duration = .milliseconds(33)
    /// Restarts after a device loss or a session error are retried this often, 200 ms
    /// apart, before the capture gives up. About 2 s, which covers macOS switching the
    /// default input after a headset goes away. Read on the capture queue.
    nonisolated static let restartAttempts = 10

    private let uptime: UptimeProviding
    private let log = SlovoLog.logger("Microphone")
    private let queue = DispatchQueue(label: "SlovoCore.MicrophoneRecorder", qos: .userInteractive)
    private var session: CaptureSession?
    var captureBuffer: CaptureBuffer? { session?.capture }
    /// What a capture recorded before its microphone was lost for good, until `stop()`
    /// hands it out; `start` and `cancel` drop it.
    private var keptSamples: [Float] = []
    private(set) var hasCaptureGap = false
    private var meterTask: Task<Void, Never>?
    private var smoothedLevel: Float = 0
    private var startedAt: TimeInterval = 0

    public init(uptime: UptimeProviding = SystemUptime()) {
        self.uptime = uptime
    }

    public func start(deviceUID: String?) throws {
        if session != nil {
            cancel()
        }
        keptSamples = []
        hasCaptureGap = false
        // A quick lookup, unlike opening the device, so it can answer at once.
        guard let device = CaptureSession.device(uid: deviceUID, log: log) else {
            throw SlovoError(CoreStrings.string("audio.error.noInputDevice", table: .audio))
        }
        let session = CaptureSession(device: device, queue: queue)
        session.onLost = { [weak self, weak session] in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let session else { return }
                    self?.captureLost(session)
                }
            }
        }
        self.session = session
        lastDeviceName = device.localizedName
        isRecording = true
        startedAt = uptime.uptime()
        startMeter(for: session)
        queue.async { [weak self] in
            do {
                try session.open()
            } catch {
                let message = error.localizedDescription
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        self?.openFailed(session, message: message)
                    }
                }
            }
        }
    }

    public func stop() -> RecordedAudio {
        guard isRecording, let session else {
            // A capture that lost its microphone keeps what it recorded before.
            let samples = keptSamples
            keptSamples = []
            return samples.isEmpty ? .empty : RecordedAudio(samples: samples, sampleRate: Self.sampleRate)
        }
        endCapture(waitForDevice: true)
        hasCaptureGap = session.capture.hasGap
        let samples = session.capture.takeSamples()
        let seconds = Double(samples.count) / Self.sampleRate
        let wall = uptime.uptime() - startedAt
        log.debug("Captured \(seconds, format: .fixed(precision: 2)) s of audio in \(wall, format: .fixed(precision: 2)) s")
        return RecordedAudio(samples: samples, sampleRate: Self.sampleRate)
    }

    public func cancel() {
        keptSamples = []
        guard session != nil else { return }
        // Nothing is kept, so a discarded tap never waits for the device.
        endCapture(waitForDevice: false)
    }

    private func openFailed(_ failed: CaptureSession, message: String) {
        log.error("Could not start the microphone: \(message, privacy: .public)")
        guard session === failed else { return }
        endCapture(waitForDevice: false)
    }

    /// The microphone went away and no other input could take over. The capture ends and
    /// `isRecording` turns false, which the dictation controller notices; the samples
    /// recorded before the loss wait for `stop()`.
    private func captureLost(_ lost: CaptureSession) {
        guard session === lost else { return }
        log.error("The microphone was lost and no other input took over; the recording ends")
        endCapture(waitForDevice: false)
        hasCaptureGap = lost.capture.hasGap
        keptSamples = lost.capture.takeSamples()
    }

    /// Ends the current session. With `waitForDevice`, a running session is stopped and its
    /// last buffers delivered before this returns, so the samples are complete; a session
    /// still opening is closed by its own open call instead of blocking the main actor.
    private func endCapture(waitForDevice: Bool) {
        guard let session else { return }
        self.session = nil
        meterTask?.cancel()
        meterTask = nil
        smoothedLevel = 0
        isRecording = false
        let isRunning = session.markClosing()
        if waitForDevice && isRunning {
            queue.sync {
                session.close()
            }
        } else {
            queue.async {
                session.close()
            }
        }
    }

    // MARK: - Level meter

    private func startMeter(for session: CaptureSession) {
        meterTask?.cancel()
        smoothedLevel = 0
        meterTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: MicrophoneRecorder.meterInterval)
                guard !Task.isCancelled, let self, self.isRecording else { return }
                self.publishLevel(rms: session.capture.recentRMS)
            }
        }
    }

    private func publishLevel(rms: Float) {
        smoothedLevel = Self.smoothedLevel(previous: smoothedLevel, target: SpeechEnergy.meterLevel(rms: rms))
        onLevel?(smoothedLevel)
    }

    /// Rises quickly so syllables show at once, falls gently so bars do not flicker.
    nonisolated static func smoothedLevel(previous: Float, target: Float) -> Float {
        let factor: Float = target > previous ? 0.6 : 0.2
        return previous + (target - previous) * factor
    }
}

/// One capture from `start` to its end. The capture session and its observers are used
/// only on the recorder's queue; samples arrive on `deliveryQueue`; two flags are shared
/// with the main actor.
private final class CaptureSession: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate, @unchecked Sendable {
    let capture = CaptureBuffer()
    /// Called on the queue once the microphone is lost for good: no other input could
    /// take over, or restarting kept failing. Set before `open`, read only on the queue.
    var onLost: (@Sendable () -> Void)?

    private struct Flags {
        /// The session was opened and the capture has not ended.
        var isRunning = false
        /// The capture ended; nothing may open the device again.
        var isClosing = false
    }

    /// The format the output is asked for, which is what the engines take, so no
    /// conversion is needed in the usual case.
    private static let outputSettings: [String: Any] = [
        AVFormatIDKey: kAudioFormatLinearPCM,
        AVSampleRateKey: MicrophoneRecorder.sampleRate,
        AVNumberOfChannelsKey: 1,
        AVLinearPCMBitDepthKey: 32,
        AVLinearPCMIsFloatKey: true,
        AVLinearPCMIsNonInterleaved: false,
        AVLinearPCMIsBigEndianKey: false
    ]

    private let queue: DispatchQueue
    private let deliveryQueue = DispatchQueue(label: "SlovoCore.MicrophoneRecorder.samples", qos: .userInteractive)
    private let flags = OSAllocatedUnfairLock(initialState: Flags())
    private let log = SlovoLog.logger("Microphone")
    /// Used on `deliveryQueue` only, for the rare device that ignores the requested format.
    private let converter = CaptureConverter(targetRate: MicrophoneRecorder.sampleRate)
    /// Touched only on `queue`.
    private var device: AVCaptureDevice
    /// Touched only on `queue`.
    private var session: AVCaptureSession?
    /// Touched only on `queue`.
    private var observers: [NSObjectProtocol] = []

    init(device: AVCaptureDevice, queue: DispatchQueue) {
        self.device = device
        self.queue = queue
        super.init()
    }

    /// The chosen microphone when it is connected, otherwise the system default input.
    static func device(uid: String?, log: Logger) -> AVCaptureDevice? {
        if let uid, !uid.isEmpty {
            if let device = AVCaptureDevice(uniqueID: uid), device.hasMediaType(.audio) {
                return device
            }
            log.notice("The chosen microphone is not connected; using the system default")
        }
        return AVCaptureDevice.default(for: .audio)
    }

    /// Opens the device and starts delivering samples. Call on the queue.
    func open() throws {
        guard !flags.withLock({ $0.isClosing }) else { return }
        try startSession()
        let closedMeanwhile = flags.withLock { flags -> Bool in
            flags.isRunning = !flags.isClosing
            return flags.isClosing
        }
        if closedMeanwhile {
            tearDownSession()
        }
    }

    /// Marks the capture as ended; returns whether the session is running. Any thread.
    func markClosing() -> Bool {
        flags.withLock { flags -> Bool in
            flags.isClosing = true
            return flags.isRunning
        }
    }

    /// Stops the session and waits for buffers already on their way. Call on the queue.
    func close() {
        flags.withLock { $0.isRunning = false }
        tearDownSession()
        deliveryQueue.sync {}
    }

    // MARK: - Session

    private func startSession() throws {
        let session = AVCaptureSession()
        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            throw SlovoError(CoreStrings.format("audio.error.engineStartFailed", table: .audio, error.localizedDescription))
        }
        let output = AVCaptureAudioDataOutput()
        output.audioSettings = Self.outputSettings
        guard session.canAddInput(input), session.canAddOutput(output) else {
            throw SlovoError(CoreStrings.string("audio.error.formatUnsupported", table: .audio))
        }
        session.addInput(input)
        session.addOutput(output)
        output.setSampleBufferDelegate(self, queue: deliveryQueue)
        session.startRunning()
        guard session.isRunning else {
            output.setSampleBufferDelegate(nil, queue: nil)
            throw SlovoError(CoreStrings.format("audio.error.engineStartFailed", table: .audio, device.localizedName))
        }
        self.session = session
        observe(session)
    }

    private func observe(_ session: AVCaptureSession) {
        let center = NotificationCenter.default
        let disconnected = center.addObserver(forName: AVCaptureDevice.wasDisconnectedNotification,
                                              object: device, queue: nil) { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.recover(deviceLost: true) }
        }
        let runtimeError = center.addObserver(forName: AVCaptureSession.runtimeErrorNotification,
                                              object: session, queue: nil) { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.recover(deviceLost: false) }
        }
        observers = [disconnected, runtimeError]
    }

    private func tearDownSession() {
        for observer in observers {
            NotificationCenter.default.removeObserver(observer)
        }
        observers = []
        guard let session else { return }
        session.stopRunning()
        for case let output as AVCaptureAudioDataOutput in session.outputs {
            output.setSampleBufferDelegate(nil, queue: nil)
        }
        self.session = nil
    }

    /// Keeps the captured samples and continues on a fresh session; a lost device is
    /// replaced by the system default.
    private func recover(deviceLost: Bool) {
        guard flags.withLock({ $0.isRunning && !$0.isClosing }) else { return }
        log.notice("Microphone \(deviceLost ? "disconnected" : "reported an error", privacy: .public); restarting capture")
        capture.invalidateIncrementalDecode()
        let lostID = deviceLost ? device.uniqueID : nil
        tearDownSession()
        restartSession(attempt: 1, avoiding: lostID)
    }

    /// Opens a fresh session, retrying a few times. After a disconnection the system default
    /// is looked up again on every attempt: right after the notification it can still name
    /// the device that just went away, or none at all.
    private func restartSession(attempt: Int, avoiding lostID: String?) {
        guard !flags.withLock({ $0.isClosing }) else { return }
        do {
            if let lostID {
                guard let fallback = AVCaptureDevice.default(for: .audio), fallback.isConnected,
                      fallback.uniqueID != lostID else {
                    throw SlovoError(CoreStrings.string("audio.error.noInputDevice", table: .audio))
                }
                device = fallback
            }
            try startSession()
        } catch {
            guard attempt < MicrophoneRecorder.restartAttempts else {
                log.error("Gave up restarting capture: \(error.localizedDescription, privacy: .public)")
                lost()
                return
            }
            queue.asyncAfter(deadline: .now() + .milliseconds(200)) { [weak self] in
                self?.restartSession(attempt: attempt + 1, avoiding: lostID)
            }
        }
    }

    /// Ends a capture whose microphone cannot come back. Buffers already on their way land
    /// first, so the samples the recorder keeps are complete. Call on the queue.
    private func lost() {
        let wasRunning = flags.withLock { flags -> Bool in
            let wasRunning = flags.isRunning && !flags.isClosing
            flags.isRunning = false
            return wasRunning
        }
        guard wasRunning else { return }
        deliveryQueue.sync {}
        onLost?()
    }

    // MARK: - Samples

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        let frames = CMSampleBufferGetNumSamples(sampleBuffer)
        guard frames > 0 else { return }
        do {
            let buffer = try Self.pcmBuffer(from: sampleBuffer, frames: frames)
            guard let converter else { throw SlovoError("Could not create the 16 kHz capture format") }
            let samples = try converter.convert(buffer)
            capture.append(samples, sourceRate: buffer.format.sampleRate)
        } catch {
            log.error("Dropped a microphone buffer: \(error.localizedDescription, privacy: .public)")
            capture.markGap()
        }
    }

    private static func pcmBuffer(from sampleBuffer: CMSampleBuffer, frames: Int) throws -> AVAudioPCMBuffer {
        guard let description = CMSampleBufferGetFormatDescription(sampleBuffer),
              var basic = CMAudioFormatDescriptionGetStreamBasicDescription(description)?.pointee,
              let format = AVAudioFormat(streamDescription: &basic),
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)) else {
            throw SlovoError("Could not allocate a PCM capture buffer")
        }
        // The buffer list reports frameLength worth of bytes, so an empty buffer has no room and
        // the copy fails; set the length first.
        buffer.frameLength = AVAudioFrameCount(frames)
        let status = CMSampleBufferCopyPCMDataIntoAudioBufferList(sampleBuffer, at: 0, frameCount: Int32(frames),
                                                                  into: buffer.mutableAudioBufferList)
        guard status == noErr else { throw SlovoError("Could not copy microphone PCM (OSStatus \(status))") }
        return buffer
    }
}

/// Samples shared between the delivery queue and the main actor.
final class CaptureBuffer: Sendable {
    /// About 50 ms at 16 kHz, the window the level meter reads.
    static let meterWindow = 800

    private struct State {
        var samples: [Float] = []
        var recentRMS: Float = 0
        var sourceRate: Double?
        var allowsIncrementalDecode = true
        var hasGap = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    func append(_ chunk: [Float], sourceRate: Double = MicrophoneRecorder.sampleRate) {
        let rms = SpeechEnergy.rms(chunk.suffix(Self.meterWindow))
        state.withLock { state in
            if let previousRate = state.sourceRate, previousRate != sourceRate {
                state.allowsIncrementalDecode = false
            }
            state.sourceRate = sourceRate
            guard !chunk.isEmpty else { return }
            state.samples.append(contentsOf: chunk)
            state.recentRMS = rms
        }
    }

    var recentRMS: Float {
        state.withLock { $0.recentRMS }
    }

    var allowsIncrementalDecode: Bool {
        state.withLock { $0.allowsIncrementalDecode }
    }

    func invalidateIncrementalDecode() {
        state.withLock { $0.allowsIncrementalDecode = false }
    }

    var hasGap: Bool {
        state.withLock { $0.hasGap }
    }

    func markGap() {
        state.withLock {
            $0.hasGap = true
            $0.allowsIncrementalDecode = false
        }
    }

    func hasFinalizedWindow(_ window: Range<Int>) -> Bool {
        state.withLock { $0.allowsIncrementalDecode && $0.samples.count > window.upperBound }
    }

    func finalizedWindow(_ window: Range<Int>) -> [Float]? {
        state.withLock { state in
            guard state.allowsIncrementalDecode, state.samples.count > window.upperBound else { return nil }
            return Array(state.samples[window])
        }
    }

    /// Everything captured so far; the buffer starts empty again.
    func takeSamples() -> [Float] {
        state.withLock { state in
            let samples = state.samples
            state.samples = []
            state.recentRMS = 0
            return samples
        }
    }
}

/// Turns PCM buffers of any format into mono Float32 at the target rate.
///
/// Used from one thread at a time. The resampler keeps its filter state between buffers,
/// so chunk borders do not click; it is recreated when the rate changes.
final class CaptureConverter: @unchecked Sendable {
    let targetRate: Double
    private let targetFormat: AVAudioFormat
    private var sourceFormat: AVAudioFormat?
    private var resampler: AVAudioConverter?

    init?(targetRate: Double) {
        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: targetRate,
                                         channels: 1, interleaved: false) else { return nil }
        self.targetRate = targetRate
        self.targetFormat = format
    }

    func convert(_ buffer: AVAudioPCMBuffer) throws -> [Float] {
        guard buffer.frameLength > 0 else { return [] }
        let rate = buffer.format.sampleRate
        guard rate > 0 else { throw SlovoError("Invalid microphone sample rate") }
        let mono = Self.downmix(buffer)
        guard !mono.isEmpty else { throw SlovoError("Unsupported microphone PCM format") }
        if abs(rate - targetRate) < 0.5 {
            return mono
        }
        return try resample(mono, from: rate)
    }

    /// Averages the channels of any PCM buffer into one Float32 channel.
    static func downmix(_ buffer: AVAudioPCMBuffer) -> [Float] {
        let frames = Int(buffer.frameLength)
        let channels = Int(buffer.format.channelCount)
        guard frames > 0, channels > 0 else { return [] }
        let interleaved = buffer.format.isInterleaved
        let stride = buffer.stride
        var mono = [Float](repeating: 0, count: frames)

        func accumulate<T>(_ data: UnsafePointer<UnsafeMutablePointer<T>>, scale: Float, value: (T) -> Float) {
            for channel in 0..<channels {
                let pointer = data[interleaved ? 0 : channel]
                let offset = interleaved ? channel : 0
                for frame in 0..<frames {
                    mono[frame] += value(pointer[frame * stride + offset]) * scale
                }
            }
        }

        let perChannel = 1 / Float(channels)
        if let data = buffer.floatChannelData {
            accumulate(data, scale: perChannel) { $0 }
        } else if let data = buffer.int16ChannelData {
            accumulate(data, scale: perChannel / 32_768) { Float($0) }
        } else if let data = buffer.int32ChannelData {
            accumulate(data, scale: perChannel / 2_147_483_648) { Float($0) }
        } else {
            return []
        }
        return mono
    }

    private func resample(_ mono: [Float], from rate: Double) throws -> [Float] {
        if resampler == nil || sourceFormat?.sampleRate != rate {
            guard let source = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: 1,
                                             interleaved: false),
                  let converter = AVAudioConverter(from: source, to: targetFormat) else {
                throw SlovoError("Could not create the microphone sample-rate converter")
            }
            sourceFormat = source
            resampler = converter
        }
        guard let resampler, let sourceFormat,
              let input = AVAudioPCMBuffer(pcmFormat: sourceFormat, frameCapacity: AVAudioFrameCount(mono.count)),
              let inputChannel = input.floatChannelData?[0] else {
            throw SlovoError("Could not allocate microphone converter input")
        }
        mono.withUnsafeBufferPointer { samples in
            guard let base = samples.baseAddress else { return }
            inputChannel.update(from: base, count: samples.count)
        }
        input.frameLength = AVAudioFrameCount(mono.count)

        let capacity = AVAudioFrameCount((Double(mono.count) * targetRate / rate).rounded(.up)) + 512
        guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity),
              let channel = output.floatChannelData?[0] else {
            throw SlovoError("Could not allocate microphone converter output")
        }
        var supplied = false
        var error: NSError?
        // `.noDataNow` instead of end of stream keeps the converter ready for the next buffer.
        let status = resampler.convert(to: output, error: &error) { _, inputStatus in
            if supplied {
                inputStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            inputStatus.pointee = .haveData
            return input
        }
        if status == .error {
            self.resampler = nil
            if let error { throw error }
            throw SlovoError("Microphone sample-rate conversion failed")
        }
        return Array(UnsafeBufferPointer(start: channel, count: Int(output.frameLength)))
    }
}

/// Decides whether a recording contains anything worth sending to an engine.
public enum SpeechEnergy {
    /// Recordings shorter than this are dropped as "Nothing heard".
    public static let minimumDuration: TimeInterval = 0.3
    /// Frame RMS at or above this counts as sound, about −46 dBFS. Kept low on purpose:
    /// dropping quiet speech is worse than sending a click to an engine that returns nothing.
    public static let speechRMS: Float = 0.005
    static let frameSeconds = 0.03
    /// About 90 ms of sound in total, so a single click or pop is not speech.
    static let minimumLoudFrames = 3

    /// True when the audio is too short or has no frame loud enough to be speech.
    public static func isEffectivelySilent(_ audio: RecordedAudio) -> Bool {
        guard audio.sampleRate > 0, audio.duration >= minimumDuration else { return true }
        let samples = audio.samples
        let frameLength = max(1, Int(frameSeconds * audio.sampleRate))
        var loudFrames = 0
        var start = 0
        while start < samples.count {
            let end = min(start + frameLength, samples.count)
            if varianceRMS(samples[start..<end]) >= speechRMS {
                loudFrames += 1
                if loudFrames >= minimumLoudFrames {
                    return false
                }
            }
            start = end
        }
        return true
    }

    /// RMS of a window of samples, the value the level meter smooths.
    public static func rms(_ samples: ArraySlice<Float>) -> Float {
        guard !samples.isEmpty else { return 0 }
        var sum: Float = 0
        for sample in samples {
            sum += sample * sample
        }
        return (sum / Float(samples.count)).squareRoot()
    }

    /// Maps an RMS value to the meter's 0…1 scale: −55 dBFS and below is empty, −15 dBFS
    /// and above is full, which spreads normal speech over most of the bar.
    public static func meterLevel(rms: Float) -> Float {
        guard rms > 0 else { return 0 }
        let decibels = 20 * log10(rms)
        return min(1, max(0, (decibels + 55) / 40))
    }

    /// RMS around the window's mean, so a constant DC offset from a cheap interface does
    /// not look like sound.
    static func varianceRMS(_ samples: ArraySlice<Float>) -> Float {
        guard !samples.isEmpty else { return 0 }
        var sum: Float = 0
        var squares: Float = 0
        for sample in samples {
            sum += sample
            squares += sample * sample
        }
        let count = Float(samples.count)
        let mean = sum / count
        return max(0, squares / count - mean * mean).squareRoot()
    }
}
