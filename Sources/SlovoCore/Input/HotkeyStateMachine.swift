import Foundation

/// One keyboard event from the event tap, reduced to the fields the state machine reads.
public struct RawKeyEvent: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case flagsChanged
        case keyDown
        case keyUp
    }

    public var kind: Kind
    public var keyCode: UInt16
    /// `CGEventFlags.rawValue`, including device-dependent left/right modifier bits.
    public var flags: UInt64
    /// Monotonic seconds (the event timestamp or `systemUptime`).
    public var timestamp: TimeInterval

    public init(kind: Kind, keyCode: UInt16, flags: UInt64, timestamp: TimeInterval) {
        self.kind = kind
        self.keyCode = keyCode
        self.flags = flags
        self.timestamp = timestamp
    }

    /// kVK_Escape.
    public static let escapeKeyCode: UInt16 = 53
}

/// Modifier bits of `CGEventFlags` the state machine reads, spelled out so the pure
/// logic does not import CoreGraphics.
public enum KeyFlagMask {
    /// `NX_DEVICELCTLKEYMASK`.
    public static let leftControl: UInt64 = 0x0000_0001
    /// `NX_DEVICELCMDKEYMASK`.
    public static let leftCommand: UInt64 = 0x0000_0008
    /// `NX_DEVICERCMDKEYMASK`.
    public static let rightCommand: UInt64 = 0x0000_0010
    /// `NX_DEVICELALTKEYMASK`.
    public static let leftOption: UInt64 = 0x0000_0020
    /// `NX_DEVICERALTKEYMASK`.
    public static let rightOption: UInt64 = 0x0000_0040
    /// `NX_DEVICERCTLKEYMASK`.
    public static let rightControl: UInt64 = 0x0000_2000
    /// Every device-dependent modifier bit (shift, control, option, command on both sides).
    public static let deviceDependent: UInt64 = 0x0000_20FF
    /// `kCGEventFlagMaskControl`.
    public static let control: UInt64 = 0x0004_0000
    /// `kCGEventFlagMaskAlternate`.
    public static let option: UInt64 = 0x0008_0000
    /// `kCGEventFlagMaskCommand`.
    public static let command: UInt64 = 0x0010_0000
    /// `kCGEventFlagMaskSecondaryFn`.
    public static let function: UInt64 = 0x0080_0000
}

/// Inputs of the hotkey state machine.
public enum HotkeyInput: Equatable, Sendable {
    case key(RawKeyEvent)
    /// The deadline the machine asked for (`nextDeadline`) has passed.
    case deadline(TimeInterval)
    /// What the controller is actually doing, nil when not recording.
    case recordingChanged(DictationMode?, at: TimeInterval)
    /// A key went down that the tap could not see: under Secure Event Input macOS hides
    /// key presses from event taps, and `HotkeyMonitor` notices them only from the system's
    /// key press counter. Treated like any other non-modifier key.
    case unseenKeyDown(at: TimeInterval)
}

/// The dictation key logic as a pure function of events: push-to-talk with a minimum
/// hold, double-tap hands-free, the finishing tap and Esc.
///
/// `HotkeyMonitor` feeds it events from the tap and schedules a timer for
/// `nextDeadline`; tests feed it synthetic events.
///
/// Capture starts on key down (`.begin(.pushToTalk)`) so the first syllable is never
/// lost. A release after `minimumHold` finishes. A shorter release is a tap: the
/// recording keeps running for `doubleTapWindow` after the release, and a second press
/// in that window promotes it to hands-free; otherwise the tap is discarded. In
/// hands-free the next press finishes. Esc cancels any recording, and a dictation still
/// being transcribed. Any other key pressed while the dictation key is held turns the
/// press into a shortcut, which is discarded.
public struct HotkeyStateMachine: Sendable {
    public var configuration: HotkeyConfiguration {
        didSet {
            guard configuration.key != oldValue.key else { return }
            // The old key's release will never be matched against the new key, so a
            // held push-to-talk would never end; let the next tap of the new key end it.
            heldKeys.removeAll()
            isKeyDown = false
            if case .holding = phase {
                phase = .handsFree
            }
        }
    }

