import CryptoKit
import Foundation
import XCTest
@testable import SlovoCore

/// The single-file download Whisper's model uses: resumes, verifies, cleans up.
final class DictationVerifiedDownloadTests: XCTestCase {
    /// Serves `StubServer.body` for any request, honouring `Range` unless told not to.
    final class StubServer: URLProtocol {
        struct Config {
            var body = Data()
            var honoursRange = true
            var status: Int?
            var chunkBytes: Int?
            /// Requests' Range headers, in order.
            var ranges: [String?] = []
        }

        static let config = DictationFakes.Locked(Config())

        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

        override func startLoading() {
            let range = request.value(forHTTPHeaderField: "Range")
            var config = Self.config.value
            config.ranges.append(range)
            Self.config.value = config
            let url = request.url ?? URL(fileURLWithPath: "/")
            let total = config.body.count
            var status = config.status ?? 200
            var body = config.body
            var headers = ["Content-Length": "\(total)"]
            if config.status == nil, config.honoursRange, let range,
               let start = Int(range.dropFirst("bytes=".count).split(separator: "-").first ?? "") {
                status = 206
                body = config.body.subdata(in: start..<total)
                headers = ["Content-Length": "\(body.count)", "Content-Range": "bytes \(start)-\(total - 1)/\(total)"]
            }
            let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            if (200..<300).contains(status) {
                let chunkSize = max(1, config.chunkBytes ?? max(1, body.count))
                for start in stride(from: 0, to: body.count, by: chunkSize) {
                    client?.urlProtocol(self, didLoad: body.subdata(in: start..<min(start + chunkSize, body.count)))
                }
            }
            client?.urlProtocolDidFinishLoading(self)
        }

        override func stopLoading() {}
    }

    private var folder: URL!
    private var body = Data()

