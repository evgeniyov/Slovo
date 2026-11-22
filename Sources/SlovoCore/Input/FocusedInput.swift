import AppKit
import ApplicationServices
import Combine
import Foundation

/// What the focused element of the frontmost app is, as far as accessibility can tell.
public enum InputFocus: Equatable, Sendable {
    /// A field that takes typed text, so dictation has somewhere to go.
    case textInput
    /// Something is focused and it takes no typing.
    case other
    /// Nothing could be read: no permission, nothing focused, or the app did not answer.
    case unknown
}

/// Watches whether keyboard focus is in a text field, so the idle Flow bar appears only
/// where dictating makes sense, the way a caret appears only where typing does.
///
/// Reads accessibility attributes that describe the element, never its value: the
/// focused element can be a password box or a private message.
@MainActor
public final class FocusedInputWatcher: ObservableObject {
    @Published public private(set) var focus: InputFocus = .unknown

    /// Soon enough after a click to feel immediate, rare enough to cost nothing.
    nonisolated static let interval: TimeInterval = 0.4
    /// An app that stops answering must not stall the watcher.
    nonisolated static let messagingTimeout: Float = 0.15
    /// A hidden switch: when on, every change of focus is written to `logURL`, to find
    /// out how an app whose fields are not recognised describes them.
    public nonisolated static let debugLogKey = "debug.focusLog"

    /// Roles that are text entry by definition.
    nonisolated static let textRoles: Set<String> = [
        kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole, "AXSearchField",
    ]
    /// Web/native editors may expose only their enclosing container as focused.
    /// Its role alone cannot tell whether typing is accepted by a child.
    nonisolated static let containerRoles: Set<String> = [
        kAXGroupRole, kAXWindowRole, kAXScrollAreaRole, kAXSplitGroupRole,
        "AXWebArea", "AXLayoutArea", "AXSheet", "AXDialog", "AXUnknown",
    ]

    private let defaults: UserDefaults
    private let logURL: URL
    private let queue = DispatchQueue(label: "local.slovo.focused-input", qos: .utility)
    private let probe = FocusProbe()
    private var timer: DispatchSourceTimer?
    private var observationID: UUID?
    private var lastLogged: String?

    public init(defaults: UserDefaults, logURL: URL) {
        self.defaults = defaults
        self.logURL = logURL
    }

    public func start() {
        guard timer == nil else { return }
        let probe = self.probe
        let id = UUID()
        observationID = id
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: Self.interval, leeway: .milliseconds(100))
        timer.setEventHandler { [weak self] in
            let reading = probe.read()
            Task { @MainActor [weak self] in
                guard let self, self.observationID == id else { return }
                self.apply(reading)
            }
        }
        timer.resume()
        self.timer = timer
    }

    public func stop() {
        guard let timer else { return }
        timer.cancel()
        observationID = nil
        self.timer = nil
        if focus != .unknown {
            focus = .unknown
        }
    }

    private func apply(_ reading: Reading) {
        if focus != reading.focus {
            focus = reading.focus
        }
        guard defaults.bool(forKey: Self.debugLogKey), reading.summary != lastLogged else { return }
        lastLogged = reading.summary
        let stamp = ISO8601DateFormatter.string(from: Date(), timeZone: .current,
                                                formatOptions: [.withTime, .withColonSeparatorInTime])
        let line = "\(stamp) \(reading.summary)\n"
        guard let data = line.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: logURL) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: logURL)
        }
    }

    // MARK: - Reading

    struct Reading: Sendable {
        var focus: InputFocus
        var summary: String
    }

    /// The rule, apart from the system calls so it can be tested.
    nonisolated static func classify(role: String?, subrole: String?, editable: Bool,
                                     valueSettable: Bool, hasSelection: Bool) -> InputFocus {
        guard let role else { return .unknown }
        // Other apps cannot paste into a password field while secure input is on.
        if subrole == kAXSecureTextFieldSubrole {
            return .other
        }
        if textRoles.contains(role) || editable {
            return .textInput
        }
        // Web and custom editors often use a generic role but still take text.
        if valueSettable && hasSelection {
            return .textInput
        }
        if containerRoles.contains(role) {
            return .unknown
        }
        return .other
    }

    nonisolated static func copy(_ element: AXUIElement, _ attribute: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value
    }

    nonisolated static func element(_ element: AXUIElement, _ attribute: String) -> AXUIElement? {
        guard let value = copy(element, attribute), CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return unsafeBitCast(value, to: AXUIElement.self)
    }

    nonisolated static func string(_ element: AXUIElement, _ attribute: String) -> String? {
        copy(element, attribute) as? String
    }

    nonisolated static func bool(_ element: AXUIElement, _ attribute: String) -> Bool {
        (copy(element, attribute) as? Bool) ?? false
    }
}

