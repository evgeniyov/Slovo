import Foundation

/// Every UserDefaults key Slovo uses. Views bind with `@AppStorage(SettingsKey.x)`,
/// services read through `AppSettings`.
public enum SettingsKey {
    // General
    public static let appLanguage = "app.language"
    public static let dictationKey = "dictation.key"
    public static let handsFreeEnabled = "dictation.handsFreeEnabled"
    public static let escapeCancels = "dictation.escapeCancels"
    public static let maxHandsFreeSeconds = "dictation.maxHandsFreeSeconds"
    public static let pasteLastShortcut = "dictation.pasteLastShortcut"
    /// Empty or missing means the system default input.
    public static let microphoneUID = "dictation.microphoneUID"
    public static let languageMode = "dictation.languageMode"

    // Recognition
    public static let russianEngine = "recognition.engine.russian"
    public static let englishEngine = "recognition.engine.english"
    public static let meetingEngine = "recognition.engine.meetings"
    /// `EnglishHelperChoice`: the model that corrects English words in mixed dictation.
    public static let englishHelper = "recognition.englishHelper"
    /// Whisper transcribes the entire recording in Russian + English mode.
    public static let accurateMixedDictation = "recognition.accurateMixedDictation"
    public static let idleUnloadMinutes = "recognition.idleUnloadMinutes"

    // System
    public static let showFlowBarWhenIdle = "system.showFlowBarWhenIdle"
    public static let soundsEnabled = "system.soundsEnabled"
    public static let showInDock = "system.showInDock"
    /// Whether the menu bar icon shows. Hidden, it still appears while a meeting records, and
    /// opening Slovo again (Finder, Spotlight) brings the window back.
    public static let showMenuBarIcon = "system.showMenuBarIcon"

    // Meetings
    public static let recordSystemAudio = "meetings.recordSystemAudio"
    /// The state the microphone starts in when a meeting recording starts; the recording
    /// itself can switch it for as long as it lasts.
    public static let recordMicrophone = "meetings.recordMicrophone"
    /// Whether a new meeting recording also captures the screen. Off by default: unlike
    /// audio, this needs an explicit opt-in each time it should happen.
    public static let recordScreen = "meetings.recordScreen"
    /// Empty means the whole display; otherwise the bundle identifier of the one app a
    /// screen recording captures, video and audio alike.
    public static let screenCaptureApp = "meetings.screenCaptureApp"
    public static let keepMeetingAudio = "meetings.keepAudio"
    public static let inboxEnabled = "inbox.enabled"
    /// Separate speakers when the speaker model is ready; the model download is the real opt-in.
    public static let speakerSeparation = "meetings.speakerSeparation"
    /// 0 = automatic, otherwise 2–8 people.
    public static let expectedSpeakers = "meetings.expectedSpeakers"
    /// `HotkeyShortcut` as JSON data: mark a moment while a meeting records.
    public static let highlightShortcut = "meetings.highlightShortcut"
    /// `HotkeyShortcut` as JSON data: switch the microphone while a meeting records.
    public static let microphoneShortcut = "meetings.microphoneShortcut"

    // Integrations
    /// Claude may read meetings through `Slovo --mcp`.
    public static let mcpEnabled = "integrations.mcp.enabled"
    /// 0 = all meetings, otherwise 90, 30 or 7 days.
    public static let mcpMaxAgeDays = "integrations.mcp.maxAgeDays"
    /// Claude may write its conclusions into a meeting. Separate from `mcpEnabled`, so
    /// letting Claude read is not also letting it write.
    public static let mcpWriteEnabled = "integrations.mcp.writeEnabled"

    // Data and privacy
    public static let keepHistory = "privacy.keepHistory"
    public static let keepDictationAudio = "privacy.keepDictationAudio"

    // Style
    public static let removeFillers = "cleanup.removeFillers"
    public static let removeRepetitions = "cleanup.removeRepetitions"