    override func setUp() {
        folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoVerifiedDownloadTests-\(UUID().uuidString)", isDirectory: true)
        body = Data((0..<200_000).map { UInt8(truncatingIfNeeded: $0 &* 31 &+ 7) })
        StubServer.config.value = StubServer.Config(body: body)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: folder)
    }

    private func download(sha256: String? = nil) -> VerifiedFileDownload {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubServer.self]
        let digest = SHA256.hash(data: body).map { String(format: "%02x", $0) }.joined()
        return VerifiedFileDownload(url: URL(string: "https://models.invalid/model.bin")!,
                                    destination: folder.appendingPathComponent("model.bin"),
                                    size: Int64(body.count), sha256: sha256 ?? digest, configuration: configuration)
    }

    func testDownloadsVerifiesAndMovesIntoPlace() async throws {
        let progress = DictationFakes.Locked<[Double]>([])
        let job = download()
        try await job.run(progress: { value in progress.value.append(value) })

        XCTAssertEqual(try Data(contentsOf: job.destination), body)
        XCTAssertFalse(FileManager.default.fileExists(atPath: job.partial.path))
        XCTAssertEqual(StubServer.config.value.ranges, [nil])
        XCTAssertEqual(progress.value.last, 1)
    }

    func testAnInterruptedDownloadResumesFromWhereItStopped() async throws {
        let job = download()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try body.prefix(80_000).write(to: job.partial)

        try await job.run(progress: { _ in })
        XCTAssertEqual(StubServer.config.value.ranges, ["bytes=80000-"])
        XCTAssertEqual(try Data(contentsOf: job.destination), body)
    }

    func testAServerIgnoringTheRangeStartsOver() async throws {
        StubServer.config.value.honoursRange = false
        let job = download()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(repeating: 0xFF, count: 50_000).write(to: job.partial)

        try await job.run(progress: { _ in })
        XCTAssertEqual(try Data(contentsOf: job.destination), body)
    }

    func testAChecksumMismatchRemovesTheFile() async throws {
        let job = download(sha256: String(repeating: "0", count: 64))
        do {
            try await job.run(progress: { _ in })
            XCTFail("A file that does not match its checksum must not be installed")
        } catch {
            XCTAssertFalse(error is CancellationError)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: job.destination.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: job.partial.path), "The next attempt starts clean")
    }

    func testOversizedResponseNeverWritesPastThePinnedSize() async throws {
        let job = download()
        StubServer.config.value.body = body + Data(repeating: 0xFF, count: 50_000)
        StubServer.config.value.chunkBytes = 40_000
        do {
            try await job.run(progress: { _ in })
            XCTFail("An oversized response must fail before it can fill the disk")
        } catch {
            XCTAssertFalse(error is CancellationError)
        }
        XCTAssertLessThanOrEqual(VerifiedFileDownload.fileSize(job.partial), job.size)
        XCTAssertFalse(FileManager.default.fileExists(atPath: job.destination.path))
    }

    func testOversizedResumedResponseDoesNotExtendPastThePinnedSize() async throws {
        let job = download()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try body.prefix(80_000).write(to: job.partial)
        StubServer.config.value.body = body + Data(repeating: 0xFF, count: 50_000)
        StubServer.config.value.chunkBytes = 40_000
        do {
            try await job.run(progress: { _ in })
            XCTFail("The size limit also applies to resumed responses")
        } catch {
            XCTAssertFalse(error is CancellationError)
        }
        XCTAssertEqual(StubServer.config.value.ranges, ["bytes=80000-"])
        XCTAssertLessThanOrEqual(VerifiedFileDownload.fileSize(job.partial), job.size)
        XCTAssertFalse(FileManager.default.fileExists(atPath: job.destination.path))
    }

    func testAnHTTPErrorKeepsWhatWasDownloaded() async throws {
        StubServer.config.value.status = 503
        let job = download()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try body.prefix(1_000).write(to: job.partial)
        do {
            try await job.run(progress: { _ in })
            XCTFail("A server error must fail the download")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("503"), error.localizedDescription)
        }
        XCTAssertEqual(VerifiedFileDownload.fileSize(job.partial), 1_000, "Download again continues from here")
    }

    func testCancellingLeavesNothingBehind() async throws {
        let job = download()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try body.prefix(1_000).write(to: job.partial)
        let pending = Task { try await job.run(progress: { _ in }) }
        pending.cancel()
        do {
            try await pending.value
        } catch {
            XCTAssertTrue(error is CancellationError, "\(error)")
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: job.partial.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: job.destination.path))
    }

    func testRangeStartIsReadFromContentRange() {
        let response = HTTPURLResponse(url: URL(string: "https://x.invalid")!, statusCode: 206, httpVersion: nil,
                                       headerFields: ["Content-Range": "bytes 100-199/200"])!
        XCTAssertEqual(RangedFileDownload.rangeStart(of: response), 100)
    }

    /// Resumes the real download for its last 3 MB, so the range request, the Hugging Face
    /// redirect and the pinned checksum are all checked without fetching 574 MB again.
    /// Needs SLOVO_NETWORK_TESTS=1 and SLOVO_WHISPER_MODEL pointing at a downloaded model.
    func testResumesTheRealWhisperDownload() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard environment["SLOVO_NETWORK_TESTS"] == "1", let path = environment["SLOVO_WHISPER_MODEL"] else {
            throw XCTSkip("Set SLOVO_NETWORK_TESTS=1 and SLOVO_WHISPER_MODEL to a downloaded model to run this test")
        }
        let model = URL(fileURLWithPath: path)
        try XCTSkipUnless(VerifiedFileDownload.fileSize(model) == WhisperEngine.Model.size, "Not the pinned model: \(path)")
        let job = try VerifiedFileDownload.whisper(into: folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // A copy on the same APFS volume is a clone and takes no space.
        try FileManager.default.copyItem(at: model, to: job.partial)
        let handle = try FileHandle(forWritingTo: job.partial)
        try handle.truncate(atOffset: UInt64(WhisperEngine.Model.size - 3_000_000))
        try handle.close()

        try await job.run(progress: { _ in })
        XCTAssertEqual(VerifiedFileDownload.fileSize(job.destination), WhisperEngine.Model.size)
        XCTAssertTrue(ModelManager.whisperStatus(folder: folder).isUsable)
    }

    func testWhisperModelIsPinned() {
        XCTAssertEqual(WhisperEngine.Model.address,
                       "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin")
        let folder = URL(fileURLWithPath: "/models/whisper-v3-turbo", isDirectory: true)
        XCTAssertEqual((try? VerifiedFileDownload.whisper(into: folder))?.destination.path,
                       "/models/whisper-v3-turbo/ggml-large-v3-turbo-q5_0.bin")
        XCTAssertEqual(WhisperEngine.Model.size, 574_041_195)
        XCTAssertEqual(WhisperEngine.Model.sha256, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")
        XCTAssertEqual(ModelManager.downloadBytes(for: .whisperTurbo), WhisperEngine.Model.size)
    }
}
