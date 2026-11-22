import AppKit
import Foundation

/// Subtle system sounds at the start and end of a dictation.
@MainActor
public final class DictationSounds: SoundPlaying {
    /// Quiet enough to confirm an action without covering speech or music.
    static let volume: Float = 0.35

    private let settings: AppSettings
    private var sounds: [SoundCue: NSSound] = [:]

    public init(settings: AppSettings) {
        self.settings = settings
    }

    /// Does nothing when the start/stop sounds setting is off.
    public func play(_ cue: SoundCue) {
        guard settings.soundsEnabled, let sound = sound(for: cue) else { return }
        if sound.isPlaying {
            sound.stop()
        }
        sound.play()
    }

    /// Short, soft built-in macOS sounds.
    nonisolated static func soundName(for cue: SoundCue) -> String {
        switch cue {
        case .start: return "Tink"
        case .stop: return "Pop"
        case .cancel: return "Bottle"
        }
    }

    private func sound(for cue: SoundCue) -> NSSound? {
        if let cached = sounds[cue] {
            return cached
        }
        // A copy, so lowering the volume does not change the shared named instance.
        guard let sound = NSSound(named: NSSound.Name(Self.soundName(for: cue)))?.copy() as? NSSound else {
            return nil
        }
        sound.volume = Self.volume
        sounds[cue] = sound
        return sound
    }
}
