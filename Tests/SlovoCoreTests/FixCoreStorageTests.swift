import Foundation
import XCTest
@testable import SlovoCore

/// Retention of dictation recordings no history entry refers to, and meetings whose
/// record was damaged after their audio was already gone.
@MainActor
final class FixCoreStorageTests: XCTestCase {
    private var suiteName = ""
    private var defaults: UserDefaults!
    private var settings: AppSettings!
    private var temporary: URL!
    private var directories: AppDirectories!

    override func setUp() async throws {
        suiteName = "slovo.tests.fixcore.storage.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoFixCoreStorage-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    private func cleaner(_ history: HistoryStore) -> RetentionCleaner {
        RetentionCleaner(history: history, meetings: MeetingStore(directories: directories), settings: settings)
    }

    /// Writes a recording as dictation leaves it: in the folder of its day, modified then.
    @discardableResult
    private func writeRecording(daysAgo: Double, now: Date = Date()) throws -> (url: URL, relativePath: String) {
        let date = now.addingTimeInterval(-daysAgo * 24 * 3600)
        let relativePath = WAVFile.relativePath(for: UUID(), date: date)
        let url = directories.recordingURL(relativePath: relativePath)
        try WAVFile.write(DictationFakes.speech(seconds: 0.5), to: url)
        try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: url.path)
        return (url, relativePath)
    }

    // MARK: - Recordings without an entry

    func testP10_AudioOfAMovedAsideHistoryIsStillSubjectToRetention() throws {
        let history = HistoryStore(directories: directories)
        let id = UUID()
        let date = Date().addingTimeInterval(-40 * 24 * 3600)
        let relative = WAVFile.relativePath(for: id, date: date)
        let wav = directories.recordingURL(relativePath: relative)
        try WAVFile.write(DictationFakes.speech(seconds: 1), to: wav)
        history.add(HistoryEntry(id: id, date: date, text: "секрет", rawText: "секрет", engineID: .gigaamRNNT,
                                 languageMode: .mixed, appBundleID: nil, appName: nil, category: .other,
                                 duration: 1, wordCount: 1, fixesCount: 0, audioFileName: relative))
        try history.flush()
        // history.json gets damaged: the next launch moves it aside and starts empty.
        try Data("{ broken".utf8).write(to: directories.historyFile)
        let reopened = HistoryStore(directories: directories)
        XCTAssertNotNil(reopened.loadWarning)

        settings.keepDictationAudio = .never
        settings.keepHistory = .days7
        let report = RetentionCleaner(history: reopened, meetings: MeetingStore(directories: directories),
                                      settings: settings).run()
        // The file was written just now, but its day folder says it is 40 days old.
        XCTAssertFalse(FileManager.default.fileExists(atPath: wav.path),
                       "a 40-day-old recording survives 'keep audio: never' because no entry points to it")
        XCTAssertEqual(report.removedDictationAudio, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: wav.deletingLastPathComponent().path),
                       "the emptied day folder goes too")
    }

    func testUnreferencedRecordingsFollowTheAudioPeriod() throws {
        let history = HistoryStore(directories: directories)
        let recent = try writeRecording(daysAgo: 3)
        let old = try writeRecording(daysAgo: 8)
        // A referenced recording of the same age is handled through its entry.
        let kept = try writeRecording(daysAgo: 8.2)
        history.add(HistoryEntry(date: Date().addingTimeInterval(-60), text: "текст", rawText: "текст",
                                 engineID: .gigaamRNNT, languageMode: .mixed, appBundleID: nil, appName: nil,
                                 category: .other, duration: 1, wordCount: 1, fixesCount: 0,
                                 audioFileName: kept.relativePath))

        settings.keepDictationAudio = .days7
        let report = cleaner(history).run()

        XCTAssertEqual(report.removedDictationAudio, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: recent.url.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: old.url.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: kept.url.path), "an entry still refers to it")
    }

    func testPreviewCountsWhatARunRemovesAndChangesNothing() throws {
        let history = HistoryStore(directories: directories)
        let old = try writeRecording(daysAgo: 8)
        let recent = try writeRecording(daysAgo: 1)
        let oldSize = try XCTUnwrap(FileManager.default.attributesOfItem(atPath: old.url.path)[.size] as? Int64
                                    ?? (FileManager.default.attributesOfItem(atPath: old.url.path)[.size] as? NSNumber)?.int64Value)
        history.add(HistoryEntry(date: Date().addingTimeInterval(-30 * 24 * 3600), text: "старое", rawText: "старое",
                                 engineID: .gigaamRNNT, languageMode: .mixed, appBundleID: nil, appName: nil,
                                 category: .other, duration: 1, wordCount: 1, fixesCount: 0, audioFileName: nil))
        settings.keepDictationAudio = .days7
        settings.keepHistory = .days7
        let cleaner = cleaner(history)

        let preview = cleaner.preview()
        XCTAssertEqual(preview.entries, 1)
        XCTAssertEqual(preview.dictationAudio, 1)
        XCTAssertEqual(preview.audioBytes, oldSize)
        XCTAssertTrue(FileManager.default.fileExists(atPath: old.url.path), "a preview removes nothing")
        XCTAssertEqual(history.entries.count, 1)

        let report = cleaner.run()
        XCTAssertEqual(report.removedEntries, preview.entries)
        XCTAssertEqual(report.removedDictationAudio, preview.dictationAudio)
        XCTAssertTrue(FileManager.default.fileExists(atPath: recent.url.path))
        XCTAssertTrue(cleaner.preview().isEmpty)
    }

    func testKeepForeverKeepsUnreferencedRecordings() throws {
        let history = HistoryStore(directories: directories)
        let old = try writeRecording(daysAgo: 400)
        settings.keepDictationAudio = .forever
        XCTAssertEqual(cleaner(history).run().removedDictationAudio, 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: old.url.path))
    }

    /// With "never", a recording nobody refers to still gets a few minutes, in case its
    /// entry is about to be added.
    func testNeverGivesAFreshRecordingAGracePeriod() throws {
        let history = HistoryStore(directories: directories)
        let now = Date()
        let fresh = try writeRecording(daysAgo: 0, now: now)
        settings.keepDictationAudio = .never

        XCTAssertEqual(cleaner(history).run(now: now.addingTimeInterval(60)).removedDictationAudio, 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: fresh.url.path))

        let later = now.addingTimeInterval(RetentionCleaner.orphanGracePeriod + 1)
        XCTAssertEqual(cleaner(history).run(now: later).removedDictationAudio, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: fresh.url.path))
    }

    func testOtherFilesInRecordingsAreLeftAlone() throws {
        let history = HistoryStore(directories: directories)
        let old = try writeRecording(daysAgo: 30)
        let note = old.url.deletingLastPathComponent().appendingPathComponent("notes.txt")
        try Data("keep".utf8).write(to: note)
        settings.keepDictationAudio = .days7

        XCTAssertEqual(cleaner(history).run().removedDictationAudio, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: note.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: old.url.deletingLastPathComponent().path),
                      "a day folder with other files stays")
    }

    func testDayFolderNames() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "Europe/Moscow"))
        let end = try XCTUnwrap(RetentionCleaner.endOfDay(named: "2026-09-15", calendar: calendar))
        XCTAssertEqual(end, calendar.date(from: DateComponents(year: 2026, month: 9, day: 16)))
        for name in ["2026-9-15", "2026-13-01", "2026-09-32", "notes", "2026-09-15-x", ""] {
            XCTAssertNil(RetentionCleaner.endOfDay(named: name, calendar: calendar), name)
        }
    }

    // MARK: - Meetings with a damaged record

    /// A transcribed meeting whose audio was removed ("delete after transcription") and
    /// whose meeting.json is later damaged: the transcript is still on disk.
    func testP11_TranscribedMeetingWithDamagedRecordStaysReachable() throws {
        let store = MeetingStore(directories: directories)
        var meeting = try store.create(title: "Планёрка")
        meeting.status = .transcribed
        meeting.hasAudio = false
        meeting.segments = [TranscriptSegment(start: 0, end: 2, speaker: .me, text: "Итоги пришлю вечером")]
        store.update(meeting)
        try store.flush()
        try Data("# Планёрка\n[00:00] Me: Итоги пришлю вечером\n".utf8).write(to: store.transcriptURL(for: meeting.id))
        try Data("{ broken".utf8).write(to: store.recordURL(for: meeting.id))

        let reloaded = MeetingStore(directories: directories)
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: store.folder(for: meeting.id).path)
        XCTAssertEqual(reloaded.meetings.map(\.id), [meeting.id],
                       "the meeting vanished from the list; files left on disk: \(leftovers.sorted())")

        let recovered = try XCTUnwrap(reloaded.meeting(id: meeting.id))
        XCTAssertEqual(recovered.title, "Планёрка")
        XCTAssertEqual(recovered.status, .transcribed)
        XCTAssertFalse(recovered.hasAudio)
        XCTAssertEqual(recovered.segments.map(\.text), ["Итоги пришлю вечером"])
        XCTAssertEqual(recovered.segments.map(\.speaker), [.me])
        XCTAssertTrue(leftovers.contains("meeting.json"), "the rebuilt record is saved")
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: meeting.id), recovered)

        reloaded.delete(id: meeting.id)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.folder(for: meeting.id).path))
    }

    func testTranscriptWrittenByTheMergerComesBackInEitherLanguage() throws {
        let segments = [
            TranscriptSegment(start: 5, end: 9, speaker: .me, text: "Начнём с релиза"),
            TranscriptSegment(start: 12, end: 20, speaker: .others, text: "Сборка готова: осталось подписать"),
            TranscriptSegment(start: 3_725, end: 3_730, speaker: .me, text: "Спасибо, [всем]")
        ]
        for language in [AppLanguage.ru, .en] {
            let id = UUID()
            let store = MeetingStore(directories: directories)
            try FileManager.default.createDirectory(at: store.folder(for: id), withIntermediateDirectories: true)
            let markdown = TranscriptMerger.markdown(title: "Релиз", date: Date(), segments: segments, language: language)
            try Data(markdown.utf8).write(to: store.transcriptURL(for: id))
            try Data([0x7B, 0x00]).write(to: store.recordURL(for: id))

            let recovered = try XCTUnwrap(MeetingStore(directories: directories).meeting(id: id), "\(language)")
            XCTAssertEqual(recovered.title, "Релиз")
            XCTAssertEqual(recovered.status, .transcribed)
            XCTAssertEqual(recovered.segments.map(\.text), segments.map(\.text), "\(language)")
            XCTAssertEqual(recovered.segments.map(\.speaker), segments.map(\.speaker), "\(language)")
            XCTAssertEqual(recovered.segments.map(\.start), [5, 12, 3_725], "\(language)")
        }
    }

    func testUnreadableTranscriptStillGivesAMeetingThatCanBeDeleted() throws {
        let id = UUID()
        let folder = directories.meetingFolder(id: id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data([0xC3, 0x28, 0xFF]).write(to: folder.appendingPathComponent("transcript.md"))
        try Data("{ broken".utf8).write(to: folder.appendingPathComponent("meeting.json"))

        let store = MeetingStore(directories: directories)
        let recovered = try XCTUnwrap(store.meeting(id: id))
        XCTAssertEqual(recovered.status, .failed)
        XCTAssertFalse(recovered.hasAudio)
        XCTAssertEqual(recovered.errorMessage, CoreStrings.string("meetings.error.recordDamaged", table: .meetings))

        store.delete(id: id)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.path))
        for key in ["meetings.error.recordDamaged"] {
            XCTAssertNotEqual(CoreStrings.string(key, table: .meetings, language: .ru),
                              CoreStrings.string(key, table: .meetings, language: .en))
        }
    }
}
