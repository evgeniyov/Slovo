import AVFoundation
import CoreAudio
import Foundation
import os

/// Records everything other apps play through a Core Audio process tap
/// (`CATapDescription` excluding Slovo, a private aggregate device and an IOProc),
/// written continuously as a 16 kHz mono CAF file.
///
/// Callbacks arrive on Core Audio's thread; the recorder hops to the main actor.
///
/// Without System Audio Recording permission macOS still creates the tap but feeds
/// it zeros, so the only sign of a denial is silence: `onSilence` fires when nothing
/// but zeros arrived during the first `silenceTimeout` seconds.
public final class SystemAudioTap: @unchecked Sendable {
    /// Process taps need macOS 14.4, where the System Audio Recording permission exists.
    public static var isSupported: Bool {
        if #available(macOS 14.4, *) {
            return true
        }
        return false
    }

    // A call commonly opens with a few quiet seconds — joining, a waiting room, nobody
    // having said anything yet — which is not a sign of a denied permission. Long enough
    // to ride past that, short enough that a genuine denial is still caught early in the
    // meeting rather than found out only once it is over.
    public static let silenceTimeout: TimeInterval = 8

    /// Level from 0 to 1, about 30 times per second. Set before `start`.
    public var onLevel: ((Float) -> Void)?
    /// Called once when the tap has delivered only zeros for `silenceTimeout` seconds. Set before `start`.
    public var onSilence: (() -> Void)?
    /// Called once when real audio arrives after `onSilence`. Set before `start`.
    public var onSignal: (() -> Void)?
    /// Called when the tap could not be rebuilt after the output device changed, so system
    /// audio stopped arriving; it keeps being retried meanwhile. Set before `start`.
    public var onStopped: ((Error) -> Void)?
    /// Called when a retry after `onStopped` brought the tap back. Set before `start`.
    public var onResumed: (() -> Void)?

    private let control = DispatchQueue(label: "local.slovo.Slovo.SystemAudioTap.control")
    /// Accessed only on `control`.
    private var session: ProcessTapSession?
    private var writer: MeetingTrackWriter?

    public init() {}

    deinit {
        session?.stop()
        writer?.finish()
    }

    public func start(writingTo url: URL) throws {
        try start(writer: MeetingTrackWriter(url: url))
    }

    /// Starts with a writer whose timeline origin other tracks share.
    func start(writer: MeetingTrackWriter) throws {
        guard #available(macOS 14.4, *) else {
            throw MeetingErrors.systemAudioUnsupported()
        }
        try control.sync {
            guard session == nil else { return }
            let processor = TapProcessor(writer: writer, onLevel: onLevel, onSilence: onSilence, onSignal: onSignal)
            let session = ProcessTapSession(processor: processor, controlQueue: control,
                                            onStopped: onStopped, onResumed: onResumed)
            try session.start()
            self.session = session
            self.writer = writer
        }
    }

    /// Tears the tap down and closes the file. Safe to call more than once.
    public func stop() {
        let writer: MeetingTrackWriter? = control.sync {
            session?.stop()
            session = nil
            let writer = self.writer
            self.writer = nil
            return writer
        }
        writer?.finish()
    }

    /// Whether any non-zero sample arrived since `start`.
    var heardSignal: Bool {
        control.sync { session?.processor.heardSignal ?? false }
    }
}

/// Converts and writes tap buffers. `process` runs on the IO queue; the counters are
/// read from the control queue under a lock.
private final class TapProcessor: @unchecked Sendable {
    private let writer: MeetingTrackWriter
    private let onLevel: ((Float) -> Void)?
    private let onSilence: (() -> Void)?
    private let onSignal: (() -> Void)?

    /// Accessed only on the IO queue (or before the IOProc exists).
    private var resampler: MeetingResampler?
    private var tapChannels = 1
    private var meter = LevelMeter()
    private var detector = SilenceDetector(timeout: SystemAudioTap.silenceTimeout)

