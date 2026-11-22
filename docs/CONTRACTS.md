# Slovo module contracts

These contracts describe the modules in `Sources/SlovoCore` and the macOS app
in `Sources/Slovo`.

## 1. Building and testing

Run `swift test` for the package tests and `python3 build.py` to assemble the
macOS app. On a fresh checkout, `build.py` repairs missing symlinks in the
ONNX Runtime framework extracted by SwiftPM. To repair the default `.build`
directory before running tests directly, use
`python3 Scripts/repair_onnxruntime_framework.py`.

## 2. Shared model (`Sources/SlovoCore/Model/`)

| File | Types |
|---|---|
| `Log.swift` | `SlovoLog` (subsystem `local.slovo.Slovo`), `UptimeProviding`, `SystemUptime` |
| `Language.swift` | `AppLanguage` (`system`/`en`/`ru`, `resolvedCode()`, `saved(in:)`), `LanguageMode` (`mixed`/`russian`/`english`) |
| `Engines.swift` | `EngineID` (`gigaamRNNT`, `gigaamCTC`, `parakeet`, `appleDictation`; choice lists, `displayName`, `modelFolder`), `EnginePurpose`, `ModelStatus`, `EngineLoadState`, `EngineIdleTimeout` |
| `Keys.swift` | `DictationKey` (`keyCodes`, `symbol`, `mayConflictWithSystem`), `HotkeyModifiers`, `HotkeyShortcut` (`.pasteLastDefault`, `.markMomentDefault`, `.markMomentChoices`), `HotkeyConfiguration` (0.25 s hold, 0.4 s double tap) |
| `Style.swift` | `AppCategory` (`defaultStyle`), `StylePreset`, `AppCategoryAssignment`, `AutoCleanupOptions`, `AppIdentity` |
| `Retention.swift` | `RetentionPeriod` (choice lists per setting, `maximumAge`) |
| `UserContent.swift` | `HistoryEntry` (`countWords(in:)`), `DictionaryItem` (`.term`, `.replacement(from:to:)`), `Snippet`, `Note` (`title`) |
| `Meeting.swift` | `Speaker`, `MeetingTrack`, `MeetingSpeaker`, `HighlightSource`, `MeetingHighlight`, `MeetingDiarization`, `TranscriptSegment`, `MeetingStatus`, `MeetingRecord` (tolerant decoding, `effectiveSpeakers`, `speaker(id:)`, `speakerID(for:)`, `talkTime(of:)`), `MeetingRecorderState`, `SystemAudioStatus`, `MeetingProcessingPhase`, `TranscriptionProgress` |
| `Diarization.swift` | `SpeakerTurn`, `SpeakerCountHint`, `SpeakerDiarizing`, `DiarizationProviding`, `DiarizationModelManaging` |
| `Dictation.swift` | `DictationMode`, `DictationTarget`, `DictationTrigger`, `RecordingInfo`, `DictationFailure`, `FlowMessage`, `DictationState`, `CopyReason`, `InsertionResult` |
| `SystemTypes.swift` | `PermissionKind`, `PermissionStatus`, `AudioInputDevice`, `RecordedAudio`, `SoundCue` |
| `Insights.swift` | `DailyWordCount`, `CategoryShare`, `AppUsage`, `Insights` |
| `AppDirectories.swift` | `AppDirectories` (`standard`, file and folder URLs, `recordingURL(relativePath:)`, `meetingFolder(id:)`, `createFolders()`) |
| `TextPipelineTypes.swift` | `TextPipelineContext`, `TextPipelineResult` (`fixesCount`) |
| `Settings.swift` | `SettingsKey` (every key), `AppSettings` (typed accessors, `registerDefaults()`, `hotkeyConfiguration`, `engine(for:)`, `style(for:)`, choice lists for people in a meeting and Claude's age limit) |
| `ServiceProtocols.swift` | `AudioRecording`, `HotkeyMonitoring`, `GlobalShortcutRegistering`, `TextInserting`, `FrontmostAppProviding`, `PermissionsProviding`, `EngineProviding`, `ModelManaging`, `TextProcessing`, `HistoryStoring`, `DictionaryStoring`, `SnippetStoring`, `NoteStoring`, `AppCategoryStoring`, `MeetingStoring`, `MeetingActivityProviding`, `SoundPlaying` |
| `SlovoError.swift` | `SlovoError` |

`Localization/CoreStrings.swift`: `CoreTable` (`engines`, `audio`, `input`, `text`,
`storage`, `dictation`, `meetings`, `diarization`, `integration`),
`CoreStrings.string/format/resourceBundle`, `ResourceBundleLocator.locate(_:)`.

Who consumes which protocol:

| Consumer (module) | Protocols it uses | Implemented by |
|---|---|---|
| `DictationController` (dictation) | `AudioRecording` | `MicrophoneRecorder` (dictation) |
| | `HotkeyMonitoring`, `TextInserting`, `FrontmostAppProviding`, `PermissionsProviding` | `HotkeyMonitor`, `TextInserter`, `FrontmostApp`, `Permissions` (input) |
| | `EngineProviding` | `EngineManager` (dictation) |
| | `TextProcessing` | `TextPipeline` (text) |
| | `HistoryStoring`, `DictionaryStoring`, `SnippetStoring`, `NoteStoring`, `AppCategoryStoring` | stores (input) |
| | `MeetingActivityProviding` | `MeetingRecorder` (meetings) |
| | `SoundPlaying` | `DictationSounds` (dictation) |
| `EngineManager` (dictation) | `ModelManaging` | `ModelManager` (dictation) |
| `TextInserter` (input) | `PermissionsProviding` | `Permissions` (input) |
| `RetentionCleaner` (input) | `HistoryStoring`, `MeetingStoring` | `HistoryStore` (input), `MeetingStore` (meetings) |
| `MeetingRecorder`, `MeetingTranscriber` (meetings) | `MeetingStoring`, `EngineProviding` | `MeetingStore` (meetings), `EngineManager` (dictation) |
| `AppModel` (ui) | every concrete type through its `init` | all modules |

Because `AppModel` constructs every service, **initializer signatures below are
frozen**. Placeholder bodies may be replaced freely.

## 3. Module `input`

Owned paths:

- `Sources/SlovoCore/Input/**`
- `Sources/SlovoCore/Storage/**` except `Storage/MeetingStore.swift`
- `Sources/SlovoCore/Resources/en.lproj/Input.strings`, `Sources/SlovoCore/Resources/ru.lproj/Input.strings`
- `Sources/SlovoCore/Resources/en.lproj/Storage.strings`, `Sources/SlovoCore/Resources/ru.lproj/Storage.strings`
- `Sources/SlovoCore/Resources/vocabulary.txt` (Dictionary seed, a copy of `Bench/vocabulary.txt`)
- `Tests/SlovoCoreTests/Input*Tests.swift`

Implement (SPEC 4.1, 4.3 step 3, 4.5 defaults, 5 Insights, 6 Data and privacy, 7 permissions, 9):

```swift
// Input/HotkeyStateMachine.swift: pure; the spec's "hotkey tap state machine" tests live here
public struct RawKeyEvent { kind: .flagsChanged/.keyDown/.keyUp; keyCode: UInt16; flags: UInt64; timestamp: TimeInterval
                            static let escapeKeyCode: UInt16 }
public enum HotkeyInput { case key(RawKeyEvent), deadline(TimeInterval), recordingChanged(DictationMode?, at: TimeInterval) }
public struct HotkeyStateMachine: Sendable {
    public var configuration: HotkeyConfiguration
    public private(set) var isKeyDown: Bool
    public private(set) var nextDeadline: TimeInterval?
    public init(configuration: HotkeyConfiguration)
    public mutating func handle(_ input: HotkeyInput) -> [DictationTrigger]
}
// Semantics: key down → .begin(.pushToTalk); release after minimumHold → .finish;
// release before minimumHold → wait doubleTapWindow (nextDeadline) → second press → .promoteToHandsFree,
// else .discard (immediately when hands-free is disabled); in hands-free the next tap → .finish;
// Esc while recording (escapeCancels) → .cancel; .recordingChanged keeps the machine in step with the
// controller (Flow bar start, time limit, errors). Fn uses keycode 63 plus the function flag;
// leftControlOption needs both 59 and 58 down. Any non-modifier key down while the key is held or a
// tap is pending makes the press a shortcut: .discard at once, however long the hold, and the release
// is ignored. Key down 179 (a stray Globe event from some Apple keyboards) is not a shortcut. Keys
// pressed during hands-free are ignored.

// Input/HotkeyMonitor.swift
@MainActor public final class HotkeyMonitor: ObservableObject, HotkeyMonitoring {
    @Published public private(set) var isRunning: Bool
    @Published public private(set) var isKeyDown: Bool
    public var onTrigger: ((DictationTrigger) -> Void)?
    public var onKeyStateChange: ((Bool) -> Void)?
    public init(uptime: UptimeProviding = SystemUptime())
    @discardableResult public func start(configuration: HotkeyConfiguration) -> Bool
    public func update(configuration: HotkeyConfiguration)
    public func syncRecording(mode: DictationMode?)
    public func stop()
}

// Input/PasteLastShortcut.swift: Carbon RegisterEventHotKey
@MainActor public final class PasteLastShortcut: GlobalShortcutRegistering {
    public private(set) var shortcut: HotkeyShortcut?
    public init()
    @discardableResult public func register(_ shortcut: HotkeyShortcut, action: @escaping @MainActor () -> Void) -> Bool
    public func unregister()
}

// Input/TextInserter.swift
@MainActor public final class TextInserter: TextInserting {
    public init(permissions: PermissionsProviding)
    public func insert(_ text: String) async -> InsertionResult
    public func copyToPasteboard(_ text: String)
}

// Input/Permissions.swift
@MainActor public final class Permissions: ObservableObject, PermissionsProviding {
    @Published public private(set) var statuses: [PermissionKind: PermissionStatus]
    public init()
    public func refresh()
    public func status(of kind: PermissionKind) -> PermissionStatus
    public func request(_ kind: PermissionKind) async -> PermissionStatus
    public func openSystemSettings(for kind: PermissionKind)
    public var isSecureInputActive: Bool { get }
    public static func settingsURL(for kind: PermissionKind) -> URL?
}

// Input/FrontmostApp.swift
@MainActor public final class FrontmostApp: FrontmostAppProviding {
    public init(ownBundleID: String? = Bundle.main.bundleIdentifier)
    public func startObserving()
    public func stopObserving()
    public func currentApp() -> AppIdentity?
}

// Storage/JSONFile.swift: shared by every store (MeetingStore may use it)
public enum JSONFileLoad<Value> { case loaded(Value), missing, corrupt(movedTo: URL?) }
public struct JSONFile<Value: Codable> { public let url: URL; public init(url: URL)
    public func load() -> JSONFileLoad<Value>          // corrupt file → renamed to <name>.corrupt-<timestamp>.json
    public func save(_ value: Value) throws }          // temporary file, then replace
@MainActor public final class SaveDebouncer { public let delay: Duration
    public init(delay: Duration = .milliseconds(500), action: @escaping @MainActor () -> Void)
    public func schedule(); public func flush() }

// Storage/HistoryStore.swift
public struct HistoryDayGroup: Hashable, Sendable { public var day: Date; public var entries: [HistoryEntry]; public init(day:entries:) }
@MainActor public final class HistoryStore: ObservableObject, HistoryStoring {
    @Published public private(set) var entries: [HistoryEntry]      // newest first
    public let directories: AppDirectories
    public init(directories: AppDirectories)                        // loads immediately
    add(_:), update(_:), delete(id:), deleteAll(), audioURL(for:), removeAudio(for:), flush() throws
    public func search(_ query: String) -> [HistoryEntry]           // case-insensitive, ё = е
    public static func groupedByDay(_ entries: [HistoryEntry], calendar: Calendar = .current) -> [HistoryDayGroup]
}

// Storage/DictionaryStore.swift
@MainActor public final class DictionaryStore: ObservableObject, DictionaryStoring {
    @Published public private(set) var items: [DictionaryItem]
    public let directories: AppDirectories
    public init(directories: AppDirectories)
    public var terms: [DictionaryItem] { get }; public var replacements: [DictionaryItem] { get }
    add(_:), update(_:), delete(id:), flush() throws
    public func seedIfNeeded(settings: AppSettings, terms: [String] = DictionarySeed.bundledTerms())
}
public enum DictionarySeed { public static func bundledTerms() -> [String] }  // CoreStrings.resourceBundle vocabulary.txt, skip # lines

// Storage/SnippetStore.swift, Storage/NoteStore.swift
@MainActor public final class SnippetStore: ObservableObject, SnippetStoring { @Published public private(set) var snippets: [Snippet]
    public let directories: AppDirectories; public init(directories:); add/update/delete(id:)/flush() }
@MainActor public final class NoteStore: ObservableObject, NoteStoring { @Published public private(set) var notes: [Note]
    public let directories: AppDirectories; public init(directories:)
    @discardableResult create(text:) -> Note; update(_:); append(_:toNote:); delete(id:); flush() }

// Storage/AppCategoryStore.swift
@MainActor public final class AppCategoryStore: ObservableObject, AppCategoryStoring {
    @Published public private(set) var assignments: [AppCategoryAssignment]
    public let directories: AppDirectories
    public init(directories: AppDirectories)
    public static let defaultCategories: [String: AppCategory]     // SPEC 4.5 bundle ids
    public func category(forBundleID bundleID: String?) -> AppCategory
    public func setCategory(_ category: AppCategory, for app: AppIdentity)
    public func flush() throws
    public func apps(in category: AppCategory, seenIn history: [HistoryEntry]) -> [AppIdentity]
}

// Storage/RetentionCleaner.swift
public struct RetentionReport: Equatable, Sendable { removedEntries, removedDictationAudio, removedMeetingAudio: Int }
@MainActor public final class RetentionCleaner {
    public static let interval: TimeInterval                        // 6 h
    public init(history: HistoryStoring, meetings: MeetingStoring, settings: AppSettings)
    @discardableResult public func run(now: Date = Date()) -> RetentionReport
    public func start(); public func stop()
    public static func expiredEntryIDs(_:keep:now:) -> [UUID]
    public static func expiredAudioIDs(_:keep:now:) -> [UUID]
    public static func expiredMeetingAudioIDs(_ meetings: [MeetingRecord], keep: RetentionPeriod, now: Date) -> [UUID]
}

// Storage/InsightsCalculator.swift: pure
public enum InsightsCalculator {
    public static func compute(entries: [HistoryEntry], now: Date, calendar: Calendar = .current,
                               weeks: Int = 20, topAppCount: Int = 5) -> Insights
}
```

Bitrix24, Яндекс Телемост and Spark have no bundle ids in the spec: look them up
from installed apps if present, otherwise match them by app name, and document the
choice in a comment.

## 4. Module `text`

Owned paths:

- `Sources/SlovoCore/Text/**`
- `Tests/SlovoCoreTests/Text*Tests.swift`
- `Sources/slovo-bench/**`, `Sources/BenchKit/**`, `Tests/BenchKitTests/**`

Implement (SPEC 4.3 step 2, 4.5 styles, 12 `slovo-bench score --postprocess`):

```swift
// Text/TextPipeline.swift: the loop in process(_:context:) is final; stages do the work
public protocol TextStage: Sendable { func apply(to result: inout TextPipelineResult, context: TextPipelineContext) }
public struct TextPipeline: TextProcessing {
    public let stages: [any TextStage]
    public init(stages: [any TextStage] = TextPipeline.standardStages)
    public static var standardStages: [any TextStage] { get }       // SnippetExpander, DictionaryCorrector, FillerCleaner, StyleFormatter
    public func process(_ raw: String, context: TextPipelineContext) -> TextPipelineResult
}
public struct SnippetExpander: TextStage { public init()
    public func expand(_ text: String, snippets: [Snippet]) -> (text: String, count: Int) }
public struct DictionaryCorrector: TextStage {
    public var maximumDistance: Double; public var minimumTermLetters: Int
    public init(maximumDistance: Double = 0.34, minimumTermLetters: Int = 4)
    public func applyReplacements(_ text: String, items: [DictionaryItem]) -> (text: String, count: Int)
    public func correctTerms(_ text: String, terms: [String]) -> (text: String, count: Int) }
public enum Transliterator { public static func latin(_ text: String) -> String }
public struct FillerCleaner: TextStage {
    public static let russianFillers: Set<String>; public static let englishFillers: Set<String>
    public init(); public func clean(_ text: String, options: AutoCleanupOptions) -> String }
public struct StyleFormatter: TextStage { public init()
    public func format(_ text: String, style: StylePreset, protectedTerms: [String]) -> String }
```

The spec's term-correction examples («кибернетес» → Kubernetes, «Гитхаб экшенс» →
GitHub Actions, «юзер Айди» → user id, «постгресс» → Postgres, «докер» → Docker,
«User Defolts» → UserDefaults; «деплой», «логи», «модель», «проект» unchanged) are
required tests. `slovo-bench score --postprocess` applies `DictionaryCorrector` with
`Bench/vocabulary.txt` to the stored engine results and prints the before/after
table; it reads `Bench/clips` and `Bench/results` only and must not change recordings.
The bench is a developer tool: its console text may stay Russian and use `print`.

