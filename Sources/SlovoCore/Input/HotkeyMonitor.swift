import AppKit
import Carbon.HIToolbox
import Combine
import CoreGraphics
import Foundation

/// A key listener that can also cancel a dictation still being transcribed. Separate
/// from the frozen `HotkeyMonitoring`, so its fakes need not implement it.
@MainActor
public protocol ProcessingAwareHotkeys: AnyObject {
    /// Tells the listener whether a finished dictation is being transcribed, so Esc
    /// cancels it although nothing records.
    func syncProcessing(_ isProcessing: Bool)
}

/// A listen-only `CGEventTap` for `flagsChanged` and `keyDown` that drives
/// `HotkeyStateMachine`.
///
/// The tap exists only while Input Monitoring is granted. It is re-enabled after
/// `tapDisabledByTimeout`/`tapDisabledByUserInput` and recreated on wake and on
/// session activation.
///
/// Under Secure Event Input macOS delivers modifier changes but hides key presses from
/// event taps, so a held key cannot see that it became part of a shortcut. The system's
/// key press counter still counts them: a count that moved while the key was held or a
/// tap was pending turns the press into a shortcut, as a seen key press would.
@MainActor
public final class HotkeyMonitor: ObservableObject, HotkeyMonitoring, ProcessingAwareHotkeys {
    @Published public private(set) var isRunning = false
    @Published public private(set) var isKeyDown = false
    public var onTrigger: ((DictationTrigger) -> Void)?
    public var onKeyStateChange: ((Bool) -> Void)?

    private let uptime: UptimeProviding
    private var machine: HotkeyStateMachine
    /// The system's count of hardware key presses when the dictation key last went down.
    private var keyDownCountAtPress: UInt32?
    /// True between `start` and `stop`, also while the tap could not be created, so
    /// the tap appears once Input Monitoring is granted and the app becomes active.
    private var wantsRunning = false
    private var tap: EventTapHandle?
    private var deadlineTask: Task<Void, Never>?
    private var observers: [(NotificationCenter, NSObjectProtocol)] = []
    private let logger = SlovoLog.logger("Hotkey")

    public init(uptime: UptimeProviding = SystemUptime()) {
        self.uptime = uptime
        self.machine = HotkeyStateMachine(configuration: HotkeyConfiguration())
    }

    @discardableResult
    public func start(configuration: HotkeyConfiguration) -> Bool {
        machine.configuration = configuration
        wantsRunning = true
        installObservers()
        if tap == nil {
            createTap()
        }
        return isRunning
    }

    public func update(configuration: HotkeyConfiguration) {
        machine.configuration = configuration
        publishKeyState()
        scheduleDeadline()
    }

    public func syncRecording(mode: DictationMode?) {
        _ = machine.handle(.recordingChanged(mode, at: uptime.uptime()))
        scheduleDeadline()
    }

    public func syncProcessing(_ isProcessing: Bool) {
        machine.isProcessing = isProcessing
    }

    public func stop() {
        wantsRunning = false
        removeObservers()
        destroyTap()
        deadlineTask?.cancel()
        deadlineTask = nil
        machine = HotkeyStateMachine(configuration: machine.configuration)
        publishKeyState()
    }

    // MARK: - Tap lifecycle

