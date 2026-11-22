import Carbon.HIToolbox
import Foundation

/// "Paste last transcript" through Carbon `RegisterEventHotKey`, which needs no permission.
@MainActor
public final class PasteLastShortcut: GlobalShortcutRegistering {
    public private(set) var shortcut: HotkeyShortcut?

    private var registration: CarbonHotKeyRegistration?
    private let logger = SlovoLog.logger("PasteLast")
    /// Hot key ids are unique per process so a stale event never runs a newer action.
    private static var nextID: UInt32 = 1

    public init() {}

    @discardableResult
    public func register(_ shortcut: HotkeyShortcut, action: @escaping @MainActor () -> Void) -> Bool {
        unregister()
        let registration = CarbonHotKeyRegistration(id: Self.nextID, action: action)
        Self.nextID &+= 1
        let status = registration.register(keyCode: shortcut.keyCode,
                                           modifiers: Self.carbonModifiers(shortcut.modifiers))
        guard status == noErr else {
            logger.error("RegisterEventHotKey failed with status \(status, privacy: .public)")
            return false
        }
        self.registration = registration
        self.shortcut = shortcut
        return true
    }

    public func unregister() {
        registration?.unregister()
        registration = nil
        shortcut = nil
    }

    /// Carbon modifier flags (`cmdKey`, `shiftKey`, `optionKey`, `controlKey`).
    static func carbonModifiers(_ modifiers: HotkeyModifiers) -> UInt32 {
        var result: UInt32 = 0
        if modifiers.contains(.command) { result |= UInt32(cmdKey) }
        if modifiers.contains(.shift) { result |= UInt32(shiftKey) }
        if modifiers.contains(.option) { result |= UInt32(optionKey) }
        if modifiers.contains(.control) { result |= UInt32(controlKey) }
        return result
    }
}

/// One registered hot key and its Carbon event handler. Not actor-isolated so
/// `deinit` can release the Carbon objects; Carbon delivers hot key events on the
/// main thread.
private final class CarbonHotKeyRegistration: @unchecked Sendable {
    /// 'SLVO'.
    private static let signature: OSType = 0x534C_564F

    private let action: @MainActor () -> Void
    private let id: UInt32
    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?

    init(id: UInt32, action: @escaping @MainActor () -> Void) {
        self.id = id
        self.action = action
    }

    func register(keyCode: UInt32, modifiers: UInt32) -> OSStatus {
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let status = InstallEventHandler(GetApplicationEventTarget(), carbonHotKeyHandler, 1, &eventType,
                                         Unmanaged.passUnretained(self).toOpaque(), &handler)
        guard status == noErr else { return status }
        let hotKeyID = EventHotKeyID(signature: Self.signature, id: id)
        let registerStatus = RegisterEventHotKey(keyCode, modifiers, hotKeyID, GetApplicationEventTarget(),
                                                 0, &hotKey)
        if registerStatus != noErr {
            unregister()
        }
        return registerStatus
    }

    func unregister() {
        if let hotKey {
            UnregisterEventHotKey(hotKey)
        }
        if let handler {
            RemoveEventHandler(handler)
        }
        hotKey = nil
        handler = nil
    }

    /// Runs the action when the event is this registration's hot key.
    func handle(_ event: EventRef) -> OSStatus {
        var hotKeyID = EventHotKeyID()
        let status = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                       nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
        guard status == noErr, hotKeyID.signature == Self.signature, hotKeyID.id == id else {
            return OSStatus(eventNotHandledErr)
        }
        let action = self.action
        MainActor.assumeIsolated {
            action()
        }
        return noErr
    }

    deinit {
        unregister()
    }
}

private func carbonHotKeyHandler(_ callRef: EventHandlerCallRef?, _ event: EventRef?,
                                 _ userData: UnsafeMutableRawPointer?) -> OSStatus {
    guard let event, let userData else { return OSStatus(eventNotHandledErr) }
    let registration = Unmanaged<CarbonHotKeyRegistration>.fromOpaque(userData).takeUnretainedValue()
    return registration.handle(event)
}
