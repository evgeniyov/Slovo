import Foundation

/// Reads and replaces one `meeting.json`: the one file the MCP server may change, shared by
/// every writer so its safety checks live in one place.
///
/// Symbolic links are not followed on read, so a read cannot be aimed outside the data
/// folder; the write is atomic, the way `MeetingStore` saves, so a reader never sees a
/// half-written file. A change goes through `update`, which holds the meeting's
/// `MeetingFileLock` from the read to the write, the same lock the app's saves take.
enum MeetingRecordFile {
    /// Why `update` changed nothing.
    enum UpdateError: Error, Equatable {
        /// `meeting.json` is missing, is not a regular file, or cannot be decoded.
        case unreadable
        /// The lock could not be taken or the file could not be replaced.
        case failed
    }

    /// The record fresh from disk, with its id set from the folder name, or nil when the
    /// file is missing, is not a regular file, is too large, or cannot be decoded.
    static func read(directories: AppDirectories, id: UUID) -> MeetingRecord? {
        guard let directory = MCPMeetingDirectory(directories: directories, folderName: id.uuidString) else { return nil }
        return read(directory: directory, id: id)
    }

    private static func read(directory: MCPMeetingDirectory, id: UUID) -> MeetingRecord? {
        guard let file = directory.read(MeetingArchive.recordFileName),
              var record = try? JSONDecoder().decode(MeetingRecord.self, from: file.data) else { return nil }
        // The folder name is what the app builds every other path from.
        record.id = id
        return record
    }

    static func write(_ record: MeetingRecord, directories: AppDirectories) throws {
        guard let directory = MCPMeetingDirectory(directories: directories, folderName: record.id.uuidString) else {
            throw UpdateError.unreadable
        }
        try directory.writeRecord(encoder.encode(record))
    }

    /// Reads the record, lets `change` edit it and replaces the file when `change` reports a
    /// change, all under the meeting's lock, so a save of the app cannot land in between and
    /// be undone, nor undo this.
    static func update<Result>(directories: AppDirectories, id: UUID, timeout: TimeInterval = MeetingFileLock.defaultTimeout,
                               _ change: (inout MeetingRecord) -> (result: Result, changed: Bool)) throws -> Result {
        let outcome: Swift.Result<Result, UpdateError>
        guard let directory = MCPMeetingDirectory(directories: directories, folderName: id.uuidString) else {
            throw UpdateError.unreadable
        }
        do {
            outcome = try MeetingFileLock.withLock(directoryDescriptor: directory.descriptor,
                                                   meetingFolder: directories.meetingFolder(id: id), timeout: timeout) {
                guard var record = read(directory: directory, id: id) else {
                    return .failure(.unreadable)
                }
                let (result, changed) = change(&record)
                guard changed else { return .success(result) }
                do {
                    try directory.writeRecord(encoder.encode(record))
                } catch {
                    logger.error("Replacing meeting.json failed: \(error.localizedDescription, privacy: .public)")
                    return .failure(.failed)
                }
                return .success(result)
            }
        } catch {
            // No lock: a missing meeting reads as one, anything else is a failure to write.
            logger.error("The meeting's lock could not be taken: \(String(describing: error), privacy: .public)")
            throw read(directory: directory, id: id) == nil ? UpdateError.unreadable : UpdateError.failed
        }
        return try outcome.get()
    }

    private static let logger = SlovoLog.logger("MCP")

    /// The encoder `MeetingStore` uses, so a file the app saved and a file written here stay
    /// byte-for-byte comparable.
    private static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }
}