## 5. Module `meetings`

Owned paths:

- `Sources/SlovoCore/Meetings/**`
- `Sources/SlovoCore/Storage/MeetingStore.swift`
- `Sources/SlovoCore/Resources/en.lproj/Meetings.strings`, `Sources/SlovoCore/Resources/ru.lproj/Meetings.strings`
- `Tests/SlovoCoreTests/Meetings*Tests.swift`

Implement (SPEC 5 Meetings screen data, 8):

```swift
// Storage/MeetingStore.swift: Meetings/<id>/meeting.json, mic.caf, system.caf, transcript.md
@MainActor public final class MeetingStore: ObservableObject, MeetingStoring {
    @Published public private(set) var meetings: [MeetingRecord]  // newest first
    public let directories: AppDirectories
    public init(directories: AppDirectories)
    create(title:) throws -> MeetingRecord, update(_:), delete(id:), deleteAudio(id:),
    folder(for:), microphoneAudioURL(for:), systemAudioURL(for:), transcriptURL(for:), flush() throws
    /// The file owns `claudeNotes`, because the MCP process writes them from outside the app:
    /// every save keeps whatever the file holds, `setClaudeNotes` changes them through the file
    /// first, and `refreshClaudeNotes` re-reads what that process wrote and brings `transcript.md`
    /// in step. The app calls the last one when it becomes active.
    setClaudeNotes(_ notes: MeetingNotes?, for id: UUID), refreshClaudeNotes()
}

// Meetings/SystemAudioTap.swift: not main-actor; callbacks from Core Audio's thread
public final class SystemAudioTap {
    public static var isSupported: Bool { get }                    // macOS 14.4+
    public var onLevel: ((Float) -> Void)?
    public var onSilence: (() -> Void)?                            // zeros for 8 s
    public init()
    public func start(writingTo url: URL) throws
    public func stop()
}

// Meetings/MeetingRecorder.swift
@MainActor public final class MeetingRecorder: ObservableObject, MeetingActivityProviding {
    @Published public private(set) var state: MeetingRecorderState
    @Published public private(set) var microphoneLevel: Float
    @Published public private(set) var systemLevel: Float
    @Published public private(set) var systemAudioStatus: SystemAudioStatus
    @Published public private(set) var microphoneStatus: MicrophoneStatus   // .lost degrades, never stops
    @Published public private(set) var elapsed: TimeInterval                // sleep-aware; what the screen shows
    public init(store: MeetingStoring, settings: AppSettings, uptime: UptimeProviding = SystemUptime())
    public var isMeetingRecording: Bool { get }
    public func start() async throws -> MeetingRecord
    public func stop() async -> MeetingRecord?
}

// Meetings/SpeechSegmenter.swift: pure; reuse GigaAMChunker (internal to SlovoCore, read-only)
public struct SpeechSegment: Equatable, Sendable { samples: Range<Int>; start: TimeInterval; end: TimeInterval }
public struct SpeechSegmenter: Sendable {
    public var maxSeconds: Double; public var silenceRMS: Float
    public init(maxSeconds: Double = 20, silenceRMS: Float = 0.005)
    public func segments(for samples: [Float], sampleRate: Int = 16_000) -> [SpeechSegment]
}

// Meetings/MeetingTranscriber.swift
@MainActor public final class MeetingTranscriber: ObservableObject {
    @Published public private(set) var progress: [UUID: TranscriptionProgress]
    public init(store: MeetingStoring, engines: EngineProviding, segmenter: SpeechSegmenter = SpeechSegmenter())
    public func isTranscribing(_ meetingID: UUID) -> Bool
    public func transcribe(meetingID: UUID) async throws -> MeetingRecord
    public func cancel(meetingID: UUID)
}

// Meetings/TranscriptMerger.swift: pure
public enum TranscriptMerger {
    public static func merge(microphone: [TranscriptSegment], system: [TranscriptSegment]) -> [TranscriptSegment]
    public static func timestamp(_ seconds: TimeInterval) -> String          // mm:ss, h:mm:ss from one hour
    public static func markdown(title: String, date: Date, segments: [TranscriptSegment], language: AppLanguage? = nil) -> String
}
```

