import AVFoundation
import CoreAudio
import CoreMedia
import Foundation
import os

/// The microphone track of a meeting: an `AVCaptureSession` of its own, separate from
/// dictation's, converted to 16 kHz mono and handed to a `MeetingTrackWriter`.
///
/// `AVAudioEngine` is not used: choosing a non-default input on its input node posts a
/// configuration change that stops the engine at random, so the track stayed empty or
/// recorded the previous device. A capture session binds to one device, starts within
/// about 0.1 s and converts to the track format itself.
///
/// Session work happens on a private serial queue and samples arrive on a separate
/// delivery queue. The session is replaced when its device disconnects, when it fails or
/// stops by itself, when the chosen microphone is connected again and, while the system
/// default is in use, when the default input changes. Every buffer carries its capture
/// time, so the writer fills the switch-over gap with silence and the track stays on the
/// timeline it shares with system audio.
final class MeetingMicrophoneCapture: @unchecked Sendable {
    /// A lost microphone is retried this long before the owner is told it is gone. A
    /// Bluetooth handoff — AirPods connecting, a headset plugged in — can leave the input
    /// missing for many seconds, which is not worth a warning; the retries themselves
    /// never stop while the recording runs.
    static let restartBudget: TimeInterval = 30
    /// The first wait between attempts; it grows towards `maximumRetryDelay`, so a device
    /// that comes back at once is caught immediately and a long outage costs few attempts.
    static let restartRetryDelay: TimeInterval = 0.3
    static let maximumRetryDelay: TimeInterval = 2
    /// Device notifications come in bursts while hardware settles; switch once they stop.
    static let switchDelay: TimeInterval = 0.3

    /// Level from 0 to 1, about 30 times per second, on the delivery queue. Set before `start`.
    var onLevel: ((Float) -> Void)?
    /// No microphone could be opened within `restartBudget`. The recording goes on and so
    /// do the retries; this only says the track is silent meanwhile. Set before `start`.
    var onMicrophoneLost: ((Error) -> Void)?
    /// A microphone opened again after `onMicrophoneLost`. Set before `start`.
    var onMicrophoneBack: (() -> Void)?

    private let writer: MeetingTrackWriter
    private let deviceUID: String?
    private let queue = DispatchQueue(label: "local.slovo.Slovo.MeetingMicrophone", qos: .userInitiated)
    private let deliveryQueue = DispatchQueue(label: "local.slovo.Slovo.MeetingMicrophone.samples", qos: .userInitiated)
    private let listenerQueue = DispatchQueue(label: "local.slovo.Slovo.MeetingMicrophone.listener")
    private let logger = SlovoLog.logger("MeetingMicrophone")

    /// Everything below is accessed only on `queue`.
    private var running = false
    /// Whether the loss has been reported and not yet taken back, so a long outage is
    /// announced once rather than at every attempt.
    private var lost = false
    /// Bumped by every restart and by `stop`, so retries scheduled earlier stand down.
    private var epoch = 0
    /// Identifies the open session to its observers; bumped when a session opens or closes.
    private var sessionToken = 0
    private var session: AVCaptureSession?
    private var output: AVCaptureAudioDataOutput?
    private var sink: MeetingMicrophoneSink?
    private var currentDeviceUID: String?
    private var sessionObservers: [NSObjectProtocol] = []
    private var deviceObservers: [NSObjectProtocol] = []
    private var defaultInputListener: AudioObjectPropertyListenerBlock?
    private var pendingSwitch: DispatchWorkItem?

    init(writer: MeetingTrackWriter, deviceUID: String?) {
        self.writer = writer
        self.deviceUID = deviceUID
    }

    /// Opens the chosen microphone, or the system default when it is missing or fails to
    /// open. Blocks while the device starts, so call it off the main thread.
    func start() throws {
        try queue.sync {
            guard !running else { return }
            try openPreferredDevice()
            running = true
            addDeviceObservers()
            addDefaultInputListener()
        }
    }

    /// Stops capture and waits until every delivered buffer has been handed to the writer.
    /// Safe to call more than once.
    func stop() {
        queue.sync {
            running = false
            epoch += 1
            pendingSwitch?.cancel()
            pendingSwitch = nil
            removeDeviceObservers()
            removeDefaultInputListener()
            closeSession()
        }
    }

    // MARK: - Devices

