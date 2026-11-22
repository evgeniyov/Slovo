import Combine
import Foundation

/// Model files on disk: status, user-started downloads and deletion.
///
/// GigaAM archives come from the sherpa-onnx `asr-models` release and are extracted
/// with `/usr/bin/tar`; Parakeet downloads through FluidAudio into
/// `ModelStore.root/parakeet-v3`; Whisper's single file comes from Hugging Face into
/// `ModelStore.root/whisper-v3-turbo`. Nothing downloads unless the user presses Download.
@MainActor
public final class ModelManager: ObservableObject, ModelManaging {
    @Published public private(set) var statuses: [EngineID: ModelStatus] = [:]
    /// The last download or delete error, already localized.
    @Published public private(set) var lastError: String?

    public let modelsRoot: URL

    private let downloader: ModelDownloading
    private let appleAvailability: @Sendable () async -> Bool
    private let log = SlovoLog.logger("Models")
    private var downloads: [EngineID: Task<Void, Never>] = [:]
    private var appleCheck: Task<Void, Never>?

    /// - Parameters:
    ///   - downloader: performs downloads; tests pass a fake so nothing touches the network.
    ///   - appleAvailability: whether Apple Dictation can run on this Mac.
    public init(modelsRoot: URL = ModelStore.root, downloader: ModelDownloading = LiveModelDownloader(),
                appleAvailability: @escaping @Sendable () async -> Bool = { await ModelManager.appleDictationAvailable() }) {
        self.modelsRoot = modelsRoot
        self.downloader = downloader
        self.appleAvailability = appleAvailability
    }

    public func status(of id: EngineID) -> ModelStatus {
        statuses[id] ?? .unknown
    }

    public var isDownloading: Bool {
        !downloads.isEmpty
    }

    /// Re-reads every model folder. Apple Dictation's availability arrives a moment later.
    public func refresh() {
        for id in EngineID.allCases where id.usesDownloadedModel && downloads[id] == nil {
            setStatus(Self.diskStatus(of: id, modelsRoot: modelsRoot), for: id)
        }
        if downloads.isEmpty {
            removeStagingLeftovers()
        }
        refreshAppleDictation()
    }

    public func startDownload(_ id: EngineID) {
        guard id.usesDownloadedModel, downloads[id] == nil else { return }
        lastError = nil
        setStatus(.downloading(progress: 0), for: id)
        log.info("Downloading \(id.rawValue, privacy: .public)")
        let downloader = self.downloader
        let root = modelsRoot
        let report: @Sendable (Double) -> Void = { [weak self] fraction in
            let manager = self
            Task { @MainActor in
                manager?.updateProgress(fraction, for: id)
            }
        }
        downloads[id] = Task { [weak self] in
            do {
                try await downloader.download(id, modelsRoot: root, progress: report)
                self?.downloadFinished(id, error: nil)
            } catch {
                self?.downloadFinished(id, error: error)
            }
        }
    }

    public func cancelDownload(_ id: EngineID) {
        guard let download = downloads[id] else { return }
        log.info("Cancelling the \(id.rawValue, privacy: .public) download")
        download.cancel()
    }

    public func delete(_ id: EngineID) throws {
        guard let folder = Self.installFolder(of: id, modelsRoot: modelsRoot) else { return }
        guard downloads[id] == nil else {
            let message = CoreStrings.format("dictation.error.deleteWhileDownloading", table: .dictation, id.displayName)
            lastError = message
            throw SlovoError(message)
        }
        let fileManager = FileManager.default
        do {
            if fileManager.fileExists(atPath: folder.path) {
                try fileManager.removeItem(at: folder)
            }
            // Both GigaAM variants live in one parent folder; it goes once both are deleted.
            let parent = folder.deletingLastPathComponent()
            if parent.standardizedFileURL != modelsRoot.standardizedFileURL,
               let remaining = try? fileManager.contentsOfDirectory(atPath: parent.path),
               remaining.allSatisfy({ $0.hasPrefix(".") }) {
                try fileManager.removeItem(at: parent)
            }
        } catch {
            let message = CoreStrings.format("dictation.error.deleteFailed", table: .dictation, id.displayName,
                                             error.localizedDescription)
            lastError = message
            setStatus(Self.diskStatus(of: id, modelsRoot: modelsRoot), for: id)
            log.error("Could not delete \(id.rawValue, privacy: .public): \(error.localizedDescription, privacy: .public)")
            throw SlovoError(message)
        }
        log.info("Deleted \(id.rawValue, privacy: .public)")
        lastError = nil
        setStatus(Self.diskStatus(of: id, modelsRoot: modelsRoot), for: id)
    }

