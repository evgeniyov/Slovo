import Foundation
import XCTest
@testable import SlovoCore

/// `MeetingStore` against a temporary folder: round trips, ordering, audio removal,
/// debounced saves, damaged files and meetings cut off by a quit.
@MainActor
final class MeetingsStoreTests: XCTestCase {
    private var temporary: URL!
    private var directories: AppDirectories!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingsStoreTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: temporary)
    }

    func testEmptyFolderLoadsNothing() {
        XCTAssertEqual(MeetingStore(directories: directories).meetings, [])
        let missing = AppDirectories(base: temporary.appendingPathComponent("nothing-here", isDirectory: true))
        XCTAssertEqual(MeetingStore(directories: missing).meetings, [])
    }

    func testCreateUpdateAndReload() throws {
        let store = MeetingStore(directories: directories)
        var meeting = try store.create(title: "  Design review ")
        XCTAssertEqual(meeting.title, "Design review")
        XCTAssertEqual(meeting.status, .recording)
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.recordURL(for: meeting.id).path))
        XCTAssertEqual(store.meetings, [meeting])

        meeting.status = .transcribed
        meeting.duration = 1_234.5
        meeting.hasSystemAudio = true
        meeting.engineID = .parakeet
        meeting.segments = [TranscriptSegment(start: 1, end: 2, speaker: .others, text: "Hello")]
        store.update(meeting)
        XCTAssertEqual(store.meeting(id: meeting.id), meeting)
        try store.flush()

        XCTAssertEqual(MeetingStore(directories: directories).meetings, [meeting])
    }

    func testEmptyTitleGetsTheDefaultTitle() throws {
        let store = MeetingStore(directories: directories)
        let meeting = try store.create(title: "   ")
        XCTAssertFalse(meeting.title.isEmpty)
        XCTAssertEqual(meeting.title, MeetingRecorder.defaultTitle(for: meeting.createdAt))
    }

    func testMeetingsAreNewestFirst() throws {
        let store = MeetingStore(directories: directories)
        var old = try store.create(title: "Old")
        var middle = try store.create(title: "Middle")
        let new = try store.create(title: "New")
        old.createdAt = Date(timeIntervalSinceNow: -7_200)
        middle.createdAt = Date(timeIntervalSinceNow: -3_600)
        store.update(middle)
        store.update(old)
        XCTAssertEqual(store.meetings.map(\.title), ["New", "Middle", "Old"])
        try store.flush()
        XCTAssertEqual(MeetingStore(directories: directories).meetings.map(\.id), [new.id, middle.id, old.id])
    }

    func testUpdateOfUnknownMeetingIsIgnored() throws {
        let store = MeetingStore(directories: directories)
        store.update(MeetingRecord(title: "Ghost"))
        XCTAssertEqual(store.meetings, [])
        try store.flush()
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: directories.meetingsFolder.path), [])
    }

    func testDebouncedSaveWritesWithoutFlush() async throws {
        let store = MeetingStore(directories: directories)
        var meeting = try store.create(title: "Draft")
        meeting.title = "Renamed"
        store.update(meeting)

        var reloaded: MeetingRecord?
        for _ in 0..<60 {
            try await Task.sleep(for: .milliseconds(50))
            reloaded = MeetingStore(directories: directories).meeting(id: meeting.id)
            if reloaded?.title == "Renamed" { break }
        }
        XCTAssertEqual(reloaded?.title, "Renamed")
    }

    func testDeleteRemovesTheFolder() throws {
        let store = MeetingStore(directories: directories)
        let keep = try store.create(title: "Keep")
        let drop = try store.create(title: "Drop")
        try Data("audio".utf8).write(to: store.microphoneAudioURL(for: drop.id))

        store.delete(id: drop.id)

        XCTAssertEqual(store.meetings.map(\.id), [keep.id])
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.folder(for: drop.id).path))
        XCTAssertEqual(MeetingStore(directories: directories).meetings.map(\.id), [keep.id])
    }

    func testDeleteAudioRemovesTracksAndClearsTheFlag() throws {
        let store = MeetingStore(directories: directories)
        var meeting = try store.create(title: "Call")
        meeting.status = .transcribed
        store.update(meeting)
        try writeTone(to: store.microphoneAudioURL(for: meeting.id), seconds: 1)
        try writeTone(to: store.systemAudioURL(for: meeting.id), seconds: 1)
        try Data("# Call".utf8).write(to: store.transcriptURL(for: meeting.id))

        store.deleteAudio(id: meeting.id)
        try store.flush()

        XCTAssertEqual(store.meeting(id: meeting.id)?.hasAudio, false)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.microphoneAudioURL(for: meeting.id).path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.systemAudioURL(for: meeting.id).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.transcriptURL(for: meeting.id).path))
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: meeting.id)?.hasAudio, false)
    }

    func testDamagedRecordIsMovedAsideAndRebuiltFromSurvivingAudio() throws {
        let id = UUID()
        let folder = directories.meetingFolder(id: id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data("{ not json".utf8).write(to: folder.appendingPathComponent("meeting.json"))
        try writeTone(to: folder.appendingPathComponent("mic.caf"), seconds: 2)

        let store = MeetingStore(directories: directories)

        let recovered = try XCTUnwrap(store.meeting(id: id))
        XCTAssertEqual(recovered.status, .recorded)
        XCTAssertTrue(recovered.hasAudio)
        XCTAssertFalse(recovered.hasSystemAudio)
        XCTAssertEqual(recovered.duration, 2, accuracy: 0.01)
        let names = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        XCTAssertTrue(names.contains { $0.hasPrefix("meeting.corrupt-") && $0.hasSuffix(".json") }, "\(names)")
        XCTAssertTrue(names.contains("meeting.json"), "the rebuilt record is saved")
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: id), recovered)
    }

    func testDamagedRecordWithoutAudioIsSkipped() throws {
        let store = MeetingStore(directories: directories)
        let good = try store.create(title: "Good")
        let id = UUID()
        let folder = directories.meetingFolder(id: id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data([0xFF, 0x00, 0x13]).write(to: folder.appendingPathComponent("meeting.json"))
        // Folders that are not meetings are left alone.
        try FileManager.default.createDirectory(at: directories.meetingsFolder.appendingPathComponent("Notes"),
                                                withIntermediateDirectories: true)

        let reloaded = MeetingStore(directories: directories)

        XCTAssertEqual(reloaded.meetings.map(\.id), [good.id])
        let names = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        XCTAssertEqual(names.count, 1)
        XCTAssertTrue(names.first?.hasPrefix("meeting.corrupt-") == true, "\(names)")
    }

    func testMeetingsCutOffByAQuitAreRepairedAtLaunch() throws {
        let store = MeetingStore(directories: directories)
        let recording = try store.create(title: "Was recording")
        try writeTone(to: store.microphoneAudioURL(for: recording.id), seconds: 3)

        var transcribing = try store.create(title: "Was transcribing")
        transcribing.status = .transcribing
        store.update(transcribing)

        var transcribingWithoutAudio = try store.create(title: "Audio gone")
        transcribingWithoutAudio.status = .transcribing
        transcribingWithoutAudio.hasAudio = false
        store.update(transcribingWithoutAudio)
        try store.flush()

        let relaunched = MeetingStore(directories: directories)

        let repairedRecording = try XCTUnwrap(relaunched.meeting(id: recording.id))
        XCTAssertEqual(repairedRecording.status, .recorded)
        XCTAssertEqual(repairedRecording.duration, 3, accuracy: 0.01)
        XCTAssertTrue(repairedRecording.hasAudio)
        XCTAssertEqual(relaunched.meeting(id: transcribing.id)?.status, .recorded)
        let failed = try XCTUnwrap(relaunched.meeting(id: transcribingWithoutAudio.id))
        XCTAssertEqual(failed.status, .failed)
        XCTAssertNotNil(failed.errorMessage)
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: recording.id)?.status, .recorded)
    }

    func testAReRunCutOffByAQuitKeepsTheEarlierTranscript() throws {
        let store = MeetingStore(directories: directories)
        store.transcriptLanguage = { .en }
        var meeting = try store.create(title: "Коворк")
        try writeTone(to: store.microphoneAudioURL(for: meeting.id), seconds: 1)
        meeting.status = .transcribed
        meeting.engineID = .parakeet
        meeting.segments = [TranscriptSegment(start: 1, end: 5, speaker: .me, text: "Привет", speakerID: "s1")]
        meeting.speakers = [MeetingSpeaker(id: "s1", track: .microphone, number: 1, displayName: "Аня", colorIndex: 0)]
        meeting.diarization = MeetingDiarization(state: .applied, speakersPerTrack: ["microphone": 1])
        store.update(meeting)
        try store.flush()
        let document = try Data(contentsOf: store.transcriptURL(for: meeting.id))

        // What "Process again" keeps on disk until the new transcript is ready.
        meeting.status = .transcribing
        store.update(meeting)
        // A transcribed meeting that was silent has no segments, only its engine.
        var silent = try store.create(title: "Тишина")
        silent.status = .transcribing
        silent.engineID = .gigaamCTC
        silent.hasAudio = false
        store.update(silent)
        try store.flush()

        let relaunched = MeetingStore(directories: directories)

        var expected = meeting
        expected.status = .transcribed
        XCTAssertEqual(relaunched.meeting(id: meeting.id), expected)
        XCTAssertEqual(try Data(contentsOf: store.transcriptURL(for: meeting.id)), document)
        XCTAssertEqual(relaunched.meeting(id: silent.id)?.status, .transcribed)
        XCTAssertNil(relaunched.meeting(id: silent.id)?.errorMessage)
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: meeting.id), expected, "the repair is saved")
    }

    func testFileLayout() {
        let store = MeetingStore(directories: directories)
        let id = UUID()
        XCTAssertEqual(store.folder(for: id), directories.meetingFolder(id: id))
        XCTAssertEqual(store.recordURL(for: id).lastPathComponent, "meeting.json")
        XCTAssertEqual(store.microphoneAudioURL(for: id).deletingLastPathComponent(), store.folder(for: id))
    }

    /// Rebuilt from the audio alone, a transcribed meeting would read as never transcribed,
    /// and transcribing it again would overwrite the document that still holds its lines,
    /// names and moments; so the transcript comes back even when the audio survived.
    func testDamagedRecordWithAudioAndATranscriptComesBackTranscribed() throws {
        let id = UUID()
        let folder = directories.meetingFolder(id: id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data("{ not json".utf8).write(to: folder.appendingPathComponent("meeting.json"))
        try writeTone(to: folder.appendingPathComponent("mic.caf"), seconds: 30)
        try writeTone(to: folder.appendingPathComponent("system.caf"), seconds: 30)
        let document = """
            # Коворк

            15 сентября 2026 г., 11:02 · 00:30

            Участники: Я, Аня

            [00:04] Я: Всем привет.

            [00:12] ★ Аня: Давайте к пятнице соберём прототип.

            """
        try Data(document.utf8).write(to: folder.appendingPathComponent("transcript.md"))

        let store = MeetingStore(directories: directories)

        let recovered = try XCTUnwrap(store.meeting(id: id))
        XCTAssertEqual(recovered.status, .transcribed)
        XCTAssertEqual(recovered.title, "Коворк")
        XCTAssertTrue(recovered.hasAudio, "the audio is still there to process again")
        XCTAssertTrue(recovered.hasSystemAudio)
        XCTAssertEqual(recovered.duration, 30, accuracy: 0.01)
        XCTAssertEqual(recovered.segments.map(\.text), ["Всем привет.", "Давайте к пятнице соберём прототип."])
        XCTAssertEqual(recovered.speakers.map(\.displayName), ["Аня"])
        XCTAssertEqual(recovered.highlights.map(\.time), [12])
        XCTAssertEqual(try String(contentsOf: folder.appendingPathComponent("transcript.md"), encoding: .utf8), document,
                       "the transcript is the source and stays as found")
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: id), recovered)
    }

    /// A star the app has not saved yet, when the app becomes active and re-reads the
    /// file, is the app's own change: the next save keeps it, and keeps an unstar too.
    func testRefreshKeepsAStarOrAnUnstarNotSavedYet() throws {
        let store = MeetingStore(directories: directories)
        var meeting = try store.create(title: "Коворк")
        let kept = MeetingHighlight(time: 5, source: .transcript)
        let removed = MeetingHighlight(time: 9, source: .transcript)
        meeting.highlights = [kept, removed]
        store.update(meeting)
        try store.flush()

        let added = MeetingHighlight(time: 12, source: .transcript)
        meeting.highlights = [kept, added]
        store.update(meeting)
        store.refreshClaudeNotes()
        XCTAssertEqual(store.meeting(id: meeting.id)?.highlights.map(\.id), [kept.id, added.id])
        try store.flush()

        XCTAssertEqual(store.meeting(id: meeting.id)?.highlights.map(\.id), [kept.id, added.id])
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: meeting.id)?.highlights.map(\.id),
                       [kept.id, added.id])
    }

    private func writeTone(to url: URL, seconds: Double) throws {
        let writer = try MeetingTrackWriter(url: url)
        writer.append((0..<Int(seconds * 16_000)).map { Float(0.2 * sin(Double($0) / 10)) })
        writer.finish()
    }
}