    /// The unique ids to try in order: the chosen microphone when it is connected, then the
    /// system default, then every other connected input. A capture device's unique id is
    /// its Core Audio UID, the value the microphone setting stores.
    ///
    /// The rest matter because a default that cannot be opened — AirPods halfway through a
    /// handoff — would otherwise take every attempt the budget buys while a working
    /// microphone sat unused. The built-in one goes last: it is the device that is always
    /// there, so it is the last resort rather than a replacement for what was plugged in.
    static func preferredDeviceUIDs(chosen: String?, isConnected: (String) -> Bool, defaultUID: String?,
                                    connected: [String] = []) -> [String] {
        var uids: [String] = []
        if let chosen, !chosen.isEmpty, isConnected(chosen) {
            uids.append(chosen)
        }
        if let defaultUID, !defaultUID.isEmpty, !uids.contains(defaultUID) {
            uids.append(defaultUID)
        }
        let rest = connected.filter { !$0.isEmpty && !uids.contains($0) }
        uids.append(contentsOf: rest.filter { !isBuiltIn($0) })
        uids.append(contentsOf: rest.filter { isBuiltIn($0) })
        return uids
    }

    /// Core Audio names the Mac's own inputs `BuiltInMicrophoneDevice` and the like.
    private static func isBuiltIn(_ uid: String) -> Bool {
        uid.hasPrefix("BuiltIn")
    }

    private func candidateDevices() -> [AVCaptureDevice] {
        var connected: [String: AVCaptureDevice] = [:]
        if let deviceUID, !deviceUID.isEmpty,
           let chosen = AVCaptureDevice(uniqueID: deviceUID), chosen.hasMediaType(.audio), chosen.isConnected {
            connected[deviceUID] = chosen
        }
        let fallback = AVCaptureDevice.default(for: .audio)
        if let fallback {
            connected[fallback.uniqueID] = fallback
        }
        // Core Audio lists every input, including the ones that are neither chosen nor
        // default and would otherwise never be tried.
        var others: [String] = []
        for uid in AudioDevices.inputDevices().map(\.uid) where connected[uid] == nil {
            guard let device = AVCaptureDevice(uniqueID: uid), device.hasMediaType(.audio), device.isConnected else {
                continue
            }
            connected[uid] = device
            others.append(uid)
        }
        let uids = Self.preferredDeviceUIDs(chosen: deviceUID, isConnected: { connected[$0] != nil },
                                            defaultUID: fallback?.uniqueID, connected: others)
        return uids.compactMap { connected[$0] }
    }

    private func openPreferredDevice() throws {
        let candidates = candidateDevices()
        guard !candidates.isEmpty else {
            throw MeetingErrors.noMicrophone()
        }
        if let deviceUID, !deviceUID.isEmpty, candidates.first?.uniqueID != deviceUID {
            logger.notice("The chosen microphone is not connected; recording from the system default")
        }
        var lastError: Error = MeetingErrors.noMicrophone()
        for device in candidates {
            do {
                try openSession(on: device)
                return
            } catch {
                logger.warning("A microphone did not open: \(error.localizedDescription, privacy: .public)")
                lastError = error
            }
        }
        throw lastError
    }

    // MARK: - Session

    /// The track format, so the capture output converts and no resampling is needed here in
    /// the usual case.
    private static let outputSettings: [String: Any] = [
        AVFormatIDKey: kAudioFormatLinearPCM,
        AVSampleRateKey: Double(MeetingAudioFormat.sampleRate),
        AVNumberOfChannelsKey: 1,
        AVLinearPCMBitDepthKey: 32,
        AVLinearPCMIsFloatKey: true,
        AVLinearPCMIsNonInterleaved: false,
        AVLinearPCMIsBigEndianKey: false
    ]

    private func openSession(on device: AVCaptureDevice) throws {
        let session = AVCaptureSession()
        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            throw MeetingErrors.microphoneFailed(error)
        }
        let output = AVCaptureAudioDataOutput()
        output.audioSettings = Self.outputSettings
        guard session.canAddInput(input), session.canAddOutput(output) else {
            throw MeetingErrors.unsupportedAudioFormat()
        }
        session.addInput(input)
        session.addOutput(output)

        let sink = MeetingMicrophoneSink(writer: writer, onLevel: onLevel, clock: session.synchronizationClock)
        output.setSampleBufferDelegate(sink, queue: deliveryQueue)
        // Observed before starting, so a session that fails right after it started is not
        // missed; notifications carrying an older token are ignored.
        sessionToken += 1
        observe(session, token: sessionToken)
        session.startRunning()
        guard session.isRunning else {
            removeSessionObservers()
            sessionToken += 1
            output.setSampleBufferDelegate(nil, queue: nil)
            throw MeetingErrors.microphoneFailed(SlovoError(device.localizedName))
        }
        // The session only has a clock once it runs (the device's audio clock, which counts
        // host time); earlier buffers are read as host time directly.
        sink.clock = session.synchronizationClock

