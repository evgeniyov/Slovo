import Foundation

/// The lock both processes take around a read-modify-write of one `meeting.json`.
///
/// The app (`MeetingStore`) and the MCP server (`MeetingNotesFile`, `MeetingHighlightsFile`)
/// each read the record, change their part of it and replace the file. Without a lock
/// shared between the two processes a save of one could land between the read and the
/// write of the other and quietly undo it: a rename, a star or Claude's notes would be
/// gone. The lock is an exclusive `flock` on `Meetings/<id>/.lock`, a hidden file that
/// holds nothing and is left in place; the kernel releases it when its descriptor closes,
/// so a process that dies holding it never blocks the other one.
///
/// Only writers lock. Reads never wait: every save replaces the file atomically, so a
/// reader sees the old or the new file, never half of one.
public enum MeetingFileLock {
    /// The file inside a meeting folder that carries the lock.
    public static let fileName = ".lock"
    /// Holders keep the lock for one decode and one encode of a record, so waiting this long
    /// means the other side is stuck, not busy.
    public static let defaultTimeout: TimeInterval = 5

    /// Why the lock could not be taken.
    public enum LockError: Error, Equatable {
        /// The meeting folder is missing or the lock file cannot be opened.
        case unavailable
        /// Another holder kept it for longer than the timeout.
        case timedOut
    }

    /// Called with the meeting folder right after the lock is taken, before the holder reads
    /// anything; tests pause a holder here to start the other writer. Nil outside tests.
    static var afterLockingForTests: ((URL) -> Void)?

    private static let logger = SlovoLog.logger("MeetingFileLock")
    private static let retryMicroseconds: useconds_t = 5_000

    /// Runs `body` while holding the lock of the meeting in `meetingFolder`; throws
    /// `LockError` without running it when the lock cannot be taken in time.
    public static func withLock<T>(meetingFolder: URL, timeout: TimeInterval = defaultTimeout,
                                   _ body: () throws -> T) throws -> T {
        let descriptor = try acquire(meetingFolder: meetingFolder, timeout: timeout)
        defer { release(descriptor) }
        afterLockingForTests?(meetingFolder)
        return try body()
    }

    /// MCP pins its directory first, so neither opening the lock nor a later read/write
    /// can be redirected by replacing the directory path with a symbolic link.
    static func withLock<T>(directoryDescriptor: Int32, meetingFolder: URL,
                            timeout: TimeInterval = defaultTimeout, _ body: () throws -> T) throws -> T {
        let descriptor = openat(directoryDescriptor, fileName,
                                O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw LockError.unavailable }
        let locked = try lock(descriptor: descriptor, timeout: timeout)
        defer { release(locked) }
        afterLockingForTests?(meetingFolder)
        return try body()
    }

    /// Runs `body` while holding the lock when it can be had, and without it otherwise. For a
    /// save of the app, which must not be dropped because a lock file cannot be created.
    public static func withLockIfPossible<T>(meetingFolder: URL, timeout: TimeInterval = defaultTimeout,
                                             _ body: () throws -> T) rethrows -> T {
        let descriptor: Int32?
        do {
            descriptor = try acquire(meetingFolder: meetingFolder, timeout: timeout)
        } catch {
            logger.error("Saving a meeting without its lock: \(String(describing: error), privacy: .public)")
            descriptor = nil
        }
        defer {
            if let descriptor {
                release(descriptor)
            }
        }
        if descriptor != nil {
            afterLockingForTests?(meetingFolder)
        }
        return try body()
    }

    /// Opens the lock file, creating it when needed, and waits for the exclusive lock.
    private static func acquire(meetingFolder: URL, timeout: TimeInterval) throws -> Int32 {
        let path = meetingFolder.appendingPathComponent(fileName).path
        // Not following a link keeps the lock file inside the meeting folder.
        let descriptor = open(path, O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw LockError.unavailable }
        return try lock(descriptor: descriptor, timeout: timeout)
    }

    private static func lock(descriptor: Int32, timeout: TimeInterval) throws -> Int32 {
        var info = stat()
        guard fstat(descriptor, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG else {
            close(descriptor)
            throw LockError.unavailable
        }
        let deadline = ProcessInfo.processInfo.systemUptime + max(0, timeout)
        while true {
            if flock(descriptor, LOCK_EX | LOCK_NB) == 0 {
                return descriptor
            }
            let failure = errno
            if failure == EINTR {
                continue
            }
            guard failure == EWOULDBLOCK, ProcessInfo.processInfo.systemUptime < deadline else {
                close(descriptor)
                throw failure == EWOULDBLOCK ? LockError.timedOut : LockError.unavailable
            }
            usleep(retryMicroseconds)
        }
    }

    private static func release(_ descriptor: Int32) {
        flock(descriptor, LOCK_UN)
        close(descriptor)
    }
}