    /// Whether the configured dictation key is held.
    public private(set) var isKeyDown = false
    /// True while the controller transcribes a finished dictation; Esc then cancels it
    /// although nothing records any more.
    public var isProcessing = false
    /// When the machine next needs `.deadline`, for example the end of the double-tap window.
    public private(set) var nextDeadline: TimeInterval?

    private enum Phase: Equatable, Sendable {
        /// Not recording. The key may still be held after a cancel or a finishing tap;
        /// its release is ignored.
        case idle
        /// Push-to-talk: the key went down at `downAt` and capture is running.
        case holding(downAt: TimeInterval)
        /// A short tap was released; capture keeps running until `deadline` in case a
        /// second tap follows.
        case awaitingSecondTap(deadline: TimeInterval)
        /// Capture continues without the key; the next press finishes.
        case handsFree
    }

    private var phase: Phase = .idle
    private var heldKeys: Set<UInt16> = []

    public init(configuration: HotkeyConfiguration) {
        self.configuration = configuration
    }

    /// The recording the machine believes is running, nil when idle. A pending tap
    /// counts as push-to-talk because its capture is still running.
    public var recordingMode: DictationMode? {
        switch phase {
        case .idle: return nil
        case .holding, .awaitingSecondTap: return .pushToTalk
        case .handsFree: return .handsFree
        }
    }

    /// Advances the machine and returns the triggers to send, in order.
    public mutating func handle(_ input: HotkeyInput) -> [DictationTrigger] {
        var triggers: [DictationTrigger] = []
        switch input {
        case let .key(event):
            triggers += expireTap(at: event.timestamp, inclusive: false)
            triggers += handleKey(event)
        case let .deadline(time):
            triggers += expireTap(at: time, inclusive: true)
        case let .recordingChanged(mode, time):
            syncRecording(mode, at: time)
        case let .unseenKeyDown(time):
            triggers += expireTap(at: time, inclusive: false)
            triggers += chordKeyPressed()
        }
        updateDeadline()
        return triggers
    }

    /// Forgets which keys are held, as if they were all released at `timestamp`.
    /// `HotkeyMonitor` calls it when the tap is recreated after sleep or a session
    /// switch, when releases may have been missed.
    public mutating func releaseKeys(at timestamp: TimeInterval) -> [DictationTrigger] {
        var triggers = expireTap(at: timestamp, inclusive: false)
        let wasDown = isKeyDown
        heldKeys.removeAll()
        isKeyDown = false
        if wasDown {
            triggers += keyReleased(at: timestamp)
        }
        updateDeadline()
        return triggers
    }

    // MARK: - Keys

    private mutating func handleKey(_ event: RawKeyEvent) -> [DictationTrigger] {
        switch event.kind {
        case .keyDown:
            return otherKeyPressed(event)
        case .keyUp:
            return []
        case .flagsChanged:
            let keyCodes = configuration.key.keyCodes
            guard keyCodes.contains(event.keyCode) else { return [] }
            if Self.isModifierDown(keyCode: event.keyCode, flags: event.flags) {
                heldKeys.insert(event.keyCode)
            } else {
                heldKeys.remove(event.keyCode)
            }
            let wasDown = isKeyDown
            isKeyDown = keyCodes.allSatisfy(heldKeys.contains)
            if !wasDown && isKeyDown {
                return keyPressed(at: event.timestamp)
            }
            if wasDown && !isKeyDown {
                return keyReleased(at: event.timestamp)
            }
            return []
        }
    }

    private mutating func keyPressed(at time: TimeInterval) -> [DictationTrigger] {
        switch phase {
        case .idle:
            phase = .holding(downAt: time)
            return [.begin(.pushToTalk)]
        case .awaitingSecondTap:
            phase = .handsFree
            return [.promoteToHandsFree]
        case .handsFree:
            phase = .idle
            return [.finish]
        case .holding:
            return []
        }
    }

