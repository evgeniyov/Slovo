import Foundation

/// A meeting as the MCP server read it, with what identifies that version of the file.
public struct ArchivedMeeting: Sendable {
    public var record: MeetingRecord
    /// Modification date of the file the record came from.
    public var recordModified: Date
    /// Size in bytes of the file the record came from.
    public var recordSize: Int

    public init(record: MeetingRecord, recordModified: Date, recordSize: Int) {
        self.record = record
        self.recordModified = recordModified
        self.recordSize = recordSize
    }
}

public protocol MeetingArchiveReading: AnyObject {
    /// Newest first; skips folders without a readable record or transcript.
    func meetings() -> [ArchivedMeeting]
    func meeting(id: UUID) -> ArchivedMeeting?
    /// The creation date of the folder named by this id when it exists but neither its
    /// record nor its transcript can be read; nil otherwise. Lets a tool say "damaged"
    /// instead of "not found" for a meeting the user can still see in Slovo.
    func unreadableMeetingDate(id: UUID) -> Date?
}

extension MeetingArchiveReading {
    public func unreadableMeetingDate(id: UUID) -> Date? {
        nil
    }
}

/// Reads `Meetings/<UUID>/` without ever changing anything: no repairs, renames or
/// writes, unlike `MeetingStore`, which the running app owns. A damaged `meeting.json`
/// falls back to parsing `transcript.md` in memory.
///
/// Files are opened read-only without following symbolic links, so every read stays
/// inside the data folder. The app saves atomically (a temporary file, then a rename),
/// so a read sees either the old or the new file.
public final class MeetingArchive: MeetingArchiveReading {
    /// Larger record files are skipped.
    public static let maximumFileBytes = 32 << 20

    static let recordFileName = "meeting.json"
    static let transcriptFileName = "transcript.md"

    public let directories: AppDirectories

    private let lock = NSLock()
    /// Parsed files by path, valid while the file's identity is unchanged; failures are
    /// cached too, so a damaged file is not decoded again on every request.
    private var cache: [String: CacheEntry] = [:]
    private let logger = SlovoLog.logger("MCP")

    public init(directories: AppDirectories) {
        self.directories = directories
    }

    public func meetings() -> [ArchivedMeeting] {
        lock.lock()
        defer { lock.unlock() }
        var usedPaths: Set<String> = []
        let loaded = meetingFolders().compactMap { folder in
            load(id: folder.id, folder: folder.url, usedPaths: &usedPaths)
        }
        // Forget meetings that were deleted, so a long session does not keep them in memory.
        cache = cache.filter { usedPaths.contains($0.key) }
        return loaded.sorted(by: Self.isNewer)
    }

    public func meeting(id: UUID) -> ArchivedMeeting? {
        lock.lock()
        defer { lock.unlock() }
        guard let folder = meetingFolders().first(where: { $0.id == id }) else { return nil }
        var usedPaths: Set<String> = []
        return load(id: id, folder: folder.url, usedPaths: &usedPaths)
    }

    public func unreadableMeetingDate(id: UUID) -> Date? {
        lock.lock()
        defer { lock.unlock() }
        guard let folder = meetingFolders().first(where: { $0.id == id }) else { return nil }
        var usedPaths: Set<String> = []
        guard load(id: id, folder: folder.url, usedPaths: &usedPaths) == nil else { return nil }
        var info = stat()
        guard lstat(folder.url.path, &info) == 0 else { return nil }
        return FileStamp(info).created
    }

    /// Newest first; equal dates keep a stable order by id, as the app's list does.
    static func isNewer(_ left: ArchivedMeeting, _ right: ArchivedMeeting) -> Bool {
        if left.record.createdAt == right.record.createdAt {
            return left.record.id.uuidString < right.record.id.uuidString
        }
        return left.record.createdAt > right.record.createdAt
    }

    // MARK: - Folders and files

    private struct FileStamp: Equatable {
        var device: Int32
        var inode: UInt64
        var size: Int64
        var modifiedSeconds: Int
        var modifiedNanoseconds: Int
        var createdSeconds: Int
        var createdNanoseconds: Int

        init(_ info: stat) {
            device = info.st_dev
            inode = info.st_ino
            size = info.st_size
            modifiedSeconds = info.st_mtimespec.tv_sec
            modifiedNanoseconds = info.st_mtimespec.tv_nsec
            createdSeconds = info.st_birthtimespec.tv_sec
            createdNanoseconds = info.st_birthtimespec.tv_nsec
        }

        var modified: Date {
            Date(timeIntervalSince1970: TimeInterval(modifiedSeconds) + TimeInterval(modifiedNanoseconds) / 1e9)
        }

        var created: Date {
            Date(timeIntervalSince1970: TimeInterval(createdSeconds) + TimeInterval(createdNanoseconds) / 1e9)
        }
    }