        self.session = session
        self.output = output
        self.sink = sink
        currentDeviceUID = device.uniqueID
        let role = device.uniqueID == deviceUID ? "the chosen microphone" : "the system default"
        logger.notice("Meeting microphone capture running on \(role, privacy: .public)")
    }

    /// Stops the session, then waits for buffers already queued for delivery: they belong
    /// on the track before the file is closed or a new session starts.
    private func closeSession() {
        removeSessionObservers()
        sessionToken += 1
        guard let session else { return }
        session.stopRunning()
        output?.setSampleBufferDelegate(nil, queue: nil)
        let sink = self.sink
        deliveryQueue.sync {
            sink?.finish()
        }
        self.session = nil
        output = nil
        self.sink = nil
        currentDeviceUID = nil
    }

    private func observe(_ session: AVCaptureSession, token: Int) {
        let center = NotificationCenter.default
        let failed = center.addObserver(forName: AVCaptureSession.runtimeErrorNotification,
                                        object: session, queue: nil) { [weak self] notification in
            guard let self else { return }
            let error = notification.userInfo?[AVCaptureSessionErrorKey] as? Error
            let reason = "reported an error: \(error?.localizedDescription ?? "unknown")"
            self.queue.async { self.sessionEnded(token: token, reason: reason) }
        }
        let stopped = center.addObserver(forName: AVCaptureSession.didStopRunningNotification,
                                         object: session, queue: nil) { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.sessionEnded(token: token, reason: "stopped by itself") }
        }
        sessionObservers = [failed, stopped]
    }

    private func removeSessionObservers() {
        for observer in sessionObservers {
            NotificationCenter.default.removeObserver(observer)
        }
        sessionObservers = []
    }

    private func sessionEnded(token: Int, reason: String) {
        guard running, token == sessionToken else { return }
        restart(reason: reason)
    }

    // MARK: - Recovery

    private func addDeviceObservers() {
        let center = NotificationCenter.default
        let disconnected = center.addObserver(forName: AVCaptureDevice.wasDisconnectedNotification,
                                              object: nil, queue: nil) { [weak self] notification in
            guard let self else { return }
            let uid = (notification.object as? AVCaptureDevice)?.uniqueID
            self.queue.async { self.deviceDisconnected(uid: uid) }
        }
        let connected = center.addObserver(forName: AVCaptureDevice.wasConnectedNotification,
                                           object: nil, queue: nil) { [weak self] notification in
            guard let self else { return }
            let uid = (notification.object as? AVCaptureDevice)?.uniqueID
            self.queue.async { self.deviceConnected(uid: uid) }
        }
        deviceObservers = [disconnected, connected]
    }

    private func removeDeviceObservers() {
        for observer in deviceObservers {
            NotificationCenter.default.removeObserver(observer)
        }
        deviceObservers = []
    }

    private func deviceDisconnected(uid: String?) {
        guard running, let uid, uid == currentDeviceUID else { return }
        restart(reason: "disconnected")
    }

    /// The chosen microphone coming back replaces the default the recording fell back to.
    private func deviceConnected(uid: String?) {
        guard running, let uid, uid == deviceUID, uid != currentDeviceUID else { return }
        scheduleSwitch()
    }

    /// A new default input (a headset plugged in, AirPods connected) matters only while the
    /// recording uses the default, which is what the old engine followed as well.
    private func defaultInputChanged() {
        guard running, currentDeviceUID == nil || currentDeviceUID != deviceUID else { return }
        scheduleSwitch()
    }

    private func scheduleSwitch() {
        pendingSwitch?.cancel()
        let epoch = self.epoch
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.running, epoch == self.epoch else { return }
            self.pendingSwitch = nil
            guard let preferred = self.candidateDevices().first,
                  preferred.uniqueID != self.currentDeviceUID || self.session == nil else { return }
            self.restart(reason: "switching devices")
        }
        pendingSwitch = work
        queue.asyncAfter(deadline: .now() + Self.switchDelay, execute: work)
    }

    private func restart(reason: String) {
        guard running else { return }
        logger.notice("Meeting microphone \(reason, privacy: .public); restarting capture")
        epoch += 1
        pendingSwitch?.cancel()
        pendingSwitch = nil
        closeSession()
        reopen(startedAt: ProcessInfo.processInfo.systemUptime, delay: Self.restartRetryDelay, epoch: epoch)
    }

    /// The wait before the next attempt, or nil once another wait would run past the
    /// budget. Separate from the retry itself so the schedule can be checked without
    /// devices.
    static func nextRetryDelay(elapsed: TimeInterval, delay: TimeInterval) -> TimeInterval? {
        guard elapsed + delay < restartBudget else { return nil }
        return min(delay * 1.5, maximumRetryDelay)
    }

    /// The system often still switches devices when the notification arrives, so a failed
    /// open is retried. Past the budget the owner is told the microphone is gone, and the
    /// retries carry on at the capped delay for the life of the recording: a device that
    /// may yet come back must not end a meeting, and when it does come back the microphone
    /// rejoins by itself.
    ///
    /// The meeting keeps recording throughout: system audio is never touched here, and
    /// the next buffer carries its own capture time, so the writer fills the microphone
    /// track with silence for the whole outage and both tracks stay on one timeline.
    private func reopen(startedAt: TimeInterval, delay: TimeInterval, epoch: Int) {
        guard running, epoch == self.epoch else { return }
        do {
            try openPreferredDevice()
            guard lost else { return }
            lost = false
            onMicrophoneBack?()
        } catch {
            let elapsed = ProcessInfo.processInfo.systemUptime - startedAt
            let next = Self.nextRetryDelay(elapsed: elapsed, delay: delay)
            if next == nil, !lost {
                lost = true
                logger.error("Meeting microphone still missing, retrying: \(error.localizedDescription, privacy: .public)")
                onMicrophoneLost?(error)
            }
            queue.asyncAfter(deadline: .now() + delay) { [weak self] in
                self?.reopen(startedAt: startedAt, delay: next ?? Self.maximumRetryDelay, epoch: epoch)
            }
        }
    }

    private static var defaultInputAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice,
                                   mScope: kAudioObjectPropertyScopeGlobal,
                                   mElement: kAudioObjectPropertyElementMain)
    }

    private func addDefaultInputListener() {
        var address = Self.defaultInputAddress
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self else { return }
            self.queue.async { self.defaultInputChanged() }
        }
        let status = AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address,
                                                         listenerQueue, listener)
        if status == noErr {
            defaultInputListener = listener
        } else {
            logger.warning("Watching the default input failed: \(status, privacy: .public)")
        }
    }

    private func removeDefaultInputListener() {
        guard let listener = defaultInputListener else { return }
        var address = Self.defaultInputAddress
        let status = AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address,
                                                            listenerQueue, listener)
        if status != noErr {
            logger.warning("Removing the default input listener failed: \(status, privacy: .public)")
        }
        defaultInputListener = nil
    }
}

