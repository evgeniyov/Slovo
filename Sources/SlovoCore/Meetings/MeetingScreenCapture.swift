import AppKit
import CoreAudio
import CoreGraphics
import Foundation
import ScreenCaptureKit

/// Records the screen into a video file via ScreenCaptureKit, on the same host-time
/// timeline the audio tracks use — either the whole main display, or one app's windows
/// with that app's own audio in place of `system.caf`'s usual system-wide capture.
///
/// Screen video is a best-effort, optional third track: any failure here degrades only
/// this track (and, in app mode, the audio riding along with it) and never touches the
/// meeting's microphone, mirroring `SystemAudioTap`. A display or app that disconnects
/// gets exactly one reconnect attempt before the track gives up for the rest of the
/// meeting; a screen lock pauses capture instead, and resumes on unlock; a system wake
/// tears the stream down and rebuilds it, since `SCStream` does not reliably survive a
/// sleep — `MeetingRecorder` calls `restart()` for that.
///
/// Two locks keep stopping from ever waiting on a sample handler that waits on it: stream
/// management runs on `control`, and the sample handler touches only the outputs behind
/// `outputLock`, which nothing holds while it waits for ScreenCaptureKit.
final class MeetingScreenCapture: NSObject, @unchecked Sendable, SCStreamOutput, SCStreamDelegate {
    static var isSupported: Bool {
        if #available(macOS 14.0, *) { return true }
        return false
    }

    /// The longer edge of the captured video; screen video is reference material, not a
    /// smooth recording, so this stays modest to bound file size over a multi-hour meeting.
    static let maximumEdge = 1920
    static let frameInterval = CMTime(value: 1, timescale: 4)
    /// How long one ScreenCaptureKit call may take before it counts as failed. Nothing
    /// here waits on the framework without a limit, so a callback that never comes cannot
    /// hold a stop, and with it the meeting, forever.
    static let callTimeout: TimeInterval = 10

    /// Level from 0 to 1, about 30 times per second, only while capturing one app's audio.
    /// Set before `start`.
    var onLevel: ((Float) -> Void)?
    /// Called once the track is done for the rest of this meeting: a reconnect after a
    /// display or app loss also failed, or the permission was revoked. Never called for
    /// a lock, which pauses instead of ending the track. Set before `start`.
    var onLost: ((Error) -> Void)?
    /// True when a locked screen paused capture, false when unlocking resumed it. Set
    /// before `start`.
    var onPauseChanged: ((Bool) -> Void)?

    private let control = DispatchQueue(label: "local.slovo.Slovo.MeetingScreenCapture.control")
    private let ioQueue = DispatchQueue(label: "local.slovo.Slovo.MeetingScreenCapture.io", qos: .userInitiated)
    private let logger = SlovoLog.logger("MeetingScreenCapture")

    /// Accessed only on `control`.
    private var stream: SCStream?
    private var target: ScreenCaptureTarget = .display
    /// Set once, at the first start: every later (re)connect scales into this same fixed
    /// canvas, since `AVAssetWriterInput`'s output dimensions cannot change mid-file.
    private var outputSize: (width: Int, height: Int)?
    private var reconnectedOnce = false
    private var stopped = false
    private var isScreenLocked = false
    private var lockObserver: NSObjectProtocol?
    private var unlockObserver: NSObjectProtocol?

    /// What the sample handler reads and writes, behind `outputLock` only.
    private let outputLock = NSLock()
    /// The stream whose buffers are written; buffers of any other are dropped.
    private var acceptedStream: ObjectIdentifier?
    private var writer: MeetingScreenWriter?
    /// Set only for `.application`: that app's audio is written here instead of coming
    /// from `SystemAudioTap`, which `MeetingRecorder` does not start in that case.
    private var audioWriter: MeetingTrackWriter?
    private var audioResampler: MeetingResampler?
    private var audioMeter = LevelMeter()
    private var heardAudio = false

    /// Whether any real (non-zero) audio sample was written, only meaningful in app mode.
    var heardAudioSignal: Bool {
        outputLock.withLock { heardAudio }
    }

    /// Whether anything is still being written: the video, the app's audio, or both.
    private var hasOutputs: Bool {
        outputLock.withLock { writer != nil || audioWriter != nil }
    }

