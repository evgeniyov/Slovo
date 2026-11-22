import Foundation
import XCTest
@testable import SlovoCore

/// A save that fails keeps the change pending, tells the interface once and succeeds
/// once the cause is gone.
@MainActor
final class StoreFileSaveFailureTests: XCTestCase {
    private var temporary: URL!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoSaveFailure-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
    }

    override func tearDown() async throws {
        try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: temporary.path)
        try? FileManager.default.removeItem(at: temporary)
    }

    func testFailedSaveIsAnnouncedOnceAndKeptPendingUntilItSucceeds() throws {
        let url = temporary.appendingPathComponent("blocked/data.json")
        // A regular file where the folder should be makes every write fail.
        FileManager.default.createFile(atPath: temporary.appendingPathComponent("blocked").path, contents: Data())
        let storage = StoreFile<[String]>(url: url, delay: .milliseconds(1))
        var announcements = 0
        let observer = NotificationCenter.default.addObserver(forName: StoreFileEvents.saveFailed, object: nil,
                                                              queue: nil) { _ in announcements += 1 }
        defer { NotificationCenter.default.removeObserver(observer) }

        storage.scheduleSave(["a"])
        let first = expectation(description: "first failure")
        Task { try? await Task.sleep(for: .milliseconds(100)); first.fulfill() }
        wait(for: [first], timeout: 2)
        XCTAssertNotNil(storage.saveError)
        XCTAssertEqual(announcements, 1)

        storage.scheduleSave(["a", "b"])
        let second = expectation(description: "second failure")
        Task { try? await Task.sleep(for: .milliseconds(100)); second.fulfill() }
        wait(for: [second], timeout: 2)
        XCTAssertEqual(announcements, 1, "a run of failures is announced once")

        try FileManager.default.removeItem(at: temporary.appendingPathComponent("blocked"))
        storage.scheduleSave(["a", "b", "c"])
        let third = expectation(description: "recovered")
        Task { try? await Task.sleep(for: .milliseconds(100)); third.fulfill() }
        wait(for: [third], timeout: 2)
        XCTAssertNil(storage.saveError)
        XCTAssertEqual(try JSONDecoder().decode([String].self, from: Data(contentsOf: url)), ["a", "b", "c"])
    }
}