    public static func style(for category: AppCategory) -> String {
        "style.\(category.rawValue)"
    }

    // Interface state
    public static let onboardingCompleted = "onboarding.completed"
    /// String array of banner ids the user dismissed.
    public static let dismissedBanners = "ui.dismissedBanners"
    public static let dictionarySeeded = "dictionary.seeded"
    public static let dismissedDictionarySuggestions = "dictionary.dismissedSuggestions"
    /// The last `DictionarySeed.additions` version added to an already seeded Dictionary.
    public static let dictionarySeedVersion = "dictionary.seedVersion"
}

/// Typed access to settings with their defaults. A value type over a UserDefaults
/// instance, so tests pass a throwaway suite.
public struct AppSettings: @unchecked Sendable {
    public let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// Default values for `UserDefaults.register(defaults:)`; `@AppStorage` bindings
    /// then see the same defaults as services.
    public static var registrationDefaults: [String: Any] {
        var values: [String: Any] = [
            SettingsKey.appLanguage: AppLanguage.system.rawValue,
            SettingsKey.dictationKey: DictationKey.fn.rawValue,
            SettingsKey.handsFreeEnabled: true,
            SettingsKey.escapeCancels: true,
            SettingsKey.maxHandsFreeSeconds: 360.0,
            SettingsKey.microphoneUID: "",
            SettingsKey.languageMode: LanguageMode.mixed.rawValue,
            SettingsKey.russianEngine: EngineID.gigaamRNNT.rawValue,
            SettingsKey.englishEngine: EngineID.parakeet.rawValue,
            SettingsKey.meetingEngine: EngineID.gigaamCTC.rawValue,
            SettingsKey.englishHelper: EnglishHelperChoice.auto.rawValue,
            SettingsKey.accurateMixedDictation: false,
            SettingsKey.idleUnloadMinutes: EngineIdleTimeout.minutes15.rawValue,
            SettingsKey.showFlowBarWhenIdle: true,
            SettingsKey.soundsEnabled: true,
            SettingsKey.showInDock: true,
            SettingsKey.showMenuBarIcon: true,
            SettingsKey.recordSystemAudio: true,
            SettingsKey.recordMicrophone: true,
            SettingsKey.recordScreen: false,
            SettingsKey.screenCaptureApp: "",
            SettingsKey.keepMeetingAudio: RetentionPeriod.forever.rawValue,
            SettingsKey.inboxEnabled: false,
            SettingsKey.speakerSeparation: true,
            SettingsKey.expectedSpeakers: 0,
            SettingsKey.mcpEnabled: false,
            SettingsKey.mcpMaxAgeDays: 0,
            SettingsKey.mcpWriteEnabled: false,
            SettingsKey.keepHistory: RetentionPeriod.forever.rawValue,
            SettingsKey.keepDictationAudio: RetentionPeriod.days7.rawValue,
            SettingsKey.removeFillers: true,
            SettingsKey.removeRepetitions: true,
            SettingsKey.onboardingCompleted: false,
            SettingsKey.dismissedBanners: [String](),
            SettingsKey.dictionarySeeded: false
        ]
        for category in AppCategory.allCases {
            values[SettingsKey.style(for: category)] = category.defaultStyle.rawValue
        }
        if let shortcut = Self.encodedShortcut(.markMomentDefault) {
            values[SettingsKey.highlightShortcut] = shortcut
        }
        if let shortcut = Self.encodedShortcut(.microphoneToggleDefault) {
            values[SettingsKey.microphoneShortcut] = shortcut
        }
        return values
    }

    /// Choices for "People in a meeting": 0 means automatic.
    public static let expectedSpeakerChoices = [0, 2, 3, 4, 5, 6, 7, 8]

    /// Choices for "Meetings Claude can see", in days: 0 means all meetings.
    public static let mcpMaxAgeDayChoices = [0, 90, 30, 7]

