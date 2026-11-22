import Foundation

// Services depend on each other only through these protocols, so each one can be
// replaced by a fake in tests and implemented without editing the others.

/// Microphone capture for dictation (`MicrophoneRecorder`).
@MainActor
public protocol AudioRecording: AnyObject {
    var isRecording: Bool { get }
    /// Smoothed RMS level from 0 to 1, about 30 times per second, on the main actor.
    var onLevel: ((Float) -> Void)? { get set }
    /// Starts capturing from the device with this UID, or the system default when nil
    /// or when the device is gone.
    func start(deviceUID: String?) throws
    /// Stops and returns everything captured as 16 kHz mono samples.
    func stop() -> RecordedAudio
    /// Stops and throws the audio away.
    func cancel()
    /// Name of the microphone the last recording used, for "Nothing heard".
    var lastDeviceName: String? { get }
}

public extension AudioRecording {
    var lastDeviceName: String? { nil }
}

/// The global dictation key listener (`HotkeyMonitor`).
@MainActor
public protocol HotkeyMonitoring: AnyObject {
    /// True while the event tap exists; false without Input Monitoring.
    var isRunning: Bool { get }
    /// Whether the dictation key is held right now, for "press to test" indicators.
    var isKeyDown: Bool { get }
    var onTrigger: ((DictationTrigger) -> Void)? { get set }
    var onKeyStateChange: ((Bool) -> Void)? { get set }
    /// Creates the event tap; returns false when Input Monitoring is missing.
    @discardableResult
    func start(configuration: HotkeyConfiguration) -> Bool
    func update(configuration: HotkeyConfiguration)
    /// Tells the state machine what the controller is actually doing, for example a
    /// hands-free recording started from the Flow bar, or nil once it stopped.
    func syncRecording(mode: DictationMode?)
    func stop()
}

/// A Carbon global shortcut (`PasteLastShortcut`).
@MainActor
public protocol GlobalShortcutRegistering: AnyObject {
    var shortcut: HotkeyShortcut? { get }
    /// Replaces any previous registration; returns false when the system refused it.
    @discardableResult
    func register(_ shortcut: HotkeyShortcut, action: @escaping @MainActor () -> Void) -> Bool
    func unregister()
}

/// Puts text into the focused field (`TextInserter`).
@MainActor
public protocol TextInserting: AnyObject {
    func captureTarget() -> TextInsertionTarget
    /// Pass nil for an explicit paste into the field focused now.
    func insert(_ text: String, into target: TextInsertionTarget?) async -> InsertionResult
    /// Copies without pasting, with the transient pasteboard markers.
    func copyToPasteboard(_ text: String)
}

public extension TextInserting {
    func insert(_ text: String) async -> InsertionResult {
        await insert(text, into: nil)
    }
}

/// The app that receives dictated text (`FrontmostApp`).
@MainActor
public protocol FrontmostAppProviding: AnyObject {
    /// The frontmost app other than Slovo itself.
    func currentApp() -> AppIdentity?
}

/// Permission status and requests (`Permissions`).
@MainActor
public protocol PermissionsProviding: AnyObject {
    func status(of kind: PermissionKind) -> PermissionStatus
    func request(_ kind: PermissionKind) async -> PermissionStatus
    /// Opens the matching pane of System Settings → Privacy & Security.
    func openSystemSettings(for kind: PermissionKind)
    /// True when `IsSecureEventInputEnabled()`: pasting would not reach the field.
    var isSecureInputActive: Bool { get }
}

/// Loaded engines (`EngineManager`).
@MainActor
public protocol EngineProviding: AnyObject {
    func engineID(for purpose: EnginePurpose) -> EngineID
    func loadState(of id: EngineID) -> EngineLoadState
    /// Returns the loaded engine, loading it first when needed.
    func engine(for id: EngineID) async throws -> any TranscriptionEngine
    /// Loads the engines current settings use, in the background.
    func preloadForCurrentSettings()
    /// Restarts the idle-unload countdown of an engine.
    func markUsed(_ id: EngineID)
    /// Releases a loaded engine, for example before its model files are deleted.
    func unload(_ id: EngineID)
    /// Returns once no load of the engine is still reading its model files.
    func waitForLoad(_ id: EngineID) async
}

public extension EngineProviding {
    /// Providers that never hold engines in memory have nothing to release.
    func unload(_ id: EngineID) {}

    /// Providers that never load engines have nothing to wait for.
    func waitForLoad(_ id: EngineID) async {}
}

