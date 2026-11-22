import Foundation

/// A meeting directory pinned for the lifetime of one operation. Only `base` is a
/// caller-selected trusted location; Meetings, the UUID directory and every file are
/// opened relative to descriptors without following links. A concurrent rename cannot
/// redirect an already-open directory to a different meeting or an external folder.
final class MCPMeetingDirectory {
    let descriptor: Int32

    init?(directories: AppDirectories, folderName: String) {
        guard UUID(uuidString: folderName) != nil else { return nil }
        let flags = O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW
        let base = open(directories.base.path, O_RDONLY | O_DIRECTORY | O_CLOEXEC)
        guard base >= 0 else { return nil }
        defer { close(base) }
        let meetings = openat(base, "Meetings", flags)
        guard meetings >= 0 else { return nil }
        defer { close(meetings) }
        let folder = openat(meetings, folderName, flags)
        guard folder >= 0 else { return nil }
        descriptor = folder
    }

    deinit { close(descriptor) }

    /// Only internal fixed basenames are accepted; the descriptor pins the actual file
    /// before its type and size are checked. O_NONBLOCK prevents a swapped FIFO from
    /// waiting forever before fstat has a chance to reject it.
    func read(_ name: String) -> (data: Data, info: stat)? {
        guard name == "meeting.json" || name == "transcript.md" else { return nil }
        let file = openat(descriptor, name, O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK)
        guard file >= 0 else { return nil }
        defer { close(file) }
        var info = stat()
        guard fstat(file, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG,
              info.st_size >= 0, info.st_size <= MeetingArchive.maximumFileBytes else { return nil }
        var data = Data()
        data.reserveCapacity(Int(info.st_size))
        var chunk = [UInt8](repeating: 0, count: 64 * 1024)
        while true {
            let count = chunk.withUnsafeMutableBytes { Darwin.read(file, $0.baseAddress, $0.count) }
            if count < 0 {
                if errno == EINTR { continue }
                return nil
            }
            if count == 0 { return (data, info) }
            guard count <= MeetingArchive.maximumFileBytes - data.count else { return nil }
            data.append(contentsOf: chunk[0..<count])
        }
    }

    func writeRecord(_ data: Data) throws {
        let temporary = ".mcp-\(UUID().uuidString).tmp"
        let file = openat(descriptor, temporary, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                          S_IRUSR | S_IWUSR)
        guard file >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let handle = FileHandle(fileDescriptor: file, closeOnDealloc: true)
        defer {
            try? handle.close()
            unlinkat(descriptor, temporary, 0)
        }
        try handle.write(contentsOf: data)
        guard renameat(descriptor, temporary, descriptor, "meeting.json") == 0 else {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
    }
}