    private struct CacheEntry {
        let stamp: FileStamp
        let meeting: ArchivedMeeting?
    }

    /// Real directories named by a meeting id; symbolic links are not followed.
    private func meetingFolders() -> [(id: UUID, url: URL)] {
        let root = directories.meetingsFolder
        var rootInfo = stat()
        guard lstat(root.path, &rootInfo) == 0, (rootInfo.st_mode & S_IFMT) == S_IFDIR else { return [] }
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: root.path) else { return [] }
        var seen: Set<UUID> = []
        var folders: [(id: UUID, url: URL)] = []
        for name in names.sorted() {
            guard let id = UUID(uuidString: name), !seen.contains(id) else { continue }
            let url = root.appendingPathComponent(name, isDirectory: true)
            var info = stat()
            guard lstat(url.path, &info) == 0, (info.st_mode & S_IFMT) == S_IFDIR else { continue }
            seen.insert(id)
            folders.append((id, url))
        }
        return folders
    }

    private func load(id: UUID, folder: URL, usedPaths: inout Set<String>) -> ArchivedMeeting? {
        let shortID = String(id.uuidString.lowercased().prefix(8))
        let recordPath = folder.appendingPathComponent(Self.recordFileName).path
        let fromRecord = parsedFile(at: recordPath, shortID: shortID, usedPaths: &usedPaths) { data, stamp in
            guard var record = try? JSONDecoder().decode(MeetingRecord.self, from: data) else {
                self.logger.error("Meeting \(shortID, privacy: .private): meeting.json cannot be decoded")
                return nil
            }
            // The folder name is what the app builds every other path from.
            record.id = id
            return ArchivedMeeting(record: record, recordModified: stamp.modified, recordSize: Int(stamp.size))
        }
        if let fromRecord {
            return fromRecord
        }

        let transcriptPath = folder.appendingPathComponent(Self.transcriptFileName).path
        // The folder is made when the meeting starts and never replaced. `transcript.md` is
        // rewritten, as a new file, on every save of the app, so its own creation date would
        // make an old meeting look new and slip past the age limit Claude is held to.
        var folderInfo = stat()
        let folderCreated = lstat(folder.path, &folderInfo) == 0 ? FileStamp(folderInfo).created : nil
        return parsedFile(at: transcriptPath, shortID: shortID, usedPaths: &usedPaths) { data, stamp in
            guard let text = String(data: data, encoding: .utf8) else { return nil }
            let parsed = MeetingStore.parseTranscript(text)
            let title = parsed.title?.trimmingCharacters(in: .whitespaces)
            guard title?.isEmpty == false || !parsed.segments.isEmpty else { return nil }
            let created = folderCreated.map { min($0, stamp.created) } ?? stamp.created
            let record = MeetingRecord(
                id: id, title: title.flatMap { $0.isEmpty ? nil : $0 } ?? MeetingTitles.defaultTitle(for: created, language: .system),
                createdAt: created, duration: parsed.segments.map(\.end).max() ?? 0, status: .transcribed,
                hasSystemAudio: parsed.segments.contains { $0.speaker == .others }, hasAudio: false,
                segments: parsed.segments, speakers: parsed.speakers, highlights: parsed.highlights)
            return ArchivedMeeting(record: record, recordModified: stamp.modified, recordSize: Int(stamp.size))
        }
    }

    /// The parsed contents of a regular file, from the cache when the file is unchanged.
    private func parsedFile(at path: String, shortID: String, usedPaths: inout Set<String>,
                            parse: (Data, FileStamp) -> ArchivedMeeting?) -> ArchivedMeeting? {
        var info = stat()
        guard lstat(path, &info) == 0 else { return nil }
        usedPaths.insert(path)
        guard (info.st_mode & S_IFMT) == S_IFREG else { return nil }
        let stamp = FileStamp(info)
        if let entry = cache[path], entry.stamp == stamp {
            return entry.meeting
        }
        guard let (data, openedStamp) = readFile(at: path, shortID: shortID) else {
            cache[path] = CacheEntry(stamp: stamp, meeting: nil)
            return nil
        }
        let meeting = parse(data, openedStamp)
        cache[path] = CacheEntry(stamp: openedStamp, meeting: meeting)
        return meeting
    }

    /// Reads a whole file through one read-only descriptor, so the contents and the
    /// identity describe the same file even when the app replaces it meanwhile.
    private func readFile(at path: String, shortID: String) -> (Data, FileStamp)? {
        let url = URL(fileURLWithPath: path)
        guard let directory = MCPMeetingDirectory(directories: directories,
                                                   folderName: url.deletingLastPathComponent().lastPathComponent),
              let file = directory.read(url.lastPathComponent) else { return nil }
        return (file.data, FileStamp(file.info))
    }
}