    /// A stored people count outside the choices means automatic.
    public static func normalizedExpectedSpeakers(_ value: Int) -> Int {
        expectedSpeakerChoices.contains(value) ? value : 0
    }

    /// A stored age limit outside the choices means all meetings, so a bad value never
    /// hides meetings the user expects Claude to see; access itself is a separate switch.
    public static func normalizedMCPMaxAgeDays(_ value: Int) -> Int {
        mcpMaxAgeDayChoices.contains(value) ? value : 0
    }

    public func registerDefaults() {
        defaults.register(defaults: Self.registrationDefaults)
    }

    public var appLanguage: AppLanguage {
        get { enumValue(SettingsKey.appLanguage, default: .system) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.appLanguage) }
    }

    public var dictationKey: DictationKey {
        get { enumValue(SettingsKey.dictationKey, default: .fn) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.dictationKey) }
    }

    public var handsFreeEnabled: Bool {
        get { bool(SettingsKey.handsFreeEnabled, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.handsFreeEnabled) }
    }

    public var escapeCancels: Bool {
        get { bool(SettingsKey.escapeCancels, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.escapeCancels) }
    }

    public var maxHandsFreeSeconds: TimeInterval {
        get {
            let value = defaults.double(forKey: SettingsKey.maxHandsFreeSeconds)
            return value > 0 ? value : 360
        }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.maxHandsFreeSeconds) }
    }

    public var pasteLastShortcut: HotkeyShortcut {
        get {
            guard let data = defaults.data(forKey: SettingsKey.pasteLastShortcut),
                  let shortcut = try? JSONDecoder().decode(HotkeyShortcut.self, from: data) else {
                return .pasteLastDefault
            }
            return shortcut
        }
        nonmutating set {
            defaults.set(try? JSONEncoder().encode(newValue), forKey: SettingsKey.pasteLastShortcut)
        }
    }

    /// Nil means the system default input device.
    public var microphoneUID: String? {
        get {
            let value = defaults.string(forKey: SettingsKey.microphoneUID) ?? ""
            return value.isEmpty ? nil : value
        }
        nonmutating set { defaults.set(newValue ?? "", forKey: SettingsKey.microphoneUID) }
    }

    public var languageMode: LanguageMode {
        get { enumValue(SettingsKey.languageMode, default: .mixed) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.languageMode) }
    }

    public var russianEngine: EngineID {
        get { enumValue(SettingsKey.russianEngine, default: .gigaamRNNT) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.russianEngine) }
    }

    public var englishEngine: EngineID {
        get { enumValue(SettingsKey.englishEngine, default: .parakeet) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.englishEngine) }
    }

    public var meetingEngine: EngineID {
        get { enumValue(SettingsKey.meetingEngine, default: .gigaamCTC) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.meetingEngine) }
    }

    public var englishHelper: EnglishHelperChoice {
        get { enumValue(SettingsKey.englishHelper, default: .auto) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.englishHelper) }
    }

    public var accurateMixedDictation: Bool {
        get { bool(SettingsKey.accurateMixedDictation, default: false) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.accurateMixedDictation) }
    }

    public var idleUnload: EngineIdleTimeout {
        get {
            guard defaults.object(forKey: SettingsKey.idleUnloadMinutes) != nil else { return .minutes15 }
            return EngineIdleTimeout(rawValue: defaults.integer(forKey: SettingsKey.idleUnloadMinutes)) ?? .minutes15
        }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.idleUnloadMinutes) }
    }

    public var showFlowBarWhenIdle: Bool {
        get { bool(SettingsKey.showFlowBarWhenIdle, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.showFlowBarWhenIdle) }
    }

    public var soundsEnabled: Bool {
        get { bool(SettingsKey.soundsEnabled, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.soundsEnabled) }
    }

    public var showInDock: Bool {
        get { bool(SettingsKey.showInDock, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.showInDock) }
    }

    public var showMenuBarIcon: Bool {
        get { bool(SettingsKey.showMenuBarIcon, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.showMenuBarIcon) }
    }

    public var recordSystemAudio: Bool {
        get { bool(SettingsKey.recordSystemAudio, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.recordSystemAudio) }
    }

    /// Whether a new meeting recording starts with the microphone recorded. Off, the
    /// microphone track still keeps the timeline but holds silence until it is switched on.
    public var recordMicrophone: Bool {
        get { bool(SettingsKey.recordMicrophone, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.recordMicrophone) }
    }

    /// Whether a new meeting recording also captures the screen. Off by default; reused
    /// as the "with screen / without" choice shown right before starting a recording.
    public var recordScreen: Bool {
        get { bool(SettingsKey.recordScreen, default: false) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.recordScreen) }
    }

    /// The whole display, or one app's windows and sound instead. Read by
    /// `MeetingRecorder.start()` only when `recordScreen` is on.
    public var screenCaptureTarget: ScreenCaptureTarget {
        get { .from(storedBundleID: defaults.string(forKey: SettingsKey.screenCaptureApp)) }
        nonmutating set { defaults.set(newValue.storedBundleID ?? "", forKey: SettingsKey.screenCaptureApp) }
    }

    public var keepMeetingAudio: RetentionPeriod {
        get { enumValue(SettingsKey.keepMeetingAudio, default: .forever) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.keepMeetingAudio) }
    }

    /// Whether audio dropped into the inbox folder is turned into meetings.
    public var inboxEnabled: Bool {
        get { bool(SettingsKey.inboxEnabled, default: false) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.inboxEnabled) }
    }

    public var speakerSeparation: Bool {
        get { bool(SettingsKey.speakerSeparation, default: true) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.speakerSeparation) }
    }

    /// 0 = automatic, otherwise 2–8.
    public var expectedSpeakers: Int {
        get { Self.normalizedExpectedSpeakers(defaults.integer(forKey: SettingsKey.expectedSpeakers)) }
        nonmutating set { defaults.set(Self.normalizedExpectedSpeakers(newValue), forKey: SettingsKey.expectedSpeakers) }
    }

    public var highlightShortcut: HotkeyShortcut {
        get {
            guard let data = defaults.data(forKey: SettingsKey.highlightShortcut),
                  let shortcut = try? JSONDecoder().decode(HotkeyShortcut.self, from: data) else {
                return .markMomentDefault
            }
            return shortcut
        }
        nonmutating set {
            defaults.set(Self.encodedShortcut(newValue), forKey: SettingsKey.highlightShortcut)
        }
    }

    public var microphoneShortcut: HotkeyShortcut {
        get {
            guard let data = defaults.data(forKey: SettingsKey.microphoneShortcut),
                  let shortcut = try? JSONDecoder().decode(HotkeyShortcut.self, from: data) else {
                return .microphoneToggleDefault
            }
            return shortcut
        }
        nonmutating set {
            defaults.set(Self.encodedShortcut(newValue), forKey: SettingsKey.microphoneShortcut)
        }
    }

    public var mcpEnabled: Bool {
        get { bool(SettingsKey.mcpEnabled, default: false) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.mcpEnabled) }
    }

    /// 0 = all meetings, otherwise 90, 30 or 7.
    public var mcpMaxAgeDays: Int {
        get { Self.normalizedMCPMaxAgeDays(defaults.integer(forKey: SettingsKey.mcpMaxAgeDays)) }
        nonmutating set { defaults.set(Self.normalizedMCPMaxAgeDays(newValue), forKey: SettingsKey.mcpMaxAgeDays) }
    }

    /// Writing is off until it is turned on separately, and does nothing while `mcpEnabled`
    /// is off: Claude cannot write into meetings it is not allowed to read.
    public var mcpWriteEnabled: Bool {
        get { bool(SettingsKey.mcpWriteEnabled, default: false) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.mcpWriteEnabled) }
    }

    public var keepHistory: RetentionPeriod {
        get { enumValue(SettingsKey.keepHistory, default: .forever) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.keepHistory) }
    }

    public var keepDictationAudio: RetentionPeriod {
        get { enumValue(SettingsKey.keepDictationAudio, default: .days7) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: SettingsKey.keepDictationAudio) }
    }

    public var cleanup: AutoCleanupOptions {
        get {
            AutoCleanupOptions(removeFillers: bool(SettingsKey.removeFillers, default: true),
                               removeRepetitions: bool(SettingsKey.removeRepetitions, default: true))
        }
        nonmutating set {
            defaults.set(newValue.removeFillers, forKey: SettingsKey.removeFillers)
            defaults.set(newValue.removeRepetitions, forKey: SettingsKey.removeRepetitions)
        }
    }

    public func style(for category: AppCategory) -> StylePreset {
        enumValue(SettingsKey.style(for: category), default: category.defaultStyle)
    }

    public func setStyle(_ style: StylePreset, for category: AppCategory) {
        defaults.set(style.rawValue, forKey: SettingsKey.style(for: category))
    }

    public var onboardingCompleted: Bool {
        get { bool(SettingsKey.onboardingCompleted, default: false) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.onboardingCompleted) }
    }

    public var dismissedBanners: Set<String> {
        get { Set(defaults.stringArray(forKey: SettingsKey.dismissedBanners) ?? []) }
        nonmutating set { defaults.set(newValue.sorted(), forKey: SettingsKey.dismissedBanners) }
    }

    public var dictionarySeeded: Bool {
        get { bool(SettingsKey.dictionarySeeded, default: false) }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.dictionarySeeded) }
    }

    public var dismissedDictionarySuggestions: Set<String> {
        get { Set(defaults.stringArray(forKey: SettingsKey.dismissedDictionarySuggestions) ?? []) }
        nonmutating set { defaults.set(newValue.sorted(), forKey: SettingsKey.dismissedDictionarySuggestions) }
    }

    public var dictionarySeedVersion: Int {
        get { defaults.object(forKey: SettingsKey.dictionarySeedVersion) as? Int ?? 1 }
        nonmutating set { defaults.set(newValue, forKey: SettingsKey.dictionarySeedVersion) }
    }

    /// The hotkey state machine configuration derived from General settings.
    public var hotkeyConfiguration: HotkeyConfiguration {
        HotkeyConfiguration(key: dictationKey, handsFreeEnabled: handsFreeEnabled, escapeCancels: escapeCancels)
    }

    /// The engine Recognition settings choose for a purpose.
    public func engine(for purpose: EnginePurpose) -> EngineID {
        switch purpose {
        case .dictation(.mixed): return accurateMixedDictation ? .whisperTurbo : russianEngine
        case .dictation(.russian): return russianEngine
        case .dictation(.english): return englishEngine
        case .meetings: return meetingEngine
        }
    }

    /// The engine that supplies English words to a dictation `dictationEngine` transcribed in
    /// `languageMode`, or nil when none applies: only mixed dictation by a Russian-only
    /// engine gets a helper, and only one whose model can run now.
    public func englishHelper(for dictationEngine: EngineID, languageMode: LanguageMode,
                              status: (EngineID) -> ModelStatus) -> EngineID? {
        guard languageMode == .mixed, dictationEngine.writesOnlyRussian else { return nil }
        return englishHelper.engine(status: status)
    }

    private static func encodedShortcut(_ shortcut: HotkeyShortcut) -> Data? {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try? encoder.encode(shortcut)
    }

    private func bool(_ key: String, default value: Bool) -> Bool {
        defaults.object(forKey: key) == nil ? value : defaults.bool(forKey: key)
    }

    private func enumValue<T: RawRepresentable>(_ key: String, default value: T) -> T where T.RawValue == String {
        defaults.string(forKey: key).flatMap(T.init(rawValue:)) ?? value
    }
}
