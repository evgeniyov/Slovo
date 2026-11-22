import AVFoundation
import CoreMedia
import Foundation

/// Writes screen-capture sample buffers to a fragmented `.mov` file on the same shared
/// host-time timeline the audio tracks use.
///
/// Unlike `MeetingTrackWriter`, `append` does not hop to its own queue: `AVAssetWriter`
/// requires `startWriting`, `startSession`, every `append` and `markAsFinished` to come
/// one after another, so this type has no queue of its own and instead documents that the
/// caller (`MeetingScreenCapture`, under its output lock) must call `append` serially.
/// Screen video is supplementary, not the primary signal, so a sample dropped for
/// backpressure costs nothing a retry would fix; buffering it instead would risk
/// unbounded memory over a multi-hour meeting.
///
/// `movieFragmentInterval` makes the file self-finalizing every few seconds: a plain
/// `.mov`'s sample index is otherwise written only inside `finishWriting()`, so a crash,
/// a force-quit, a failed write or a slow app-quit that never reaches that call would
/// normally leave the whole file unplayable, unlike `mic.caf`/`system.caf`, which stay
/// readable up to whatever was flushed. The fragments written before a failure are
/// therefore kept rather than thrown away with the file.
final class MeetingScreenWriter: @unchecked Sendable {
    static let fragmentInterval = CMTime(seconds: 10, preferredTimescale: 600)

    let url: URL
    let originHostTime: UInt64

    /// The first failure to start or to append; nothing is appended afterwards. Called
    /// once, from whatever queue `append` runs on.
    private let onWriteFailure: ((Error) -> Void)?

    private let assetWriter: AVAssetWriter
    private let input: AVAssetWriterInput
    private var sessionStarted = false
    private var writeFailed = false
    /// Whether any sample was actually appended; an empty or immediately-failed track is
    /// deleted rather than kept, the same way a silent `system.caf` is.
    private(set) var wroteAnySamples = false

    init(url: URL, originHostTime: UInt64, width: Int, height: Int,
         onWriteFailure: ((Error) -> Void)? = nil) throws {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
            writer.movieFragmentInterval = Self.fragmentInterval
            let settings: [String: Any] = [
                AVVideoCodecKey: AVVideoCodecType.h264,
                AVVideoWidthKey: width,
                AVVideoHeightKey: height
            ]
            let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
            input.expectsMediaDataInRealTime = true
            guard writer.canAdd(input) else {
                throw MeetingErrors.screenRecordingUnavailable()
            }
            writer.add(input)
            assetWriter = writer
            self.input = input
        } catch let error as SlovoError {
            throw error
        } catch {
            throw MeetingErrors.fileFailed(error)
        }
        self.url = url
        self.originHostTime = originHostTime
        self.onWriteFailure = onWriteFailure
    }

    /// Appends one sample buffer, starting the session from the shared origin on the
    /// first call. Drops the buffer instead of blocking when the writer is not ready.
    func append(_ sampleBuffer: CMSampleBuffer) {
        guard !writeFailed else { return }
        if assetWriter.status == .unknown {
            // `startSession` raises unless writing really started, so a writer that could
            // not start (no room, no access) fails the track instead of the app.
            guard assetWriter.startWriting() else {
                fail()
                return
            }
            assetWriter.startSession(atSourceTime: CMClockMakeHostTimeFromSystemUnits(originHostTime))
            sessionStarted = true
        }
        guard assetWriter.status == .writing else {
            if assetWriter.status == .failed {
                fail()
            }
            return
        }
        guard input.isReadyForMoreMediaData else { return }
        if input.append(sampleBuffer) {
            wroteAnySamples = true
        } else {
            fail()
        }
    }

    /// Finishes the file if a session ever started; call only once nothing can call
    /// `append` again. Returns whether the file holds real samples, including those
    /// written before a failure: the fragments before it stay playable.
    @discardableResult
    func finish() -> Bool {
        guard sessionStarted else { return false }
        if assetWriter.status == .writing {
            input.markAsFinished()
            let semaphore = DispatchSemaphore(value: 0)
            assetWriter.finishWriting { semaphore.signal() }
            semaphore.wait()
        }
        return wroteAnySamples
    }

    private func fail() {
        guard !writeFailed else { return }
        writeFailed = true
        let error = assetWriter.error ?? MeetingErrors.fileFailed(CocoaError(.fileWriteUnknown))
        SlovoLog.logger("Meetings").error(
            "Writing \(self.url.lastPathComponent, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
        onWriteFailure?(error)
    }
}
