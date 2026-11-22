import Foundation

/// SlovoCore's string tables. Each module owns one table so modules never edit the
/// same file.
public enum CoreTable: String, CaseIterable, Sendable {
    case engines = "Engines"
    case audio = "Audio"
    case input = "Input"
    case storage = "Storage"
    case dictation = "Dictation"
    case meetings = "Meetings"
    case diarization = "Diarization"
    case integration = "Integration"
}

/// User-visible strings of SlovoCore, in the language the app shows.
///
/// Engines call this from background queues, so it reads the `app.language` value
/// on every lookup instead of observing the app's `@MainActor` localizer.
public enum CoreStrings {
    /// The localized string for a key, or the key itself when it is missing.
    public static func string(_ key: String, table: CoreTable, language: AppLanguage? = nil) -> String {
        let bundle = localizedBundle(for: language ?? AppLanguage.saved())
        return bundle.localizedString(forKey: key, value: key, table: table.rawValue)
    }

    /// A localized format string filled with arguments, formatted for the same language.
    public static func format(_ key: String, table: CoreTable, language: AppLanguage? = nil,
                              _ arguments: CVarArg...) -> String {
        let resolved = language ?? AppLanguage.saved()
        let pattern = string(key, table: table, language: resolved)
        return String(format: pattern, locale: Locale(identifier: resolved.resolvedCode()), arguments: arguments)
    }

    /// SlovoCore's resource bundle, also for non-localized resources such as the
    /// Dictionary seed vocabulary.
    public static var resourceBundle: Bundle {
        ResourceBundleLocator.coreBundle
    }

    private static func localizedBundle(for language: AppLanguage) -> Bundle {
        let base = ResourceBundleLocator.coreBundle
        let code = language.resolvedCode()
        guard let path = base.path(forResource: code, ofType: "lproj"), let bundle = Bundle(path: path) else {
            return base
        }
        return bundle
    }
}

/// Finds SwiftPM resource bundles without trapping.
///
/// The generated `Bundle.module` stops the process when its bundle is missing, and
/// depending on the build system it looks either next to the executable or in the
/// app's `Contents/Resources`. Checking both, plus the test bundle's folder, keeps
/// the app bundle, `swift run` and `swift test` working.
public enum ResourceBundleLocator {
    static let coreBundle: Bundle = locate("Slovo_SlovoCore") ?? .module

    /// The bundle named `<package>_<target>` from the usual places, or nil.
    public static func locate(_ name: String) -> Bundle? {
        let fileName = name + ".bundle"
        var candidates: [URL] = []
        if let resources = Bundle.main.resourceURL {
            candidates.append(resources)
        }
        candidates.append(Bundle.main.bundleURL)
        let finder = Bundle(for: BundleFinder.self)
        if let resources = finder.resourceURL {
            candidates.append(resources)
        }
        // A test bundle sits in the products folder next to the resource bundles.
        candidates.append(finder.bundleURL.deletingLastPathComponent())
        for folder in candidates {
            if let bundle = Bundle(url: folder.appendingPathComponent(fileName)) {
                return bundle
            }
        }
        return nil
    }

    private final class BundleFinder {}
}
