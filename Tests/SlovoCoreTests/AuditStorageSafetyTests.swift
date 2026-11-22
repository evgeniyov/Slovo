import Foundation
import XCTest
@testable import SlovoCore

@MainActor
final class AuditStorageSafetyTests: XCTestCase {
    private var temporary: URL!
    private var directories: AppDirectories!
    private var defaults: UserDefaults!
    private var suiteName = ""

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoAuditStorage-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary.appendingPathComponent("Slovo"))
        try directories.createFolders()
        suiteName = "slovo.tests.audit.storage.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    func testHistoryDoesNotReadOrDeleteThroughASymlinkOutsideRecordings() throws {
        let outside = temporary.appendingPathComponent("Personal", isDirectory: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        let audio = outside.appendingPathComponent("keep.wav")
        let original = Data("personal recording".utf8)
        try original.write(to: audio)
        try FileManager.default.createSymbolicLink(
            at: directories.recordingsFolder.appendingPathComponent("2026-01-01"),
            withDestinationURL: outside)
        let history = HistoryStore(directories: directories)
        let entry = entry(audio: "2026-01-01/keep.wav")
        history.add(entry)

        XCTAssertNil(history.audioURL(for: entry), "a stored path must not expose an external recording")
        history.removeAudio(for: entry.id)
        XCTAssertEqual(try? Data(contentsOf: audio), original, "retention must not delete the symlink target")
    }

    func testHistoryDoesNotTreatADirectoryAsARecording() throws {
        let folder = directories.recordingsFolder.appendingPathComponent("2026-01-01/album.wav")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let personal = folder.appendingPathComponent("keep.txt")
        let original = Data("keep".utf8)
        try original.write(to: personal)
        let history = HistoryStore(directories: directories)
        let entry = entry(audio: "2026-01-01/album.wav")
        history.add(entry)

        XCTAssertNil(history.audioURL(for: entry))
        history.delete(id: entry.id)
        XCTAssertEqual(try? Data(contentsOf: personal), original)
    }

    func testOrphanRetentionKeepsDirectoriesAndSymlinksNamedWav() throws {
        let day = directories.recordingsFolder.appendingPathComponent("2000-01-01", isDirectory: true)
        let album = day.appendingPathComponent("album.wav", isDirectory: true)
        try FileManager.default.createDirectory(at: album, withIntermediateDirectories: true)
        let personal = album.appendingPathComponent("keep.txt")
        let original = Data("keep".utf8)
        try original.write(to: personal)
        let link = day.appendingPathComponent("linked.wav")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: personal)
        let plain = day.appendingPathComponent("recording.wav")
        try Data([1, 2, 3]).write(to: plain)
        let settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        settings.keepDictationAudio = .days7
        let cleaner = RetentionCleaner(history: HistoryStore(directories: directories),
                                       meetings: MeetingStore(directories: directories), settings: settings)

        XCTAssertEqual(cleaner.run().removedDictationAudio, 1)
        XCTAssertEqual(try? Data(contentsOf: personal), original)
        XCTAssertNotNil(try? FileManager.default.destinationOfSymbolicLink(atPath: link.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: plain.path))
    }

    func testTranscriptRecoveryRejectsOverflowingAndOutOfRangeTimestamps() {
        let text = """
            # Recovery
            [\(Int.max):00] Me: overflowing minutes
            [00:\(Int.max):00] Me: overflowing seconds
            [00:\(Int.max)] Me: unrepresentable meeting time
            [100000:00:00] Me: above the stored time limit
            [01:02:03] Me: valid
            [99999:59:59] Me: maximum supported time
            """
        let parsed = MeetingStore.parseTranscript(text)
        XCTAssertEqual(parsed.segments.map(\.text), ["valid", "maximum supported time"])
        XCTAssertEqual(parsed.segments.map(\.start), [3_723, MeetingTime.maximumSeconds])
    }

    func testOrphanRetentionDoesNotFollowALinkedDayDirectory() throws {
        let outside = temporary.appendingPathComponent("Personal", isDirectory: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        let audio = outside.appendingPathComponent("keep.wav")
        let original = Data("personal recording".utf8)
        try original.write(to: audio)
        try FileManager.default.createSymbolicLink(
            at: directories.recordingsFolder.appendingPathComponent("2000-01-01"),
            withDestinationURL: outside)
        let settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        settings.keepDictationAudio = .days7
        let cleaner = RetentionCleaner(history: HistoryStore(directories: directories),
                                       meetings: MeetingStore(directories: directories), settings: settings)

        XCTAssertEqual(cleaner.run().removedDictationAudio, 0)
        XCTAssertEqual(try Data(contentsOf: audio), original)
    }

    private func entry(audio: String) -> HistoryEntry {
        HistoryEntry(date: Date(), text: "test", rawText: "test", engineID: .gigaamRNNT,
                     languageMode: .mixed, appBundleID: nil, appName: nil, category: .other,
                     duration: 1, wordCount: 1, fixesCount: 0, audioFileName: audio)
    }
}
