import Combine
import Foundation

/// Creates an engine for an id; the language mode picks Apple Dictation's locale or Whisper's hint.
public typealias EngineFactory = @MainActor (EngineID, LanguageMode) throws -> any TranscriptionEngine

/// Thrown instead of loading an engine whose model is not usable, so an engine never
/// downloads anything on its own.
public struct EngineUnavailableError: LocalizedError, Equatable, Sendable {
    public let engineID: EngineID
    public let status: ModelStatus

    public init(engineID: EngineID, status: ModelStatus) {
        self.engineID = engineID
        self.status = status
    }

    public var errorDescription: String? {
        switch status {
        case .damaged:
            return CoreStrings.format("dictation.error.modelDamaged", table: .dictation, engineID.displayName)
        case .downloading:
            return CoreStrings.format("dictation.error.modelDownloading", table: .dictation, engineID.displayName)
        case .systemManaged:
            return CoreStrings.string("dictation.error.appleAssetsMissing", table: .dictation)
        case .unknown, .ready, .notDownloaded:
            return CoreStrings.format("dictation.error.modelMissing", table: .dictation, engineID.displayName)
        }
    }
}

/// An engine provider that can load an engine for a language other than the current
/// setting: Apple Dictation's locale follows the language mode, so re-running an old
/// dictation must use the language it was dictated in. Separate from the frozen
/// `EngineProviding`, so its fakes need not implement it.
@MainActor
public protocol LanguageEngineProviding: EngineProviding {
    func engine(for id: EngineID, languageMode: LanguageMode) async throws -> any TranscriptionEngine
}

/// Chooses engines from Recognition settings, preloads them and releases idle ones.
///
/// Loads run one at a time, and a preload for new settings first releases the engine
/// the old settings used, so two big models are never held or loaded for nothing.
@MainActor
public final class EngineManager: ObservableObject, LanguageEngineProviding {
    @Published public private(set) var loadStates: [EngineID: EngineLoadState] = [:]

    private let settings: AppSettings
    private let models: ModelManaging
    private let factory: EngineFactory
    private let uptime: UptimeProviding
    private let log = SlovoLog.logger("Engines")

    private struct Loaded {
        let engine: any TranscriptionEngine
        let variant: LanguageMode
    }

    private struct Load {
        let number: Int
        let variant: LanguageMode
        let task: Task<EngineBox, Error>
    }

    private var loaded: [EngineID: Loaded] = [:]
    private var loads: [EngineID: Load] = [:]
    /// Loads whose task has not finished, including ones `unload` dropped: they still
    /// read the model files.
    private var running: [EngineID: Load] = [:]
    /// Engines `waitForLoad` is waiting for, with how many callers wait. Their files are
    /// about to be deleted, so no new load of them may start.
    private var draining: [EngineID: Int] = [:]
    private var lastUsed: [EngineID: TimeInterval] = [:]
    /// Completes when the most recently started load has finished.
    private var lastLoad: Task<Void, Never>?
    private var loadCount = 0
    private var idleTask: Task<Void, Never>?
    /// The dictation engine the last preload chose; nil until the app first preloads.
    private var preloadedID: EngineID?
    private var defaultsObserver: NSObjectProtocol?

