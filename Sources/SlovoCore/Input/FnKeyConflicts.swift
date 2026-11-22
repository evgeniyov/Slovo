import AppKit
import Foundation

/// Why the Fn/Globe key may not reach Slovo alone. Slovo explains these and never
/// changes the system setting itself.
public enum FnKeyWarning: Equatable, Sendable {
    /// `AppleFnUsageType` is not "Do Nothing": macOS also switches input source,
    /// shows emoji or starts its own dictation. Nil when the value is unset, which
    /// means the system default and is not "Do Nothing" either.
    case systemUsesFn(usage: Int?)
    /// Wispr Flow is running and reacts to Fn too.
    case wisprFlowRunning

    /// A short localized explanation for the inline warning.
    public var message: String {
        switch self {
        case .systemUsesFn:
            return CoreStrings.string("input.fn.warning.systemUsage", table: .input)
        case .wisprFlowRunning:
            return CoreStrings.string("input.fn.warning.wisprFlow", table: .input)
        }
    }
}

/// Checks behind the Fn warnings next to the dictation key choice (re-run them
/// whenever the app becomes active).
public enum FnKeyConflicts {
    public static let wisprFlowBundleID = "com.electron.wispr-flow"
    /// System Settings → Keyboard, where "Press 🌐 key to" lives.
    public static let keyboardSettingsURL = URL(string: "x-apple.systempreferences:com.apple.Keyboard-Settings.extension")

    /// `AppleFnUsageType` in `com.apple.HIToolbox`: 0 do nothing, 1 change input
    /// source, 2 emoji and symbols, 3 start dictation; nil when unset.
    public static func systemFnUsage() -> Int? {
        let domain = "com.apple.HIToolbox" as CFString
        CFPreferencesAppSynchronize(domain)
        return (CFPreferencesCopyAppValue("AppleFnUsageType" as CFString, domain) as? NSNumber)?.intValue
    }

    public static func isWisprFlowRunning() -> Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: wisprFlowBundleID).isEmpty
    }

    /// Current warnings for a key choice; empty for keys other than Fn.
    public static func warnings(for key: DictationKey) -> [FnKeyWarning] {
        warnings(for: key, fnUsage: systemFnUsage(), wisprFlowRunning: isWisprFlowRunning())
    }

    /// The pure decision behind `warnings(for:)`.
    public static func warnings(for key: DictationKey, fnUsage: Int?, wisprFlowRunning: Bool) -> [FnKeyWarning] {
        guard key == .fn else { return [] }
        var result: [FnKeyWarning] = []
        if fnUsage != 0 {
            result.append(.systemUsesFn(usage: fnUsage))
        }
        if wisprFlowRunning {
            result.append(.wisprFlowRunning)
        }
        return result
    }

    public static func openKeyboardSettings() {
        guard let url = keyboardSettingsURL else { return }
        NSWorkspace.shared.open(url)
    }
}