    /// Approximate download size shown next to "Not downloaded".
    nonisolated public static func downloadBytes(for id: EngineID) -> Int64 {
        switch id {
        // Sizes of the extracted sherpa-onnx release folders; the bzip2 archives are only
        // a little smaller because int8 weights compress poorly.
        case .gigaamRNNT: return 232_300_000
        case .gigaamCTC: return 225_300_000
        // The int8 Core ML files and vocabulary FluidAudio fetches for Parakeet TDT 0.6B v3.
        case .parakeet: return 482_300_000
        case .appleDictation: return 0
        case .whisperTurbo: return WhisperEngine.Model.size
        }
    }

    /// Whether macOS can run Apple Dictation in Russian or English with installed assets.
    nonisolated public static func appleDictationAvailable() async -> Bool {
        guard #available(macOS 26.0, *) else { return false }
        for identifier in ["ru-RU", "en-US"] {
            if await AppleDictationEngine.hasInstalledAssets(for: Locale(identifier: identifier)) {
                return true
            }
        }
        return false
    }

    // MARK: - Downloads

    /// Waits for running downloads and the Apple Dictation check; for tests.
    func waitForBackgroundWork() async {
        for download in downloads.values {
            await download.value
        }
        await appleCheck?.value
    }

    private func updateProgress(_ fraction: Double, for id: EngineID) {
        // Late updates can arrive after the download finished.
        guard downloads[id] != nil, case let .downloading(current) = status(of: id) else { return }
        let rounded = (min(1, max(0, fraction)) * 100).rounded() / 100
        if rounded > current {
            statuses[id] = .downloading(progress: rounded)
        }
    }

