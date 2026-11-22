import Foundation

/// Why a highlight change could not be written.
public enum MeetingHighlightsWriteError: Error, Equatable {
    /// `meeting.json` is missing, is not a regular file, or cannot be decoded.
    case unreadable
    /// The file could not be replaced.
    case failed
}

/// The edited record and the highlight `mark` touched. `created` is false when a highlight
/// already sat at that time with that note, so the call changed nothing.
public struct HighlightMarkResult: Sendable {
    public let record: MeetingRecord
    public let highlight: MeetingHighlight
    public let created: Bool
}

/// The edited record `unmark` produced, and whether a highlight with that id was there to
/// remove.
public struct HighlightUnmarkResult: Sendable {
    public let record: MeetingRecord
    public let removed: Bool
}

/// Marks or unmarks an important moment (`highlights`) on one meeting, from Claude or
/// another MCP client rather than from the app's own star or shortcut.
public protocol MeetingHighlightsWriting: AnyObject {
    @discardableResult
    func mark(time: TimeInterval, note: String?, on id: UUID, now: Date) throws -> HighlightMarkResult
    @discardableResult
    func unmark(_ highlightID: UUID, on id: UUID) throws -> HighlightUnmarkResult
}

/// The other thing the MCP server may change in Slovo's data folder besides
/// `MeetingNotesFile`: the `highlights` of one `meeting.json`.
///
/// Same reasoning throughout: the record is read again here immediately before it is
/// replaced, under the meeting's `MeetingFileLock`, so nothing the app saved meanwhile is
/// lost, and only `highlights` is touched.
/// `transcript.md` is left alone; the app rewrites its ★ marks from the record on its next
/// save, the same way it picks up notes written here.
public final class MeetingHighlightsFile: MeetingHighlightsWriting {
    public let directories: AppDirectories

    private let lock = NSLock()
    private static let logger = SlovoLog.logger("MCP")

    public init(directories: AppDirectories) {
        self.directories = directories
    }

    @discardableResult
    public func mark(time: TimeInterval, note: String?, on id: UUID, now: Date) throws -> HighlightMarkResult {
        lock.lock()
        defer { lock.unlock() }
        return try Self.mapped {
            try MeetingRecordFile.update(directories: directories, id: id) { record -> (result: HighlightMarkResult, changed: Bool) in
                let (edited, highlight, created) = MeetingEdits.markHighlight(at: time, note: note, in: record, now: now)
                record = edited
                return (HighlightMarkResult(record: edited, highlight: highlight, created: created), created)
            }
        }
    }

    @discardableResult
    public func unmark(_ highlightID: UUID, on id: UUID) throws -> HighlightUnmarkResult {
        lock.lock()
        defer { lock.unlock() }
        return try Self.mapped {
            try MeetingRecordFile.update(directories: directories, id: id) { record -> (result: HighlightUnmarkResult, changed: Bool) in
                let (edited, removed) = MeetingEdits.unmarkHighlight(highlightID, in: record)
                record = edited
                return (HighlightUnmarkResult(record: edited, removed: removed), removed)
            }
        }
    }

    /// The shared file errors as this writer's own.
    private static func mapped<Result>(_ body: () throws -> Result) throws -> Result {
        do {
            return try body()
        } catch MeetingRecordFile.UpdateError.unreadable {
            throw MeetingHighlightsWriteError.unreadable
        } catch {
            logger.error("Changing a meeting's highlights failed")
            throw MeetingHighlightsWriteError.failed
        }
    }
}
