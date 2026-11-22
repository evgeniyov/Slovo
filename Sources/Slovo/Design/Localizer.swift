import Foundation
import SlovoCore
import SwiftUI
import os

/// Runtime localization with the in-app language switch (System / English / Русский).
/// Views observe it, so changing the language re-renders the interface without a restart.
@MainActor
final class Localizer: ObservableObject {
    @Published var language: AppLanguage {
        didSet {
            if let defaults {
                defaults.set(language.rawValue, forKey: SettingsKey.appLanguage)
            }
            resolveBundle()
        }
    }

    /// The `.lproj` code strings currently come from.
    private(set) var languageCode = "en"
    private var bundle: Bundle
    /// Nil for a fixed-language localizer that must not touch settings (screen rendering).
    private let defaults: UserDefaults?

    /// The app's resource bundle. The generated `Bundle.module` traps when the bundle
    /// is missing, so the locator is tried first.
    static let resourceBundle: Bundle = {
        let bundle = ResourceBundleLocator.locate("Slovo_Slovo") ?? .module
        SlovoLog.logger("Localizer").info("Resource bundle: \(bundle.bundlePath, privacy: .public)")
        return bundle
    }()

    /// Follows and persists `app.language` in these defaults.
    init(defaults: UserDefaults) {
        self.defaults = defaults
        self.language = AppLanguage.saved(in: defaults)
        self.bundle = Self.resourceBundle
        resolveBundle()
    }

    /// A localizer pinned to one language, for rendering screens.
    init(fixed language: AppLanguage) {
        self.defaults = nil
        self.language = language
        self.bundle = Self.resourceBundle
        resolveBundle()
    }

    /// Locale for formatters and `\.locale`: the interface language in the user's own
    /// region, so an English interface in Russia still shows 24-hour times and local
    /// dates. A fixed-language localizer leaves the region out, so screenshots look the
    /// same on every Mac.
    private(set) var locale = Locale(identifier: "en")

    /// `en_RU`, `ru_DE`: the language code with the region, or the bare code without one.
    static func locale(languageCode: String, region: Locale.Region?) -> Locale {
        guard let region else { return Locale(identifier: languageCode) }
        return Locale(identifier: "\(languageCode)_\(region.identifier)")
    }

    func t(_ key: String) -> String {
        bundle.localizedString(forKey: key, value: key, table: nil)
    }

    func t(_ key: String, _ arguments: CVarArg...) -> String {
        String(format: t(key), locale: locale, arguments: arguments)
    }

    /// A count phrase from `Localizable.stringsdict` ("1 word", «5 слов»). The count
    /// picks the plural form; the phrases print the second argument, the count with
    /// digit grouping ("12,480", «12 480»).
    func plural(_ key: String, _ count: Int) -> String {
        let grouped = count.formatted(IntegerFormatStyle<Int>(locale: locale))
        return String(format: t(key), locale: locale, arguments: [count, grouped])
    }

    private func resolveBundle() {
        let code = language.resolvedCode()
        languageCode = code
        locale = Self.locale(languageCode: code, region: defaults == nil ? nil : Locale.current.region)
        if let path = Self.resourceBundle.path(forResource: code, ofType: "lproj"), let localized = Bundle(path: path) {
            bundle = localized
        } else {
            bundle = Self.resourceBundle
        }
    }
}
