import AppKit
import Combine
import Foundation

/// `app-categories.json`: the user's category choices over the built-in defaults.
@MainActor
public final class AppCategoryStore: ObservableObject, AppCategoryStoring {
    @Published public private(set) var assignments: [AppCategoryAssignment] = []

    public let directories: AppDirectories
    /// Set when `app-categories.json` was damaged and moved aside at launch; localized.
    public let loadWarning: String?

    private let storage: StoreFile<[AppCategoryAssignment]>
    /// Installed app names by bundle id, looked up once per id.
    private var installedNames: [String: String?] = [:]
    private let installedNameLookup: (String) -> String?

    public convenience init(directories: AppDirectories) {
        self.init(directories: directories, installedNameLookup: AppCategoryStore.installedAppName)
    }

    /// Tests replace the installed-app lookup so results never depend on this Mac.
    init(directories: AppDirectories, installedNameLookup: @escaping (String) -> String?) {
        self.directories = directories
        self.installedNameLookup = installedNameLookup
        storage = StoreFile(url: directories.appCategoriesFile)
        loadWarning = storage.loadWarning
        assignments = storage.initialValue ?? []
    }

    /// Built-in categories by bundle id (Telegram, Slack, Mail and the rest).
    ///
    /// Bitrix24 and Яндекс Телемост use the ids of the copies installed on this Mac.
    /// Telegram also ships from telegram.org as `com.tdesktop.Telegram`. Spark and
    /// other builds of these apps are caught by `defaultCategoriesByName`.
    public static let defaultCategories: [String: AppCategory] = [
        "ru.keepcoder.Telegram": .personal,
        "org.telegram.desktop": .personal,
        "com.tdesktop.Telegram": .personal,
        "com.apple.MobileSMS": .personal,
        "net.whatsapp.WhatsApp": .personal,
        "com.hnc.Discord": .personal,
        "com.tinyspeck.slackmacgap": .work,
        "com.microsoft.teams2": .work,
        "us.zoom.xos": .work,
        "ru.bitrix.bitrix24desktop": .work,
        "ru.yandex.desktop.telemost": .work,
        "com.apple.mail": .email,
        "com.microsoft.Outlook": .email
    ]

    /// Apps the spec names without a bundle id, matched by normalized app name
    /// (lowercased, dots, dashes and underscores read as spaces) when the bundle id is
    /// not in `defaultCategories`.
    public static let defaultCategoriesByName: [String: AppCategory] = [
        "bitrix24": .work,
        "битрикс24": .work,
        "yandex telemost": .work,
        "яндекс телемост": .work,
        "telemost": .work,
        "телемост": .work,
        "spark": .email,
        "spark desktop": .email,
        "spark mail": .email
    ]

    public func category(forBundleID bundleID: String?) -> AppCategory {
        guard let bundleID, !bundleID.isEmpty else { return .other }
        if let assigned = assignedCategory(bundleID) {
            return assigned
        }
        return Self.defaultCategory(bundleID: bundleID, name: installedName(for: bundleID))
    }

    /// Like `category(forBundleID:)`, using the name the app reported, which also
    /// matches apps that are no longer installed.
    public func category(for app: AppIdentity) -> AppCategory {
        if let assigned = assignedCategory(app.bundleID) {
            return assigned
        }
        let reported = Self.defaultCategory(bundleID: app.bundleID, name: app.name)
        if reported != .other {
            return reported
        }
        return Self.defaultCategory(bundleID: app.bundleID, name: installedName(for: app.bundleID))
    }

    public func setCategory(_ category: AppCategory, for app: AppIdentity) {
        let assignment = AppCategoryAssignment(bundleID: app.bundleID, appName: app.name, category: category)
        if let index = assignments.firstIndex(where: { $0.bundleID == app.bundleID }) {
            guard assignments[index] != assignment else { return }
            assignments[index] = assignment
        } else {
            assignments.append(assignment)
        }
        storage.scheduleSave(assignments)
    }

    public func flush() throws {
        try storage.flush()
    }

    /// Apps in a category among those seen in history, sorted by name.
    ///
    /// Apps the user assigned a category stay listed even after their history is gone.
    public func apps(in category: AppCategory, seenIn history: [HistoryEntry]) -> [AppIdentity] {
        var names: [String: String] = [:]
        for entry in history {
            guard let bundleID = entry.appBundleID, !bundleID.isEmpty, names[bundleID] == nil else { continue }
            let name = entry.appName?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            names[bundleID] = name.isEmpty ? bundleID : name
        }
        for assignment in assignments where names[assignment.bundleID] == nil {
            names[assignment.bundleID] = assignment.appName.isEmpty ? assignment.bundleID : assignment.appName
        }
        return names.map { AppIdentity(bundleID: $0.key, name: $0.value) }
            .filter { self.category(for: $0) == category }
            .sorted { lhs, rhs in
                let order = lhs.name.localizedStandardCompare(rhs.name)
                return order == .orderedSame ? lhs.bundleID < rhs.bundleID : order == .orderedAscending
            }
    }

    /// The built-in category for an app: by bundle id, else by name, else `.other`.
    public static func defaultCategory(bundleID: String?, name: String?) -> AppCategory {
        if let bundleID, let category = defaultCategories[bundleID] {
            return category
        }
        if let name, let category = defaultCategoriesByName[normalizedName(name)] {
            return category
        }
        return .other
    }

    static func normalizedName(_ name: String) -> String {
        var text = name.lowercased()
        if text.hasSuffix(".app") {
            text.removeLast(4)
        }
        let separators = CharacterSet(charactersIn: ".-_").union(.whitespacesAndNewlines)
        return text.components(separatedBy: separators).filter { !$0.isEmpty }.joined(separator: " ")
    }

    private func assignedCategory(_ bundleID: String) -> AppCategory? {
        assignments.first { $0.bundleID == bundleID }?.category
    }

    private func installedName(for bundleID: String) -> String? {
        if let cached = installedNames[bundleID] {
            return cached
        }
        let name = installedNameLookup(bundleID)
        installedNames[bundleID] = name
        return name
    }

    /// The display name of an installed app, from Launch Services.
    nonisolated static func installedAppName(bundleID: String) -> String? {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return nil }
        let bundle = Bundle(url: url)
        let name = bundle?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String
            ?? bundle?.object(forInfoDictionaryKey: "CFBundleName") as? String
        return name ?? url.deletingPathExtension().lastPathComponent
    }
}