/// Turns the sample buffers of one capture session into track samples. `process` and
/// `finish` run on the delivery queue, one call at a time.
final class MeetingMicrophoneSink: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate, @unchecked Sendable {
    private let writer: MeetingTrackWriter
    private let onLevel: ((Float) -> Void)?
    private let converter = MeetingCaptureConverter()
    private let clockLock = NSLock()
    private var sessionClock: CMClock?
    private var meter = LevelMeter()
    private var delivered = false

    init(writer: MeetingTrackWriter, onLevel: ((Float) -> Void)?, clock: CMClock?) {
        self.writer = writer
        self.onLevel = onLevel
        sessionClock = clock
    }

    /// The clock presentation timestamps are expressed in; set from the session queue.
    var clock: CMClock? {
        get { clockLock.withLock { sessionClock } }
        set { clockLock.withLock { sessionClock = newValue } }
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        process(sampleBuffer, arrivalHostTime: AudioGetCurrentHostTime())
    }

    func process(_ sampleBuffer: CMSampleBuffer, arrivalHostTime: UInt64) {
        guard let buffer = MeetingCaptureBuffers.pcmBuffer(from: sampleBuffer) else { return }
        let converted = converter.convert(buffer)
        guard !converted.isEmpty else { return }
        // Boosted here, not just for the level meter: a quiet or distant voice should be
        // easier to make out in the saved track itself, not only on the recording indicator.
        let samples = MeetingAudioMath.microphoneBoost(converted)
        let hostTime = MeetingCaptureBuffers.hostTime(presentation: CMSampleBufferGetPresentationTimeStamp(sampleBuffer),
                                                      clock: clock, arrivalHostTime: arrivalHostTime,
                                                      frames: Int(buffer.frameLength),
                                                      sampleRate: buffer.format.sampleRate)
        if !delivered {
            delivered = true
            let delay = Double(AudioConvertHostTimeToNanos(arrivalHostTime)) - Double(AudioConvertHostTimeToNanos(hostTime))
            SlovoLog.logger("MeetingMicrophone").info(
                "First microphone buffer delivered \(delay / 1_000_000, format: .fixed(precision: 1), privacy: .public) ms after capture")
        }
        writer.append(samples, hostTime: hostTime)
        if let level = meter.consume(samples) {
            onLevel?(level)
        }
    }

