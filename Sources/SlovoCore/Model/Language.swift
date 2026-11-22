import Foundation

/// The interface language choice stored under `SettingsKey.appLanguage`.
public enum AppLanguage: String, CaseIterable, Codable, Sendable, Identifiable {
    case system
    case en
    case ru

    public var id: String { rawValue }

    /// Localizations shipped in every string table.
    public static let supportedCodes = ["en", "ru"]

    /// The `.lproj` code to read strings from: the explicit choice, or the first
    /// preferred system language Slovo has, falling back to English.
    public func resolvedCode(preferredLanguages: [String] = Locale.preferredLanguages) -> String {
        switch self {
        case .en: return "en"
        case .ru: return "ru"
        case .system:
            for identifier in preferredLanguages {
                let code = Locale(identifier: identifier).language.languageCode?.identifier
                    ?? String(identifier.prefix(2))
                if Self.supportedCodes.contains(code) {
                    return code
                }
            }
            return "en"
        }
    }

    /// Reads the saved choice; unknown or missing values mean `.system`.
    public static func saved(in defaults: UserDefaults = .standard) -> AppLanguage {
        AppLanguage(rawValue: defaults.string(forKey: SettingsKey.appLanguage) ?? "") ?? .system
    }
}

/// Which languages the user dictates in; picks the engine through Recognition settings.
public enum LanguageMode: String, CaseIterable, Codable, Sendable, Identifiable {
    /// Russian speech with English terms, the default.
    case mixed
    case russian
    case english

    public var id: String { rawValue }
}
