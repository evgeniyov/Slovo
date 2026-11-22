import Foundation

/// How long history, dictation audio or meeting audio is kept.
public enum RetentionPeriod: String, CaseIterable, Codable, Sendable, Identifiable {
    case forever
    case days30
    case days7
    case hours24
    /// Dictation audio only: nothing is kept for playback.
    case never
    /// Meeting audio only: removed once the transcript is written.
    case afterTranscription

    public var id: String { rawValue }

    /// Choices for "Keep dictation history", in display order.
    public static let historyChoices: [RetentionPeriod] = [.forever, .days30, .days7, .hours24]
    /// Choices for "Keep dictation audio for playback", default first.
    public static let dictationAudioChoices: [RetentionPeriod] = [.days7, .days30, .hours24, .never]
    /// Choices for "Keep meeting audio".
    public static let meetingAudioChoices: [RetentionPeriod] = [.forever, .days30, .days7, .afterTranscription]

    /// Maximum age of an item; nil when age alone never removes it (`forever`,
    /// `afterTranscription`). `never` keeps nothing, so its age limit is zero.
    public var maximumAge: TimeInterval? {
        switch self {
        case .forever, .afterTranscription: return nil
        case .days30: return 30 * 24 * 3600
        case .days7: return 7 * 24 * 3600
        case .hours24: return 24 * 3600
        case .never: return 0
        }
    }
}
