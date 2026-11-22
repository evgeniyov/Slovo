import CryptoKit
import Foundation

/// Fetches and installs model files the user asked for.
public protocol ModelDownloading: Sendable {
    /// Downloads and installs the model of `id` under `modelsRoot`, reporting progress
    /// from 0 to 1 on any thread. Stops promptly when the calling task is cancelled.
    func download(_ id: EngineID, modelsRoot: URL, progress: @escaping @Sendable (Double) -> Void) async throws
}

/// The real downloads: GigaAM archives from the sherpa-onnx release, Parakeet through
/// FluidAudio, Whisper's file from Hugging Face. Used only when the user presses Download.
public struct LiveModelDownloader: ModelDownloading {
    public static let gigaAMReleaseURL = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/"
    /// Download progress fills this share of the bar; unpacking the archive takes the rest.
    static let downloadShare = 0.9

    public init() {}

    public func download(_ id: EngineID, modelsRoot: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
        switch id {
        case .gigaamRNNT:
            try await downloadGigaAM(.transducer, id: id, modelsRoot: modelsRoot, progress: progress)
        case .gigaamCTC:
            try await downloadGigaAM(.ctc, id: id, modelsRoot: modelsRoot, progress: progress)
        case .parakeet:
            guard let folder = id.modelFolder else { return }
            try await ParakeetEngine.downloadModels(to: modelsRoot.appendingPathComponent(folder, isDirectory: true),
                                                    progress: progress)
        case .whisperTurbo:
            guard let folder = id.modelFolder else { return }
            try await VerifiedFileDownload.whisper(into: modelsRoot.appendingPathComponent(folder, isDirectory: true),
                                                   progress: progress)
        case .appleDictation:
            return
        }
    }

    private func downloadGigaAM(_ variant: GigaAMEngine.Variant, id: EngineID, modelsRoot: URL,
                                progress: @escaping @Sendable (Double) -> Void) async throws {
        let archiveName = variant.folderName + ".tar.bz2"
        guard let url = URL(string: Self.gigaAMReleaseURL + archiveName), let folder = id.modelFolder else {
            throw SlovoError(CoreStrings.format("dictation.error.modelMissing", table: .dictation, id.displayName))
        }
        let staging = try ModelInstaller.makeStagingFolder(in: modelsRoot)
        defer { try? FileManager.default.removeItem(at: staging) }

        let archive = staging.appendingPathComponent(archiveName)
        let share = Self.downloadShare
        try await FileDownload(destination: archive, progress: { progress($0 * share) }).run(url)
        progress(share)
        try await ModelInstaller.installArchive(
            archive, folderName: variant.folderName, expectedFiles: variant.files,
            destinationParent: modelsRoot.appendingPathComponent(folder, isDirectory: true).deletingLastPathComponent(),
            staging: staging, displayName: id.displayName)
        progress(1)
    }
}

/// Unpacking and moving downloaded models into place.
enum ModelInstaller {
    /// Hidden folder under the models root for downloads in progress. It lives on the
    /// same volume as the models, so the final move is a rename.
    static let stagingFolderName = ".downloads"