    public init(settings: AppSettings, models: ModelManaging,
                factory: @escaping EngineFactory = EngineManager.makeEngine,
                uptime: UptimeProviding = SystemUptime()) {
        self.settings = settings
        self.models = models
        self.factory = factory
        self.uptime = uptime
        defaultsObserver = NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: settings.defaults, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.settingsDidChange()
            }
        }
    }

    deinit {
        if let defaultsObserver { NotificationCenter.default.removeObserver(defaultsObserver) }
        idleTask?.cancel()
    }

    public func engineID(for purpose: EnginePurpose) -> EngineID {
        settings.engine(for: purpose)
    }

    public func loadState(of id: EngineID) -> EngineLoadState {
        loadStates[id] ?? .unloaded
    }

    /// Returns the loaded engine, loading it first. Throws `EngineUnavailableError` when
    /// the model is not downloaded, damaged or still downloading.
    public func engine(for id: EngineID) async throws -> any TranscriptionEngine {
        try await engine(for: id, languageMode: settings.languageMode)
    }

    /// Returns the engine for `languageMode`, which Apple Dictation and Whisper tell apart. A
    /// different mode than the loaded one replaces the loaded engine; the next dictation
    /// in the current mode loads it back, which for Apple Dictation takes a moment.
    public func engine(for id: EngineID, languageMode: LanguageMode) async throws -> any TranscriptionEngine {
        // Its model files are about to be deleted; a load now would read files that
        // disappear halfway, which aborts the app.
        guard draining[id] == nil else {
            throw EngineUnavailableError(engineID: id, status: models.status(of: id))
        }
        let variant = Self.variant(of: id, languageMode: languageMode)
        if let current = loaded[id] {
            if current.variant == variant {
                markUsed(id)
                return current.engine
            }
            unload(id)
        }
        if let load = loads[id], load.variant == variant {
            return try await load.task.value.engine
        }
        try checkModel(id)
        return try await startLoad(id, variant: variant).value.engine
    }

    /// Loads the dictation engine for the current language mode in the background and
    /// releases the one previous settings chose.
    public func preloadForCurrentSettings() {
        let wanted = engineID(for: .dictation(settings.languageMode))
        if let previous = preloadedID, previous != wanted {
            unload(previous)
            if wanted == .whisperTurbo, previous.writesOnlyRussian {
                // Parakeet may have been cached as GigaAM's helper. Accurate mode
                // uses one recognizer; in-flight callers still own their engine.
                unload(.parakeet)
            }
        }
        preloadedID = wanted
        let variant = self.variant(of: wanted)
        if let current = loaded[wanted] {
            guard current.variant != variant else { return }
            unload(wanted)
        }
        if let load = loads[wanted], load.variant == variant {
            return
        }
        guard draining[wanted] == nil else {
            log.notice("Not preloading \(wanted.rawValue, privacy: .public): its model is being deleted")
            return
        }
        do {
            try checkModel(wanted)
        } catch {
            log.notice("Not preloading \(wanted.rawValue, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return
        }
        _ = startLoad(wanted, variant: variant)
    }

    public func markUsed(_ id: EngineID) {
        guard loaded[id] != nil else { return }
        lastUsed[id] = uptime.uptime()
        scheduleIdleUnload()
    }

    /// Releases an engine. Callers still holding it keep using it; memory is freed once
    /// they are done.
    public func unload(_ id: EngineID) {
        let wasActive = loaded[id] != nil || loads[id] != nil
        // A load in progress finishes anyway (GigaAM cannot be interrupted) and its result is dropped.
        loads[id] = nil
        loaded[id] = nil
        lastUsed[id] = nil
        if let state = loadStates[id], state != .unloaded {
            loadStates[id] = .unloaded
        }
        if wasActive {
            log.info("Released \(id.rawValue, privacy: .public)")
        }
        scheduleIdleUnload()
    }

    public func unloadAll() {
        for id in Set(loaded.keys).union(loads.keys) {
            unload(id)
        }
    }

    /// Returns once no load of the engine is running, including loads `unload` dropped.
    /// GigaAM cannot be interrupted, and ONNX Runtime aborts the whole process when its
    /// model files disappear mid-load, so deleting files waits for this.
    ///
    /// While it waits, `engine(for:)` refuses the engine and preloading skips it, so the
    /// wait cannot be extended by a new load that would read the files again. It
    /// returns without suspending when nothing is loading, so the caller can remove the
    /// files before anything else runs on the main actor.
    public func waitForLoad(_ id: EngineID) async {
        guard running[id] != nil else { return }
        draining[id, default: 0] += 1
        defer {
            if let count = draining[id], count > 1 {
                draining[id] = count - 1
            } else {
                draining[id] = nil
            }
        }
        // Loads run one after another, so once the newest load of the engine is done,
        // every earlier one is done too.
        while let load = running[id] {
            _ = try? await load.task.value
            if running[id]?.number == load.number {
                running[id] = nil
            }
        }
    }

    /// The engines Slovo ships, wired to the shared models folder.
    public static func makeEngine(_ id: EngineID, languageMode: LanguageMode) throws -> any TranscriptionEngine {
        try bundledEngine(id, languageMode: languageMode, modelsRoot: ModelStore.root)
    }

    /// A factory for the engines Slovo ships with models under `modelsRoot`.
    public static func factory(modelsRoot: URL) -> EngineFactory {
        { id, languageMode in
            try bundledEngine(id, languageMode: languageMode, modelsRoot: modelsRoot)
        }
    }

    private static func bundledEngine(_ id: EngineID, languageMode: LanguageMode,
                                      modelsRoot: URL) throws -> any TranscriptionEngine {
        let gigaAM = modelsRoot.appendingPathComponent("gigaam-v3", isDirectory: true)
        switch id {
        case .gigaamRNNT:
            return GigaAMEngine(id: id.rawValue, variant: .transducer, modelsDirectory: gigaAM)
        case .gigaamCTC:
            return GigaAMEngine(id: id.rawValue, variant: .ctc, modelsDirectory: gigaAM)
        case .parakeet:
            // Downloads happen only from the Download button, never while loading.
            return ParakeetEngine(id: id.rawValue,
                                  modelsDirectory: modelsRoot.appendingPathComponent("parakeet-v3", isDirectory: true),
                                  allowsDownload: false)
        case .appleDictation:
            guard #available(macOS 26.0, *) else {
                throw SlovoError(CoreStrings.string("dictation.error.appleUnsupported", table: .dictation))
            }
            let locale = Locale(identifier: languageMode == .english ? "en-US" : "ru-RU")
            return AppleDictationEngine(id: id.rawValue, locale: locale)
        case .whisperTurbo:
            let folder = modelsRoot.appendingPathComponent(id.modelFolder ?? "", isDirectory: true)
            return WhisperEngine(id: id.rawValue, modelFile: folder.appendingPathComponent(WhisperEngine.Model.fileName),
                                 languageMode: languageMode)
        }
    }

    // MARK: - Loading

    /// Apple Dictation's locale and Whisper's decoding hint follow the language mode.
    private func variant(of id: EngineID) -> LanguageMode {
        Self.variant(of: id, languageMode: settings.languageMode)
    }

    static func variant(of id: EngineID, languageMode: LanguageMode) -> LanguageMode {
        guard id == .appleDictation || id == .whisperTurbo else { return .mixed }
        return languageMode == .english ? .english : .russian
    }

    private func checkModel(_ id: EngineID) throws {
        var status = models.status(of: id)
        if status == .unknown {
            models.refresh()
            status = models.status(of: id)
        }
        if id.usesDownloadedModel {
            guard case .ready = status else {
                throw EngineUnavailableError(engineID: id, status: status)
            }
        } else if status == .systemManaged(available: false) {
            throw EngineUnavailableError(engineID: id, status: status)
        }
    }

    private func startLoad(_ id: EngineID, variant: LanguageMode) -> Task<EngineBox, Error> {
        loadCount += 1
        let number = loadCount
        let previous = lastLoad
        let factory = self.factory
        loadStates[id] = .loading
        log.info("Loading \(id.rawValue, privacy: .public)")
        let task = Task { [weak self] () throws -> EngineBox in
            // One load at a time: two big models loading together double peak memory and
            // compete for the same cores.
            await previous?.value
            do {
                let engine = try factory(id, variant)
                try await engine.prepare()
                self?.loadFinished(id, number: number, variant: variant, result: .success(engine))
                return EngineBox(engine)
            } catch {
                self?.loadFinished(id, number: number, variant: variant, result: .failure(error))
                throw error
            }
        }
        loads[id] = Load(number: number, variant: variant, task: task)
        running[id] = Load(number: number, variant: variant, task: task)
        lastLoad = Task { [weak self] in
            _ = try? await task.value
            // A load task's result owns its EngineBox. Drop the last barrier once it
            // has served its purpose so unloading cannot leave a model pinned here.
            if self?.loadCount == number {
                self?.lastLoad = nil
            }
        }
        return task
    }

    private func loadFinished(_ id: EngineID, number: Int, variant: LanguageMode,
                              result: Result<any TranscriptionEngine, Error>) {
        if running[id]?.number == number {
            running[id] = nil
        }
        // Unloaded or replaced while loading: drop the result.
        guard loads[id]?.number == number else { return }
        loads[id] = nil
        switch result {
        case let .success(engine):
            loaded[id] = Loaded(engine: engine, variant: variant)
            lastUsed[id] = uptime.uptime()
            loadStates[id] = .ready
            log.info("Loaded \(id.rawValue, privacy: .public)")
            scheduleIdleUnload()
        case let .failure(error):
            if error is CancellationError {
                loadStates[id] = .unloaded
                return
            }
            loadStates[id] = .failed(error.localizedDescription)
            log.error("Loading \(id.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: - Settings and idle unload

    func settingsDidChange() {
        scheduleIdleUnload()
        releaseUnwantedHelper()
        // Before the first preload the app is still launching and preloads on its own.
        guard let preloadedID else { return }
        let wanted = engineID(for: .dictation(settings.languageMode))
        let variant = self.variant(of: wanted)
        let current = loaded[wanted]?.variant ?? loads[wanted]?.variant
        if wanted != preloadedID || (current != nil && current != variant) {
            preloadForCurrentSettings()
        }
    }

    /// An unused Whisper is released promptly; a selected primary Whisper stays loaded.
    private func releaseUnwantedHelper() {
        let whisper = EngineID.whisperTurbo
        guard loaded[whisper] != nil || loads[whisper] != nil else { return }
        let mode = settings.languageMode
        if engineID(for: .dictation(mode)) == whisper { return }
        let helper = settings.englishHelper(for: engineID(for: .dictation(mode)), languageMode: mode,
                                            status: { self.models.status(of: $0) })
        if helper != whisper {
            unload(whisper)
        }
    }

    /// Releases engines unused for longer than the idle setting; returns their ids.
    @discardableResult
    func unloadIdleEngines() -> [EngineID] {
        guard let timeout = settings.idleUnload.seconds else { return [] }
        let now = uptime.uptime()
        let expired = loaded.keys.filter { now - (lastUsed[$0] ?? now) >= timeout }.sorted { $0.rawValue < $1.rawValue }
        for id in expired {
            unload(id)
        }
        return expired
    }

    private func scheduleIdleUnload() {
        idleTask?.cancel()
        idleTask = nil
        guard let timeout = settings.idleUnload.seconds, !loaded.isEmpty else { return }
        let now = uptime.uptime()
        let deadline = loaded.keys.map { (lastUsed[$0] ?? now) + timeout }.min() ?? now + timeout
        let delay = max(1, deadline - now)
        idleTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self else { return }
            self.unloadIdleEngines()
            self.scheduleIdleUnload()
        }
    }
}

/// Carries an engine out of its loading task. Engines serialize their own work.
final class EngineBox: @unchecked Sendable {
    let engine: any TranscriptionEngine

    init(_ engine: any TranscriptionEngine) {
        self.engine = engine
    }
}