    /// Resolves the display or app to capture and the video's output size, builds the
    /// video writer through `makeVideoWriter`, and starts the stream. `audioWriter`, only
    /// for `.application`, receives that app's audio for as long as capture lasts.
    func start(target: ScreenCaptureTarget, audioWriter: MeetingTrackWriter?,
               makeVideoWriter: (_ width: Int, _ height: Int) throws -> MeetingScreenWriter) throws {
        try control.sync {
            self.target = target
            let (_, width, height) = try Self.videoOutputSize(for: target)
            let writer = try makeVideoWriter(width, height)
            outputSize = (width, height)
            outputLock.withLock {
                self.writer = writer
                self.audioWriter = audioWriter
            }
            do {
                try startStreamLocked()
            } catch {
                outputLock.withLock {
                    self.writer = nil
                    self.audioWriter = nil
                }
                throw error
            }
        }
        addLockObservers()
    }

    /// Tears everything down for good; safe to call more than once. The writers
    /// themselves are finished by whoever owns them (`RecordingSession`), not here.
    func stop() {
        removeLockObservers()
        control.sync {
            stopped = true
            stopLocked(keepingWriter: false)
        }
    }

    /// Tears the stream down and rebuilds it against the same target, keeping the same
    /// writers and their open sessions — for a system wake. A locked screen waits for the
    /// unlock instead.
    func restart() {
        control.sync {
            guard !stopped, hasOutputs else { return }
            stopLocked(keepingWriter: true)
            guard !isScreenLocked else { return }
            do {
                try startStreamLocked()
            } catch {
                logger.error("Restarting screen capture failed: \(error.localizedDescription, privacy: .public)")
                reportLost(error)
            }
        }
    }

    /// Stops writing video but keeps the stream, and the app's audio it carries, running:
    /// for a video track that failed or that the disk has no room for. No buffer reaches the
    /// video writer once this returns, so its owner can finish it.
    func detachVideo() {
        outputLock.withLock {
            writer = nil
        }
    }

    // MARK: - SCStreamOutput / SCStreamDelegate

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sampleBuffer.isValid else { return }
        let level: Float? = outputLock.withLock {
            guard acceptedStream == ObjectIdentifier(stream) else { return nil }
            switch type {
            case .screen:
                if Self.carriesPicture(sampleBuffer) {
                    writer?.append(sampleBuffer)
                }
                return nil
            case .audio:
                return processAudioLocked(sampleBuffer)
            default:
                return nil
            }
        }
        if let level {
            onLevel?(level)
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        control.async { [weak self] in
            guard let self, !self.stopped, self.stream === stream else { return }
            self.logger.error("Screen capture stopped: \(error.localizedDescription, privacy: .public)")
            guard !self.reconnectedOnce, self.hasOutputs else {
                self.reportLost(error)
                return
            }
            self.reconnectedOnce = true
            self.stopLocked(keepingWriter: true)
            guard !self.isScreenLocked else { return }
            do {
                try self.startStreamLocked()
            } catch {
                self.reportLost(error)
            }
        }
    }

    // MARK: - Private

    /// ScreenCaptureKit also hands over buffers with no picture in them (idle, blank,
    /// suspended) while the captured content does not change, which is most of the time
    /// for one quiet app window; appending one to the video input fails the whole track.
    private static func carriesPicture(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard CMSampleBufferGetImageBuffer(sampleBuffer) != nil else { return false }
        let attachments = (CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
            as? [[SCStreamFrameInfo: Any]])?.first
        if let raw = attachments?[.status] as? Int, let status = SCFrameStatus(rawValue: raw) {
            return status == .complete
        }
        return true
    }