    private mutating func keyReleased(at time: TimeInterval) -> [DictationTrigger] {
        guard case let .holding(downAt) = phase else { return [] }
        if time - downAt >= configuration.minimumHold {
            phase = .idle
            return [.finish]
        }
        if configuration.handsFreeEnabled {
            phase = .awaitingSecondTap(deadline: time + configuration.doubleTapWindow)
            return []
        }
        phase = .idle
        return [.discard]
    }

    /// Esc cancels. Any other key pressed while the dictation key is held, or during a
    /// pending tap, means the key was part of a shortcut, however long it was held:
    /// Fn+Down is Page Down, Right Command+Tab switches apps. Finishing would paste
    /// whatever the microphone picked up meanwhile.
    private mutating func otherKeyPressed(_ event: RawKeyEvent) -> [DictationTrigger] {
        // Modifier keycodes (54...63) never form a shortcut on their own. Globe on recent
        // Apple keyboards can also arrive as a stray key down with a code no other key
        // uses; counting it as a chord would throw away every Fn dictation.
        guard !Self.modifierKeyCodes.contains(event.keyCode), event.keyCode != Self.globeKeyCode else { return [] }
        if event.keyCode == RawKeyEvent.escapeKeyCode {
            guard configuration.escapeCancels, phase != .idle || isProcessing else { return [] }
            phase = .idle
            return [.cancel]
        }
        return chordKeyPressed()
    }

    /// A non-modifier key other than Esc: a held key or a pending tap was part of a shortcut.
    private mutating func chordKeyPressed() -> [DictationTrigger] {
        switch phase {
        case .holding, .awaitingSecondTap:
            phase = .idle
            return [.discard]
        case .idle, .handsFree:
            return []
        }
    }

    // MARK: - Time and controller state

    private mutating func expireTap(at time: TimeInterval, inclusive: Bool) -> [DictationTrigger] {
        guard case let .awaitingSecondTap(deadline) = phase else { return [] }
        let expired = inclusive ? time >= deadline : time > deadline
        guard expired else { return [] }
        phase = .idle
        return [.discard]
    }

    private mutating func syncRecording(_ mode: DictationMode?, at time: TimeInterval) {
        switch mode {
        case nil:
            phase = .idle
        case .handsFree?:
            phase = .handsFree
        case .pushToTalk?:
            guard phase == .idle else { return }
            // Push-to-talk without the key held cannot end on a release, so a tap ends it.
            phase = isKeyDown ? .holding(downAt: time) : .handsFree
        }
    }

    private mutating func updateDeadline() {
        if case let .awaitingSecondTap(deadline) = phase {
            nextDeadline = deadline
        } else {
            nextDeadline = nil
        }
    }

    /// kVK_RightCommand (54) through kVK_Function (63).
    static let modifierKeyCodes: ClosedRange<UInt16> = 54...63
    /// The key down some Apple keyboards post for Globe; no Carbon virtual key uses it.
    static let globeKeyCode: UInt16 = 179

    /// Whether a modifier key is down after a `flagsChanged` event for it.
    ///
    /// The device-dependent bit tells left from right. Keyboards that report no
    /// device-dependent bits at all fall back to the side-independent mask.
    static func isModifierDown(keyCode: UInt16, flags: UInt64) -> Bool {
        let deviceBit: UInt64
        let generalBit: UInt64
        switch keyCode {
        case 63:
            return flags & KeyFlagMask.function != 0
        case 61:
            deviceBit = KeyFlagMask.rightOption
            generalBit = KeyFlagMask.option
        case 58:
            deviceBit = KeyFlagMask.leftOption
            generalBit = KeyFlagMask.option
        case 54:
            deviceBit = KeyFlagMask.rightCommand
            generalBit = KeyFlagMask.command
        case 55:
            deviceBit = KeyFlagMask.leftCommand
            generalBit = KeyFlagMask.command
        case 59:
            deviceBit = KeyFlagMask.leftControl
            generalBit = KeyFlagMask.control
        case 62:
            deviceBit = KeyFlagMask.rightControl
            generalBit = KeyFlagMask.control
        default:
            return false
        }
        if flags & deviceBit != 0 {
            return true
        }
        return flags & KeyFlagMask.deviceDependent == 0 && flags & generalBit != 0
    }
}
