import Foundation
import XCTest
@testable import SlovoCore

/// A background save that fails keeps the meeting dirty and tells the interface once.
@MainActor
final class MeetingStoreSaveFailureTests: XCTestCase {
    private var temporary: URL!
    private var directories: AppDirectories!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingSaveFailure-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        Self.makeWritable(temporary)
        try? FileManager.default.removeItem(at: temporary)
    }

    private static func makeWritable(_ root: URL) {
        let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)
        try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: root.path)
        while let url = walker?.nextObject() as? URL {
            try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        }
    }

    func testFailedBackgroundSaveIsAnnouncedOnceAndFlushSavesAfterRecovery() async throws {
        let store = MeetingStore(directories: directories)
        var meeting = try store.create(title: "Before")
        let folder = store.folder(for: meeting.id)
        var announcements = 0
        let observer = NotificationCenter.default.addObserver(forName: StoreFileEvents.saveFailed, object: nil,
                                                              queue: nil) { _ in announcements += 1 }
        defer { NotificationCenter.default.removeObserver(observer) }

        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder.path)
        meeting.title = "After"
        store.update(meeting)
        try await Task.sleep(for: MeetingStore.saveDelay + .milliseconds(400))
        XCTAssertEqual(announcements, 1)
        XCTAssertThrowsError(try store.flush(), "the change is still waiting to be saved")

        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: folder.path)
        try store.flush()
        let saved = try String(contentsOf: store.recordURL(for: meeting.id), encoding: .utf8)
        XCTAssertTrue(saved.contains("After"))
    }
}
