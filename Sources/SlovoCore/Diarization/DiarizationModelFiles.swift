import FluidAudio
import Foundation

/// The speaker model files FluidAudio loads, checked on disk without loading them.
///
/// FluidAudio keeps them inside `modelFolder` in a folder named after the Hugging Face
/// repository without its `-coreml` suffix, `speaker-diarization/`, and both the download
/// and the loader derive that name the same way. `config.json` and
/// `xvector-transform.json` arrive with the download, but the loader does not read them,
/// so they do not decide completeness.
struct DiarizationModelFiles {
    let modelFolder: URL

    var repositoryFolder: URL {
        modelFolder.appendingPathComponent(Repo.diarizer.folderName, isDirectory: true)
    }

    /// Every compiled model has its description and weights, the PLDA parameters exist,
    /// and no download was left half-finished.
    var isComplete: Bool {
        let fileManager = FileManager.default
        for name in ModelNames.OfflineDiarizer.requiredModels {
            let item = repositoryFolder.appendingPathComponent(name)
            if name.hasSuffix(".mlmodelc") {
                for part in ["coremldata.bin", "weights/weight.bin"] where !Self.isRegularFile(item.appendingPathComponent(part)) {
                    return false
                }
            } else if !Self.isRegularFile(item) {
                return false
            }
        }
        guard let items = fileManager.enumerator(at: modelFolder, includingPropertiesForKeys: nil) else {
            return false
        }
        for case let item as URL in items where item.pathExtension == "partial" {
            return false
        }
        return true
    }

    var status: ModelStatus {
        if isComplete {
            return .ready(diskBytes: Self.size(of: modelFolder))
        }
        if Self.containsFiles(modelFolder) {
            return .damaged(reason: CoreStrings.string("diarization.error.modelDamaged", table: .diarization))
        }
        return .notDownloaded(downloadBytes: DiarizationService.downloadBytes)
    }

    private static func isRegularFile(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true
    }

    /// Any file other than Finder's hidden ones; empty folders left by a load or a
    /// cancelled download count as nothing downloaded.
    private static func containsFiles(_ folder: URL) -> Bool {
        guard let items = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey]) else {
            return false
        }
        for case let item as URL in items where isRegularFile(item) && !item.lastPathComponent.hasPrefix(".") {
            return true
        }
        return false
    }

    private static func size(of folder: URL) -> Int64 {
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .fileSizeKey]
        guard let items = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: Array(keys)) else {
            return 0
        }
        var total: Int64 = 0
        for case let item as URL in items {
            guard let values = try? item.resourceValues(forKeys: keys), values.isRegularFile == true else { continue }
            total += Int64(values.fileSize ?? 0)
        }
        return total
    }
}
