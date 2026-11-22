import Foundation

/// Localized errors of the meetings module. Every message comes from `Meetings.strings`.
enum MeetingErrors {
    struct EmptyTranscription: LocalizedError {
        var errorDescription: String? {
            CoreStrings.string("meetings.transcription.noRecognizedSpeech", table: .meetings)
        }
    }

    static func alreadyRecording() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.alreadyRecording", table: .meetings))
    }

    static func microphoneDenied() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.microphoneDenied", table: .meetings))
    }

    static func noMicrophone() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.noMicrophone", table: .meetings))
    }

    static func microphoneFailed(_ error: Error) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.microphoneFailed", table: .meetings, error.localizedDescription))
    }

    static func systemAudioUnsupported() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.systemAudioUnsupported", table: .meetings))
    }

    static func systemAudioDenied() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.systemAudioDenied", table: .meetings))
    }

    static func screenRecordingDenied() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.screenRecordingDenied", table: .meetings))
    }

    static func screenRecordingUnavailable() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.screenRecordingUnavailable", table: .meetings))
    }

    static func screenRecordingAppNotRunning() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.screenRecordingAppNotRunning", table: .meetings))
    }

    /// A Core Audio call failed; the function name and status stay untranslated for bug reports.
    static func tapFailed(_ function: String, _ status: OSStatus) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.tapFailed", table: .meetings, "\(function) (\(status))"))
    }

    static func unsupportedAudioFormat() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.unsupportedFormat", table: .meetings))
    }

    /// The free space is spelled out, so the message says how far short the disk is.
    static func lowDiskSpace(_ availableBytes: Int64) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.lowDiskSpace", table: .meetings,
                                      ByteCountFormatter.string(fromByteCount: availableBytes, countStyle: .file)))
    }

    /// A recording ended by itself before the disk filled; what it holds is saved.
    static func lowDiskSpaceWhileRecording(_ availableBytes: Int64) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.lowDiskSpaceWhileRecording", table: .meetings,
                                      ByteCountFormatter.string(fromByteCount: availableBytes, countStyle: .file)))
    }

    /// The system audio tap could not be rebuilt; it keeps retrying.
    static func systemAudioInterrupted(_ error: Error) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.systemAudioInterrupted", table: .meetings,
                                      error.localizedDescription))
    }

    /// An app-scoped screen recording ended, and the app's own sound with it.
    static func appAudioEnded() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.appAudioEnded", table: .meetings))
    }

    static func fileFailed(_ error: Error) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.fileFailed", table: .meetings, error.localizedDescription))
    }

    static func readFailed(_ error: Error) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.readFailed", table: .meetings, error.localizedDescription))
    }

    static func createFailed(_ error: Error) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.createFailed", table: .meetings, error.localizedDescription))
    }

    static func saveFailed(_ error: Error) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.saveFailed", table: .meetings, error.localizedDescription))
    }

    static func notFound() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.notFound", table: .meetings))
    }

    static func noAudio() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.noAudio", table: .meetings))
    }

    static func stillRecording() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.stillRecording", table: .meetings))
    }

    static func alreadyTranscribing() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.alreadyTranscribing", table: .meetings))
    }

    static func speakersOnlyUnavailable() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.speakersOnlyUnavailable", table: .meetings))
    }

    static func fileTooShort() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.fileTooShort", table: .meetings))
    }

    static func transcriptFailed(_ error: Error) -> SlovoError {
        SlovoError(CoreStrings.format("meetings.error.transcriptFailed", table: .meetings, error.localizedDescription))
    }

    static func cancelledMessage() -> String {
        CoreStrings.string("meetings.transcription.cancelled", table: .meetings)
    }

    static func interruptedMessage() -> String {
        CoreStrings.string("meetings.transcription.interrupted", table: .meetings)
    }
}

/// Default meeting titles, shared by the store (recovered meetings) and the recorder.
enum MeetingTitles {
    static func defaultTitle(for date: Date, language: AppLanguage? = nil) -> String {
        let resolved = language ?? AppLanguage.saved()
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: resolved.resolvedCode())
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return CoreStrings.format("meetings.defaultTitle", table: .meetings, language: resolved,
                                  formatter.string(from: date))
    }
}
