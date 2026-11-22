import XCTest
@testable import SlovoCore

/// Fixtures shared by the diarization tests. Everything lives in temporary folders:
/// the real Models folder is never read or written, and nothing is downloaded.
enum DiarizationFixtures {
    static let compiledModels = ["Segmentation.mlmodelc", "Embedding.mlmodelc", "FBank.mlmodelc", "PldaRho.mlmodelc"]

    static func temporaryFolder(_ name: String) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(name)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// Writes the files the loader needs into `modelFolder/speaker-diarization`,
    /// leaving out any path listed in `skipping`. Returns the bytes written.
    @discardableResult
    static func installModel(in modelFolder: URL, skipping: Set<String> = [], extras: [String] = []) throws -> Int64 {
        let repository = modelFolder.appendingPathComponent("speaker-diarization", isDirectory: true)
        var paths = compiledModels.flatMap { ["\($0)/coremldata.bin", "\($0)/weights/weight.bin", "\($0)/model.mil"] }
        paths += ["plda-parameters.json", "config.json"] + extras
        var bytes: Int64 = 0
        for path in paths where !skipping.contains(path) {
            let url = repository.appendingPathComponent(path)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = Data(repeating: 7, count: 16 + path.count)
            try data.write(to: url)
            bytes += Int64(data.count)
        }
        return bytes
    }

    /// Polls a main-actor condition, failing after `timeout` seconds.
    @MainActor
    static func waitUntil(timeout: TimeInterval = 5, file: StaticString = #filePath, line: UInt = #line,
                          _ condition: () async -> Bool) async {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while ProcessInfo.processInfo.systemUptime < deadline {
            if await condition() {
                return
            }
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        XCTFail("Condition not met within \(timeout) s", file: file, line: line)
    }
}

final class DiarizationModelFilesTests: XCTestCase {
    private var folder: URL!

    override func setUpWithError() throws {
        folder = try DiarizationFixtures.temporaryFolder("SlovoDiarizationFiles").appendingPathComponent("diarization")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder.deletingLastPathComponent())
    }

    private var damaged: ModelStatus {
        .damaged(reason: CoreStrings.string("diarization.error.modelDamaged", table: .diarization))
    }

    func testNoFolderIsNotDownloaded() {
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, .notDownloaded(downloadBytes: 21_776_918))
        XCTAssertFalse(DiarizationModelFiles(modelFolder: folder).isComplete)
    }

    func testEmptyFoldersAreNotDownloaded() throws {
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("speaker-diarization/Embedding.mlmodelc"),
                                                withIntermediateDirectories: true)
        try Data().write(to: folder.appendingPathComponent(".DS_Store"))
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, .notDownloaded(downloadBytes: 21_776_918))
    }

    func testCompleteModelIsReadyWithItsSize() throws {
        let bytes = try DiarizationFixtures.installModel(in: folder)
        XCTAssertTrue(DiarizationModelFiles(modelFolder: folder).isComplete)
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, .ready(diskBytes: bytes))
    }

    func testFilesTheLoaderIgnoresAreNotRequired() throws {
        try DiarizationFixtures.installModel(in: folder, skipping: ["config.json"])
        XCTAssertTrue(DiarizationModelFiles(modelFolder: folder).isComplete)
    }

    func testPartialModelIsDamaged() throws {
        let others = DiarizationFixtures.compiledModels.dropFirst()
            .flatMap { ["\($0)/coremldata.bin", "\($0)/weights/weight.bin", "\($0)/model.mil"] }
        try DiarizationFixtures.installModel(in: folder, skipping: Set(others + ["plda-parameters.json"]))
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, damaged)
    }

    func testLeftoverPartialFileIsDamaged() throws {
        try DiarizationFixtures.installModel(in: folder, extras: ["Embedding.mlmodelc/weights/weight.bin.partial"])
        XCTAssertFalse(DiarizationModelFiles(modelFolder: folder).isComplete)
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, damaged)
    }

    func testMissingWeightsAreDamaged() throws {
        try DiarizationFixtures.installModel(in: folder, skipping: ["FBank.mlmodelc/weights/weight.bin"])
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, damaged)
    }

    func testMissingModelDescriptionIsDamaged() throws {
        try DiarizationFixtures.installModel(in: folder, skipping: ["PldaRho.mlmodelc/coremldata.bin"])
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, damaged)
    }

    func testMissingPLDAParametersAreDamaged() throws {
        try DiarizationFixtures.installModel(in: folder, skipping: ["plda-parameters.json"])
        XCTAssertEqual(DiarizationModelFiles(modelFolder: folder).status, damaged)
    }
}
