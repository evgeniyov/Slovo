import Combine
import Foundation

/// Connections between the meeting services that no single module owns: the mark-moment
/// and microphone shortcuts exist only while a meeting records, and the speaker model
/// leaves memory before its files are deleted.
///
/// The shortcuts are global hot keys. Holding them outside recordings would take ⌃⌘M and
/// ⌃⌘U (or the chosen combinations) away from every other app for nothing, so they are
/// registered when a recording starts and removed when it stops.
@MainActor
public final class MeetingWiring: ObservableObject {
    /// The mark-moment shortcut macOS refused at the last registration, until one
    /// succeeds. Settings asks for another combination while this one is still selected.
    @Published public private(set) var refusedShortcut: HotkeyShortcut?
    /// The same for the microphone shortcut.
    @Published public private(set) var refusedMicrophoneShortcut: HotkeyShortcut?

    public var isShortcutRegistered: Bool { markShortcut.isRegistered }
    public var isMicrophoneShortcutRegistered: Bool { microphoneShortcut?.isRegistered ?? false }

    private let settings: AppSettings
    private let diarization: any DiarizationProviding & DiarizationModelManaging
    private let recordingState: AnyPublisher<Bool, Never>
    private let markShortcut: RecordingShortcut
    private let microphoneShortcut: RecordingShortcut?
    private let log = SlovoLog.logger("MeetingWiring")

    private var isRecording = false
    private var recordingObserver: AnyCancellable?
    private var defaultsObserver: NSObjectProtocol?

    /// - Parameters:
    ///   - shortcut: a registrar of its own for marking moments; the paste-last shortcut
    ///     keeps a separate one, because a registrar holds one hot key at a time.
    ///   - microphoneShortcut: another registrar, for switching the microphone; nil leaves
    ///     that shortcut out.
    ///   - isRecording: true while a meeting records; delivered on the main actor.
    ///   - markMoment: runs when the mark-moment shortcut is pressed.
    ///   - toggleMicrophone: runs when the microphone shortcut is pressed.
    public init(settings: AppSettings, shortcut: GlobalShortcutRegistering,
                microphoneShortcut: GlobalShortcutRegistering? = nil,
                diarization: any DiarizationProviding & DiarizationModelManaging,
                isRecording: AnyPublisher<Bool, Never>, markMoment: @escaping @MainActor () -> Void,
                toggleMicrophone: @escaping @MainActor () -> Void = {}) {
        self.settings = settings
        self.diarization = diarization
        recordingState = isRecording
        markShortcut = RecordingShortcut(registrar: shortcut, action: markMoment)
        self.microphoneShortcut = microphoneShortcut.map { RecordingShortcut(registrar: $0, action: toggleMicrophone) }
    }

    /// The app's composition: the recorder's state decides when the shortcuts exist, and
    /// they mark a moment in that recorder and switch its microphone.
    public convenience init(settings: AppSettings, recorder: MeetingRecorder, shortcut: GlobalShortcutRegistering,
                            microphoneShortcut: GlobalShortcutRegistering? = nil,
                            diarization: any DiarizationProviding & DiarizationModelManaging) {
        // Only `.recording` counts: while the files close after Stop there is nothing to mark.
        let recording = recorder.$state
            .map { state -> Bool in
                if case .recording = state {
                    return true
                }
                return false
            }
            .eraseToAnyPublisher()
        self.init(settings: settings, shortcut: shortcut, microphoneShortcut: microphoneShortcut,
                  diarization: diarization, isRecording: recording,
                  markMoment: { [weak recorder] in _ = recorder?.markMoment() },
                  toggleMicrophone: { [weak recorder] in _ = recorder?.toggleMicrophone() })
    }

    /// Follows the recording state and the shortcut setting until `stop()`.
    public func start() {
        guard recordingObserver == nil else { return }
        // No hop to the main queue: the registration changes in the same turn as the
        // recorder's state, so a press right after Start already marks a moment.
        recordingObserver = recordingState
            .removeDuplicates()
            .sink { [weak self] recording in
                self?.recordingDidChange(recording)
            }
        defaultsObserver = NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: settings.defaults, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                _ = self?.applySettings()
            }
        }
    }

    /// Removes the shortcut and stops following the recorder and settings.
    public func stop() {
        recordingObserver?.cancel()
        recordingObserver = nil
        if let defaultsObserver {
            NotificationCenter.default.removeObserver(defaultsObserver)
        }
        defaultsObserver = nil
        isRecording = false
        unregister()
    }

    /// Registers the current shortcuts while recording and removes them otherwise. Returns
    /// whether the mark-moment shortcut is registered now.
    @discardableResult
    public func applySettings() -> Bool {
        guard isRecording else {
            unregister()
            return false
        }
        let wanted = settings.highlightShortcut
        if let registered = markShortcut.apply(wanted) {
            report(registered, wanted, refused: \.refusedShortcut, name: "mark-moment")
        }
        if let microphoneShortcut {
            let wanted = settings.microphoneShortcut
            if let registered = microphoneShortcut.apply(wanted) {
                report(registered, wanted, refused: \.refusedMicrophoneShortcut, name: "microphone")
            }
        }
        return isShortcutRegistered
    }

    /// Publishes a refusal, and clears it once a registration succeeds, without touching
    /// the published value when nothing changed.
    private func report(_ registered: Bool, _ shortcut: HotkeyShortcut,
                        refused: ReferenceWritableKeyPath<MeetingWiring, HotkeyShortcut?>, name: String) {
        if registered {
            if self[keyPath: refused] != nil {
                self[keyPath: refused] = nil
            }
        } else {
            self[keyPath: refused] = shortcut
            log.error("The \(name, privacy: .public) shortcut was refused by the system (key code \(shortcut.keyCode), modifiers \(shortcut.modifiers.rawValue))")
        }
    }

    /// Unloads the speaker model, waits for a load that is still reading the files, then
    /// deletes them. Core ML must not lose a model file in the middle of a load.
    ///
    /// `DiarizationService` refuses new loads while it waits, and none can start between
    /// the wait and the removal: both run on the main actor with no suspension in between.
    public func deleteDiarizationModel() async throws {
        diarization.release()
        await diarization.waitForLoad()
        try diarization.delete()
    }

    private func recordingDidChange(_ recording: Bool) {
        isRecording = recording
        applySettings()
    }

    private func unregister() {
        markShortcut.unregister()
        microphoneShortcut?.unregister()
    }
}

/// One global shortcut that exists only while a meeting records.
@MainActor
private final class RecordingShortcut {
    private let registrar: GlobalShortcutRegistering
    private let action: @MainActor () -> Void
    /// The shortcut last handed to the registrar, registered or refused, so a refused one
    /// is not retried on every unrelated settings change.
    private var applied: HotkeyShortcut?
    private(set) var isRegistered = false

    init(registrar: GlobalShortcutRegistering, action: @escaping @MainActor () -> Void) {
        self.registrar = registrar
        self.action = action
    }

    /// Registers `wanted` unless it is the shortcut already handed over. Returns whether
    /// the system accepted it, or nil when nothing was attempted.
    func apply(_ wanted: HotkeyShortcut) -> Bool? {
        guard wanted != applied else { return nil }
        applied = wanted
        isRegistered = registrar.register(wanted, action: action)
        return isRegistered
    }

    func unregister() {
        guard applied != nil || isRegistered else { return }
        registrar.unregister()
        applied = nil
        isRegistered = false
    }
}