/// One poll of the frontmost app, plus what polls remember between each other. Used only
/// on the watcher's serial queue.
final class FocusProbe: @unchecked Sendable {
    /// Electron's own switch for building its accessibility tree.
    static let electronAccessibility = "AXManualAccessibility"
    /// Plenty for a day of app switching; cleared rather than grown without end.
    static let rememberedProcesses = 256

    /// Whether each process is an Electron app, so the bundle is inspected once.
    private var electron: [pid_t: Bool] = [:]
    /// Electron apps already asked to build their tree.
    private var asked: Set<pid_t> = []

    func read() -> FocusedInputWatcher.Reading {
        guard AXIsProcessTrusted() else {
            return .init(focus: .unknown, summary: "no accessibility permission")
        }
        // The app comes from the workspace, not from accessibility: an app that describes
        // nothing still has a name, so the log can say which one it was.
        guard let app = NSWorkspace.shared.frontmostApplication else {
            return .init(focus: .unknown, summary: "no frontmost app")
        }
        let pid = app.processIdentifier
        let name = app.localizedName ?? app.bundleIdentifier ?? "?"
        let appElement = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(appElement, FocusedInputWatcher.messagingTimeout)

        let isElectron = self.isElectron(app)
        if isElectron, !asked.contains(pid) {
            // Electron builds its accessibility tree only for an assistive app that asks,
            // so without this its fields are invisible and the bar would stay everywhere.
            // It costs that app some work, which is why only Electron apps are asked.
            AXUIElementSetAttributeValue(appElement, Self.electronAccessibility as CFString, kCFBooleanTrue)
            remember(pid, in: &asked)
        }

        let label = isElectron ? "\(name) (Electron)" : name
        var raw: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(appElement, kAXFocusedUIElementAttribute as CFString, &raw)
        guard result == .success, let raw, CFGetTypeID(raw) == AXUIElementGetTypeID() else {
            // No AX element is not evidence of no text input. ChatGPT, for example,
            // returns .noValue even while its composer has keyboard focus.
            let focus = Self.focusWithoutElement(result)
            return .init(focus: focus, summary: "\(label) · nothing focused (\(result.rawValue)) → \(focus)")
        }
        let focused = unsafeBitCast(raw, to: AXUIElement.self)
        // The timeout belongs to each element, not to its app: without this the five
        // reads below wait the system default of several seconds each on a hung app.
        AXUIElementSetMessagingTimeout(focused, FocusedInputWatcher.messagingTimeout)
        let role = FocusedInputWatcher.string(focused, kAXRoleAttribute)
        let subrole = FocusedInputWatcher.string(focused, kAXSubroleAttribute)
        let editable = FocusedInputWatcher.bool(focused, "AXEditable")
        var settable: DarwinBoolean = false
        let valueSettable = AXUIElementIsAttributeSettable(focused, kAXValueAttribute as CFString, &settable) == .success
            && settable.boolValue
        let hasSelection = FocusedInputWatcher.copy(focused, kAXSelectedTextRangeAttribute) != nil
        let focus = FocusedInputWatcher.classify(role: role, subrole: subrole, editable: editable,
                                                 valueSettable: valueSettable, hasSelection: hasSelection)
        let summary = "\(label) · \(role ?? "—")/\(subrole ?? "—") · editable=\(editable) "
            + "settable=\(valueSettable) selection=\(hasSelection) → \(focus)"
        return .init(focus: focus, summary: summary)
    }

    /// What an app's failure to name a focused element means.
    static func focusWithoutElement(_ result: AXError) -> InputFocus {
        .unknown
    }

    private func isElectron(_ app: NSRunningApplication) -> Bool {
        let pid = app.processIdentifier
        if let known = electron[pid] {
            return known
        }
        let framework = app.bundleURL?.appendingPathComponent("Contents/Frameworks/Electron Framework.framework")
        let value = framework.map { FileManager.default.fileExists(atPath: $0.path) } ?? false
        if electron.count >= Self.rememberedProcesses {
            electron.removeAll()
        }
        electron[pid] = value
        return value
    }

    private func remember(_ pid: pid_t, in set: inout Set<pid_t>) {
        if set.count >= Self.rememberedProcesses {
            set.removeAll()
        }
        set.insert(pid)
    }
}
