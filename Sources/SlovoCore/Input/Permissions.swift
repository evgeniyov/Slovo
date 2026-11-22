import AppKit
import ApplicationServices
import AVFoundation
import Carbon.HIToolbox
import Combine
import CoreGraphics
import Foundation

/// Live status of the permissions Slovo needs. The app calls `refresh()` whenever it
/// becomes active, so rows update after the user returns from System Settings.
@MainActor
public final class Permissions: ObservableObject, PermissionsProviding {
    @Published public private(set) var statuses: [PermissionKind: PermissionStatus] = [:]

    private let logger = SlovoLog.logger("Permissions")

    public init() {
        refresh()
    }

    public func refresh() {
        var updated: [PermissionKind: PermissionStatus] = [:]
        for kind in PermissionKind.allCases {
            updated[kind] = liveStatus(of: kind)
        }
        if updated != statuses {
            statuses = updated
        }
    }

    /// Reads the system directly: `TextInserter` asks right before pasting, and a
    /// grant made a moment ago in System Settings must count without a refresh.
    public func status(of kind: PermissionKind) -> PermissionStatus {
        liveStatus(of: kind)
    }

    /// Whether `request(_:)` asks macOS for `kind` itself rather than only opening its
    /// System Settings pane. Asking is what puts Slovo into the Accessibility, Input
    /// Monitoring and Screen Recording lists: a pane opened before Slovo ever asked does
    /// not show it, and the user would have to add the app with "+". System Audio
    /// Recording has no request API; macOS asks when the first meeting starts its tap.
    public nonisolated static func asksThroughRequest(_ kind: PermissionKind) -> Bool {
        switch kind {
        case .microphone, .accessibility, .inputMonitoring, .screenRecording: return true
        case .systemAudioRecording: return false
        }
    }

    public func request(_ kind: PermissionKind) async -> PermissionStatus {
        switch kind {
        case .microphone:
            if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
                _ = await AVCaptureDevice.requestAccess(for: .audio)
            } else if liveStatus(of: .microphone) == .denied {
                // macOS asks only once; afterwards only System Settings can change it.
                openSystemSettings(for: .microphone)
            }
        case .accessibility:
            // The prompt adds Slovo to the Accessibility list and offers to open the pane.
            // macOS may show it only once, so the pane opens as well: the switch is there.
            let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
            if !AXIsProcessTrustedWithOptions(options) {
                openSystemSettings(for: .accessibility)
            }
        case .inputMonitoring:
            if !CGRequestListenEventAccess() {
                openSystemSettings(for: .inputMonitoring)
            }
        case .systemAudioRecording:
            // macOS asks when the first process tap starts recording; the meetings
            // module does that on the first meeting.
            break
        case .screenRecording:
            if !CGRequestScreenCaptureAccess() {
                // Already asked and denied, or granted but not yet in effect for this
                // running process (TCC needs a relaunch): either way, only System
                // Settings can change it from here.
                openSystemSettings(for: .screenRecording)
            }
        }
        refresh()
        return status(of: kind)
    }

    public func openSystemSettings(for kind: PermissionKind) {
        guard let url = Self.settingsURL(for: kind) else { return }
        if !NSWorkspace.shared.open(url) {
            logger.error("Could not open System Settings for \(kind.rawValue, privacy: .public)")
        }
    }

    public var isSecureInputActive: Bool {
        IsSecureEventInputEnabled()
    }

    /// The System Settings deep link for a permission's privacy pane.
    public static func settingsURL(for kind: PermissionKind) -> URL? {
        let anchor: String
        switch kind {
        case .microphone: anchor = "Privacy_Microphone"
        case .accessibility: anchor = "Privacy_Accessibility"
        case .inputMonitoring: anchor = "Privacy_ListenEvent"
        case .systemAudioRecording: anchor = "Privacy_AudioCapture"
        case .screenRecording: anchor = "Privacy_ScreenCapture"
        }
        return URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)")
    }

    private func liveStatus(of kind: PermissionKind) -> PermissionStatus {
        switch kind {
        case .microphone:
            switch AVCaptureDevice.authorizationStatus(for: .audio) {
            case .authorized: return .granted
            case .denied, .restricted: return .denied
            case .notDetermined: return .notDetermined
            @unknown default: return .unknown
            }
        case .accessibility:
            // macOS does not tell "never asked" from "turned off".
            return AXIsProcessTrusted() ? .granted : .denied
        case .inputMonitoring:
            return CGPreflightListenEventAccess() ? .granted : .denied
        case .systemAudioRecording:
            // There is no public query; a silent tap is the only signal.
            return .unknown
        case .screenRecording:
            return CGPreflightScreenCaptureAccess() ? .granted : .denied
        }
    }
}