Speaker names in transcripts come from `Meetings.strings` (`meetings.speaker.me`,
`meetings.speaker.others`). System Audio Recording is requested on the first
meeting, not in onboarding.

## 6. Module `dictation`

Owned paths:

- `Sources/SlovoCore/Dictation/**`
- `Sources/SlovoCore/Audio/**`
- The existing engine files: `Sources/SlovoCore/GigaAMEngine.swift`, `GigaAMRecognizer.swift`,
  `GigaAMChunker.swift`, `ParakeetEngine.swift`, `AppleDictationEngine.swift`,
  `AudioLoader.swift`, `ModelStore.swift`, `TranscriptionEngine.swift`. Their public API
  and behaviour must stay compatible with `slovo-bench`; additions are fine.
- `Sources/SlovoCore/Resources/en.lproj/Dictation.strings`, `ru.lproj/Dictation.strings`,
  `en.lproj/Audio.strings`, `ru.lproj/Audio.strings`, `en.lproj/Engines.strings`, `ru.lproj/Engines.strings`
- `Tests/SlovoCoreTests/Dictation*Tests.swift`

Implement (SPEC 4.2, 4.3 steps 1, 3 and 4, 4.4 state, 6 Recognition):

```swift
// Audio/MicrophoneRecorder.swift
@MainActor public final class MicrophoneRecorder: AudioRecording {
    public private(set) var isRecording: Bool
    public var onLevel: ((Float) -> Void)?
    public init(uptime: UptimeProviding = SystemUptime())
    public func start(deviceUID: String?) throws
    public func stop() -> RecordedAudio
    public func cancel()
}
public enum SpeechEnergy {
    public static let minimumDuration: TimeInterval                // 0.3 s
    public static func isEffectivelySilent(_ audio: RecordedAudio) -> Bool
    public static func rms(_ samples: ArraySlice<Float>) -> Float
}

// Audio/AudioDevices.swift
public enum AudioDevices { inputDevices() -> [AudioInputDevice]; defaultInputDevice() -> AudioInputDevice?; device(uid:) -> AudioInputDevice? }
@MainActor public final class AudioDeviceObserver: ObservableObject {
    @Published public private(set) var devices: [AudioInputDevice]
    @Published public private(set) var defaultDeviceUID: String?
    public init(); public func start(); public func stop(); public func refresh()
}

// Audio/WAVFile.swift
public enum WAVFile {
    public static func write(_ audio: RecordedAudio, to url: URL) throws
    public static func read(from url: URL) throws -> RecordedAudio
    public static func relativePath(for id: UUID, date: Date, calendar: Calendar = .current) -> String   // final
}

// Dictation/EngineManager.swift
public typealias EngineFactory = @MainActor (EngineID, LanguageMode) throws -> any TranscriptionEngine
@MainActor public final class EngineManager: ObservableObject, EngineProviding {
    @Published public private(set) var loadStates: [EngineID: EngineLoadState]
    public init(settings: AppSettings, models: ModelManaging, factory: @escaping EngineFactory = EngineManager.makeEngine,
                uptime: UptimeProviding = SystemUptime())
    engineID(for:), loadState(of:), engine(for:) async throws, preloadForCurrentSettings(), markUsed(_:)
    public func unload(_ id: EngineID); public func unloadAll()
    public static func makeEngine(_ id: EngineID, languageMode: LanguageMode) throws -> any TranscriptionEngine
}
// Never let an engine download implicitly: ParakeetEngine.prepare() downloads when files are
// missing, so check ModelManaging.status(of:) first.
// Accurate mixed dictation (0.4): SettingsKey.accurateMixedDictation / AppSettings.accurateMixedDictation
// defaults to false. When true, engine(for: .dictation(.mixed)) is .whisperTurbo; other purposes
// keep their selected engines. The UI explicitly starts the verified download on toggle enable.
// Whisper is a standalone transcription engine as well as an optional helper. VocabularyTranscribing
// receives Dictionary terms for primary recognition and history reruns. Whisper uses a Russian hint
// for Russian/mixed and an English hint for English; EngineManager replaces mismatched variants.
// prepare() warms kernels once with synthetic silence. Completed loads release their Task results;
// unloading releases the context after any active caller finishes. Idle unload policy still applies.

// Dictation/ModelManager.swift
@MainActor public final class ModelManager: ObservableObject, ModelManaging {
    @Published public private(set) var statuses: [EngineID: ModelStatus]
    @Published public private(set) var lastError: String?
    public let modelsRoot: URL
    public init(modelsRoot: URL = ModelStore.root)
    status(of:), refresh(), startDownload(_:), cancelDownload(_:), delete(_:) throws
    public static func downloadBytes(for id: EngineID) -> Int64
}

// Dictation/DictationController.swift
@MainActor public final class DictationController: ObservableObject {
    public struct Dependencies { recorder, hotkeys, inserter, frontmostApp, permissions, engines, pipeline,
        history, dictionary, snippets, notes, categories, meetings, sounds: SoundPlaying?, settings, directories, uptime
        public init(... uptime: UptimeProviding = SystemUptime()) }
    public static let messageDuration: TimeInterval               // 1.5 s
    public static let countdownThreshold: TimeInterval            // 30 s
    @Published public private(set) var state: DictationState
    @Published public private(set) var level: Float
    @Published public private(set) var handsFreeRemaining: TimeInterval?
    @Published public private(set) var lastTranscript: String?
    @Published public private(set) var practiceResult: String?
    public init(dependencies: Dependencies)
    public func start(); public func stop()
    public func handle(_ trigger: DictationTrigger)
    public func startHandsFree(target: DictationTarget = .frontmostApp)
    public func finish(); public func cancel()
    public func pasteLastTranscript() async
    public func rerun(entryID: UUID, with engine: EngineID) async throws
    public func dismissMessage()
}

// Dictation/DictationSounds.swift
@MainActor public final class DictationSounds: SoundPlaying { public init(settings: AppSettings); public func play(_ cue: SoundCue) }
```

`AppModel` (ui) registers `PasteLastShortcut` and calls
`DictationController.pasteLastTranscript()` from it; the controller does not
register shortcuts itself. The controller must call `hotkeys.syncRecording(mode:)`
whenever recording starts or stops for a reason other than a trigger (Flow bar
click, time limit, error).

## 7. Module `ui`

Owned paths:

- `Sources/Slovo/**`: `SlovoApp.swift`, `AppDelegate.swift`, `AppModel.swift`,
  `Design/` (Theme, Localizer, Components, Controls), `Screens/`, `Settings/`,
  `FlowBar/`, `MenuBar/`, `Onboarding/`, `Debug/ScreenRenderer.swift`, and
  `Resources/{en,ru}.lproj/{Localizable.strings, Localizable.stringsdict, InfoPlist.strings}`
  plus any assets added there
- Not `build.py` or `Package.swift`. There is no ui test target; check work with
  `--render-screens` and the localization tests.

Current app API that other stages rely on:

```swift
@MainActor final class Localizer: ObservableObject {      // runtime language switch, app.language
    @Published var language: AppLanguage
    private(set) var languageCode: String
    static let resourceBundle: Bundle
    init(defaults: UserDefaults); init(fixed language: AppLanguage)
    var locale: Locale
    func t(_ key: String) -> String; func t(_ key: String, _ arguments: CVarArg...) -> String
    func plural(_ key: String, _ count: Int) -> String
}
struct Theme { ...tokens...; static let light, dark; static func resolve(_:) -> Theme }  // EnvironmentValues.theme, ThemedRoot
enum Screen, enum SettingsSection
@MainActor final class AppModel: ObservableObject {
    init(localizer: Localizer, settings: AppSettings, directories: AppDirectories, modelsRoot: URL)
    static func live(localizer: Localizer) -> AppModel
    func start(); func openSettings(_ section: SettingsSection? = nil)
    // owns every service listed in sections 3–6
}
ScreenRenderer.renderAll(to:)   // DEBUG only; `--render-screens <dir>`
```

Implement (SPEC 4.4 Flow bar, 5 screens, 6 settings, 7 onboarding, menu bar item,
10 localization, 11 visual design): real screens bound to the stores and services,
the Flow bar driven by `DictationController.state`/`level`/`handsFreeRemaining`,
Settings rows wired to `AppSettings` keys and `ModelManager`, launch at login
(`SMAppService.mainApp`), Help menu, "Delete all history" confirmation, playback,
re-run with another engine, paste-last shortcut wiring (`PasteLastShortcut` +
`DictationController.pasteLastTranscript()`), dismissed banners
(`AppSettings.dismissedBanners`), Reduce Motion. Every colour stays a `Theme` token.

