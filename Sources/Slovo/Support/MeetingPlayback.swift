import AVFoundation
import SlovoCore

/// The meeting has no audio left to play.
enum MeetingPlaybackError: Error {
    case noAudio
}

/// Plays a meeting's recorded audio back for listening: play/pause, seeking to any moment,
/// and a speed change. Separate from the app's single-file `AudioPlayback`, since a meeting
/// can have a microphone track, a system-audio track, or both, and needs seek and rate
/// control neither of which `AudioPlayback` offers.
///
/// Both tracks share one timeline — the recorder pads silence into whichever one falls
/// behind during capture — so starting two `AVAudioPlayer`s together with `play(atTime:)`
/// keeps them in sync without mixing them into a single file first.
@MainActor
final class MeetingPlayback: NSObject, ObservableObject, AVAudioPlayerDelegate {
    @Published private(set) var meetingID: UUID?
    @Published private(set) var isPlaying = false
    @Published private(set) var duration: TimeInterval = 0
    @Published private(set) var rate: Float = 1

    /// The longer of the two tracks, so its delegate fires exactly when playback truly ends.
    private var primary: AVAudioPlayer?
    private var secondary: AVAudioPlayer?

    /// Loads a meeting's audio, paused at the start. Loading the meeting already loaded is
    /// a no-op, so seeking mid-playback never restarts the file.
    func load(meeting: MeetingRecord, store: MeetingStore) throws {
        guard meetingID != meeting.id else { return }
        stop()
        let mic = try player(at: store.microphoneAudioURL(for: meeting.id))
        let system = try player(at: store.systemAudioURL(for: meeting.id))
        guard let (chosenPrimary, chosenSecondary) = order(mic, system) else {
            throw MeetingPlaybackError.noAudio
        }
        chosenPrimary.enableRate = true
        chosenSecondary?.enableRate = true
        chosenPrimary.rate = rate
        chosenSecondary?.rate = rate
        chosenPrimary.prepareToPlay()
        chosenSecondary?.prepareToPlay()
        chosenPrimary.delegate = self
        primary = chosenPrimary
        secondary = chosenSecondary
        meetingID = meeting.id
        duration = chosenPrimary.duration
    }

    /// Starts both tracks at the same device time, so a mic/system overlap in the audio
    /// itself does not turn into an audible echo from a few milliseconds of drift at start.
    func play() {
        guard let primary else { return }
        if let secondary {
            let when = primary.deviceCurrentTime + 0.05
            secondary.play(atTime: when)
            primary.play(atTime: when)
        } else {
            primary.play()
        }
        isPlaying = true
    }

    func pause() {
        primary?.pause()
        secondary?.pause()
        isPlaying = false
    }

    /// Safe to call whether paused or playing: `AVAudioPlayer.currentTime` can be set either
    /// way, and both tracks keep whatever sync `play()` already gave them.
    func seek(to time: TimeInterval) {
        guard primary != nil else { return }
        let clamped = min(max(time, 0), duration)
        primary?.currentTime = clamped
        secondary?.currentTime = clamped
    }

    func setRate(_ value: Float) {
        rate = value
        primary?.rate = value
        secondary?.rate = value
    }

    /// The playhead right now. Polled by the view instead of published, since it changes
    /// many times a second while playing.
    func currentTime() -> TimeInterval {
        primary?.currentTime ?? 0
    }

    func stop() {
        primary?.stop()
        secondary?.stop()
        primary = nil
        secondary = nil
        meetingID = nil
        isPlaying = false
        duration = 0
    }

    nonisolated func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        Task { @MainActor [weak self] in
            self?.finish(player)
        }
    }

    private func finish(_ player: AVAudioPlayer) {
        guard player === primary else { return }
        isPlaying = false
        primary?.currentTime = 0
        secondary?.currentTime = 0
    }

    private func player(at url: URL) throws -> AVAudioPlayer? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try AVAudioPlayer(contentsOf: url)
    }

    /// The longer track first, so its delegate marks the true end; nil when neither exists.
    private func order(_ mic: AVAudioPlayer?, _ system: AVAudioPlayer?) -> (AVAudioPlayer, AVAudioPlayer?)? {
        switch (mic, system) {
        case let (mic?, system?):
            return mic.duration >= system.duration ? (mic, system) : (system, mic)
        case let (mic?, nil):
            return (mic, nil)
        case let (nil, system?):
            return (system, nil)
        case (nil, nil):
            return nil
        }
    }
}
