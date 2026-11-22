import Combine
import Foundation

/// Dictations of one calendar day, for the Home list.
public struct HistoryDayGroup: Hashable, Sendable {
    /// Start of the day.
    public var day: Date
    /// Newest first.
    public var entries: [HistoryEntry]

    public init(day: Date, entries: [HistoryEntry]) {
        self.day = day
        self.entries = entries
    }
}

/// `history.json` plus the WAV files history entries point to.
@MainActor
public final class HistoryStore: ObservableObject, HistoryStoring {
    @Published public private(set) var entries: [HistoryEntry] = []

    public let directories: AppDirectories
    /// Set when `history.json` was damaged and moved aside at launch; localized.
    public let loadWarning: String?

    private let storage: StoreFile<[HistoryEntry]>
    private let logger = SlovoLog.logger("History")

    /// Loads the file at once; a damaged file is moved aside and the store starts empty.
    public init(directories: AppDirectories) {
        self.directories = directories
        storage = StoreFile(url: directories.historyFile)
        loadWarning = storage.loadWarning
        entries = Self.newestFirst(storage.initialValue ?? [])
    }

    public func add(_ entry: HistoryEntry) {
        entries.removeAll { $0.id == entry.id }
        let index = entries.firstIndex { $0.date <= entry.date } ?? entries.endIndex
        entries.insert(entry, at: index)
        save()
    }

    public func update(_ entry: HistoryEntry) {
        guard let index = entries.firstIndex(where: { $0.id == entry.id }) else { return }
        let dateChanged = entries[index].date != entry.date
        entries[index] = entry
        if dateChanged {
            entries = Self.newestFirst(entries)
        }
        save()
    }

    public func delete(id: UUID) {
        _ = deleteEntry(id: id)
    }

    /// Removes the entry once its recording is gone. A failed removal leaves it available for retry.
    public func deleteEntry(id: UUID) -> Bool {
        guard let index = entries.firstIndex(where: { $0.id == id }) else { return true }
        if let path = entries[index].audioFileName, !removeRecording(relativePath: path) {
            return false
        }
        entries.remove(at: index)
        save()
        return true
    }

    public func deleteAll() {
        _ = clearHistory()
    }

    /// Includes recordings without an entry. Returns false if removal or saving fails.
    public func clearHistory() -> Bool {
        let fileManager = FileManager.default
        var removedAll = true
        do {
            if try directories.recordingsFolder.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == true {
                return false
            }
            let items = try fileManager.contentsOfDirectory(at: directories.recordingsFolder, includingPropertiesForKeys: nil)
            for item in items {
                if !removeItem(at: item) { removedAll = false }
            }
        } catch where Self.isMissing(error) {
            // No recordings folder.
        } catch {
            logger.error("Could not list recordings: \(error.localizedDescription, privacy: .public)")
            removedAll = false
        }
        let remaining = entries.filter { entry in
            guard let path = entry.audioFileName else { return false }
            do {
                return try recordingURL(relativePath: path) != nil
            } catch {
                return true
            }
        }
        if remaining != entries {
            entries = remaining
            save()
        }
        do {
            // The caller reports incomplete deletion. The failed write stays pending for retry.
            try storage.flush(notifyOnFailure: false)
        } catch {
            return false
        }
        return removedAll && remaining.isEmpty
    }

    public var hasHistoryToDelete: Bool {
        if !entries.isEmpty || storage.hasPendingSave { return true }
        do {
            return try !FileManager.default.contentsOfDirectory(atPath: directories.recordingsFolder.path).isEmpty
        } catch {
            return !Self.isMissing(error)
        }
    }

    public func audioURL(for entry: HistoryEntry) -> URL? {
        guard let path = entry.audioFileName else { return nil }
        return try? recordingURL(relativePath: path)
    }

    public func removeAudio(for id: UUID) {
        guard let index = entries.firstIndex(where: { $0.id == id }), let path = entries[index].audioFileName,
              removeRecording(relativePath: path) else { return }
        entries[index].audioFileName = nil
        save()
    }

    public func flush() throws {
        try storage.flush()
    }

    /// Entries whose text contains the query, ignoring case and ё/е.
    public func search(_ query: String) -> [HistoryEntry] {
        let needle = Self.searchKey(query.trimmingCharacters(in: .whitespacesAndNewlines))
        guard !needle.isEmpty else { return entries }
        return entries.filter { Self.searchKey($0.text).contains(needle) }
    }

    /// Groups entries by day, newest day first.
    public static func groupedByDay(_ entries: [HistoryEntry], calendar: Calendar = .current) -> [HistoryDayGroup] {
        let byDay = Dictionary(grouping: entries) { calendar.startOfDay(for: $0.date) }
        return byDay.keys.sorted(by: >).map { day in
            HistoryDayGroup(day: day, entries: newestFirst(byDay[day] ?? []))
        }
    }

    /// Lowercased with ё folded into е, as Russian text is often typed without ё.
    static func searchKey(_ text: String) -> String {
        text.lowercased().replacingOccurrences(of: "ё", with: "е")
    }

    private static func newestFirst(_ entries: [HistoryEntry]) -> [HistoryEntry] {
        entries.sorted { $0.date > $1.date }
    }

    private func save() {
        storage.scheduleSave(entries)
    }

    /// A plain file for a stored relative path. Check both the lexical and resolved
    /// path: a day folder that is a symlink must not expose or delete external files.
    /// A directory is never a recording, even when its name ends in `.wav`.
    private func recordingURL(relativePath: String) throws -> URL? {
        let folder = directories.recordingsFolder.standardizedFileURL
        let url = directories.recordingURL(relativePath: relativePath).standardizedFileURL
        guard url.path.hasPrefix(folder.path + "/"),
              url.resolvingSymlinksInPath().path.hasPrefix(folder.resolvingSymlinksInPath().path + "/") else {
            logger.error("Ignoring a recording path outside Recordings")
            return nil
        }
        do {
            guard try folder.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink != true else { return nil }
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            return values.isRegularFile == true && values.isSymbolicLink != true ? url : nil
        } catch where Self.isMissing(error) {
            return nil
        }
    }

    private static func isMissing(_ error: Error) -> Bool {
        let error = error as NSError
        return error.domain == NSCocoaErrorDomain
            && (error.code == NSFileNoSuchFileError || error.code == NSFileReadNoSuchFileError)
    }

    private func removeItem(at url: URL) -> Bool {
        do {
            try FileManager.default.removeItem(at: url)
            return true
        } catch where Self.isMissing(error) {
            return true
        } catch {
            logger.error("Could not remove \(url.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    private func removeRecording(relativePath: String) -> Bool {
        let url: URL
        do {
            guard let recording = try recordingURL(relativePath: relativePath) else { return true }
            url = recording
        } catch {
            logger.error("Could not access recording: \(error.localizedDescription, privacy: .public)")
            return false
        }
        guard removeItem(at: url) else { return false }
        let fileManager = FileManager.default
        // Drop the day folder once its last recording is gone.
        let dayFolder = url.deletingLastPathComponent()
        if dayFolder.standardizedFileURL != directories.recordingsFolder.standardizedFileURL,
           let remaining = try? fileManager.contentsOfDirectory(atPath: dayFolder.path),
           remaining.allSatisfy({ $0 == ".DS_Store" }) {
            try? fileManager.removeItem(at: dayFolder)
        }
        return true
    }
}