    /// Writes what the resampler still holds; call once after the session stopped.
    func finish() {
        writer.append(MeetingAudioMath.microphoneBoost(converter.flush()))
    }
}

/// Sample buffer plumbing kept free of capture objects, so it can be tested with buffers
/// built in memory.
enum MeetingCaptureBuffers {
    /// A presentation time further than this from the arrival time is not trusted: it can
    /// only come from a clock that does not count host time, and would put seconds or hours
    /// of silence on the track.
    static let maximumTimestampSkew: TimeInterval = 10

    /// Copies a sample buffer into a PCM buffer of the same format.
    static func pcmBuffer(from sampleBuffer: CMSampleBuffer) -> AVAudioPCMBuffer? {
        let frames = CMSampleBufferGetNumSamples(sampleBuffer)
        guard frames > 0, frames <= Int(Int32.max),
              let description = CMSampleBufferGetFormatDescription(sampleBuffer),
              let basic = CMAudioFormatDescriptionGetStreamBasicDescription(description) else {
            return nil
        }
        var streamDescription = basic.pointee
        guard streamDescription.mFormatID == kAudioFormatLinearPCM,
              let format = AVAudioFormat(streamDescription: &streamDescription),
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)) else {
            return nil
        }
        // The buffer list advertises frameLength worth of bytes, so with the length still at
        // zero there is no room and the copy fails without a trace; set it first.
        buffer.frameLength = AVAudioFrameCount(frames)
        let status = CMSampleBufferCopyPCMDataIntoAudioBufferList(sampleBuffer, at: 0, frameCount: Int32(frames),
                                                                  into: buffer.mutableAudioBufferList)
        guard status == noErr else { return nil }
        return buffer
    }

    /// The host time of a buffer's first sample, the time base the system audio tap reports
    /// and the writer aligns tracks on. The presentation timestamp converted from the
    /// session clock is exact; when it is missing or implausible, the arrival time minus the
    /// buffer's duration is close enough, since buffers are delivered as soon as they fill.
    static func hostTime(presentation: CMTime, clock: CMClock?, arrivalHostTime: UInt64,
                         frames: Int, sampleRate: Double) -> UInt64 {
        let seconds = sampleRate > 0 ? Double(max(0, frames)) / sampleRate : 0
        let duration = AudioConvertNanosToHostTime(UInt64(seconds * 1_000_000_000))
        let estimate = arrivalHostTime > duration ? arrivalHostTime - duration : arrivalHostTime
        guard presentation.isNumeric else { return estimate }

        let host = clock.map { CMSyncConvertTime(presentation, from: $0, to: CMClockGetHostTimeClock()) } ?? presentation
        guard host.isNumeric, host.seconds > 0 else { return estimate }
        let converted = CMClockConvertHostTimeToSystemUnits(host)
        let skew = abs(Double(AudioConvertHostTimeToNanos(converted)) - Double(AudioConvertHostTimeToNanos(arrivalHostTime)))
        return skew <= maximumTimestampSkew * 1_000_000_000 ? converted : estimate
    }
}

/// Brings capture buffers of any PCM format to 16 kHz mono. The capture output is asked
/// for exactly that, so this usually passes samples through; it covers a device that
/// ignores the request. Used from one queue at a time.
final class MeetingCaptureConverter {
    private var resampler: MeetingResampler?

    func convert(_ buffer: AVAudioPCMBuffer) -> [Float] {
        let rate = buffer.format.sampleRate
        guard rate > 0 else { return [] }
        let mono = MeetingAudioMath.mixdown(buffer)
        guard !mono.isEmpty else { return [] }
        var tail: [Float] = []
        if resampler?.sourceSampleRate != rate {
            tail = resampler?.flush() ?? []
            resampler = try? MeetingResampler(sourceSampleRate: rate)
        }
        guard let resampler else { return tail }
        return tail + resampler.process(mono)
    }

    /// The samples the resampler still holds back; call once at the end.
    func flush() -> [Float] {
        resampler?.flush() ?? []
    }
}
