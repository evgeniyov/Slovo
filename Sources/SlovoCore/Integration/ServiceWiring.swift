import Foundation

/// Connections between services that no single module owns: the paste-last shortcut
/// follows its setting, the dictation engine is preloaded once its model becomes
/// usable (a finished download, or Apple Dictation's asset check arriving after
/// launch), and an engine is released before its model files are deleted.
///
/// Hotkey configuration and engine choice are followed by `DictationController` and
/// `EngineManager` themselves.
@MainActor
public final class ServiceWiring {
    private let settings: AppSettings
    private let models: ModelManaging
    private let engines: EngineProviding
    private let pasteLast: GlobalShortcutRegistering
    private let pasteLastAction: @MainActor () -> Void
    private let log = SlovoLog.logger("Wiring")

    /// The shortcut last handed to the registrar, registered or refused, so a refused
    /// one is not retried on every unrelated settings change.
    private var appliedShortcut: HotkeyShortcut?
    public private(set) var isPasteLastRegistered = false
    /// Engines whose models were usable at the last check.
    private var usableEngines: Set<EngineID> = []
    private var defaultsObserver: NSObjectProtocol?

    public init(settings: AppSettings, models: ModelManaging, engines: EngineProviding,
                pasteLast: GlobalShortcutRegistering, pasteLastAction: @escaping @MainActor () -> Void) {
        self.settings = settings
        self.models = models
        self.engines = engines
        self.pasteLast = pasteLast
        self.pasteLastAction = pasteLastAction
    }

    /// Registers the paste-last shortcut, remembers which models are usable now and
    /// starts following settings. Call after the first `ModelManaging.refresh()`.
    public func start() {
        usableEngines = currentlyUsableEngines()
        applySettings()
        guard defaultsObserver == nil else { return }
        defaultsObserver = NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: settings.defaults, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                _ = self?.applySettings()
            }
        }
    }

    public func stop() {
        if let defaultsObserver {
            NotificationCenter.default.removeObserver(defaultsObserver)
        }
        defaultsObserver = nil
        pasteLast.unregister()
        appliedShortcut = nil
        isPasteLastRegistered = false
    }

    /// Re-registers the paste-last shortcut when its setting changed. Returns whether
    /// the current shortcut is registered.
    @discardableResult
    public func applySettings() -> Bool {
        let shortcut = settings.pasteLastShortcut
        guard shortcut != appliedShortcut else { return isPasteLastRegistered }
        appliedShortcut = shortcut
        isPasteLastRegistered = pasteLast.register(shortcut, action: pasteLastAction)
        if !isPasteLastRegistered {
            log.error("The paste-last shortcut was refused by the system (key code \(shortcut.keyCode), modifiers \(shortcut.modifiers.rawValue))")
        }
        return isPasteLastRegistered
    }

    /// Call after model statuses change. Preloads the dictation engine when its model
    /// has just become usable; `EngineManager` does not watch model files itself.
    public func modelStatusesDidChange() {
        let usable = currentlyUsableEngines()
        let becameUsable = usable.subtracting(usableEngines)
        usableEngines = usable
        let wanted = engines.engineID(for: .dictation(settings.languageMode))
        guard becameUsable.contains(wanted) else { return }
        log.info("\(wanted.rawValue, privacy: .public) became usable; preloading")
        engines.preloadForCurrentSettings()
    }

    /// Releases the engine from memory, waits for a load that is still reading the
    /// files, then deletes them. ONNX Runtime aborts the whole app when a model file
    /// disappears in the middle of a load.
    ///
    /// `EngineManager` refuses new loads of the engine while it waits, and none can
    /// start between the wait and the removal: both run on the main actor with no
    /// suspension in between, and the removal changes the model status at once.
    public func deleteModel(_ id: EngineID) async throws {
        engines.unload(id)
        await engines.waitForLoad(id)
        defer { usableEngines = currentlyUsableEngines() }
        try models.delete(id)
    }

    private func currentlyUsableEngines() -> Set<EngineID> {
        Set(EngineID.allCases.filter { models.status(of: $0).isUsable })
    }
}
