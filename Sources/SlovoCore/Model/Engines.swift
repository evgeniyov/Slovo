import Foundation

/// Every speech engine the app can use. Raw values match the bench engine ids and
/// are what history entries and settings store.
public enum EngineID: String, CaseIterable, Codable, Sendable, Identifiable {
    case gigaamRNNT = "gigaam-v3-rnnt"
    case gigaamCTC = "gigaam-v3-ctc"
    case parakeet = "parakeet-v3"
    case appleDictation = "apple-dictation"
    /// Primary engine for accurate mixed dictation, also usable as an English helper.
    case whisperTurbo = "whisper-large-v3-turbo"

    public var id: String { rawValue }

    /// Engines that can transcribe a recording on their own, including "Run again".
    public static let transcriptionEngines: [EngineID] = allCases.filter { !$0.isHelperOnly }

    /// Choices for Russian and mixed speech, recommended first.
    public static let russianChoices: [EngineID] = [.gigaamRNNT, .gigaamCTC, .parakeet, .appleDictation]
    /// Choices for English speech, recommended first.
    public static let englishChoices: [EngineID] = [.parakeet, .gigaamRNNT, .appleDictation]
    /// Choices for meeting transcription, default first.
    public static let meetingChoices: [EngineID] = [.gigaamCTC, .parakeet]

    /// Product names are the same in every interface language.
    public var displayName: String {
        switch self {
        case .gigaamRNNT: return "GigaAM v3 RNNT"
        case .gigaamCTC: return "GigaAM v3 CTC"
        case .parakeet: return "Parakeet v3"
        case .appleDictation: return "Apple Dictation"
        case .whisperTurbo: return "Whisper large-v3 turbo"
        }
    }

    /// Kept for callers that distinguish standalone engines from helper-only models.
    public var isHelperOnly: Bool {
        false
    }

    /// GigaAM knows only Russian and garbles English, so mixed dictation with it gets an
    /// English helper.
    public var writesOnlyRussian: Bool {
        self == .gigaamRNNT || self == .gigaamCTC
    }

    /// False for Apple Dictation, whose language assets macOS manages.
    public var usesDownloadedModel: Bool {
        self != .appleDictation
    }

    /// The model folder relative to `ModelStore.root`, or nil when macOS manages the model.
    public var modelFolder: String? {
        switch self {
        case .gigaamRNNT: return "gigaam-v3/sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16"
        case .gigaamCTC: return "gigaam-v3/sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16"
        case .parakeet: return "parakeet-v3"
        case .appleDictation: return nil
        case .whisperTurbo: return "whisper-v3-turbo"
        }
    }
}

/// Which multilingual model supplies English words to mixed dictation, stored under
/// `SettingsKey.englishHelper`. GigaAM keeps writing the text; `MixedLanguageMerger` puts
/// the helper's English in only where GigaAM was unsure.
public enum EnglishHelperChoice: String, CaseIterable, Codable, Sendable, Identifiable {
    /// Whisper when its model is downloaded, else Parakeet when its model is, else none.
    case auto
    case parakeet
    case whisper
    case off

    public var id: String { rawValue }

    /// The helper this choice uses while models have these statuses; nil when none can run.
    public func engine(status: (EngineID) -> ModelStatus) -> EngineID? {
        switch self {
        case .auto:
            return [EngineID.whisperTurbo, .parakeet].first { status($0).isUsable }
        case .parakeet:
            return status(.parakeet).isUsable ? .parakeet : nil
        case .whisper:
            return status(.whisperTurbo).isUsable ? .whisperTurbo : nil
        case .off:
            return nil
        }
    }
}

/// What an engine is chosen for.
public enum EnginePurpose: Equatable, Sendable {
    case dictation(LanguageMode)
    case meetings
}

/// Whether an engine's model files are on disk and usable.
public enum ModelStatus: Equatable, Sendable {
    /// Not checked yet.
    case unknown
    case ready(diskBytes: Int64)
    case notDownloaded(downloadBytes: Int64)
    /// Progress from 0 to 1.
    case downloading(progress: Double)
    /// Files are present but truncated or unreadable; the reason is already localized.
    case damaged(reason: String)
    /// Apple Dictation: macOS manages the assets; `available` is false when this macOS
    /// version or the language assets cannot run it.
    case systemManaged(available: Bool)

    public var isUsable: Bool {
        switch self {
        case .ready: return true
        case let .systemManaged(available): return available
        default: return false
        }
    }
}

/// Whether an engine is loaded in memory.
public enum EngineLoadState: Equatable, Sendable {
    case unloaded
    case loading
    case ready
    /// The message is already localized.
    case failed(String)
}

/// How long a loaded engine may stay idle before it is released.
public enum EngineIdleTimeout: Int, CaseIterable, Codable, Sendable, Identifiable {
    case minutes5 = 5
    case minutes15 = 15
    case minutes60 = 60
    case never = 0

    public var id: Int { rawValue }

    /// Nil means the engine is never unloaded.
    public var seconds: TimeInterval? {
        self == .never ? nil : TimeInterval(rawValue * 60)
    }
}