/// English words in mixed dictation (`MixedLanguageHelper`).
@MainActor
public protocol MixedLanguageHelping: AnyObject {
    /// The text `engine` wrote with a helper's English merged in, or the text unchanged when
    /// no helper applies, none is needed, it fails or it takes too long. Never throws: a
    /// helper can only improve a dictation, not fail it. Returns promptly when the calling
    /// task is cancelled.
    func improve(_ text: String, audio: URL, duration: TimeInterval, engine: EngineID,
                 languageMode: LanguageMode, vocabulary: [String]) async -> String
    /// Starts loading the helper a dictation by `engine` that begins now would use; only loads.
    func warmUp(for engine: EngineID, languageMode: LanguageMode)
}

/// Model files on disk (`ModelManager`).
@MainActor
public protocol ModelManaging: AnyObject {
    func status(of id: EngineID) -> ModelStatus
    func refresh()
    /// Starts a download the user asked for; progress appears in `status(of:)`.
    func startDownload(_ id: EngineID)
    func cancelDownload(_ id: EngineID)
    func delete(_ id: EngineID) throws
}

/// The text pipeline (`TextPipeline`); pure and synchronous.
public protocol TextProcessing: Sendable {
    func process(_ raw: String, context: TextPipelineContext) -> TextPipelineResult
}

@MainActor
public protocol HistoryStoring: AnyObject {
    /// Newest first.
    var entries: [HistoryEntry] { get }
    func add(_ entry: HistoryEntry)
    func update(_ entry: HistoryEntry)
    /// Also removes the entry's audio file.
    func delete(id: UUID)
    func deleteAll()
    /// The audio file of an entry when it still exists.
    func audioURL(for entry: HistoryEntry) -> URL?
    /// Deletes an entry's audio file and clears its `audioFileName`.
    func removeAudio(for id: UUID)
    /// Writes pending changes now instead of after the debounce.
    func flush() throws
}

@MainActor
public protocol DictionaryStoring: AnyObject {
    var items: [DictionaryItem] { get }
    func add(_ item: DictionaryItem)
    func update(_ item: DictionaryItem)
    func delete(id: UUID)
    func flush() throws
}

@MainActor
public protocol SnippetStoring: AnyObject {
    var snippets: [Snippet] { get }
    func add(_ snippet: Snippet)
    func update(_ snippet: Snippet)
    func delete(id: UUID)
    func flush() throws
}

@MainActor
public protocol NoteStoring: AnyObject {
    /// Most recently updated first.
    var notes: [Note] { get }
    @discardableResult
    func create(text: String) -> Note
    func update(_ note: Note)
    /// Appends dictated text to a note, separated by a space or a new line.
    func append(_ text: String, toNote id: UUID)
    func delete(id: UUID)
    func flush() throws
}

@MainActor
public protocol AppCategoryStoring: AnyObject {
    var assignments: [AppCategoryAssignment] { get }
    /// The user's assignment, else the built-in default, else `.other`.
    func category(forBundleID bundleID: String?) -> AppCategory
    /// Like `category(forBundleID:)`, also matching apps by the name they report, for
    /// apps whose bundle id differs between builds.
    func category(for app: AppIdentity) -> AppCategory
    func setCategory(_ category: AppCategory, for app: AppIdentity)
    func flush() throws
}

public extension AppCategoryStoring {
    func category(for app: AppIdentity) -> AppCategory {
        category(forBundleID: app.bundleID)
    }
}

@MainActor
public protocol MeetingStoring: AnyObject {
    /// Newest first.
    var meetings: [MeetingRecord] { get }
    /// Creates the meeting folder and its `meeting.json`.
    func create(title: String) throws -> MeetingRecord
    func update(_ meeting: MeetingRecord)
    /// Removes the whole meeting folder.
    func delete(id: UUID)
    /// Removes `mic.caf`, `system.caf` and `screen.mov`, and sets `hasAudio` and
    /// `hasScreenRecording` to false.
    func deleteAudio(id: UUID)
    /// The folder every meeting folder sits in; its volume is what a recording needs
    /// room on.
    var meetingsFolder: URL { get }
    func folder(for id: UUID) -> URL
    func microphoneAudioURL(for id: UUID) -> URL
    func systemAudioURL(for id: UUID) -> URL
    func screenRecordingURL(for id: UUID) -> URL
    func transcriptURL(for id: UUID) -> URL
    func flush() throws
}

/// Lets dictation refuse to start while a meeting records (`MeetingRecorder`).
@MainActor
public protocol MeetingActivityProviding: AnyObject {
    var isMeetingRecording: Bool { get }
}

/// Start and stop sounds.
@MainActor
public protocol SoundPlaying: AnyObject {
    func play(_ cue: SoundCue)
}