    static func makeStagingFolder(in modelsRoot: URL) throws -> URL {
        let folder = modelsRoot.appendingPathComponent(stagingFolderName, isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// Extracts a release archive inside `staging`, checks every file against the release
    /// sizes, then moves the model folder into `destinationParent` with one rename, so
    /// engines never see a half-extracted model.
    static func installArchive(_ archive: URL, folderName: String, expectedFiles: [(name: String, size: Int)],
                               destinationParent: URL, staging: URL, displayName: String) async throws {
        let fileManager = FileManager.default
        let extracted = staging.appendingPathComponent("extracted", isDirectory: true)
        try fileManager.createDirectory(at: extracted, withIntermediateDirectories: true)
        try await TarExtractor.extract(archive, into: extracted)
        try Task.checkCancellation()

        let folder = extracted.appendingPathComponent(folderName, isDirectory: true)
        var folderInfo = stat()
        let isDirectory = lstat(folder.path, &folderInfo) == 0 && (folderInfo.st_mode & S_IFMT) == S_IFDIR
        let wrong = expectedFiles.filter { file in
            var info = stat()
            // A link (including the model directory itself) must never point an engine
            // outside the verified release, even when its target has the expected size.
            return !isDirectory || lstat(folder.appendingPathComponent(file.name).path, &info) != 0
                || (info.st_mode & S_IFMT) != S_IFREG || info.st_size != Int64(file.size)
        }
        guard wrong.isEmpty else {
            throw SlovoError(CoreStrings.format("dictation.error.archiveIncomplete", table: .dictation, displayName,
                                                wrong.map(\.name).joined(separator: ", ")))
        }

        try fileManager.createDirectory(at: destinationParent, withIntermediateDirectories: true)
        let destination = destinationParent.appendingPathComponent(folderName, isDirectory: true)
        let previous = staging.appendingPathComponent("previous", isDirectory: true)
        let hadPrevious = fileManager.fileExists(atPath: destination.path)
        if hadPrevious {
            // A damaged copy moves aside and is deleted together with the staging folder.
            try fileManager.moveItem(at: destination, to: previous)
        }
        do {
            try fileManager.moveItem(at: folder, to: destination)
        } catch {
            if hadPrevious {
                try? fileManager.moveItem(at: previous, to: destination)
            }
            throw error
        }
    }
}

/// Runs `/usr/bin/tar` on a bzip2 archive.
enum TarExtractor {
    static let executable = URL(fileURLWithPath: "/usr/bin/tar")

    static func extract(_ archive: URL, into folder: URL) async throws {
        let process = Process()
        process.executableURL = executable
        // bsdtar strips leading slashes and refuses ".." entries unless told otherwise.
        process.arguments = ["-xjf", archive.path, "-C", folder.path]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        let handle = ProcessHandle(process)
        let name = archive.lastPathComponent
        do {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    process.terminationHandler = { finished in
                        if finished.terminationReason == .exit, finished.terminationStatus == 0 {
                            continuation.resume()
                        } else {
                            continuation.resume(throwing: SlovoError(CoreStrings.format(
                                "dictation.error.extractFailed", table: .dictation, name, Int(finished.terminationStatus))))
                        }
                    }
                    do {
                        try process.run()
                    } catch {
                        process.terminationHandler = nil
                        continuation.resume(throwing: error)
                    }
                }
            } onCancel: {
                handle.terminate()
            }
        } catch {
            // A tar killed by cancellation reports a signal; the caller should see cancellation.
            try Task.checkCancellation()
            throw error
        }
        try Task.checkCancellation()
    }

    private final class ProcessHandle: @unchecked Sendable {
        private let process: Process

        init(_ process: Process) {
            self.process = process
        }

        func terminate() {
            if process.isRunning {
                process.terminate()
            }
        }
    }
}

/// One URLSession download into a file, with progress and cancellation.
final class FileDownload: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let destination: URL
    private let progress: @Sendable (Double) -> Void
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?
    private var fileError: Error?

    init(destination: URL, progress: @escaping @Sendable (Double) -> Void) {
        self.destination = destination
        self.progress = progress
    }

    func run(_ url: URL) async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 60
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        // The session retains its delegate until it is invalidated.
        defer { session.finishTasksAndInvalidate() }
        let task = session.downloadTask(with: url)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                lock.withLock { self.continuation = continuation }
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard totalBytesExpectedToWrite > 0 else { return }
        progress(min(1, Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // The temporary file disappears when this method returns, so it moves now.
        var failure: Error?
        if let response = downloadTask.response as? HTTPURLResponse, !(200..<300).contains(response.statusCode) {
            failure = SlovoError(CoreStrings.format("dictation.error.httpStatus", table: .dictation, response.statusCode))
        } else {
            do {
                if FileManager.default.fileExists(atPath: destination.path) {
                    try FileManager.default.removeItem(at: destination)
                }
                try FileManager.default.moveItem(at: location, to: destination)
            } catch {
                failure = error
            }
        }
        lock.withLock { fileError = failure }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let result: Result<Void, Error>
        if let error {
            result = .failure((error as? URLError)?.code == .cancelled ? CancellationError() : error)
        } else if let failure = lock.withLock({ fileError }) {
            result = .failure(failure)
        } else {
            result = .success(())
        }
        let waiting = lock.withLock { () -> CheckedContinuation<Void, Error>? in
            let current = continuation
            continuation = nil
            return current
        }
        waiting?.resume(with: result)
    }
}