    private let lock = NSLock()
    private var buffers = 0
    private var signal = false

    init(writer: MeetingTrackWriter, onLevel: ((Float) -> Void)?, onSilence: (() -> Void)?, onSignal: (() -> Void)?) {
        self.writer = writer
        self.onLevel = onLevel
        self.onSilence = onSilence
        self.onSignal = onSignal
    }

    var bufferCount: Int {
        lock.withLock { buffers }
    }

    var heardSignal: Bool {
        lock.withLock { signal }
    }

    func configure(format: AudioStreamBasicDescription) throws {
        resampler = try MeetingResampler(sourceSampleRate: format.mSampleRate)
        tapChannels = max(1, Int(format.mChannelsPerFrame))
    }

    func process(_ input: UnsafePointer<AudioBufferList>, time: UnsafePointer<AudioTimeStamp>) {
        guard let resampler else { return }
        let mono = MeetingAudioMath.mixdown(input, trailingChannels: tapChannels)
        guard !mono.isEmpty else { return }
        let allZero = !mono.contains { $0 != 0 }
        lock.withLock {
            buffers += 1
            if !allZero {
                signal = true
            }
        }
        report(detector.consume(frames: mono.count, sampleRate: resampler.sourceSampleRate, allZero: allZero))

        let converted = resampler.process(mono)
        let stamp = time.pointee
        writer.append(converted, hostTime: stamp.mFlags.contains(.hostTimeValid) ? stamp.mHostTime : nil)
        if let level = meter.consume(converted) {
            onLevel?(level)
        }
    }

    /// No buffers at all for this long counts as silence too.
    func noAudio(forSeconds seconds: TimeInterval) {
        report(detector.noAudio(forSeconds: seconds))
    }

    private func report(_ event: SilenceDetector.Event?) {
        switch event {
        case .silence: onSilence?()
        case .signalAfterSilence: onSignal?()
        case nil: break
        }
    }
}

/// The Core Audio objects behind one tap: the process tap, a private aggregate device
/// that exposes it as an input stream, and an IOProc reading that stream. Every
/// method runs on the control queue; the IOProc runs on its own queue, because
/// destroying an IOProc waits for its running block and must not be called from it.
private final class ProcessTapSession {
    enum Composition {
        /// Only the tap, as in Apple's sample: nothing else is opened, in particular not
        /// the microphone of a headset that is also the output device.
        case tapOnly
        /// The default output device as clock plus the tap (the AudioCap layout), for
        /// when a tap-only device never starts delivering buffers.
        case withOutputDevice
    }

    /// Fallback delay when a tap-only device delivers nothing.
    static let fallbackDelay: TimeInterval = 1.5
    /// Output device changes come in bursts; rebuild once they settle.
    static let rebuildDelay: TimeInterval = 0.3
    /// The wait between attempts to bring back a tap that could not be rebuilt.
    static let retryDelay: TimeInterval = 3

    let processor: TapProcessor
    private let controlQueue: DispatchQueue
    private let onStopped: ((Error) -> Void)?
    private let onResumed: (() -> Void)?
    private let ioQueue = DispatchQueue(label: "local.slovo.Slovo.SystemAudioTap.io", qos: .userInitiated)
    private let listenerQueue = DispatchQueue(label: "local.slovo.Slovo.SystemAudioTap.listener")
    private let logger = SlovoLog.logger("SystemAudioTap")

    private var composition: Composition = .tapOnly
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var ioProcID: AudioDeviceIOProcID?
    private var outputListener: AudioObjectPropertyListenerBlock?
    private var pendingRebuild: DispatchWorkItem?
    private var running = false
    private var startedAt: TimeInterval = 0
    /// Whether the tap is down after a failed rebuild and `onStopped` was reported.
    private var interrupted = false

    init(processor: TapProcessor, controlQueue: DispatchQueue, onStopped: ((Error) -> Void)?,
         onResumed: (() -> Void)?) {
        self.processor = processor
        self.controlQueue = controlQueue
        self.onStopped = onStopped
        self.onResumed = onResumed
    }