    /// Must run on `control`. Builds a fresh stream sized to `outputSize`, targeting
    /// `target` as it resolves right now, so a display swap or the target app quitting
    /// and relaunching mid-meeting scales into the same fixed canvas instead of changing
    /// the writer's dimensions.
    private func startStreamLocked() throws {
        guard stream == nil, let outputSize else { return }
        let content = try Self.shareableContentBlocking()
        let display = try Self.mainDisplay(in: content)
        let filter: SCContentFilter
        let capturesAppAudio: Bool
        switch target {
        case .display:
            filter = SCContentFilter(display: display, excludingWindows: [])
            capturesAppAudio = false
        case let .application(bundleID):
            guard let application = content.applications.first(where: { $0.bundleIdentifier == bundleID }) else {
                throw MeetingErrors.screenRecordingAppNotRunning()
            }
            filter = SCContentFilter(display: display, including: [application], exceptingWindows: [])
            capturesAppAudio = outputLock.withLock { audioWriter != nil }
        }

        let configuration = SCStreamConfiguration()
        configuration.width = outputSize.width
        configuration.height = outputSize.height
        configuration.minimumFrameInterval = Self.frameInterval
        configuration.showsCursor = true
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.queueDepth = 5
        if capturesAppAudio {
            configuration.capturesAudio = true
            configuration.excludesCurrentProcessAudio = true
            configuration.channelCount = 1
        }

        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        // The screen output stays even once the video was let go: a stream delivers frames
        // regardless, and they are simply not written.
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: ioQueue)
        if capturesAppAudio {
            try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: ioQueue)
        }
        outputLock.withLock { acceptedStream = ObjectIdentifier(stream) }
        do {
            try Self.startCaptureBlocking(stream)
        } catch {
            outputLock.withLock { acceptedStream = nil }
            throw error
        }
        self.stream = stream
    }

    /// Runs under `outputLock`. Converts one app-audio sample buffer to the 16 kHz mono
    /// the track writers share, reusing the same `CMSampleBuffer` -> `AVAudioPCMBuffer`
    /// bridge and host-time math `MeetingMicrophoneCapture` already uses. Returns a level
    /// for the meter, reported once the lock is released.
    private func processAudioLocked(_ sampleBuffer: CMSampleBuffer) -> Float? {
        guard let audioWriter, let buffer = MeetingCaptureBuffers.pcmBuffer(from: sampleBuffer) else { return nil }
        let rate = buffer.format.sampleRate
        guard rate > 0 else { return nil }
        if audioResampler?.sourceSampleRate != rate {
            audioResampler = try? MeetingResampler(sourceSampleRate: rate)
        }
        guard let audioResampler else { return nil }
        let mono = MeetingAudioMath.mixdown(buffer)
        guard !mono.isEmpty else { return nil }
        if !heardAudio, mono.contains(where: { $0 != 0 }) {
            heardAudio = true
        }
        let converted = audioResampler.process(mono)
        // No session clock at hand for this stream; the presentation timestamp is
        // already on the host-time clock (`MeetingScreenWriter` relies on the same
        // fact for video), so `clock: nil` uses it as-is.
        let hostTime = MeetingCaptureBuffers.hostTime(presentation: CMSampleBufferGetPresentationTimeStamp(sampleBuffer),
                                                      clock: nil, arrivalHostTime: AudioGetCurrentHostTime(),
                                                      frames: Int(buffer.frameLength), sampleRate: rate)
        audioWriter.append(converted, hostTime: hostTime)
        return audioMeter.consume(converted)
    }

    /// Must run on `control`. The outputs stop accepting buffers before the stream is
    /// stopped, and `outputLock` is not held while ScreenCaptureKit is waited for.
    private func stopLocked(keepingWriter: Bool) {
        outputLock.withLock {
            acceptedStream = nil
            if !keepingWriter {
                writer = nil
                audioWriter = nil
            }
        }
        guard let stream else { return }
        self.stream = nil
        Self.stopCaptureBlocking(stream)
    }

    /// Must run on `control`.
    private func reportLost(_ error: Error) {
        stopLocked(keepingWriter: false)
        onLost?(error)
    }

    /// Must run on `control`.
    private func screenLocked() {
        guard !stopped else { return }
        isScreenLocked = true
        let wasRunning = stream != nil
        stopLocked(keepingWriter: true)
        if wasRunning {
            onPauseChanged?(true)
        }
    }

    /// Must run on `control`.
    private func screenUnlocked() {
        isScreenLocked = false
        guard !stopped, hasOutputs, stream == nil else { return }
        do {
            try startStreamLocked()
            onPauseChanged?(false)
        } catch {
            reportLost(error)
        }
    }

    private func addLockObservers() {
        let center = DistributedNotificationCenter.default()
        lockObserver = center.addObserver(forName: Notification.Name("com.apple.screenIsLocked"), object: nil,
                                          queue: .main) { [weak self] _ in
            self?.control.async { self?.screenLocked() }
        }
        unlockObserver = center.addObserver(forName: Notification.Name("com.apple.screenIsUnlocked"), object: nil,
                                            queue: .main) { [weak self] _ in
            self?.control.async { self?.screenUnlocked() }
        }
    }

    private func removeLockObservers() {
        let center = DistributedNotificationCenter.default()
        if let lockObserver { center.removeObserver(lockObserver) }
        if let unlockObserver { center.removeObserver(unlockObserver) }
        lockObserver = nil
        unlockObserver = nil
    }

    private static func mainDisplay(in content: SCShareableContent) throws -> SCDisplay {
        guard !content.displays.isEmpty else {
            throw MeetingErrors.screenRecordingUnavailable()
        }
        let mainID = (NSScreen.main?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?
            .uint32Value
        return content.displays.first { $0.displayID == mainID } ?? content.displays[0]
    }

    /// The display under the menu bar and the pixel size to capture at: the display's
    /// real backing size, scaled down to `maximumEdge` on the longer side. The target app
    /// only decides which windows are composited into that same canvas (`startStreamLocked`),
    /// not the canvas size, so an app's own window size never resizes the video track.
    private static func videoOutputSize(for target: ScreenCaptureTarget) throws -> (display: SCDisplay, width: Int, height: Int) {
        let display = try mainDisplay(in: try shareableContentBlocking())
        let scale = NSScreen.screens.first {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == display.displayID
        }?.backingScaleFactor ?? 2
        let nativeWidth = Int(Double(display.width) * scale)
        let nativeHeight = Int(Double(display.height) * scale)
        let longerEdge = max(nativeWidth, nativeHeight)
        let factor = longerEdge > maximumEdge ? Double(maximumEdge) / Double(longerEdge) : 1
        // H.264 wants even dimensions.
        let width = max(2, Int(Double(nativeWidth) * factor)) & ~1
        let height = max(2, Int(Double(nativeHeight) * factor)) & ~1
        return (display, width, height)
    }

    // MARK: - Blocking bridges over ScreenCaptureKit's completion-handler APIs, so this
    // type stays synchronous like `SystemAudioTap`; callers already run it off the main
    // actor via `MeetingRecorder`'s `offMainActor` helper. Each waits at most `callTimeout`.

    private static func shareableContentBlocking() throws -> SCShareableContent {
        let completion = BlockingCompletion<Result<SCShareableContent, Error>>()
        SCShareableContent.getWithCompletionHandler { content, error in
            if let content {
                completion.finish(.success(content))
            } else {
                completion.finish(.failure(error ?? MeetingErrors.screenRecordingUnavailable()))
            }
        }
        guard let result = completion.wait(seconds: callTimeout) else {
            throw MeetingErrors.screenRecordingUnavailable()
        }
        return try result.get()
    }

    private static func startCaptureBlocking(_ stream: SCStream) throws {
        let completion = BlockingCompletion<Result<Void, Error>>()
        stream.startCapture { error in
            completion.finish(error.map { .failure($0) } ?? .success(()))
        }
        guard let result = completion.wait(seconds: callTimeout) else {
            throw MeetingErrors.screenRecordingUnavailable()
        }
        try result.get()
    }

    private static func stopCaptureBlocking(_ stream: SCStream) {
        let completion = BlockingCompletion<Void>()
        stream.stopCapture { _ in completion.finish(()) }
        if completion.wait(seconds: callTimeout) == nil {
            SlovoLog.logger("MeetingScreenCapture").error("Stopping screen capture did not finish in time")
        }
    }
}

/// A completion handler's value handed to a thread that waits for it, with a time limit.
/// A value that arrives after the wait gave up is dropped.
private final class BlockingCompletion<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private var value: Value?

    func finish(_ value: Value) {
        lock.withLock { self.value = value }
        semaphore.signal()
    }

    /// The value, or nil when none came within `seconds`.
    func wait(seconds: TimeInterval) -> Value? {
        guard semaphore.wait(timeout: .now() + seconds) == .success else { return nil }
        return lock.withLock { value }
    }
}