/// A single model file fetched in place. `<name>.partial` grows across attempts: a download
/// cut by the network or by quitting resumes with an HTTP range from where it stopped. The
/// finished file is checked against its SHA-256 and only then renamed to its own name, so a
/// file under that name is always a verified one. A download the user cancels leaves
/// nothing behind.
struct VerifiedFileDownload {
    let url: URL
    let destination: URL
    let size: Int64
    let sha256: String
    /// Tests pass a configuration whose protocol classes answer instead of the network.
    var configuration: URLSessionConfiguration = .ephemeral

    /// Download progress fills this share of the bar; the checksum takes the rest.
    static let downloadShare = 0.97

    /// Whisper large-v3 turbo into `folder`.
    static func whisper(into folder: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
        try await whisper(into: folder).run(progress: progress)
    }

    static func whisper(into folder: URL) throws -> VerifiedFileDownload {
        guard let url = URL(string: WhisperEngine.Model.address) else {
            throw SlovoError(CoreStrings.format("dictation.error.modelMissing", table: .dictation,
                                                EngineID.whisperTurbo.displayName))
        }
        return VerifiedFileDownload(url: url, destination: folder.appendingPathComponent(WhisperEngine.Model.fileName),
                                    size: WhisperEngine.Model.size, sha256: WhisperEngine.Model.sha256)
    }

    var partial: URL {
        destination.appendingPathExtension("partial")
    }

    func run(progress: @escaping @Sendable (Double) -> Void) async throws {
        let fileManager = FileManager.default
        let name = destination.lastPathComponent
        do {
            try Task.checkCancellation()
            try fileManager.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            if Self.fileSize(partial) > size {
                try fileManager.removeItem(at: partial)
            }
            let share = Self.downloadShare
            if Self.fileSize(partial) < size {
                try await RangedFileDownload(file: partial, expectedSize: size, configuration: configuration,
                                             progress: { progress($0 * share) }).run(url)
            }
            guard Self.fileSize(partial) == size else {
                throw SlovoError(CoreStrings.format("dictation.error.downloadCut", table: .dictation, name))
            }
            progress(share)
            guard try await Self.sha256(of: partial) == sha256 else {
                try? fileManager.removeItem(at: partial)
                throw SlovoError(CoreStrings.format("dictation.error.checksumMismatch", table: .dictation, name))
            }
            if fileManager.fileExists(atPath: destination.path) {
                try fileManager.removeItem(at: destination)
            }
            try fileManager.moveItem(at: partial, to: destination)
            progress(1)
        } catch {
            if error is CancellationError || Task.isCancelled {
                try? fileManager.removeItem(at: partial)
                throw CancellationError()
            }
            throw error
        }
    }

    static func fileSize(_ file: URL) -> Int64 {
        let attributes = try? FileManager.default.attributesOfItem(atPath: file.path)
        return (attributes?[.size] as? NSNumber)?.int64Value ?? 0
    }

