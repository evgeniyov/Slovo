import Foundation

/// Why notes could not be written.
public enum MeetingNotesWriteError: Error, Equatable {
    /// `meeting.json` is missing, is not a regular file, or cannot be decoded.
    case unreadable
    /// The file could not be replaced.
    case failed
}

/// Writes what Claude concluded into one meeting.
public protocol MeetingNotesWriting: AnyObject {
    /// Replaces the meeting's notes, or removes them when `notes` is nil, and returns what
    /// the file holds afterwards.
    @discardableResult
    func write(_ notes: MeetingNotes?, to id: UUID) throws -> MeetingNotes?
}

/// The one thing the MCP server may change in Slovo's data folder: the `claudeNotes` of
/// one `meeting.json`.
///
/// The server runs in its own process while the app may be running too, and the app owns
/// the file. So the record is read again here immediately before it is replaced, under the
/// meeting's `MeetingFileLock`, and only the notes are changed: every other field keeps
/// whatever the app last saved. The app does the mirror image of this under the same lock
/// and keeps whatever notes the file holds whenever it saves (`MeetingStore.write`), so
/// neither process drops the other's work.
///
/// `transcript.md` is left alone. It is derived from the record, the app rewrites it on the
/// meeting's next save, and a second writer of it would be a second way to damage it.
public final class MeetingNotesFile: MeetingNotesWriting {
    public let directories: AppDirectories

    private let lock = NSLock()
    private let logger = SlovoLog.logger("MCP")

    public init(directories: AppDirectories) {
        self.directories = directories
    }

    @discardableResult
    public func write(_ notes: MeetingNotes?, to id: UUID) throws -> MeetingNotes? {
        lock.lock()
        defer { lock.unlock() }

        let written = notes.flatMap { $0.isEmpty ? nil : $0 }
        do {
            // Read, compare and replace under the meeting's lock, which the app's saves take
            // too, so neither side's change can land between the other's read and write.
            return try MeetingRecordFile.update(directories: directories, id: id) { record -> (result: MeetingNotes?, changed: Bool) in
                // What the notes say is weighed, never when they were written: the server
                // stamps every call with the current time, so comparing that too would
                // rewrite the file for notes saying exactly what it already says, move its
                // modification date and invalidate the transcript cursors that key on it.
                // Notes that say the same thing keep the date they were first written.
                guard !Self.saysTheSame(record.claudeNotes, written) else { return (record.claudeNotes, false) }
                record.claudeNotes = written
                return (written, true)
            }
        } catch MeetingRecordFile.UpdateError.unreadable {
            throw MeetingNotesWriteError.unreadable
        } catch {
            logger.error("Writing notes into a meeting failed")
            throw MeetingNotesWriteError.failed
        }
    }

    /// True when both sides hold the same notes, whenever each of them was written.
    private static func saysTheSame(_ left: MeetingNotes?, _ right: MeetingNotes?) -> Bool {
        guard let left, let right else { return left == nil && right == nil }
        return left.summary == right.summary && left.decisions == right.decisions
            && left.tasks == right.tasks && left.author == right.author
    }
}
