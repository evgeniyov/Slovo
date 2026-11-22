import Foundation

/// App categories that each get their own writing style.
public enum AppCategory: String, CaseIterable, Codable, Sendable, Identifiable {
    case personal
    case work
    case email
    case other

    public var id: String { rawValue }

    /// The style a category starts with.
    public var defaultStyle: StylePreset {
        switch self {
        case .personal: return .casual
        case .work, .email, .other: return .formal
        }
    }
}

/// How the final text is capitalized and punctuated.
public enum StylePreset: String, CaseIterable, Codable, Sendable, Identifiable {
    /// Sentence case, punctuation kept, a final period added when missing.
    case formal
    /// Sentence case, the final period of the last sentence dropped.
    case casual
    /// Lowercase sentence starts except terms and names, final periods dropped.
    case veryCasual

    public var id: String { rawValue }
}

/// A user's choice to put an app into a category, overriding the built-in defaults.
public struct AppCategoryAssignment: Codable, Hashable, Sendable, Identifiable {
    public var bundleID: String
    public var appName: String
    public var category: AppCategory

    public var id: String { bundleID }

    public init(bundleID: String, appName: String, category: AppCategory) {
        self.bundleID = bundleID
        self.appName = appName
        self.category = category
    }
}

/// Auto cleanup toggles from the Style screen.
public struct AutoCleanupOptions: Codable, Hashable, Sendable {
    /// Filler words such as «э», «ээ», "um", "uh".
    public var removeFillers: Bool
    /// Immediate word repetitions such as «я я».
    public var removeRepetitions: Bool

    public init(removeFillers: Bool = true, removeRepetitions: Bool = true) {
        self.removeFillers = removeFillers
        self.removeRepetitions = removeRepetitions
    }

    public static let `default` = AutoCleanupOptions()
    public static let off = AutoCleanupOptions(removeFillers: false, removeRepetitions: false)
}

/// The frontmost application at the moment a dictation started.
public struct AppIdentity: Codable, Hashable, Sendable {
    public var bundleID: String
    public var name: String

    public init(bundleID: String, name: String) {
        self.bundleID = bundleID
        self.name = name
    }
}
