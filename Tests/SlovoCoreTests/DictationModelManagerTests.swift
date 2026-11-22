import Foundation
import XCTest
@testable import SlovoCore

/// Every test uses a temporary models folder and a fake downloader: the real Models
/// folder is read-only and nothing is ever downloaded.
@MainActor
final class DictationModelManagerTests: XCTestCase {
    final class Downloader: ModelDownloading, @unchecked Sendable {
        typealias Behavior = @Sendable (EngineID, URL, @escaping @Sendable (Double) -> Void) async throws -> Void
        let behavior: DictationFakes.Locked<Behavior>
        let requests = DictationFakes.Locked<[EngineID]>([])

        init(_ behavior: @escaping Behavior = { _, _, _ in }) {
            self.behavior = DictationFakes.Locked(behavior)
        }

        func download(_ id: EngineID, modelsRoot: URL, progress: @escaping @Sendable (Double) -> Void) async throws {
            requests.value.append(id)
            try await behavior.value(id, modelsRoot, progress)
        }
    }

    private var root: URL!
    private var downloader: Downloader!
    private var manager: ModelManager!

    override func setUp() async throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoModelManagerTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        downloader = Downloader()
        manager = ModelManager(modelsRoot: root, downloader: downloader, appleAvailability: { true })
    }

    override func tearDown() async throws {
        manager = nil
        try? FileManager.default.removeItem(at: root)
    }

    /// Writes the GigaAM files as sparse files of the release sizes, optionally changing one.
    nonisolated private static func installGigaAM(_ id: EngineID, root: URL, sizeChange: (name: String, delta: Int)? = nil,
                                      skipping skipped: String? = nil) throws {
        let variant: GigaAMEngine.Variant = id == .gigaamRNNT ? .transducer : .ctc
        let folder = root.appendingPathComponent(try XCTUnwrap(id.modelFolder), isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for file in variant.files where file.name != skipped {
            let url = folder.appendingPathComponent(file.name)
            XCTAssertTrue(FileManager.default.createFile(atPath: url.path, contents: nil))
            let handle = try FileHandle(forWritingTo: url)
            let size = file.size + (sizeChange?.name == file.name ? sizeChange?.delta ?? 0 : 0)
            try handle.truncate(atOffset: UInt64(size))
            try handle.close()
        }
    }

    nonisolated private static func installParakeet(root: URL, files: [String]? = nil) throws {
        let repo = root.appendingPathComponent("parakeet-v3/parakeet-tdt-0.6b-v3", isDirectory: true)
        let models = ["Preprocessor.mlmodelc", "Encoder.mlmodelc", "Decoder.mlmodelc", "JointDecisionv3.mlmodelc"]
        let vocabularies = ["parakeet_vocab.json", "parakeet_v3_vocab.json"]
        for name in files ?? (models + vocabularies) {
            let url = repo.appendingPathComponent(name)
            if name.hasSuffix(".mlmodelc") {
                try FileManager.default.createDirectory(at: url.appendingPathComponent("weights"), withIntermediateDirectories: true)
                try Data([1, 2, 3]).write(to: url.appendingPathComponent("weights/weight.bin"))
            } else {
                try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
                try Data("{}".utf8).write(to: url)
            }
        }
    }

    // MARK: - Status

    func testEmptyFolderReportsNotDownloaded() async {
        XCTAssertEqual(manager.status(of: .gigaamRNNT), .unknown)
        manager.refresh()
        for id in [EngineID.gigaamRNNT, .gigaamCTC, .parakeet] {
            XCTAssertEqual(manager.status(of: id), .notDownloaded(downloadBytes: ModelManager.downloadBytes(for: id)))
        }
        await manager.waitForBackgroundWork()
        XCTAssertEqual(manager.status(of: .appleDictation), .systemManaged(available: true))
    }

    func testAppleDictationAvailabilityComesFromTheCheck() async {
        manager = ModelManager(modelsRoot: root, downloader: downloader, appleAvailability: { false })
        manager.refresh()
        await manager.waitForBackgroundWork()
        XCTAssertEqual(manager.status(of: .appleDictation), .systemManaged(available: false))
    }

    func testCompleteGigaAMIsReady() throws {
        try Self.installGigaAM(.gigaamRNNT, root: root)
        manager.refresh()
        let expected = GigaAMEngine.Variant.transducer.files.reduce(Int64(0)) { $0 + Int64($1.size) }
        XCTAssertEqual(manager.status(of: .gigaamRNNT), .ready(diskBytes: expected))
        XCTAssertEqual(manager.status(of: .gigaamCTC), .notDownloaded(downloadBytes: ModelManager.downloadBytes(for: .gigaamCTC)))
    }

    func testTruncatedOrPartialGigaAMIsDamaged() throws {
        try Self.installGigaAM(.gigaamCTC, root: root, sizeChange: ("model.int8.onnx", -1))
        manager.refresh()
        guard case let .damaged(reason) = manager.status(of: .gigaamCTC) else {
            return XCTFail("Expected damaged, got \(manager.status(of: .gigaamCTC))")
        }
        XCTAssertTrue(reason.contains("model.int8.onnx"), reason)

        try Self.installGigaAM(.gigaamRNNT, root: root, skipping: "joiner.onnx")
        manager.refresh()
        guard case let .damaged(missingReason) = manager.status(of: .gigaamRNNT) else {
            return XCTFail("Expected damaged, got \(manager.status(of: .gigaamRNNT))")
        }
        XCTAssertTrue(missingReason.contains("joiner.onnx"), missingReason)
    }

    func testParakeetStatusFollowsFiles() throws {
        try Self.installParakeet(root: root, files: ["Encoder.mlmodelc"])
        manager.refresh()
        XCTAssertEqual(manager.status(of: .parakeet),
                       .damaged(reason: CoreStrings.string("dictation.model.downloadUnfinished", table: .dictation)))

        try Self.installParakeet(root: root)
        manager.refresh()
        guard case let .ready(bytes) = manager.status(of: .parakeet) else {
            return XCTFail("Expected ready, got \(manager.status(of: .parakeet))")
        }
        XCTAssertGreaterThan(bytes, 0)

        let partial = root.appendingPathComponent("parakeet-v3/parakeet-tdt-0.6b-v3/Encoder.mlmodelc/weights/weight.bin.partial")
        try Data([0]).write(to: partial)
        manager.refresh()
        XCTAssertEqual(manager.status(of: .parakeet),
                       .damaged(reason: CoreStrings.string("dictation.model.downloadUnfinished", table: .dictation)))
    }

    func testDownloadBytes() {
        XCTAssertGreaterThan(ModelManager.downloadBytes(for: .gigaamRNNT), 200_000_000)
        XCTAssertGreaterThan(ModelManager.downloadBytes(for: .gigaamCTC), 200_000_000)
        XCTAssertGreaterThan(ModelManager.downloadBytes(for: .parakeet), 400_000_000)
        XCTAssertEqual(ModelManager.downloadBytes(for: .appleDictation), 0)
    }

    func testRefreshRemovesStagingLeftovers() throws {
        let leftover = root.appendingPathComponent(".downloads/old/archive.tar.bz2")
        try FileManager.default.createDirectory(at: leftover.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data([1]).write(to: leftover)
        manager.refresh()
        XCTAssertFalse(FileManager.default.fileExists(atPath: root.appendingPathComponent(".downloads").path))
    }

    // MARK: - Downloads

    func testDownloadReportsProgressAndEndsReady() async throws {
        let gate = DictationFakes.Gate()
        let root = self.root!
        downloader.behavior.value = { id, modelsRoot, progress in
            progress(0.5)
            await gate.wait()
            try DictationModelManagerTests.installGigaAM(id, root: modelsRoot)
            progress(1)
        }
        manager.startDownload(.gigaamCTC)
        XCTAssertEqual(manager.status(of: .gigaamCTC), .downloading(progress: 0))
        XCTAssertTrue(manager.isDownloading)

        let halfway = await DictationFakes.waitUntil { self.manager.status(of: .gigaamCTC) == .downloading(progress: 0.5) }
        XCTAssertTrue(halfway)
        manager.startDownload(.gigaamCTC)
        XCTAssertEqual(downloader.requests.value, [.gigaamCTC], "A second press while downloading starts nothing")

        gate.open()
        await manager.waitForBackgroundWork()
        let ready = await DictationFakes.waitUntil {
            if case .ready = self.manager.status(of: .gigaamCTC) { return true }
            return false
        }
        XCTAssertTrue(ready, "\(manager.status(of: .gigaamCTC))")
        XCTAssertFalse(manager.isDownloading)
        XCTAssertNil(manager.lastError)
        XCTAssertEqual(ModelManager.diskStatus(of: .gigaamCTC, modelsRoot: root), manager.status(of: .gigaamCTC))
    }

    func testFailedDownloadSetsLastError() async {
        downloader.behavior.value = { _, _, _ in
            throw SlovoError("offline")
        }
        manager.startDownload(.parakeet)
        await manager.waitForBackgroundWork()
        let finished = await DictationFakes.waitUntil { !self.manager.isDownloading }
        XCTAssertTrue(finished)

        let message = try? XCTUnwrap(manager.lastError)
        XCTAssertTrue(message?.contains("offline") == true, message ?? "nil")
        XCTAssertTrue(message?.contains("Parakeet v3") == true, message ?? "nil")
        XCTAssertEqual(manager.status(of: .parakeet), .notDownloaded(downloadBytes: ModelManager.downloadBytes(for: .parakeet)))
    }

    func testCancelledDownloadLeavesNoError() async {
        downloader.behavior.value = { _, _, _ in
            try await Task.sleep(for: .seconds(30))
        }
        manager.startDownload(.gigaamRNNT)
        manager.cancelDownload(.gigaamRNNT)
        await manager.waitForBackgroundWork()
        let finished = await DictationFakes.waitUntil { !self.manager.isDownloading }
        XCTAssertTrue(finished)

        XCTAssertNil(manager.lastError)
        XCTAssertEqual(manager.status(of: .gigaamRNNT), .notDownloaded(downloadBytes: ModelManager.downloadBytes(for: .gigaamRNNT)))
    }

    func testAppleDictationHasNothingToDownload() {
        manager.startDownload(.appleDictation)
        XCTAssertTrue(downloader.requests.value.isEmpty)
        XCTAssertFalse(manager.isDownloading)
    }

    // MARK: - Delete

    func testDeleteRemovesOneVariantThenTheSharedFolder() throws {
        try Self.installGigaAM(.gigaamRNNT, root: root)
        try Self.installGigaAM(.gigaamCTC, root: root)
        manager.refresh()
        let shared = root.appendingPathComponent("gigaam-v3")

        try manager.delete(.gigaamCTC)
        XCTAssertEqual(manager.status(of: .gigaamCTC), .notDownloaded(downloadBytes: ModelManager.downloadBytes(for: .gigaamCTC)))
        XCTAssertTrue(FileManager.default.fileExists(atPath: shared.path))
        guard case .ready = manager.status(of: .gigaamRNNT) else {
            return XCTFail("Deleting CTC must keep RNNT")
        }

        try manager.delete(.gigaamRNNT)
        XCTAssertFalse(FileManager.default.fileExists(atPath: shared.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: root.path), "The models root itself stays")
    }

    func testDeleteWhileDownloadingIsRefused() async {
        let gate = DictationFakes.Gate()
        downloader.behavior.value = { _, _, _ in await gate.wait() }
        manager.startDownload(.parakeet)
        XCTAssertThrowsError(try manager.delete(.parakeet))
        XCTAssertNotNil(manager.lastError)
        gate.open()
        await manager.waitForBackgroundWork()
    }

    func testDeletingAppleDictationDoesNothing() throws {
        try manager.delete(.appleDictation)
        XCTAssertTrue(FileManager.default.fileExists(atPath: root.path))
    }

    // MARK: - Archive installation

    private func makeArchive(folderName: String, files: [String: Data]) throws -> URL {
        let source = root.appendingPathComponent("source", isDirectory: true)
        let folder = source.appendingPathComponent(folderName, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for (name, data) in files {
            try data.write(to: folder.appendingPathComponent(name))
        }
        let archive = root.appendingPathComponent("\(folderName).tar.bz2")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        process.arguments = ["-cjf", archive.path, "-C", source.path, folderName]
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
        return archive
    }

    func testInstallArchiveExtractsVerifiesAndReplaces() async throws {
        let folderName = "sherpa-onnx-test-model"
        let archive = try makeArchive(folderName: folderName, files: ["model.onnx": Data(count: 10), "tokens.txt": Data("abc".utf8)])
        let parent = root.appendingPathComponent("gigaam-v3", isDirectory: true)
        let destination = parent.appendingPathComponent(folderName, isDirectory: true)
        // A damaged earlier copy is replaced.
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        try Data([9]).write(to: destination.appendingPathComponent("model.onnx"))

        let staging = try ModelInstaller.makeStagingFolder(in: root)
        try await ModelInstaller.installArchive(archive, folderName: folderName,
                                                expectedFiles: [("model.onnx", 10), ("tokens.txt", 3)],
                                                destinationParent: parent, staging: staging, displayName: "Test")

        let size = try FileManager.default.attributesOfItem(atPath: destination.appendingPathComponent("model.onnx").path)[.size] as? Int
        XCTAssertEqual(size, 10)
        XCTAssertEqual(try String(contentsOf: destination.appendingPathComponent("tokens.txt"), encoding: .utf8), "abc")
        XCTAssertFalse(FileManager.default.fileExists(atPath: staging.appendingPathComponent("extracted/\(folderName)").path))
    }

    func testInstallArchiveRejectsWrongSizesAndKeepsTheOldCopy() async throws {
        let folderName = "sherpa-onnx-test-model"
        let archive = try makeArchive(folderName: folderName, files: ["model.onnx": Data(count: 4)])
        let parent = root.appendingPathComponent("gigaam-v3", isDirectory: true)
        let destination = parent.appendingPathComponent(folderName, isDirectory: true)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        try Data([9]).write(to: destination.appendingPathComponent("old.txt"))

        let staging = try ModelInstaller.makeStagingFolder(in: root)
        do {
            try await ModelInstaller.installArchive(archive, folderName: folderName, expectedFiles: [("model.onnx", 10)],
                                                    destinationParent: parent, staging: staging, displayName: "Test")
            XCTFail("A truncated archive must be rejected")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("model.onnx"), error.localizedDescription)
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent("old.txt").path))
    }

    func testTarFailureIsReported() async throws {
        let bogus = root.appendingPathComponent("broken.tar.bz2")
        try Data("not an archive".utf8).write(to: bogus)
        let target = root.appendingPathComponent("out", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        do {
            try await TarExtractor.extract(bogus, into: target)
            XCTFail("tar must fail on a broken archive")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("broken.tar.bz2"), error.localizedDescription)
        }
    }

    func testInstallArchiveRejectsALinkedModelDirectory() async throws {
        let folderName = "linked-model"
        let source = root.appendingPathComponent("source", isDirectory: true)
        let external = root.appendingPathComponent("external", isDirectory: true)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: external, withIntermediateDirectories: true)
        try Data(count: 10).write(to: external.appendingPathComponent("model.onnx"))
        try FileManager.default.createSymbolicLink(at: source.appendingPathComponent(folderName),
                                                   withDestinationURL: external)
        let archive = root.appendingPathComponent("linked.tar.bz2")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        process.arguments = ["-cjf", archive.path, "-C", source.path, folderName]
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)

        let parent = root.appendingPathComponent("models", isDirectory: true)
        let staging = try ModelInstaller.makeStagingFolder(in: root)
        do {
            try await ModelInstaller.installArchive(archive, folderName: folderName, expectedFiles: [("model.onnx", 10)],
                                                    destinationParent: parent, staging: staging, displayName: "Test")
            XCTFail("A release directory must not be a link to an external model")
        } catch {
            XCTAssertFalse(error is CancellationError)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: parent.appendingPathComponent(folderName).path))
        XCTAssertEqual(try Data(contentsOf: external.appendingPathComponent("model.onnx")), Data(count: 10))
    }
}
