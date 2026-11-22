import Foundation

/// The key held for push-to-talk and double-tapped for hands-free dictation.
public enum DictationKey: String, CaseIterable, Codable, Sendable, Identifiable {
    /// Fn/Globe, keycode 63. Apple keyboards only; macOS may also use it to switch
    /// input source or start its own dictation (`AppleFnUsageType`). The default, as in
    /// Wispr Flow.
    case fn
    /// Right Option, keycode 61.
    case rightOption
    /// Right Command, keycode 54.
    case rightCommand
    /// Left Control (59) and Left Option (58) held together.
    case leftControlOption

    public var id: String { rawValue }

    /// Virtual keycodes that must all be down for the key to count as held.
    public var keyCodes: [UInt16] {
        switch self {
        case .fn: return [63]
        case .rightOption: return [61]
        case .rightCommand: return [54]
        case .leftControlOption: return [59, 58]
        }
    }

    /// Keycap symbol shown in the interface; symbols are the same in every language.
    public var symbol: String {
        switch self {
        case .fn: return "fn"
        case .rightOption: return "⌥"
        case .rightCommand: return "⌘"
        case .leftControlOption: return "⌃⌥"
        }
    }

    /// Fn can collide with the system's own use of the key, so the UI warns about it.
    public var mayConflictWithSystem: Bool {
        self == .fn
    }
}

/// Modifier set of a global shortcut, independent of AppKit and Carbon types.
public struct HotkeyModifiers: OptionSet, Codable, Hashable, Sendable {
    public let rawValue: Int

    public init(rawValue: Int) {
        self.rawValue = rawValue
    }

    public static let command = HotkeyModifiers(rawValue: 1 << 0)
    public static let control = HotkeyModifiers(rawValue: 1 << 1)
    public static let option = HotkeyModifiers(rawValue: 1 << 2)
    public static let shift = HotkeyModifiers(rawValue: 1 << 3)
}

/// A key plus modifiers, such as Control+Command+V for "Paste last transcript".
public struct HotkeyShortcut: Codable, Hashable, Sendable {
    /// Virtual keycode (kVK_ANSI_V is 9).
    public var keyCode: UInt32
    public var modifiers: HotkeyModifiers

    public init(keyCode: UInt32, modifiers: HotkeyModifiers) {
        self.keyCode = keyCode
        self.modifiers = modifiers
    }

    public static let pasteLastDefault = HotkeyShortcut(keyCode: 9, modifiers: [.control, .command])

    /// Control+Command+M (kVK_ANSI_M is 46): mark an important moment while a meeting records.
    public static let markMomentDefault = HotkeyShortcut(keyCode: 46, modifiers: [.control, .command])

    /// The mark-moment shortcuts Settings offers: ⌃⌘M, ⌃⌥M, ⌃⇧M.
    public static let markMomentChoices: [HotkeyShortcut] = [
        markMomentDefault,
        HotkeyShortcut(keyCode: 46, modifiers: [.control, .option]),
        HotkeyShortcut(keyCode: 46, modifiers: [.control, .shift])
    ]

    /// Control+Command+U (kVK_ANSI_U is 32): switch the microphone while a meeting records.
    /// Not a neighbour of M, so a press aimed at "mark moment" cannot silence the room by
    /// accident, and nothing in macOS or the usual call apps claims it.
    public static let microphoneToggleDefault = HotkeyShortcut(keyCode: 32, modifiers: [.control, .command])

    /// The microphone shortcuts Settings offers: ⌃⌘U, ⌃⌥U, ⌃⇧U.
    public static let microphoneToggleChoices: [HotkeyShortcut] = [
        microphoneToggleDefault,
        HotkeyShortcut(keyCode: 32, modifiers: [.control, .option]),
        HotkeyShortcut(keyCode: 32, modifiers: [.control, .shift])
    ]
}

/// How the hotkey state machine interprets the dictation key.
public struct HotkeyConfiguration: Equatable, Sendable {
    public var key: DictationKey
    public var handsFreeEnabled: Bool
    public var escapeCancels: Bool
    /// Holds shorter than this are taps, not dictations.
    public var minimumHold: TimeInterval
    /// Two taps within this interval start hands-free recording.
    public var doubleTapWindow: TimeInterval

    public init(key: DictationKey = .fn, handsFreeEnabled: Bool = true, escapeCancels: Bool = true,
                minimumHold: TimeInterval = 0.25, doubleTapWindow: TimeInterval = 0.4) {
        self.key = key
        self.handsFreeEnabled = handsFreeEnabled
        self.escapeCancels = escapeCancels
        self.minimumHold = minimumHold
        self.doubleTapWindow = doubleTapWindow
    }
}