    private func downloadFinished(_ id: EngineID, error: Error?) {
        downloads[id] = nil
        if let error {
            if error is CancellationError || (error as? URLError)?.code == .cancelled {
                log.info("The \(id.rawValue, privacy: .public) download was cancelled")
            } else {
                lastError = CoreStrings.format("dictation.error.downloadFailed", table: .dictation, id.displayName,
                                               error.localizedDescription)
                log.error("Downloading \(id.rawValue, privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
            }
        } else {
            log.info("Downloaded \(id.rawValue, privacy: .public)")
        }
        setStatus(Self.diskStatus(of: id, modelsRoot: modelsRoot), for: id)
        if downloads.isEmpty {
            removeStagingLeftovers()
        }
    }

    private func refreshAppleDictation() {
        guard appleCheck == nil else { return }
        let check = appleAvailability
        appleCheck = Task { [weak self] in
            let available = await check()
            guard let self else { return }
            self.appleCheck = nil
            self.setStatus(.systemManaged(available: available), for: .appleDictation)
        }
    }

    private func setStatus(_ status: ModelStatus, for id: EngineID) {
        if statuses[id] != status {
            statuses[id] = status
        }
    }

    /// Staging folders left by a download that was interrupted by quitting the app.
    private func removeStagingLeftovers() {
        let staging = modelsRoot.appendingPathComponent(ModelInstaller.stagingFolderName, isDirectory: true)
        guard FileManager.default.fileExists(atPath: staging.path) else { return }
        try? FileManager.default.removeItem(at: staging)
    }

    // MARK: - Disk status

    static func installFolder(of id: EngineID, modelsRoot: URL) -> URL? {
        id.modelFolder.map { modelsRoot.appendingPathComponent($0, isDirectory: true) }
    }

    /// The status the files on disk give; `.unknown` for Apple Dictation.
    nonisolated static func diskStatus(of id: EngineID, modelsRoot: URL) -> ModelStatus {
        guard let folder = id.modelFolder.map({ modelsRoot.appendingPathComponent($0, isDirectory: true) }) else {
            return .unknown
        }
        switch id {
        case .gigaamRNNT:
            return gigaAMStatus(of: id, folder: folder, files: GigaAMEngine.Variant.transducer.files)
        case .gigaamCTC:
            return gigaAMStatus(of: id, folder: folder, files: GigaAMEngine.Variant.ctc.files)
        case .parakeet:
            if ParakeetEngine.modelFilesComplete(in: folder) {
                return .ready(diskBytes: folderSize(folder))
            }
            if containsFiles(folder) {
                return .damaged(reason: CoreStrings.string("dictation.model.downloadUnfinished", table: .dictation))
            }
            return .notDownloaded(downloadBytes: downloadBytes(for: id))
        case .whisperTurbo:
            return whisperStatus(folder: folder)
        case .appleDictation:
            return .unknown
        }
    }

    /// The file is renamed into place only after its checksum matched, so a file of the
    /// right size is a verified one; anything else in the folder is an unfinished download.
    nonisolated static func whisperStatus(folder: URL) -> ModelStatus {
        let file = folder.appendingPathComponent(WhisperEngine.Model.fileName)
        if let attributes = try? FileManager.default.attributesOfItem(atPath: file.path),
           attributes[.type] as? FileAttributeType == .typeRegular {
            guard (attributes[.size] as? NSNumber)?.int64Value == WhisperEngine.Model.size else {
                return .damaged(reason: CoreStrings.format("dictation.model.incompleteFiles", table: .dictation,
                                                           WhisperEngine.Model.fileName))
            }
            return .ready(diskBytes: folderSize(folder))
        }
        if containsFiles(folder) {
            return .damaged(reason: CoreStrings.string("dictation.model.downloadUnfinished", table: .dictation))
        }
        return .notDownloaded(downloadBytes: downloadBytes(for: .whisperTurbo))
    }

    /// Every file must match the release size: ONNX Runtime aborts the whole process on a
    /// truncated model instead of returning an error.
    nonisolated static func gigaAMStatus(of id: EngineID, folder: URL, files: [(name: String, size: Int)]) -> ModelStatus {
        var missing: [String] = []
        var incomplete: [String] = []
        for file in files {
            let path = folder.appendingPathComponent(file.name).resolvingSymlinksInPath().path
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: path),
                  attributes[.type] as? FileAttributeType == .typeRegular else {
                missing.append(file.name)
                continue
            }
            if attributes[.size] as? Int != file.size {
                incomplete.append(file.name)
            }
        }
        if missing.count == files.count {
            return .notDownloaded(downloadBytes: downloadBytes(for: id))
        }
        if missing.isEmpty, incomplete.isEmpty {
            return .ready(diskBytes: folderSize(folder))
        }
        var reasons: [String] = []
        if !missing.isEmpty {
            reasons.append(CoreStrings.format("dictation.model.missingFiles", table: .dictation,
                                              missing.joined(separator: ", ")))
        }
        if !incomplete.isEmpty {
            reasons.append(CoreStrings.format("dictation.model.incompleteFiles", table: .dictation,
                                              incomplete.joined(separator: ", ")))
        }
        return .damaged(reason: reasons.joined(separator: " "))
    }

    nonisolated static func folderSize(_ folder: URL) -> Int64 {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey]
        guard let items = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: keys) else { return 0 }
        var total: Int64 = 0
        for case let item as URL in items {
            guard let values = try? item.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else { continue }
            total += Int64(values.fileSize ?? 0)
        }
        return total
    }

    nonisolated private static func containsFiles(_ folder: URL) -> Bool {
        guard let items = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey]) else {
            return false
        }
        for case let item as URL in items where (try? item.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true {
            return true
        }
        return false
    }
}