Rendering: `ImageRenderer` cannot draw AppKit-backed controls (scroll views, text
fields, switches, pop-up menus). Views check `@Environment(\.isSnapshot)` and use the
snapshot-friendly controls in `Design/Controls.swift` (`PanelScroll`, `SearchField`,
`ThemedToggle`, `PopupChoice`). Extend `ScreenRenderer` with fake data as screens
grow; it must keep using a throwaway `UserDefaults` suite and a temporary data folder.

While other modules are unfinished the services are placeholders (empty lists,
`.unknown` statuses, thrown "not available yet" errors); the ui must handle those
states instead of assuming data.

## 8. Integration

Changes made when the modules were joined into one app:

- The default dictation key is Fn (`SettingsKey.dictationKey`, `AppSettings.dictationKey`),
  as SPEC 4.1 says. `HotkeyConfiguration.init` defaults to `.fn` as well; the app always
  passes the configured key.
- `EngineProviding.unload(_:)` is a protocol requirement with a no-op default, so fakes
  without engines need no change; `EngineManager` implements it.
- `AppCategoryStoring.category(for: AppIdentity)` is a requirement with a default that
  uses the bundle id; `AppCategoryStore` also matches by app name (Spark, other Bitrix24
  builds). `DictationController` uses it for the style of the frontmost app.