    func start() throws {
        guard #available(macOS 14.4, *) else {
            throw MeetingErrors.systemAudioUnsupported()
        }
        try build(.tapOnly)
        running = true
        startedAt = ProcessInfo.processInfo.systemUptime
        addOutputListener()
        scheduleWatchdogs()
    }

    func stop() {
        guard running else { return }
        running = false
        pendingRebuild?.cancel()
        pendingRebuild = nil
        removeOutputListener()
        teardown()
    }

    // MARK: Building

    @available(macOS 14.4, *)
    private func build(_ composition: Composition) throws {
        do {
            try createTap()
            let format = try Self.tapFormat(tapID)
            guard format.mFormatID == kAudioFormatLinearPCM,
                  format.mFormatFlags & kAudioFormatFlagIsFloat != 0,
                  format.mBitsPerChannel == 32,
                  format.mSampleRate > 0,
                  format.mChannelsPerFrame > 0 else {
                throw MeetingErrors.unsupportedAudioFormat()
            }
            try createAggregateDevice(composition)
            // Buffers arrive at the aggregate device's rate, which with an output device as
            // clock can differ from the tap's own format.
            var ioFormat = format
            if let rate = Self.nominalSampleRate(aggregateID), rate != format.mSampleRate {
                logger.info("Tap format \(format.mSampleRate, privacy: .public) Hz, device runs at \(rate, privacy: .public) Hz")
                ioFormat.mSampleRate = rate
            }
            try ioQueue.sync {
                try processor.configure(format: ioFormat)
            }
            try startIO()
            self.composition = composition
            logger.info("System audio tap running (\(String(describing: composition), privacy: .public), \(format.mSampleRate, privacy: .public) Hz)")
        } catch {
            teardown()
            throw error
        }
    }

    @available(macOS 14.4, *)
    private func createTap() throws {
        // Excluding Slovo keeps its own sounds out; when the process has no audio
        // object yet there is nothing of ours to exclude.
        let ownProcess = Self.processObject(for: getpid())
        let description = CATapDescription(monoGlobalTapButExcludeProcesses: ownProcess.map { [$0] } ?? [])
        description.name = "Slovo meeting audio"
        description.uuid = UUID()
        description.isPrivate = true
        description.muteBehavior = .unmuted

        var newTap = AudioObjectID(kAudioObjectUnknown)
        let status = AudioHardwareCreateProcessTap(description, &newTap)
        guard status == noErr, newTap != AudioObjectID(kAudioObjectUnknown) else {
            throw MeetingErrors.tapFailed("AudioHardwareCreateProcessTap", status)
        }
        tapID = newTap
    }

    private func createAggregateDevice(_ composition: Composition) throws {
        let tapUID = try Self.tapUID(tapID)
        var description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Slovo meeting audio",
            kAudioAggregateDeviceUIDKey: "local.slovo.Slovo.meeting-tap.\(UUID().uuidString)",
            kAudioAggregateDeviceIsPrivateKey: 1,
            kAudioAggregateDeviceIsStackedKey: 0,
            // Keep IO running while nothing plays, so silence stays on the timeline and
            // a denied permission shows up as zeros.
            kAudioAggregateDeviceTapAutoStartKey: 0
        ]
        switch composition {
        case .tapOnly:
            description[kAudioAggregateDeviceTapListKey] = [[kAudioSubTapUIDKey: tapUID]]
        case .withOutputDevice:
            let outputUID = try Self.defaultOutputDeviceUID()
            description[kAudioAggregateDeviceMainSubDeviceKey] = outputUID
            description[kAudioAggregateDeviceSubDeviceListKey] = [[kAudioSubDeviceUIDKey: outputUID]]
            description[kAudioAggregateDeviceTapListKey] = [[kAudioSubTapUIDKey: tapUID, kAudioSubTapDriftCompensationKey: 1]]
        }

        var newDevice = AudioObjectID(kAudioObjectUnknown)
        let status = AudioHardwareCreateAggregateDevice(description as CFDictionary, &newDevice)
        guard status == noErr, newDevice != AudioObjectID(kAudioObjectUnknown) else {
            throw MeetingErrors.tapFailed("AudioHardwareCreateAggregateDevice", status)
        }
        aggregateID = newDevice
    }

    private func startIO() throws {
        let processor = self.processor
        var newProc: AudioDeviceIOProcID?
        var status = AudioDeviceCreateIOProcIDWithBlock(&newProc, aggregateID, ioQueue) { _, input, inputTime, _, _ in
            processor.process(input, time: inputTime)
        }
        guard status == noErr, let newProc else {
            throw MeetingErrors.tapFailed("AudioDeviceCreateIOProcIDWithBlock", status)
        }
        ioProcID = newProc
        status = AudioDeviceStart(aggregateID, newProc)
        guard status == noErr else {
            throw MeetingErrors.tapFailed("AudioDeviceStart", status)
        }
    }

    /// Stop, then destroy in reverse order of creation.
    private func teardown() {
        if aggregateID != AudioObjectID(kAudioObjectUnknown) {
            if let ioProcID {
                logFailure(AudioDeviceStop(aggregateID, ioProcID), "AudioDeviceStop")
                logFailure(AudioDeviceDestroyIOProcID(aggregateID, ioProcID), "AudioDeviceDestroyIOProcID")
            }
            logFailure(AudioHardwareDestroyAggregateDevice(aggregateID), "AudioHardwareDestroyAggregateDevice")
        }
        ioProcID = nil
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        if tapID != AudioObjectID(kAudioObjectUnknown) {
            if #available(macOS 14.2, *) {
                logFailure(AudioHardwareDestroyProcessTap(tapID), "AudioHardwareDestroyProcessTap")
            }
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
    }

    // MARK: Recovery

    /// A tap-only device that delivers nothing gets the output-device layout; if no
    /// buffers arrive at all, that is reported like silence.
    private func scheduleWatchdogs() {
        controlQueue.asyncAfter(deadline: .now() + Self.fallbackDelay) { [weak self] in
            guard let self, self.running, self.composition == .tapOnly, self.processor.bufferCount == 0 else { return }
            self.logger.warning("Tap-only aggregate device delivered no audio; adding the output device as clock")
            self.rebuild(preferring: .withOutputDevice)
        }
        controlQueue.asyncAfter(deadline: .now() + SystemAudioTap.silenceTimeout + 0.5) { [weak self] in
            guard let self, self.running, self.processor.bufferCount == 0 else { return }
            let seconds = ProcessInfo.processInfo.systemUptime - self.startedAt
            self.ioQueue.async {
                self.processor.noAudio(forSeconds: seconds)
            }
        }
    }

    /// A tap that cannot be rebuilt in either layout is not given up on silently: the
    /// owner hears about it once, and it is tried again every `retryDelay` for as long as
    /// the recording runs, since the device that broke it is often back seconds later.
    private func rebuild(preferring preferred: Composition) {
        guard #available(macOS 14.4, *), running else { return }
        teardown()
        do {
            try build(preferred)
        } catch {
            logger.error("Rebuilding the system audio tap failed: \(error.localizedDescription, privacy: .public)")
            let other: Composition = preferred == .tapOnly ? .withOutputDevice : .tapOnly
            do {
                try build(other)
            } catch {
                logger.error("System audio tap unavailable: \(error.localizedDescription, privacy: .public)")
                interrupt(error, preferring: preferred)
                return
            }
        }
        if interrupted {
            interrupted = false
            logger.notice("The system audio tap is back")
            onResumed?()
        }
    }

    private func interrupt(_ error: Error, preferring preferred: Composition) {
        if !interrupted {
            interrupted = true
            onStopped?(error)
        }
        pendingRebuild?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.running else { return }
            self.rebuild(preferring: preferred)
        }
        pendingRebuild = work
        controlQueue.asyncAfter(deadline: .now() + Self.retryDelay, execute: work)
    }

    /// A new default output device (headphones plugged in, AirPods connected) can stop
    /// the aggregate device, so the objects are rebuilt with the same writer; the
    /// writer fills the short gap with silence.
    private func addOutputListener() {
        var address = Self.address(kAudioHardwarePropertyDefaultOutputDevice)
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self else { return }
            self.controlQueue.async {
                self.outputDeviceChanged()
            }
        }
        let status = AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address,
                                                         listenerQueue, listener)
        if status == noErr {
            outputListener = listener
        } else {
            logFailure(status, "AudioObjectAddPropertyListenerBlock")
        }
    }

    private func removeOutputListener() {
        guard let outputListener else { return }
        var address = Self.address(kAudioHardwarePropertyDefaultOutputDevice)
        logFailure(AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address,
                                                          listenerQueue, outputListener),
                   "AudioObjectRemovePropertyListenerBlock")
        self.outputListener = nil
    }

    private func outputDeviceChanged() {
        guard running else { return }
        pendingRebuild?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.running else { return }
            self.logger.info("Default output device changed; rebuilding the system audio tap")
            self.rebuild(preferring: self.composition)
        }
        pendingRebuild = work
        controlQueue.asyncAfter(deadline: .now() + Self.rebuildDelay, execute: work)
    }

    private func logFailure(_ status: OSStatus, _ function: String) {
        if status != noErr {
            logger.warning("\(function, privacy: .public) failed: \(status, privacy: .public)")
        }
    }

    // MARK: Core Audio properties

    private static func address(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                   mElement: kAudioObjectPropertyElementMain)
    }

    static func processObject(for pid: pid_t) -> AudioObjectID? {
        var address = address(kAudioHardwarePropertyTranslatePIDToProcessObject)
        var qualifier = pid
        var object = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                                UInt32(MemoryLayout<pid_t>.size), &qualifier, &size, &object)
        guard status == noErr, object != AudioObjectID(kAudioObjectUnknown) else { return nil }
        return object
    }

    private static func tapFormat(_ tap: AudioObjectID) throws -> AudioStreamBasicDescription {
        var address = address(kAudioTapPropertyFormat)
        var format = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        let status = AudioObjectGetPropertyData(tap, &address, 0, nil, &size, &format)
        guard status == noErr else {
            throw MeetingErrors.tapFailed("kAudioTapPropertyFormat", status)
        }
        return format
    }

    private static func nominalSampleRate(_ device: AudioObjectID) -> Double? {
        var address = address(kAudioDevicePropertyNominalSampleRate)
        var rate: Float64 = 0
        var size = UInt32(MemoryLayout<Float64>.size)
        let status = AudioObjectGetPropertyData(device, &address, 0, nil, &size, &rate)
        return status == noErr && rate > 0 ? rate : nil
    }

    private static func tapUID(_ tap: AudioObjectID) throws -> String {
        try stringProperty(tap, kAudioTapPropertyUID, name: "kAudioTapPropertyUID")
    }

    private static func defaultOutputDeviceUID() throws -> String {
        var address = address(kAudioHardwarePropertyDefaultOutputDevice)
        var device = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device)
        guard status == noErr, device != AudioObjectID(kAudioObjectUnknown) else {
            throw MeetingErrors.tapFailed("kAudioHardwarePropertyDefaultOutputDevice", status)
        }
        return try stringProperty(device, kAudioDevicePropertyDeviceUID, name: "kAudioDevicePropertyDeviceUID")
    }

    private static func stringProperty(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector,
                                       name: String) throws -> String {
        var address = address(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value)
        guard status == noErr, let value else {
            throw MeetingErrors.tapFailed(name, status)
        }
        return value.takeRetainedValue() as String
    }
}
