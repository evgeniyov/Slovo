import Foundation

/// One stretch of speech attributed to one voice by a diarization run.
public struct SpeakerTurn: Equatable, Sendable {
    public var start: TimeInterval
    public var end: TimeInterval
    /// 0-based, local to one diarization run of one track.
    public var cluster: Int

    public init(start: TimeInterval, end: TimeInterval, cluster: Int) {
        self.start = start
        self.end = end
        self.cluster = cluster
    }
}

/// What is known about the number of voices on a track before separating them.
public struct SpeakerCountHint: Equatable, Sendable {
    public var exactly: Int?
    public var maximum: Int?

    public static let automatic = SpeakerCountHint(exactly: nil, maximum: nil)

    public init(exactly: Int? = nil, maximum: Int? = nil) {
        self.exactly = exactly
        self.maximum = maximum
    }
}

/// One loaded diarizer. Calls are serialized by the implementation.
public protocol SpeakerDiarizing: AnyObject, Sendable {
    /// Turns of a 16 kHz mono file, sorted, possibly empty. Progress from 0 to 1 on
    /// any thread. Throws CancellationError when the calling task is cancelled.
    func diarize(fileAt url: URL, hint: SpeakerCountHint,
                 progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn]
}

/// Loaded diarizers for the meeting pipeline (DiarizationService).
@MainActor
public protocol DiarizationProviding: AnyObject {
    var modelStatus: ModelStatus { get }
    /// False below macOS 15.
    var isSupportedOnThisSystem: Bool { get }
    /// Loads the model when needed; throws when it is not ready. Never downloads.
    func diarizer() async throws -> any SpeakerDiarizing
    /// Releases loaded Core ML models.
    func release()
    /// Returns once no load is reading model files.
    func waitForLoad() async
}

/// The speaker model files on disk (DiarizationService).
@MainActor
public protocol DiarizationModelManaging: AnyObject {
    var status: ModelStatus { get }
    /// The last download or delete failure, already localized.
    var lastError: String? { get }
    func refresh()
    /// Starts a download the user asked for; progress appears in `status`.
    func startDownload()
    func cancelDownload()
    func delete() throws
}