- `Integration/ServiceWiring.swift` holds connections no module owns: the paste-last
  shortcut follows its setting and calls `DictationController.pasteLastTranscript()`,
  the dictation engine is preloaded once its model becomes usable (a download finishing,
  Apple Dictation's asset check), and `deleteModel(_:) async throws` unloads the engine,
  awaits `EngineProviding.waitForLoad(_:)` (a requirement with a no-op default) so a load
  in flight finishes before any file is removed, then deletes the files. While it waits,
  `EngineManager.engine(for:)` refuses that engine and preloads skip it; the Recognition
  rows also disable Download and Delete while the engine loads. Tests:
  `Tests/SlovoCoreTests/IntegrationWiringTests.swift`, which also run `DictationController`
  over the real stores, pipeline and retention.
- Settings changes reach services through their own observers: `DictationController`
  (hotkey configuration), `EngineManager` (engine choice, idle unload), `ServiceWiring`
  (paste-last shortcut). `AppModel` does not duplicate them.
- `AppModel` builds `MeetingTranscriber(store:engines:settings:diarization:)` (section 10.8) and
  `EngineManager(... factory: EngineManager.factory(modelsRoot:))`, calls
  `DictationController.refreshHotkeys()` when the app becomes active, sets
  `DictationController.keyTarget` to `.practice` while onboarding's practice step is
  visible, and on quit calls `MeetingRecorder.stopImmediately()` before flushing stores.
- The app's Fn warning state (`FnKeyStatus`) reads through SlovoCore's `FnKeyConflicts`.
- Unused placeholder keys `dictation.unavailable`, `meetings.unavailable`,
  `audio.unavailable` and `text.unavailable` were removed from the SlovoCore tables.

## 9. Speakers, important moments and Claude access

`docs/SPEC-speakers-mcp.md` describes speaker separation, important moments,
speaker renaming and the local MCP server for Claude. The optional
real-model tests use `SLOVO_DIARIZATION_MODELS` to locate the model.

### 9.1 Shared model

```swift
// Model/Meeting.swift
public enum Speaker: String, Codable, Sendable, CaseIterable { case me, others }   // the stored track of a segment
public enum MeetingTrack: String, Codable, Sendable, CaseIterable {
    case microphone, system
    public init(_ speaker: Speaker)                     // .me → .microphone, .others → .system
    public var speaker: Speaker { get }
}
public struct MeetingSpeaker: Codable, Hashable, Sendable, Identifiable {
    public static let paletteSize: Int                  // 8
    public var id: String                               // "s1", "s2"…; "me"/"others" for an unseparated track
    public var track: MeetingTrack
    public var number: Int?                             // N in «Спикер N»; nil for "me"/"others"; never changes
    public var displayName: String?                     // nil shows the default label
    public var colorIndex: Int                          // 0..<8; init and decoding wrap other values
    public var isMe: Bool                               // at most one per meeting
    public init(id: String, track: MeetingTrack, number: Int? = nil, displayName: String? = nil,
                colorIndex: Int, isMe: Bool = false)
    public static func fallback(for track: MeetingTrack) -> MeetingSpeaker   // "me" color 0 / "others" color 1
}
public enum HighlightSource: String, Codable, Sendable { case recording, transcript }
public struct MeetingHighlight: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var time: TimeInterval                       // seconds on the tracks' shared 16 kHz timeline
    public var note: String?                            // at most 500 characters, one line
    public var source: HighlightSource
    public var createdAt: Date
    public init(id: UUID = UUID(), time: TimeInterval, note: String? = nil, source: HighlightSource,
                createdAt: Date = Date())
}
public struct MeetingDiarization: Codable, Hashable, Sendable {
    public enum State: String, Codable, Sendable { case applied, off, modelMissing, unsupportedSystem, failed }
    public var state: State
    public var requestedSpeakers: Int?                  // nil = automatic
    public var speakersPerTrack: [String: Int]          // keyed by MeetingTrack raw value
    public var model: String?                           // DiarizationService.modelIdentifier
    public var configuration: String?                   // DiarizationTuning.description
    public var message: String?                         // localized; set for .failed
    public var processedAt: Date?
    public init(state: State, requestedSpeakers: Int? = nil, speakersPerTrack: [String: Int] = [:],
                model: String? = nil, configuration: String? = nil, message: String? = nil, processedAt: Date? = nil)
}
public struct TranscriptSegment: Codable, Hashable, Sendable {
    public var start: TimeInterval; public var end: TimeInterval; public var speaker: Speaker; public var text: String
    public var speakerID: String?                       // nil = the fallback speaker of the segment's track
    public init(start: TimeInterval, end: TimeInterval, speaker: Speaker, text: String, speakerID: String? = nil)
}
/// What Claude wrote about a meeting after reading it, never a record of what was said. Both the
/// init and decoding put every value through `MCPSanitizer`: one line, no control characters,
/// nothing that could close the untrusted wrapper or come back as a transcript line.
public struct MeetingNotes: Codable, Hashable, Sendable {
    public static let maximumSummaryCharacters: Int     // 2,000
    public static let maximumItemCharacters: Int        // 500
    public static let maximumItems: Int                 // 50
    public static let maximumAuthorCharacters: Int      // 60
    public static let defaultAuthor: String             // "Claude"
    public var summary: String                          // empty when only lists were written
    public var decisions: [String]; public var tasks: [String]
    public var author: String; public var writtenAt: Date
    public init(summary: String = "", decisions: [String] = [], tasks: [String] = [],
                author: String = MeetingNotes.defaultAuthor, writtenAt: Date = Date())
    public var isEmpty: Bool { get }                    // nothing written; such notes are neither kept nor shown
}
public struct MeetingRecord: Codable, Hashable, Sendable, Identifiable {
    public static let currentFormatVersion: Int         // 2
    // id, title, createdAt, duration, status, hasSystemAudio, hasAudio, engineID, segments, errorMessage as before
    public var speakers: [MeetingSpeaker]
    public var highlights: [MeetingHighlight]
    public var diarization: MeetingDiarization?
    public var formatVersion: Int                       // a file without the key is 1
    public var highestSpeakerNumber: Int?               // never lowered by merges; nil until recorded
    public var claudeNotes: MeetingNotes?               // nil until Claude writes; never part of the transcript
    public init(id: UUID = UUID(), title: String, createdAt: Date = Date(), duration: TimeInterval = 0,
                status: MeetingStatus = .recording, hasSystemAudio: Bool = false, hasAudio: Bool = true,
                engineID: EngineID? = nil, segments: [TranscriptSegment] = [], errorMessage: String? = nil,
                speakers: [MeetingSpeaker] = [], highlights: [MeetingHighlight] = [],
                diarization: MeetingDiarization? = nil, formatVersion: Int = MeetingRecord.currentFormatVersion,
                highestSpeakerNumber: Int? = nil, claudeNotes: MeetingNotes? = nil)
    /// Stored speakers plus the fallback of each track whose segments name no stored speaker of that
    /// track; microphone speakers first, stored order kept within a track.
    public var effectiveSpeakers: [MeetingSpeaker] { get }
    /// A stored speaker, or the fallback for "me"/"others".
    public func speaker(id: String) -> MeetingSpeaker?
    /// segment.speakerID when it names a stored speaker of the segment's track, else "me"/"others".
    public func speakerID(for segment: TranscriptSegment) -> String
    public func talkTime(of speakerID: String) -> TimeInterval
}
public enum MeetingProcessingPhase: Equatable, Sendable {
    case preparing, loadingSpeakerModel, separatingSpeakers(MeetingTrack, fraction: Double), transcribing
}
public struct TranscriptionProgress: Equatable, Sendable {
    public var completedSegments: Int; public var totalSegments: Int
    public var phase: MeetingProcessingPhase
    public var overallFraction: Double                  // 0...1 over the whole run
    public init(completedSegments: Int, totalSegments: Int, phase: MeetingProcessingPhase = .transcribing,
                overallFraction: Double? = nil)          // nil = fraction
    public var fraction: Double { get }
}

// Model/Diarization.swift
public struct SpeakerTurn: Equatable, Sendable {
    public var start: TimeInterval; public var end: TimeInterval
    public var cluster: Int                             // 0-based, local to one run of one track
    public init(start: TimeInterval, end: TimeInterval, cluster: Int)
}
public struct SpeakerCountHint: Equatable, Sendable {
    public var exactly: Int?; public var maximum: Int?
    public static let automatic: SpeakerCountHint
    public init(exactly: Int? = nil, maximum: Int? = nil)
}
public protocol SpeakerDiarizing: AnyObject, Sendable {
    func diarize(fileAt url: URL, hint: SpeakerCountHint,
                 progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn]
}
@MainActor public protocol DiarizationProviding: AnyObject {
    var modelStatus: ModelStatus { get }
    var isSupportedOnThisSystem: Bool { get }           // false below macOS 15
    func diarizer() async throws -> any SpeakerDiarizing   // never downloads
    func release()
    func waitForLoad() async
}
@MainActor public protocol DiarizationModelManaging: AnyObject {
    var status: ModelStatus { get }
    var lastError: String? { get }
    func refresh(); func startDownload(); func cancelDownload(); func delete() throws
}

// Model/Keys.swift
extension HotkeyShortcut {
    public static let markMomentDefault: HotkeyShortcut      // ⌃⌘M, keyCode 46
    public static let markMomentChoices: [HotkeyShortcut]    // ⌃⌘M, ⌃⌥M, ⌃⇧M
}

// Model/Settings.swift
SettingsKey.speakerSeparation   // "meetings.speakerSeparation", Bool, true
SettingsKey.expectedSpeakers    // "meetings.expectedSpeakers", Int, 0 = automatic, otherwise 2–8
SettingsKey.highlightShortcut   // "meetings.highlightShortcut", HotkeyShortcut as JSON data, ⌃⌘M
SettingsKey.mcpEnabled          // "integrations.mcp.enabled", Bool, false
SettingsKey.mcpMaxAgeDays       // "integrations.mcp.maxAgeDays", Int, 0 = all, otherwise 90, 30 or 7
SettingsKey.mcpWriteEnabled     // "integrations.mcp.writeEnabled", Bool, false; separate from mcpEnabled
extension AppSettings {
    public var speakerSeparation: Bool; public var expectedSpeakers: Int; public var highlightShortcut: HotkeyShortcut
    public var mcpEnabled: Bool; public var mcpMaxAgeDays: Int; public var mcpWriteEnabled: Bool
    public static let expectedSpeakerChoices: [Int]          // [0, 2, 3, 4, 5, 6, 7, 8]
    public static let mcpMaxAgeDayChoices: [Int]             // [0, 90, 30, 7]
    public static func normalizedExpectedSpeakers(_ value: Int) -> Int   // outside the choices → 0
    public static func normalizedMCPMaxAgeDays(_ value: Int) -> Int      // outside the choices → 0
}
```

All six keys are in `registrationDefaults`, the shortcut as encoded data.

`meeting.json` decoding (hand-written `init(from:)`, synthesized encoding with the
store's sorted, pretty-printed encoder; dates stay seconds since 2001):

- The first format's keys stay required: `id`, `title`, `createdAt`, `duration`,
  `status`, `hasSystemAudio`, `hasAudio`, `segments`, and in each segment `start`,
  `end`, `speaker`, `text`. `errorMessage` stays optional.
- `speakers`, `highlights`, `diarization`, `formatVersion` and `speakerID` are read
  with `decodeIfPresent`; a value of the wrong type counts as missing (`[]`, `nil`, 1).
- Unknown `engineID` → nil. Unknown `status` → `.recorded` with audio, otherwise
  `.transcribed` with segments, otherwise `.failed`.
- A speaker with an unknown track, without an id, or with an id other than `me`,
  `others` or `s1`…`s999999` (no leading zeros) is dropped; a `number` or
  `highestSpeakerNumber` outside 1…999999 → nil; a missing
  `colorIndex` becomes `(number − 1)` or the track fallback's color; colors wrap
  modulo 8; later speakers with a repeated id are dropped; only the first `isMe`
  stays true.
- A highlight without an id or a finite time is dropped; an unknown or missing
  source → `.transcript`; a missing `createdAt` → the 2001 reference date; later
  highlights with a repeated id are dropped.
- `duration`, segment `start`/`end` and highlight `time` are clamped to
  0…359,999,999 s, so every stored time formats as a timestamp.
- Unknown `MeetingDiarization.State` → `.off`; every diarization field is optional.
- A segment whose `speakerID` names no stored speaker of its track is shown as the
  track's fallback (`speakerID(for:)`).

### 9.2 Diarization

```swift
// Diarization/DiarizationService.swift
@MainActor public final class DiarizationService: ObservableObject, DiarizationProviding, DiarizationModelManaging {
    public static let downloadBytes: Int64 = 21_776_918
    public static let modelIdentifier = "pyannote-community-1-coreml@1ed7a66"
    @Published public private(set) var status: ModelStatus
    @Published public private(set) var lastError: String?
    @Published public private(set) var loadState: EngineLoadState
    public let modelsRoot: URL
    public init(modelsRoot: URL = ModelStore.root,
                downloader: DiarizationModelDownloading = LiveDiarizationModelDownloader(),
                factory: @escaping @MainActor (URL) async throws -> any SpeakerDiarizing = FluidSpeakerDiarizer.load)
    public var modelFolder: URL { get }                // modelsRoot/diarization; the factory receives it
    // DiarizationProviding and DiarizationModelManaging requirements
}
public protocol DiarizationModelDownloading: Sendable {
    func download(to folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws
}
public struct LiveDiarizationModelDownloader: DiarizationModelDownloading { public init() }

// Diarization/FluidSpeakerDiarizer.swift
public final class FluidSpeakerDiarizer: SpeakerDiarizing, @unchecked Sendable {
    public static func load(modelFolder: URL) async throws -> FluidSpeakerDiarizer
    public func diarize(fileAt url: URL, hint: SpeakerCountHint,
                        progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerTurn]   // standard tuning
    public func diagnose(fileAt url: URL, config: DiarizationTuning,
                         hint: SpeakerCountHint = .automatic) async throws -> DiarizationDiagnosis
}
public struct DiarizationDiagnosis: Sendable {
    public var turns: [SpeakerTurn]; public var centroids: [Int: [Float]]; public var timings: [String: TimeInterval]
    public init(turns:centroids:timings:)
}

// Diarization/DiarizationTuning.swift
public struct DiarizationTuning: Equatable, Sendable, CustomStringConvertible {
    public var segmentationStepRatio: Double; public var minimumSegmentSeconds: Double
    public var zeroVoteReembed: Bool; public var zeroVoteMinimumSeconds: Double
    public var clusteringThreshold: Double; public var excludeOverlap: Bool; public var exclusiveSegments: Bool
    public static let minimumTrackSpeechSeconds: TimeInterval   // 5
    public static let standard: DiarizationTuning               // 0.1, 0, on, 0.4, 0.775, true, true
    public init(segmentationStepRatio:minimumSegmentSeconds:zeroVoteReembed:zeroVoteMinimumSeconds:
                clusteringThreshold:excludeOverlap:exclusiveSegments:)
    public init?(configuration: String)                // "step=0.1,minSeg=0,zeroVote=on,threshold=0.775"
    public var description: String                     // "step=0.1 minSeg=0 threshold=0.775 zeroVote=on"
}

// Diarization/SpeakerTurns.swift
public enum SpeakerTurns {
    public static func normalize(_ turns: [SpeakerTurn], trackDuration: TimeInterval, hint: SpeakerCountHint) -> [SpeakerTurn]
}

// Diarization/DiarizationScoring.swift
public enum DiarizationScoring {
    public struct Segment: Equatable, Sendable { speaker: String; start, end: TimeInterval; init(speaker:start:end:) }
    public struct Score: Equatable, Sendable { der, miss, falseAlarm, confusion, referenceSpeechSeconds: Double
                                               mapping: [String: String]; init(...) }
    public static func der(reference: [Segment], hypothesis: [Segment], collar: TimeInterval) -> Score
}
```

- `description` is implemented and is what `MeetingDiarization.configuration` stores.
  The FluidAudio configuration builder, the file checks and `FluidAudioJobQueue`
  are internal to the module.
- No file under `Sources/` may call FluidAudio's model preparation helper that
  purges and re-downloads (SPEC-speakers-mcp D3); a guard test enforces it.

### 9.3 Meetings

```swift
// Meetings/MeetingTranscriber.swift
public struct MeetingProcessingOptions: Equatable, Sendable {
    public enum Mode: Equatable, Sendable { case full, speakersOnly }
    public var mode: Mode; public var separateSpeakers: Bool
    public var expectedSpeakers: Int?                  // nil or 0 = automatic; otherwise 2–8
    public var engineID: EngineID?                     // nil = the meetings engine from settings
    public init(mode: Mode = .full, separateSpeakers: Bool = true, expectedSpeakers: Int? = nil, engineID: EngineID? = nil)
    public static func fromSettings(_ settings: AppSettings) -> MeetingProcessingOptions   // implemented
}
@MainActor public final class MeetingTranscriber: ObservableObject {
    // init(store:engines:segmenter:) and init(store:engines:settings:segmenter:) stay and mean no diarization
    public init(store: MeetingStoring, engines: EngineProviding, settings: AppSettings,
                diarization: DiarizationProviding?, segmenter: SpeechSegmenter = SpeechSegmenter())
    public func transcribe(meetingID: UUID) async throws -> MeetingRecord   // options from settings
    public func transcribe(meetingID: UUID, options: MeetingProcessingOptions) async throws -> MeetingRecord
    public func canProcess(_ meeting: MeetingRecord, mode: MeetingProcessingOptions.Mode) -> Bool
}

// Meetings/SpeechSegmenter.swift
func segments(for envelope: EnergyEnvelope, within frames: Range<Int>) -> [SpeechSegment]   // internal

// Meetings/ClipPlanner.swift
public struct PlannedClip: Equatable, Sendable { public var range: Range<TimeInterval>; public var cluster: Int?
                                                 public init(range:cluster:) }
public enum ClipPlanner {
    public static func plan(turns: [SpeakerTurn], speech: [Range<TimeInterval>], trackDuration: TimeInterval,
                            maxSeconds: Double = 20) -> [PlannedClip]
    public static func assign(segments: [TranscriptSegment], turns: [SpeakerTurn]) -> [Int?]   // nil without turns
}

// Meetings/SpeakerRegistry.swift
public enum SpeakerRegistry {
    public static func build(microphoneClusters: [Int: [Range<TimeInterval>]]?, systemClusters: [Int: [Range<TimeInterval>]]?,
                             hasSystemTrack: Bool, previous: MeetingRecord?)
        -> (speakers: [MeetingSpeaker], idForCluster: [MeetingTrack: [Int: String]])
}

// Meetings/TranscriptMerger.swift
public struct TranscriptLine: Equatable, Sendable, Identifiable {
    public var id: Int                                 // index in the returned lines array
    public var start: TimeInterval; public var end: TimeInterval
    public var speakerID: String; public var track: MeetingTrack; public var text: String
    public var segmentIndices: [Int]                   // into MeetingRecord.segments
    public var highlightIDs: [UUID]
    public init(id:start:end:speakerID:track:text:segmentIndices:highlightIDs: = [])
}
extension TranscriptMerger {
    public static func lines(for meeting: MeetingRecord, speakerFilter: Set<String>? = nil) -> [TranscriptLine]
    public static func speakerLabel(_ speaker: MeetingSpeaker, language: AppLanguage? = nil) -> String   // implemented
    // In a meeting: the microphone fallback is «Я» while nobody has isMe; once someone does,
    // an unnamed, unnumbered microphone speaker reads «Микрофон».
    public static func isMe(_ speaker: MeetingSpeaker, in meeting: MeetingRecord) -> Bool
    public static func speakerLabel(_ speaker: MeetingSpeaker, in meeting: MeetingRecord, language: AppLanguage? = nil) -> String
    public static func markdown(meeting: MeetingRecord, language: AppLanguage? = nil, speakerFilter: Set<String>? = nil) -> String
    public static func plainText(meeting: MeetingRecord, language: AppLanguage? = nil, speakerFilter: Set<String>? = nil) -> String
    public static func highlightLineIndex(_ highlight: MeetingHighlight, in lines: [TranscriptLine]) -> Int?
}

// Meetings/SpeechStatistics.swift
public struct SpeakerSpeechStats: Hashable, Sendable, Identifiable {
    public var speakerID: String                       // id
    public var speakingSeconds: TimeInterval           // overlapping segments of one speaker count once
    public var share: Double                           // of all the speaking in the meeting, 0...1; the shares add to 1
    public var turns: Int                              // runs of one voice, ended by another or by its own long pause
    public var averageTurnSeconds: TimeInterval        // speakingSeconds / turns
    public var words: Int
    public var wordsPerMinute: Double?                 // nil below minimumPaceSeconds of speech
    public init(speakerID: String, speakingSeconds: = 0, share: = 0, turns: = 0,
                averageTurnSeconds: = 0, words: = 0, wordsPerMinute: Double? = nil)
}
public struct SpeechStats: Hashable, Sendable {
    public var speakers: [SpeakerSpeechStats]          // only those who spoke, longest speaking time first
    public var totalSeconds: TimeInterval; public var speechSeconds: TimeInterval; public var speechShare: Double
    public var pauseCount: Int; public var longestPauseSeconds: TimeInterval; public var pauseSeconds: TimeInterval
    public var silenceSeconds: TimeInterval { get }
    public var isEmpty: Bool { get }                   // nothing was said at all
    public init(speakers: = [], totalSeconds: = 0, speechSeconds: = 0, speechShare: = 0,
                pauseCount: = 0, longestPauseSeconds: = 0, pauseSeconds: = 0)
}
/// Derived from the segments every time it is shown and never stored, so a meeting recorded long
/// before these numbers existed has them the moment it is opened. Pure.
public enum SpeechStatsCalculator {
    public static let defaultPauseSeconds: TimeInterval   // 5
    public static let minimumPaceSeconds: TimeInterval    // 10, below which no pace is reported
    public static func compute(for meeting: MeetingRecord,
                               pauseSeconds: TimeInterval = defaultPauseSeconds) -> SpeechStats
}

// Meetings/MeetingEdits.swift
public enum MeetingEdits {
    public static func renameSpeaker(_ id: String, to name: String?, in meeting: MeetingRecord) -> MeetingRecord
    public static func markAsMe(_ id: String, in meeting: MeetingRecord) -> MeetingRecord
    public static func mergeSpeaker(_ source: String, into target: String, in meeting: MeetingRecord) throws -> MeetingRecord
    public static func reassignLine(_ line: TranscriptLine, to speakerID: String, in meeting: MeetingRecord) throws -> MeetingRecord
    public static func toggleHighlight(for line: TranscriptLine, in meeting: MeetingRecord, now: Date) -> MeetingRecord
    public static func setHighlightNote(_ id: UUID, note: String?, in meeting: MeetingRecord) -> MeetingRecord
    public static func deleteHighlight(_ id: UUID, in meeting: MeetingRecord) -> MeetingRecord
    public static func sanitizedName(_ name: String) -> String?
}

// Meetings/MeetingRecorder.swift
@Published public private(set) var lastMarkedMoment: MeetingHighlight?
@discardableResult public func markMoment(note: String? = nil) -> MeetingHighlight?

// Storage/MeetingStore.swift (internal, nonisolated so MCP/ can call it off the main actor)
nonisolated static func parseTranscript(_ text: String)
    -> (title: String?, segments: [TranscriptSegment], speakers: [MeetingSpeaker], highlights: [MeetingHighlight])
```

- `ClipPlanner.plan` has no energy envelope, but splitting a long turn at quiet
  points needs one; add an internal overload that takes the track's
  `EnergyEnvelope` (with `SpeechSegmenter.segments(for:within:)`) and keep the
  public one working.
- `parseTranscript` must stay free of file access and main-actor state:
  `FoundationMeetingMigrationTests` calls it from a nonisolated test.
### 9.4 MCP

```swift
// MCP/MCPServer.swift
public final class MCPServer {
    public static let maximumLineBytes: Int            // 1 MiB
    public init(input: Int32, output: Int32, handler: MCPHandler)
    public func run()                                  // returns on input EOF or a failed write
}

// MCP/MCPHandler.swift
public struct MCPAccess: Equatable, Sendable {
    public var enabled: Bool                               // the read switch
    public var canWrite: Bool                              // its own switch; means nothing while enabled is false
    public var maxAgeDays: Int                             // 0 = all
    public init(enabled: Bool, canWrite: Bool = false, maxAgeDays: Int)
}
public final class MCPHandler {
    // notes, highlights: nil for a handler that cannot write at all, whatever the switch says
    public init(archive: MeetingArchiveReading, notes: MeetingNotesWriting? = nil,
                highlights: MeetingHighlightsWriting? = nil, access: @escaping () -> MCPAccess,
                language: @escaping () -> AppLanguage, now: @escaping () -> Date,
                timeZone: TimeZone = .current, serverVersion: String, clock: UptimeProviding = SystemUptime())
    public func handle(line: Data) -> Data?            // nil for notifications; numeric ids echoed as written
    public var requestObserver: ((MCPRequestSummary) -> Void)?   // what --verbose prints; never content
}

// MCP/JSONValue.swift
public enum JSONValue: Equatable, Sendable {
    case null, bool(Bool), integer(Int), number(Double), string(String), array([JSONValue]), object([String: JSONValue])
    public init(parsing data: Data) throws             // an unpaired \uD800-style escape reads as U+FFFD
    public func serialized() -> Data                   // one line, sorted keys
    public subscript(key: String) -> JSONValue? { get }
    public var stringValue: String?; boolValue: Bool?; intValue: Int?; doubleValue: Double?
    public var arrayValue: [JSONValue]?; objectValue: [String: JSONValue]?; isNull: Bool   // implemented
}
// plus Boolean, Integer, Float, String, Array and Dictionary literal conformances (no nil literal)

// MCP/MCPText.swift
public enum MCPText {
    public static let serverName: String; serverTitle: String
    public static let modernProtocolVersion: String; legacyProtocolVersions: [String]; supportedProtocolVersions: [String]
    public static let instructions: String             // SPEC-speakers-mcp 8.5 verbatim, 1,366 bytes (< 1,500)
    public static let transcriptNotice: String
    public struct Tool: Equatable, Sendable {
        public let name, title, description: String
        public let readOnly: Bool                      // false for mark_highlight, unmark_highlight, write_meeting_notes
        public init(name:title:description:readOnly: = true)
    }
    public static let listMeetings, getMeeting, getTranscript, searchMeetings, listHighlights: Tool
    public static let markHighlight, unmarkHighlight: Tool
    public static let writeMeetingNotes: Tool
    public static let tools: [Tool]                    // tools/list order; the tools that change a meeting come last
}

// MCP/MCPSanitizer.swift
public enum MCPSanitizer {
    public static func sanitize(_ text: String) -> String   // SPEC-speakers-mcp 8.6 and 10: controls, invisibles, look-alike tags
    public static func isControl(_ scalar: Unicode.Scalar) -> Bool     // what Claude's own text is refused for
    public static func isInvisible(_ scalar: Unicode.Scalar) -> Bool   // likewise
}

// MCP/MeetingArchive.swift
public struct ArchivedMeeting: Sendable {
    public var record: MeetingRecord; public var recordModified: Date; public var recordSize: Int
    public init(record:recordModified:recordSize:)
}
public protocol MeetingArchiveReading: AnyObject {
    func meetings() -> [ArchivedMeeting]               // newest first
    func meeting(id: UUID) -> ArchivedMeeting?
}
public final class MeetingArchive: MeetingArchiveReading {
    public static let maximumFileBytes: Int            // 32 MiB
    public let directories: AppDirectories
    public init(directories: AppDirectories)
}

// MCP/MeetingRecordFile.swift
enum MeetingRecordFile {                                // shared read/write for meeting.json, used by both writers below
    enum UpdateError: Error, Equatable { case unreadable, failed }
    static func read(directories: AppDirectories, id: UUID) -> MeetingRecord?
    static func write(_ record: MeetingRecord, directories: AppDirectories) throws
    /// Read, change, replace, all under `MeetingFileLock`; writes only when `change` reports a change.
    static func update<Result>(directories: AppDirectories, id: UUID, timeout: TimeInterval = MeetingFileLock.defaultTimeout,
                               _ change: (inout MeetingRecord) -> (result: Result, changed: Bool)) throws -> Result
}

// Storage/MeetingFileLock.swift — taken by MeetingStore.write and MeetingRecordFile.update
public enum MeetingFileLock {
    public static let fileName: String                 // ".lock" inside Meetings/<id>/, an exclusive flock
    public static let defaultTimeout: TimeInterval     // 5 s
    public enum LockError: Error, Equatable { case unavailable, timedOut }
    public static func withLock<T>(meetingFolder: URL, timeout: TimeInterval = defaultTimeout, _ body: () throws -> T) throws -> T
    public static func withLockIfPossible<T>(meetingFolder: URL, timeout: TimeInterval = defaultTimeout,
                                             _ body: () throws -> T) rethrows -> T   // the app: saves unlocked rather than not at all
}

// MCP/MeetingNotesFile.swift
public enum MeetingNotesWriteError: Error, Equatable { case unreadable, failed }
public protocol MeetingNotesWriting: AnyObject {
    /// Replaces the meeting's notes, or removes them when nil; returns what the file holds afterwards.
    @discardableResult func write(_ notes: MeetingNotes?, to id: UUID) throws -> MeetingNotes?
}
/// One of the two things the MCP process may change: `claudeNotes` of one `meeting.json`. It
/// re-reads the record immediately before replacing it and keeps every other field, so a save by
/// the app is not lost; `transcript.md` is left to the app. Notes saying what the file already
/// says are not written again, so its modification date, which transcript cursors key on, stays put.
public final class MeetingNotesFile: MeetingNotesWriting {
    public let directories: AppDirectories
    public init(directories: AppDirectories)
}

// MCP/MeetingHighlightsFile.swift
public enum MeetingHighlightsWriteError: Error, Equatable { case unreadable, failed }
public struct HighlightMarkResult: Sendable { public let record: MeetingRecord; highlight: MeetingHighlight; created: Bool }
public struct HighlightUnmarkResult: Sendable { public let record: MeetingRecord; removed: Bool }
public protocol MeetingHighlightsWriting: AnyObject {
    @discardableResult func mark(time: TimeInterval, note: String?, on id: UUID, now: Date) throws -> HighlightMarkResult
    @discardableResult func unmark(_ highlightID: UUID, on id: UUID) throws -> HighlightUnmarkResult
}
/// The other thing the MCP process may change: `highlights` of one `meeting.json`. Re-reads the
/// record immediately before replacing it, like `MeetingNotesFile`; marking the same time and
/// note twice, or unmarking an id that is already gone, changes nothing and writes nothing.
public final class MeetingHighlightsFile: MeetingHighlightsWriting {
    public let directories: AppDirectories
    public init(directories: AppDirectories)
}

// MCP/SlovoPreferences.swift
public struct SlovoPreferences: Sendable {
    public static let appDomain = "local.slovo.Slovo"
    public let domain: String                          // tests pass a throwaway suite name
    public init(domain: String = SlovoPreferences.appDomain)
    public func access() -> MCPAccess                  // synchronizes, then reads; use AppSettings.normalized… for values
    public func language() -> AppLanguage
}

// Sources/Slovo/Support/MCPServerMode.swift
@MainActor enum MCPServerMode { static let argument = "--mcp"; static func runIfRequested() }
```

- The tool input schemas and result shapes are SPEC-speakers-mcp 8.6; `MCPText`
  holds the English texts verbatim so wording is not duplicated.
- The MCP process builds no `MeetingStore`, `JSONFile`, `Localizer`, `AppModel`,
  engine or diarizer, and only reads Slovo's preferences. The two things it writes
  under the data folder are `MeetingRecord.claudeNotes` (through `MeetingNotesFile`)
  and `MeetingRecord.highlights` (through `MeetingHighlightsFile`) of a single
  `meeting.json`, both only while `integrations.mcp.writeEnabled` is on, and both
  through `MeetingRecordFile.update` under `MeetingFileLock` (which leaves an empty
  `Meetings/<id>/.lock`). `MeetingStore.write` takes the same lock around its own
  read-modify-write, so neither process can undo the other's change.
- `SlovoPreferences.access()` normalizes the age limit with
  `AppSettings.normalizedMCPMaxAgeDays`: a value outside the offered choices means
  all meetings, on purpose (SPEC-speakers-mcp 8.10).
- `unmark_highlight` removes any highlight, the user's own included; its description
  says so, and it is declared `destructiveHint: true`.

### 9.5 String tables

- `Meetings.strings` gains `meetings.speaker.numbered` (`%ld`),
  `meetings.transcript.participants` (`%@`), `meetings.transcript.highlightsHeading`,
  `meetings.transcript.heading`, `meetings.diarization.failed` (`%@`),
  `meetings.diarization.unsupportedSystem`, `meetings.error.mergeAcrossTracks`,
  `meetings.error.speakersOnlyUnavailable`; the notes Claude writes add
  `meetings.transcript.claudeHeading`, `.claudeWritten` (`%@` author, `%@` date),
  `.claudeDecisions` and `.claudeTasks`, which head that section of `transcript.md`.
- `Diarization.strings` (new): `diarization.error.modelMissing`, `.modelDamaged`,
  `.loadFailed` (`%@`), `.downloadFailed` (`%@`), `.filesMissingAfterDownload`,
  `.deleteWhileDownloading`, `.unsupportedSystem`.
- `Integration.strings` (new): `integration.mcp.disabled`, `.notFound` (`%@`),
  `.ambiguous` (no argument; the matching ids follow the message), `.noTranscript`,
  `.stillProcessing`, `.badCursor`, `.badArgument` (`%@`), `.noMeetings`,
  `.tooManyRequests` (`%ld` seconds), `.damaged`, `.writeDisabled` and `.writeFailed`
  (the two refusals shared by every tool that changes a meeting); `integration.prompt.tasks`,
  `.summary.short`, `.summary.full` with `{title}`, `{date}`, `{duration}` and
  `{speakers}` filled in code; `integration.prompt.paging` with `{meeting_id}`; and
  `integration.prompt.slovoUser` («это пользователь Slovo» / "the Slovo user"), the
  suffix after the `is_me` speaker in a prompt's speaker list.
### 9.6 Data invariants

- `JSONValue` keeps `integer` and `number` apart so integer request ids echo
  exactly; `intValue` also accepts whole doubles.
- `MeetingRecord.currentFormatVersion` names format 2; `effectiveSpeakers` orders
  microphone speakers first; `speakerID(for:)` requires the named speaker to be on
  the segment's track; `speaker(id:)` also returns the fallbacks.
- `TranscriptionProgress.init` takes an optional `overallFraction` (nil means the
  segment fraction) and clamps it to 0...1.
- Settings accessors normalize values outside the offered choices (people count →
  automatic, age limit → all meetings).
- `DiarizationTuning` is a value type with `standard`, so the bench's sweep and the
  meeting pipeline share one description format; `diagnose` takes a `hint:` with a
  default; `DiarizationScoring` has its own `Segment` and `Score` types because
  SlovoCore cannot see BenchKit.
- `ClipPlanner` and `SpeakerRegistry` are public; `ClipPlanner.assign` returns one
  optional cluster per segment.
- `MeetingStore.parseTranscript` returns speakers and highlights too, so the
  recovery parser of SPEC-speakers-mcp 3.4 needs no second signature.
- `SlovoPreferences` is a value type whose reads synchronize the domain each time;
  `MeetingArchive.maximumFileBytes` and `MCPServer.maximumLineBytes` name the size
  limits.

### 9.7 Integration

Agents A–D landed; none of the placeholders listed in 10.1 remain.

Wiring:

```swift
// Sources/Slovo/SlovoApp.swift: the first statement of SlovoMain.main()
MCPServerMode.runIfRequested()        // then MicrophoneDiagnostics.runIfRequested()

// Sources/SlovoCore/Integration/MeetingWiring.swift
@MainActor public final class MeetingWiring: ObservableObject {
    @Published public private(set) var refusedShortcut: HotkeyShortcut?   // until a registration succeeds
    public private(set) var isShortcutRegistered: Bool
    public init(settings: AppSettings, shortcut: GlobalShortcutRegistering,
                diarization: any DiarizationProviding & DiarizationModelManaging,
                isRecording: AnyPublisher<Bool, Never>, markMoment: @escaping @MainActor () -> Void)
    public convenience init(settings: AppSettings, recorder: MeetingRecorder, shortcut: GlobalShortcutRegistering,
                            diarization: any DiarizationProviding & DiarizationModelManaging)
    public func start()
    public func stop()
    @discardableResult public func applySettings() -> Bool
    public func deleteDiarizationModel() async throws   // release(), await waitForLoad(), delete()
}
```

- The mark-moment shortcut is registered through its own `PasteLastShortcut`
  instance (a registrar holds one hot key) when `MeetingRecorder.state` becomes
  `.recording`, removed on `.stopping`, `.idle` or `.failed`, and re-registered
  when `meetings.highlightShortcut` changes during a recording. The state is
  followed without a hop to the main queue, so the shortcut exists in the same
  turn the recording starts. A refused shortcut is not retried until the setting
  or the recording changes; `refusedShortcut` keeps it after the recording so
  Settings can show the warning while it is still selected.
- `AppModel` creates the wiring after the recorder, calls `start()` in `start()`
  and `stop()` in `prepareForTermination()`, copies `refusedShortcut` into
  `refusedHighlightShortcut`, forwards `DiarizationService.objectWillChange`, and
  deletes the speaker model through `deleteDiarizationModel()`.
- Tests: `IntegrationMeetingWiringTests.swift` (the shortcut over the real recorder
  and store, refusal reporting, deletion order with a fake and with the real
  service holding a load in flight, the meeting pipeline separating speakers
  through a real `DiarizationService` over a fake model load) and
  `IntegrationMCPProcessTests.swift` (the debug `Slovo` executable with `--mcp`
  serving a meeting saved by the app's `MeetingStore`; skipped unless the binary
  is newer than `SlovoApp.swift` and `MCPServerMode.swift` and the call opens
  `main()`, so an old binary never starts the menu bar app).
- Platform fact: FluidAudio 0.15.7 keeps the diarizer files in
  `modelsRoot/diarization/speaker-diarization/` (the repository name without its
  `-coreml` suffix); download and loader derive the same name.
- Release builds ignore `--data-dir` and `--defaults-suite`. `CFFIXED_USER_HOME`
  moves the data folder of a release `--mcp` process to another home folder, but
  not its preferences, which `cfprefsd` still reads from the user's domain.

API additions reported by A (diarization):

- `DiarizationService.downloadBytes` and `.modelIdentifier` are
  `nonisolated public static let`, readable off the main actor.
- `DiarizationService.init(modelsRoot:downloader:factory:)` is a
  `convenience init`; an internal designated init adds `systemSupported:` so tests
  can play macOS 14. Completeness and status come from the internal
  `DiarizationModelFiles`.
- `DiarizationTuning(configuration:)` also accepts `zeroVoteMin`,
  `excludeOverlap`, `exclusive` and the space-separated form `description` prints,
  so a recorded configuration parses back.
- `FluidSpeakerDiarizer.diarize` returns `[]` when FluidAudio reports no speech.
  `DiarizationDiagnosis.timings` keys: `wall`, `modelLoad`, `audioLoading`,
  `segmentation`, `embedding`, `clustering`, `postProcessing`; centroids come from
  FluidAudio's per-speaker database.
- `LiveDiarizationModelDownloader` rescales FluidAudio's repository progress, which
  stops at 0.5 because loading fills the rest, to 0–1. Parakeet's download progress
  still stops at 0.5 for the same reason.
- `FluidAudioJobQueue` (internal actor) replaces `ParakeetJobQueue` with the same
  FIFO and cancellation.
- BenchKit: `RTTM.swift` (`SpeakerSegment`, `AnnotationError`, RTTM and `TruthCSV`
  reading and writing) and `DiarizationMetrics.swift` (`ReferenceTurn`,
  `AttributedText`, `TurnLength`, `Tally`, `AttributionResult`, `ShortTurnResult`,
  `SpeakerCountResult`, `DERBreakdown`, `WordErrors`, the `DiarizationMetrics`
  functions, `TrackResult`, `TranscriptionResult`, `EchoPair`, `MeLabelResult`,
  `DiarizationCaseResult` and `DiarizationReport` with ranking and gate tables).
  DER itself stays in `DiarizationScoring`.
- `slovo-bench`: `diarize --summary`, `--fake-diarizer oracle`, `--models`,
  `--long-minutes`, `--truth-system`, `--condition`; `diarize-synth --out` and
  `--substitute-voices`; scenario voice source `tone` and the `track` and `me`
  fields; results of real meetings in `Bench/results/diarization-meetings/`;
  synthetic cases in `Bench/diarization/out/` (not in git);
  `Bench/diarization/scenarios/generate.py` regenerates the scenarios byte for byte.

API additions reported by B (meetings), all internal:

- `MeetingStore.transcriptLanguage` (language provider for tests),
  `MeetingRecorder.hostTime` and `startSessionWithoutCapture(meetingID:originHostTime:)`
  (test seams without hardware), `SpeechSegmenter.envelope(inFileAt:)`, the
  `ClipPlanner.plan` overload with an envelope and segmenter, and
  `MeetingErrors.speakersOnlyUnavailable()`.
- Highlight anchoring: when several lines contain the time, the line that started
  last wins, so a star placed at a line's start toggles that line again.
- `SpeakerRegistry` runs after transcription, over clips that produced text, with
  the record re-read from the store as `previous`; a voice without recognized text
  does not become a speaker, and renames made during the run are matched.
- A failed or cancelled re-run restores only `status` and `errorMessage`; nothing
  else is written before a run succeeds. A speakers-only run fails and restores the
  record when separation fails, never deletes audio, and leaves a track that gives no
  turns (under 5 s of speech, or none found) with its speakers and line assignments.
- A fallback speaker the user renamed or marked as «Я» passes its name and «Я» to
  the voice of its track that overlaps its lines the most once that track is
  separated; the voice gets a new number.
- `MeetingStore` reads a record left `.transcribing` with segments or an engine as
  `.transcribed`: a run replaces the transcript only when it succeeds.
- Speech the diarizer missed that holds no frame above the silence level does not
  become a clip.
- "Highest number ever used" is `MeetingRecord.highestSpeakerNumber`, which every run
  and every merge of a numbered speaker record, together with stored numbers,
  speaker ids in segments and the last run's `speakersPerTrack` for older files.
- `speakersPerTrack` counts speakers with text on each separated track; the state is
  `.applied` when the model is ready but no track had 5 s of speech;
  `MeetingDiarization.message` holds the whole localized sentence for `.failed` and
  is also set for `.unsupportedSystem`. The inits without a diarization provider
  record `.off` and store format 2.
- Markdown v2: a moment without an anchor line is `- [mm:ss]` plus ` — note`; with a
  speaker filter such moments are left out; the participants line lists speakers
  with visible lines; an empty moments section is omitted; ` · duration` appears only
  when the duration is above zero.
- Recovery from `transcript.md` writes only `meeting.json` (format 1); starred lines
  become `.transcript` highlights with FNV-1a ids stable across processes and a
  `createdAt` of the 2001 reference date; named speakers are numbered above every
  «Спикер N» in the document.

API additions reported by C (mcp):

- `MeetingArchiveReading.unreadableMeetingDate(id:) -> Date?`, a requirement with a
  default that returns nil, so a full-UUID lookup of an unreadable folder answers
  `integration.mcp.damaged` within the age limit.
- `MCPRequestSummary` (method, tool, duration, error code, tool error) and
  `MCPHandler.requestObserver`, used by `--verbose`; it never carries arguments or
  content.
- `JSONValue.serializedString`, the bytes of `serialized()` as a string.
- `MeetingArchive` titles a meeting recovered without a title through
  `MeetingTitles.defaultTitle(for:language:)` (internal, B).
- `MCPSanitizer` turns tab, LF, CR, VT, FF and NEL into spaces and removes other
  C0/C1 controls, escape sequences and the listed direction marks.
- The `list_meetings` cursor is base64url
  `{k:"m", t:<createdAt seconds since 2001>, i:<uuid>, q:<filter hash>}` and is
  rejected when `query`, `since` or `until` change.
- Results stay under 48,000 characters with `speakers_truncated` (over 30 speakers
  in `list_meetings`, over 200 in `get_meeting`) and `highlights_truncated`; titles
  are capped at 200 characters, names at 80, notes at 500.
- A transcript page header can say that a line continues from the previous page, the
  footer names the continued line, and a page always holds at least 400 body
  characters.
- `prompts/get` treats empty-string arguments as absent, rejects unknown argument
  names with −32602 and answers −32602 for a meeting without a transcript;
  `resources/read` of an untranscribed meeting is "not found"; `resources/list`
  always lists `latest`.
- `search_meetings` hits: all words in a line; all words in a speaker label (one hit
  at that speaker's first line); all words in the title (null time and speaker).
  An empty result adds `integration.mcp.noMeetings`; `next_step` uses the short id
  only when it is unambiguous.

API additions reported by D (ui), all in the `Slovo` target:

- `AppModel`: `diarization`, `meetingWiring`, `refusedHighlightShortcut`,
  `claudeRegistration`, `mcpCheckState`, `canProcess(_:mode:)`, `speakerLabel(_:in:)`,
  `highlightsText(_:speakerFilter:)`, `transcriptText(_:speakerFilter:)`,
  `highlightForNote(on:in:)`, `speakerModelStatus`, `speakerModelLoadState`,
  `isSpeakerSeparationSupported`, `executablePath`, the view actions of
  SPEC-speakers-mcp 13.5 and `deleteSpeakerModel()`; `speakerName(_:)` is removed.
  New types `MCPCheckResult`, `MCPCheckState`, `ClaudeRegistration`, `MCPSelfCheck`,
  `MeetingDetailTab`; new snapshot fields for renders.
- `Theme.speakerPalette` (8 colors per appearance, index 0 `accentText`, 1
  `secondaryAccent`, all at least 4.5:1 on the panel, on `rowHover` and on `accentSoft`
  over the panel, checked by `InterfaceThemeContrastTests`), `codeFont`, `highlightStar`;
  the generic `ModelRow` behind `EngineModelRow`; `Support/ClaudeIntegration.swift`.
- Check sends the requests of SPEC-speakers-mcp 7.2; «Работает: Claude видит N
  встреч» counts the app's meetings within `mcpMaxAgeDays`, and «Работает, но доступ
  выключен» is chosen when the reply is `isError` and access is off in the app.
- The failed-separation notice shows `MeetingDiarization.message`, or
  `meetings.diarization.failedGeneric` when it is missing.
- Transcript line text is not selectable, so right-click opens the line menu.
- The Claude Code snippet is shown with a line continuation before the path; Copy
  puts the one-line command on the pasteboard.
- The mark-moment sound is the system sound Purr at 0.35 volume, played by
  `FlowBarController`, because `SoundCue` is frozen.
- App string keys beyond SPEC-speakers-mcp 6.4: `settings.integrations.*`,
  `settings.meetings.*` (speakers, model row, shortcut), `meetings.reprocess.*`
  details and reasons, `meetings.highlight.quoted`, `.show`, `.noLine`, `.delete`,
  `meetings.speaker.edit`, `.nameHint`, `.source.*`, `meetings.transcript.filtered`,
  `meetings.highlights.*` states and `common.copy`, in both languages.
