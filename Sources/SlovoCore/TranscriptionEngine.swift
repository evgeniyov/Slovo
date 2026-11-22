import Foundation

/// A speech-to-text backend that turns a finished recording into text.
public protocol TranscriptionEngine: AnyObject {
    /// Stable identifier used in settings and benchmark results.
    var id: String { get }

    /// Loads models and checks that the engine can run. Safe to call more than once.
    func prepare() async throws

    /// Transcribes an audio file in any format AVFoundation can read.
    func transcribe(fileURL: URL) async throws -> String
}

/// An engine that can be told which words to expect, such as Whisper's initial prompt.
public protocol VocabularyTranscribing: TranscriptionEngine {
    /// Transcribes like `transcribe(fileURL:)`, expecting these terms (the Dictionary's).
    func transcribe(fileURL: URL, vocabulary: [String]) async throws -> String
}

public struct TranscriptionError: LocalizedError {
    public let message: String

    public init(_ message: String) {
        self.message = message
    }

    public var errorDescription: String? { message }
}
