import Foundation

public enum DictationMode: String, Codable, Sendable {
    /// Recording lasts while the key is held.
    case pushToTalk
    /// Recording continues after release until the next tap, the stop button or the time limit.
    case handsFree
}

/// Where the text of a dictation goes.
public enum DictationTarget: Equatable, Sendable {
    /// Pasted into the focused field of the frontmost app.
    case frontmostApp
    /// Appended to a Scratchpad note.
    case note(UUID)
    /// Returned to onboarding's practice step without pasting anywhere.
    case practice
}

/// Semantic events produced by the hotkey state machine (or the Flow bar) for the controller.
public enum DictationTrigger: Equatable, Sendable {
    /// The dictation key went down: start capturing at once so no speech is lost.
    case begin(DictationMode)
    /// A double tap was confirmed: keep the running recording going after release.
    case promoteToHandsFree
    /// Stop and transcribe: push-to-talk release after the minimum hold, or the
    /// finishing tap in hands-free mode.
    case finish
    /// A lone tap shorter than the minimum hold: stop silently, show nothing.
    case discard
    /// Esc while recording: stop, nothing is transcribed or inserted.
    case cancel
}

/// Information about the recording in progress.
public struct RecordingInfo: Equatable, Sendable {
    public var mode: DictationMode
    /// `ProcessInfo.systemUptime` when capture began.
    public var startedAt: TimeInterval
    public var target: DictationTarget

    public init(mode: DictationMode, startedAt: TimeInterval, target: DictationTarget) {
        self.mode = mode
        self.startedAt = startedAt
        self.target = target
    }
}

/// Short errors the Flow bar can show; each may point at the settings that fix it.
public enum DictationFailure: Equatable, Sendable {
    case microphoneDenied
    case accessibilityMissing
    case inputMonitoringMissing
    case modelMissing(EngineID)
    /// Engine or recording failure; the message is already localized.
    case engineFailed(String)
    case recordingFailed(String)

    /// The permission whose settings pane fixes this failure, if any.
    public var permission: PermissionKind? {
        switch self {
        case .microphoneDenied: return .microphone
        case .accessibilityMissing: return .accessibility
        case .inputMonitoringMissing: return .inputMonitoring
        case .modelMissing, .engineFailed, .recordingFailed: return nil
        }
    }
}

/// Result states the Flow bar shows for about 1.5 s.
public enum FlowMessage: Equatable, Sendable {
    /// Pasting was not possible, and the text is on the pasteboard.
    case copied(CopyReason)
    case nothingHeard
    case blockedByMeeting
    case failure(DictationFailure)
}

/// The dictation state the Flow bar, menu bar and screens render.
public enum DictationState: Equatable, Sendable {
    case idle
    case recording(RecordingInfo)
    /// `isLoadingModel` is true while waiting for the engine to load ("Loading model…").
    case processing(isLoadingModel: Bool)
    case message(FlowMessage)

    public var isRecording: Bool {
        if case .recording = self { return true }
        return false
    }
}

/// Why text went to the pasteboard instead of being pasted.
public enum CopyReason: String, Codable, Sendable {
    case secureInput
    case accessibilityMissing
    case focusChanged
    case pasteFailed
}

/// Outcome of inserting text into the focused field.
public enum InsertionResult: Equatable, Sendable {
    case pasted
    case copied(CopyReason)
    /// Nothing to insert.
    case empty
}