    /// The file's SHA-256 in lowercase hex, read in chunks off the caller's thread; stops
    /// when the task is cancelled.
    static func sha256(of file: URL) async throws -> String {
        let reading = Task.detached(priority: .utility) { () throws -> String in
            let handle = try FileHandle(forReadingFrom: file)
            defer { try? handle.close() }
            var hasher = SHA256()
            while let chunk = try handle.read(upToCount: 4 << 20), !chunk.isEmpty {
                try Task.checkCancellation()
                hasher.update(data: chunk)
            }
            return hasher.finalize().map { String(format: "%02x", $0) }.joined()
        }
        return try await withTaskCancellationHandler {
            try await reading.value
        } onCancel: {
            reading.cancel()
        }
    }
}

/// One URLSession data task that appends to a file from where it ends. A server that
/// ignores the range sends the whole file, which then replaces the partial one.
final class RangedFileDownload: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let file: URL
    private let expectedSize: Int64
    private let configuration: URLSessionConfiguration
    private let progress: @Sendable (Double) -> Void
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?
    // The rest is set before the task starts or touched only on the session's serial
    // delegate queue.
    private var handle: FileHandle?
    private var offset: Int64 = 0
    private var written: Int64 = 0
    private var failure: Error?

    init(file: URL, expectedSize: Int64, configuration: URLSessionConfiguration,
         progress: @escaping @Sendable (Double) -> Void) {
        self.file = file
        self.expectedSize = expectedSize
        self.configuration = configuration
        self.progress = progress
    }

    func run(_ url: URL) async throws {
        offset = VerifiedFileDownload.fileSize(file)
        var request = URLRequest(url: url)
        if offset > 0 {
            request.setValue("bytes=\(offset)-", forHTTPHeaderField: "Range")
        }
        configuration.timeoutIntervalForRequest = 60
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        // The session retains its delegate until it is invalidated.
        defer { session.finishTasksAndInvalidate() }
        let task = session.dataTask(with: request)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                lock.withLock { self.continuation = continuation }
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        do {
            switch status {
            case 206 where Self.rangeStart(of: response) == offset:
                let handle = try FileHandle(forWritingTo: file)
                try handle.seekToEnd()
                self.handle = handle
                written = offset
            case 200:
                // The whole file, from the start.
                guard FileManager.default.createFile(atPath: file.path, contents: nil) else {
                    throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: file.path])
                }
                handle = try FileHandle(forWritingTo: file)
                written = 0
            default:
                // A range the server cannot serve means the partial file is not what it
                // has; the next attempt starts over.
                if status == 206 || status == 416 {
                    try? FileManager.default.removeItem(at: file)
                }
                throw SlovoError(CoreStrings.format("dictation.error.httpStatus", table: .dictation, status))
            }
            completionHandler(.allow)
        } catch {
            failure = error
            completionHandler(.cancel)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard let handle, failure == nil else { return }
        do {
            // Check before writing: a broken/malicious response can otherwise fill the
            // disk before the final size/checksum validation ever runs. Do not trust
            // Content-Length; chunked responses may not supply it at all.
            guard written <= expectedSize, Int64(data.count) <= expectedSize - written else {
                throw SlovoError(CoreStrings.format("dictation.error.downloadCut", table: .dictation,
                                                    file.deletingPathExtension().lastPathComponent))
            }
            try handle.write(contentsOf: data)
            written += Int64(data.count)
            progress(min(1, Double(written) / Double(max(expectedSize, 1))))
        } catch {
            failure = error
            dataTask.cancel()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        try? handle?.close()
        handle = nil
        let result: Result<Void, Error>
        if let failure {
            result = .failure(failure)
        } else if let error {
            result = .failure((error as? URLError)?.code == .cancelled ? CancellationError() : error)
        } else {
            result = .success(())
        }
        let waiting = lock.withLock { () -> CheckedContinuation<Void, Error>? in
            let current = continuation
            continuation = nil
            return current
        }
        waiting?.resume(with: result)
    }

    /// The first byte of a 206 response, from `Content-Range: bytes 100-199/200`.
    static func rangeStart(of response: URLResponse) -> Int64? {
        guard let value = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Range"),
              let range = value.split(separator: " ").last,
              let start = range.split(separator: "-").first else { return nil }
        return Int64(start)
    }
}
