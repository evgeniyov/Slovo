import Foundation

/// Where Slovo keeps its data. Stores take this instead of reading global paths,
/// so tests run against a temporary directory.
public struct AppDirectories: Hashable, Sendable {
    public let base: URL

    public init(base: URL) {
        self.base = base
    }

    /// `~/Library/Application Support/Slovo`.
    public static var standard: AppDirectories {
        AppDirectories(base: ModelStore.root.deletingLastPathComponent())
    }

    public var historyFile: URL { base.appendingPathComponent("history.json") }
    public var dictionaryFile: URL { base.appendingPathComponent("dictionary.json") }
    public var snippetsFile: URL { base.appendingPathComponent("snippets.json") }
    public var notesFile: URL { base.appendingPathComponent("notes.json") }
    public var appCategoriesFile: URL { base.appendingPathComponent("app-categories.json") }
    /// `Recordings/<yyyy-mm-dd>/<uuid>.wav`.
    public var recordingsFolder: URL { base.appendingPathComponent("Recordings", isDirectory: true) }
    /// `Meetings/<id>/`.
    public var meetingsFolder: URL { base.appendingPathComponent("Meetings", isDirectory: true) }
    /// Speech models; read-only for everything except `ModelManager`.
    public var modelsFolder: URL { base.appendingPathComponent("Models", isDirectory: true) }
    /// Audio dropped here becomes a meeting. Whatever fills it — a sync tool, Finder — is
    /// none of Slovo's business, which keeps the import path independent of any transport.
    public var inboxFolder: URL { base.appendingPathComponent("Inbox", isDirectory: true) }

    /// The file a history entry's `audioFileName` points to.
    public func recordingURL(relativePath: String) -> URL {
        recordingsFolder.appendingPathComponent(relativePath)
    }

    public func meetingFolder(id: UUID) -> URL {
        meetingsFolder.appendingPathComponent(id.uuidString, isDirectory: true)
    }

    /// Creates the base, recordings, meetings and inbox folders.
    public func createFolders() throws {
        for folder in [base, recordingsFolder, meetingsFolder, inboxFolder] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        }
    }
}