    private func createTap() {
        guard CGPreflightListenEventAccess() else {
            logger.info("Input Monitoring is not granted; the dictation key is inactive")
            isRunning = false
            return
        }
        let handle = EventTapHandle()
        let mask = (CGEventMask(1) << CGEventType.flagsChanged.rawValue) | (CGEventMask(1) << CGEventType.keyDown.rawValue)
        guard let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
                                           options: .listenOnly, eventsOfInterest: mask,
                                           callback: hotkeyTapCallback,
                                           userInfo: Unmanaged.passUnretained(handle).toOpaque()) else {
            logger.error("CGEvent.tapCreate failed")
            isRunning = false
            return
        }
        handle.port = port
        handle.onEvent = { [weak self] type, keyCode, flags in
            self?.receive(type: type, keyCode: keyCode, flags: flags)
        }
        guard let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, port, 0) else {
            CFMachPortInvalidate(port)
            logger.error("Could not create a run loop source for the event tap")
            isRunning = false
            return
        }
        handle.source = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: port, enable: true)
        tap = handle
        isRunning = true
        logger.info("Dictation key tap created")
    }

    private func destroyTap() {
        tap?.invalidate()
        tap = nil
        isRunning = false
    }

    /// Recreates the tap after sleep or a session switch, when macOS may have
    /// invalidated it and key releases may have been missed.
    private func recreateTap() {
        guard wantsRunning else { return }
        destroyTap()
        deliver(machine.releaseKeys(at: uptime.uptime()))
        createTap()
    }

    /// Creates a missing tap once permission appears, and re-enables a disabled one.
    private func checkTap() {
        guard wantsRunning else { return }
        if let tap, let port = tap.port {
            if !CGEvent.tapIsEnabled(tap: port) {
                CGEvent.tapEnable(tap: port, enable: true)
            }
        } else {
            createTap()
        }
    }

    private func installObservers() {
        guard observers.isEmpty else { return }
        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.sessionDidBecomeActiveNotification] {
            let token = workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.recreateTap() }
            }
            observers.append((workspace, token))
        }
        let token = NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification,
                                                           object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.checkTap() }
        }
        observers.append((NotificationCenter.default, token))
    }

    private func removeObservers() {
        for (center, token) in observers {
            center.removeObserver(token)
        }
        observers.removeAll()
    }

    // MARK: - Events

    private func receive(type: CGEventType, keyCode: UInt16, flags: UInt64) {
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            logger.info("Event tap was disabled by the system; re-enabling")
            if let port = tap?.port {
                CGEvent.tapEnable(tap: port, enable: true)
            }
        case .flagsChanged, .keyDown:
            let kind: RawKeyEvent.Kind = type == .keyDown ? .keyDown : .flagsChanged
            let now = uptime.uptime()
            let event = RawKeyEvent(kind: kind, keyCode: keyCode, flags: flags, timestamp: now)
            var triggers: [DictationTrigger] = []
            if kind == .flagsChanged, machine.recordingMode == .pushToTalk, hiddenKeyWasPressed() {
                logger.info("A key pressed under Secure Event Input made the dictation key a shortcut")
                triggers += machine.handle(.unseenKeyDown(at: now))
            }
            let wasDown = machine.isKeyDown
            triggers += machine.handle(.key(event))
            if !wasDown && machine.isKeyDown {
                keyDownCountAtPress = Self.hardwareKeyDownCount()
            }
            deliver(triggers)
        default:
            break
        }
    }

    /// Whether a key went down since the dictation key did while Secure Event Input hid it
    /// from the tap. Key presses the tap saw have already made the press a shortcut, so a
    /// count that moved for them changes nothing. Checked only under Secure Event Input:
    /// otherwise the tap sees every press, and a count read a moment late could include a
    /// key typed just after the release.
    private func hiddenKeyWasPressed() -> Bool {
        guard let keyDownCountAtPress, IsSecureEventInputEnabled() else { return false }
        return Self.hardwareKeyDownCount() != keyDownCountAtPress
    }

    /// Key presses the window server has seen from keyboards since it started, including
    /// the ones Secure Event Input keeps from event taps. Posted events do not count.
    private static func hardwareKeyDownCount() -> UInt32 {
        CGEventSource.counterForEventType(.hidSystemState, eventType: .keyDown)
    }

    private func deliver(_ triggers: [DictationTrigger]) {
        publishKeyState()
        scheduleDeadline()
        for trigger in triggers {
            onTrigger?(trigger)
        }
    }

    private func publishKeyState() {
        guard isKeyDown != machine.isKeyDown else { return }
        isKeyDown = machine.isKeyDown
        onKeyStateChange?(isKeyDown)
    }

    private func scheduleDeadline() {
        deadlineTask?.cancel()
        deadlineTask = nil
        guard let deadline = machine.nextDeadline else { return }
        let delay = max(0, deadline - uptime.uptime())
        deadlineTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            self?.fireDeadline()
        }
    }

    private func fireDeadline() {
        deadlineTask = nil
        deliver(machine.handle(.deadline(uptime.uptime())))
    }
}

/// The Core Foundation objects of one event tap. Not actor-isolated so the C callback
/// can reach it through `userInfo`; it is only touched on the main run loop.
private final class EventTapHandle: @unchecked Sendable {
    var port: CFMachPort?
    var source: CFRunLoopSource?
    var onEvent: (@MainActor (CGEventType, UInt16, UInt64) -> Void)?

    func invalidate() {
        if let source {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }
        if let port {
            CGEvent.tapEnable(tap: port, enable: false)
            CFMachPortInvalidate(port)
        }
        source = nil
        port = nil
        onEvent = nil
    }

    deinit {
        invalidate()
    }
}

/// The tap's source is on the main run loop, so the callback runs on the main thread.
private func hotkeyTapCallback(proxy: CGEventTapProxy, type: CGEventType, event: CGEvent,
                               userInfo: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    guard let userInfo else { return Unmanaged.passUnretained(event) }
    let handle = Unmanaged<EventTapHandle>.fromOpaque(userInfo).takeUnretainedValue()
    let keyCode = UInt16(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeycode))
    let flags = event.flags.rawValue
    MainActor.assumeIsolated {
        handle.onEvent?(type, keyCode, flags)
    }
    return Unmanaged.passUnretained(event)
}
