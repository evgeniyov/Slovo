import CoreFoundation
import Foundation

/// Slovo's settings as a second process sees them.
///
/// The MCP process reads the app's preferences domain through `CFPreferences`, synchronized
/// before every read, so a switch changed in the running app applies to the next request,
/// and the same code works for the bundled executable and a bare build product (whose
/// `UserDefaults.standard` would be a different domain). Missing keys use the
/// `AppSettings` defaults. Nothing is ever written.
public struct SlovoPreferences: Sendable {
    public static let appDomain = "local.slovo.Slovo"

    /// The preferences domain; tests pass a throwaway suite name.
    public let domain: String

    public init(domain: String = SlovoPreferences.appDomain) {
        self.domain = domain
    }

    /// `integrations.mcp.enabled`, `integrations.mcp.writeEnabled` and
    /// `integrations.mcp.maxAgeDays`, re-read from disk. An age limit that is not one of
    /// the offered choices means all meetings, as it does in the app: a bad value never
    /// hides meetings the user expects Claude to see, and the access switch is what
    /// decides whether Claude sees any.
    public func access() -> MCPAccess {
        CFPreferencesAppSynchronize(domain as CFString)
        let enabled = Self.bool(value(SettingsKey.mcpEnabled)) ?? false
        let canWrite = Self.bool(value(SettingsKey.mcpWriteEnabled)) ?? false
        let days = Self.int(value(SettingsKey.mcpMaxAgeDays)) ?? 0
        return MCPAccess(enabled: enabled, canWrite: canWrite,
                         maxAgeDays: AppSettings.normalizedMCPMaxAgeDays(days))
    }

    /// `app.language`, re-read from disk; unknown or missing values mean `.system`.
    public func language() -> AppLanguage {
        CFPreferencesAppSynchronize(domain as CFString)
        guard let raw = value(SettingsKey.appLanguage) as? String else { return .system }
        return AppLanguage(rawValue: raw) ?? .system
    }

    private func value(_ key: String) -> Any? {
        CFPreferencesCopyAppValue(key as CFString, domain as CFString)
    }

    /// Accepts what `UserDefaults.bool(forKey:)` accepts: numbers and "YES"/"true"/"1".
    static func bool(_ value: Any?) -> Bool? {
        switch value {
        case let number as NSNumber:
            return number.boolValue
        case let text as String:
            return ["yes", "true", "1"].contains(text.trimmingCharacters(in: .whitespaces).lowercased())
        default:
            return nil
        }
    }

    /// Accepts what `UserDefaults.integer(forKey:)` accepts: numbers and numeric strings.
    static func int(_ value: Any?) -> Int? {
        switch value {
        case let number as NSNumber:
            return number.intValue
        case let text as String:
            return Int(text.trimmingCharacters(in: .whitespaces))
        default:
            return nil
        }
    }
}
