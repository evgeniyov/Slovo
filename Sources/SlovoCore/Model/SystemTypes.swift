import Foundation

/// Privacy permissions Slovo needs.
public enum PermissionKind: String, CaseIterable, Codable, Sendable, Identifiable {
    case microphone
    /// Needed to paste text.
    case accessibility
    /// Needed to notice the dictation key.
    case inputMonitoring
    /// Needed to record other participants of a meeting.
    case systemAudioRecording
    /// Needed to record the screen during a meeting.
    case screenRecording

    public var id: String { rawValue }
}

public enum PermissionStatus: String, Codable, Sendable {
    case granted
    case denied
    case notDetermined
    /// macOS offers no way to query it (System Audio Recording before the first tap).
    case unknown
}

/// An audio input device as Core Audio reports it.
public struct AudioInputDevice: Codable, Hashable, Sendable, Identifiable {
    /// `kAudioDevicePropertyDeviceUID`, stable across launches; stored in settings.
    public var uid: String
    public var name: String

    public var id: String { uid }

    public init(uid: String, name: String) {
        self.uid = uid
        self.name = name
    }
}

/// A finished microphone recording: 16 kHz mono Float32 samples.
public struct RecordedAudio: Equatable, Sendable {
    public var samples: [Float]
    public var sampleRate: Double

    public init(samples: [Float], sampleRate: Double = 16_000) {
        self.samples = samples
        self.sampleRate = sampleRate
    }

    public var duration: TimeInterval {
        sampleRate > 0 ? Double(samples.count) / sampleRate : 0
    }

    public static let empty = RecordedAudio(samples: [])
}

/// Sounds played around a dictation.
public enum SoundCue: String, Sendable {
    case start
    case stop
    case cancel
}
