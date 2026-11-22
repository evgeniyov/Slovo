import AppKit
import AVFoundation
import Combine
import Foundation
import ServiceManagement
import SlovoCore

/// Launch at login through `SMAppService.mainApp`.
@MainActor
final class LoginItem: ObservableObject {
    @Published private(set) var isEnabled = false
    /// The user must allow Slovo in System Settings → General → Login Items.
    @Published private(set) var needsApproval = false

    func refresh() {
        let status = SMAppService.mainApp.status
        isEnabled = status == .enabled
        needsApproval = status == .requiresApproval
    }

    func setEnabled(_ enabled: Bool) throws {
        defer { refresh() }
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}

/// The Fn conflicts `FnKeyConflicts` detects, published for the key choice rows and
/// re-read whenever the app becomes active. Slovo explains them and never changes
/// the system setting itself.
@MainActor
final class FnKeyStatus: ObservableObject {
    /// `AppleFnUsageType` in `com.apple.HIToolbox`.
    enum Usage: Equatable {
        case doNothing
        case changeInputSource
        case emoji
        case appleDictation
        /// Missing value: macOS then uses the key for something, so it still conflicts.
        case unknown
    }

    @Published private(set) var usage: Usage = .doNothing
    @Published private(set) var isWisprFlowRunning = false

    func refresh() {
        let current: Usage
        switch FnKeyConflicts.systemFnUsage() {
        case 0: current = .doNothing
        case 1: current = .changeInputSource
        case 2: current = .emoji
        case 3: current = .appleDictation
        default: current = .unknown
        }
        if usage != current {
            usage = current
        }
        let running = FnKeyConflicts.isWisprFlowRunning()
        if isWisprFlowRunning != running {
            isWisprFlowRunning = running
        }
    }

    func openKeyboardSettings() {
        FnKeyConflicts.openKeyboardSettings()
    }
}

/// Plays a history entry's recording; one at a time.
@MainActor
final class AudioPlayback: NSObject, ObservableObject, AVAudioPlayerDelegate {
    @Published private(set) var playingID: UUID?
    private var player: AVAudioPlayer?

    /// Starts playing, or stops when this entry is already playing.
    func toggle(id: UUID, url: URL) throws {
        if playingID == id {
            stop()
            return
        }
        stop()
        let player = try AVAudioPlayer(contentsOf: url)
        player.delegate = self
        player.play()
        self.player = player
        playingID = id
    }

    func stop() {
        player?.stop()
        player = nil
        playingID = nil
    }

    nonisolated func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        Task { @MainActor [weak self] in
            self?.stop()
        }
    }
}
