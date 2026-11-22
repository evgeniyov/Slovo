import Foundation
import XCTest
@testable import SlovoCore

/// Retention rules and the statistics derived from history.
@MainActor
final class InputRetentionInsightsTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_789_000_000) // 2026-09-10 00:26:40 UTC
    private let day: TimeInterval = 86_400
    private var temporary = FileManager.default.temporaryDirectory
    private var suiteName = ""
    private var defaults = UserDefaults.standard

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoInputRetentionTests-\(UUID().uuidString)", isDirectory: true)
        suiteName = "slovo.tests.retention.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Retention helpers

    func testExpiredEntries() {
        let fresh = entry(age: 3600)
        let week = entry(age: 7 * day)
        let old = entry(age: 40 * day)
        let entries = [fresh, week, old]
        XCTAssertEqual(RetentionCleaner.expiredEntryIDs(entries, keep: .forever, now: now), [])
        XCTAssertEqual(RetentionCleaner.expiredEntryIDs(entries, keep: .days30, now: now), [old.id])
        XCTAssertEqual(RetentionCleaner.expiredEntryIDs(entries, keep: .days7, now: now), [week.id, old.id])
        XCTAssertEqual(RetentionCleaner.expiredEntryIDs(entries, keep: .hours24, now: now), [week.id, old.id])
        XCTAssertEqual(RetentionCleaner.expiredEntryIDs([entry(age: 23 * 3600)], keep: .hours24, now: now), [])
    }

    func testExpiredAudioOnlyCountsEntriesWithAudio() {
        let fresh = entry(age: 60, audio: "a.wav")
        let old = entry(age: 8 * day, audio: "b.wav")
        let oldWithoutAudio = entry(age: 8 * day)
        let entries = [fresh, old, oldWithoutAudio]
        XCTAssertEqual(RetentionCleaner.expiredAudioIDs(entries, keep: .days7, now: now), [old.id])
        XCTAssertEqual(RetentionCleaner.expiredAudioIDs(entries, keep: .never, now: now), [fresh.id, old.id])
        XCTAssertEqual(RetentionCleaner.expiredAudioIDs(entries, keep: .days30, now: now), [])
        XCTAssertEqual(RetentionCleaner.expiredAudioIDs(entries, keep: .forever, now: now), [])
    }

    func testExpiredMeetingAudio() {
        let transcribedOld = meeting(age: 40 * day, status: .transcribed)
        let transcribedNew = meeting(age: day, status: .transcribed)
        let recordedOld = meeting(age: 40 * day, status: .recorded)
        let recording = meeting(age: 40 * day, status: .recording)
        let transcribing = meeting(age: 40 * day, status: .transcribing)
        let noAudio = meeting(age: 40 * day, status: .transcribed, hasAudio: false)
        let all = [transcribedOld, transcribedNew, recordedOld, recording, transcribing, noAudio]

        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs(all, keep: .forever, now: now), [])
        // A meeting never transcribed keeps its audio whatever its age: it has nothing else.
        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs(all, keep: .days30, now: now), [transcribedOld.id])
        let failedOld = meeting(age: 40 * day, status: .failed)
        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs([recordedOld, failedOld], keep: .days7, now: now), [])
        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs(all, keep: .afterTranscription, now: now),
                       [transcribedOld.id, transcribedNew.id])
    }

    func testMeetingAudioAgesFromWhenItBecameThisMacsToKeep() {
        // Recorded on a phone eight days ago and collected a minute ago: the meeting still
        // says it happened eight days ago, and its audio has been here for a minute.
        let collected = meeting(age: 8 * day, status: .transcribed, stored: 60)
        let recordedHere = meeting(age: 8 * day, status: .transcribed)
        let collectedLongAgo = meeting(age: 40 * day, status: .transcribed, stored: 8 * day)
        let all = [collected, recordedHere, collectedLongAgo]

        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs(all, keep: .days7, now: now),
                       [recordedHere.id, collectedLongAgo.id])
        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs(all, keep: .days30, now: now), [])
        // The rule that counts no days at all is untouched.
        XCTAssertEqual(RetentionCleaner.expiredMeetingAudioIDs(all, keep: .afterTranscription, now: now), all.map(\.id))
    }

    func testRunAppliesSettingsToStores() throws {
        let directories = AppDirectories(base: temporary)
        try directories.createFolders()
        let history = HistoryStore(directories: directories)
        let settings = AppSettings(defaults: defaults)
        settings.keepHistory = .days30
        settings.keepDictationAudio = .days7
        settings.keepMeetingAudio = .afterTranscription

        let ancient = entry(age: 45 * day, audio: "2026-07-27/ancient.wav")
        let lastWeek = entry(age: 10 * day, audio: "2026-08-31/week.wav")
        let today = entry(age: 3600, audio: "2026-09-09/today.wav")
        for item in [ancient, lastWeek, today] {
            let url = directories.recordingURL(relativePath: try XCTUnwrap(item.audioFileName))
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data([1, 2, 3]).write(to: url)
            history.add(item)
        }
        let meetings = FakeMeetingStore(meetings: [meeting(age: day, status: .transcribed),
                                                   meeting(age: day, status: .recording)])

        let mic = meetings.microphoneAudioURL(for: meetings.meetings[0].id)
        try FileManager.default.createDirectory(at: mic.deletingLastPathComponent(), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: mic.deletingLastPathComponent()) }
        try Data([4, 5, 6]).write(to: mic)

        let cleaner = RetentionCleaner(history: history, meetings: meetings, settings: settings)
        let report = cleaner.run(now: now)
        XCTAssertEqual(report, RetentionReport(removedEntries: 1, removedDictationAudio: 2, removedMeetingAudio: 1))
        XCTAssertEqual(history.entries.map(\.id), [today.id, lastWeek.id])
        XCTAssertNil(history.entries.first { $0.id == lastWeek.id }?.audioFileName)
        XCTAssertEqual(history.entries.first { $0.id == today.id }?.audioFileName, today.audioFileName)
        XCTAssertFalse(FileManager.default.fileExists(atPath: directories.recordingURL(relativePath: "2026-07-27/ancient.wav").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: directories.recordingURL(relativePath: "2026-08-31/week.wav").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: directories.recordingURL(relativePath: "2026-09-09/today.wav").path))
        // "Delete after transcription" removes the tracks and leaves a screen recording.
        XCTAssertEqual(meetings.deletedAudio.count, 0)
        XCTAssertEqual(meetings.meetings.filter { !$0.hasAudio }.map(\.status), [.transcribed])
        XCTAssertEqual(HistoryStore(directories: directories).entries.count, 2, "a pass saves at once")

        XCTAssertEqual(cleaner.run(now: now), RetentionReport())
    }

    // MARK: - Insights

    func testEmptyHistory() throws {
        let insights = InsightsCalculator.compute(entries: [], now: now, calendar: try utcCalendar())
        XCTAssertEqual(insights.totalWords, 0)
        XCTAssertEqual(insights.wordsPerMinute, 0)
        XCTAssertEqual(insights.currentStreak, 0)
        XCTAssertEqual(insights.longestStreak, 0)
        XCTAssertTrue(insights.topApps.isEmpty)
        XCTAssertEqual(insights.categoryShares.map(\.category), AppCategory.allCases)
        XCTAssertEqual(insights.categoryShares.map(\.fraction), [0, 0, 0, 0])
        XCTAssertFalse(insights.dailyWords.isEmpty)
        XCTAssertTrue(insights.dailyWords.allSatisfy { $0.words == 0 })
    }

    func testTotalsSpeedAndFixes() throws {
        let entries = [
            entry(age: 60, words: 60, duration: 30, fixes: 2),
            entry(age: 120, words: 40, duration: 30, fixes: 1),
            // No duration: counts toward totals, not toward speed.
            entry(age: 180, words: 10, duration: 0, fixes: 0)
        ]
        let insights = InsightsCalculator.compute(entries: entries, now: now, calendar: try utcCalendar())
        XCTAssertEqual(insights.totalWords, 110)
        XCTAssertEqual(insights.dictationCount, 3)
        XCTAssertEqual(insights.wordsPerMinute, 100, accuracy: 1e-9)
        XCTAssertEqual(insights.fixesMade, 3)
    }

    func testStreaks() throws {
        let calendar = try utcCalendar()
        let today = calendar.startOfDay(for: now)
        func onDay(_ offset: Int) -> HistoryEntry {
            entry(date: today.addingTimeInterval(Double(offset) * day + 3600))
        }
        // Active: today, -1, -2; gap at -3; a five-day run from -10 to -6.
        let entries = [0, -1, -2, -2, -6, -7, -8, -9, -10].map(onDay)
        var insights = InsightsCalculator.compute(entries: entries, now: now, calendar: calendar)
        XCTAssertEqual(insights.currentStreak, 3)
        XCTAssertEqual(insights.longestStreak, 5)

        // Nothing yet today: the streak still counts from yesterday.
        insights = InsightsCalculator.compute(entries: [-1, -2].map(onDay), now: now, calendar: calendar)
        XCTAssertEqual(insights.currentStreak, 2)

        insights = InsightsCalculator.compute(entries: [-2, -3].map(onDay), now: now, calendar: calendar)
        XCTAssertEqual(insights.currentStreak, 0)
        XCTAssertEqual(insights.longestStreak, 2)
    }

    func testDailyWordsCoverTwentyWeeksAlignedToWeekStart() throws {
        let calendar = try utcCalendar() // Monday first
        let today = calendar.startOfDay(for: now)
        let entries = [
            entry(date: today.addingTimeInterval(600), words: 7),
            entry(date: today.addingTimeInterval(700), words: 5),
            entry(date: today.addingTimeInterval(-day + 60), words: 3),
            entry(date: today.addingTimeInterval(-400 * day), words: 100)
        ]
        let daily = InsightsCalculator.compute(entries: entries, now: now, calendar: calendar).dailyWords
        let first = try XCTUnwrap(daily.first)
        let last = try XCTUnwrap(daily.last)
        XCTAssertEqual(calendar.component(.weekday, from: first.day), 2, "starts on Monday")
        XCTAssertEqual(last.day, today)
        // 2026-09-10 is a Thursday: 19 full weeks plus Monday through Thursday.
        XCTAssertEqual(daily.count, 19 * 7 + 4)
        XCTAssertEqual(last.words, 12)
        XCTAssertEqual(daily[daily.count - 2].words, 3)
        XCTAssertEqual(daily.reduce(0) { $0 + $1.words }, 15, "older days fall outside the calendar")
        XCTAssertEqual(zip(daily, daily.dropFirst()).filter { $1.day <= $0.day }.count, 0, "oldest first")

        XCTAssertEqual(InsightsCalculator.compute(entries: entries, now: now, calendar: calendar, weeks: 1).dailyWords.count, 4)
        XCTAssertTrue(InsightsCalculator.compute(entries: entries, now: now, calendar: calendar, weeks: 0).dailyWords.isEmpty)
    }

    func testCategorySharesAndTopApps() throws {
        let entries = [
            entry(age: 10, words: 30, category: .work, app: ("com.tinyspeck.slackmacgap", "Slack")),
            entry(age: 20, words: 20, category: .work, app: ("com.tinyspeck.slackmacgap", "Slack Old")),
            entry(age: 30, words: 25, category: .personal, app: ("ru.keepcoder.Telegram", "Telegram")),
            entry(age: 40, words: 25, category: .email, app: ("com.apple.mail", "Mail")),
            entry(age: 50, words: 0, category: .other, app: ("com.apple.mail", "Mail")),
            entry(age: 60, words: 0, category: .other, app: nil)
        ]
        let insights = InsightsCalculator.compute(entries: entries, now: now, calendar: try utcCalendar(), topAppCount: 2)
        XCTAssertEqual(insights.categoryShares.map(\.category), [.personal, .work, .email, .other])
        XCTAssertEqual(insights.categoryShares.map(\.words), [25, 50, 25, 0])
        XCTAssertEqual(insights.categoryShares.map(\.fraction), [0.25, 0.5, 0.25, 0])

        XCTAssertEqual(insights.topApps.count, 2)
        XCTAssertEqual(insights.topApps[0], AppUsage(app: AppIdentity(bundleID: "com.tinyspeck.slackmacgap", name: "Slack"),
                                                     words: 50, dictations: 2))
        // Mail and Telegram tie on words; Mail has more dictations.
        XCTAssertEqual(insights.topApps[1].app.name, "Mail")
        XCTAssertEqual(insights.topApps[1].dictations, 2)
    }

    // MARK: - Helpers

    private func utcCalendar() throws -> Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "UTC"))
        calendar.firstWeekday = 2
        return calendar
    }

    private func entry(age: TimeInterval = 0, audio: String? = nil, words: Int = 5, duration: TimeInterval = 2,
                       fixes: Int = 0, category: AppCategory = .other,
                       app: (String, String)? = ("com.apple.Notes", "Notes")) -> HistoryEntry {
        entry(date: now.addingTimeInterval(-age), audio: audio, words: words, duration: duration, fixes: fixes,
              category: category, app: app)
    }

    private func entry(date: Date, audio: String? = nil, words: Int = 5, duration: TimeInterval = 2, fixes: Int = 0,
                       category: AppCategory = .other,
                       app: (String, String)? = ("com.apple.Notes", "Notes")) -> HistoryEntry {
        HistoryEntry(date: date, text: "text", rawText: "text", engineID: .parakeet, languageMode: .english,
                     appBundleID: app?.0, appName: app?.1, category: category, duration: duration,
                     wordCount: words, fixesCount: fixes, audioFileName: audio)
    }

    /// `stored` is how long ago the audio became this Mac's to keep; nil for a meeting
    /// recorded here, which is every meeting until one is collected from a phone.
    private func meeting(age: TimeInterval, status: MeetingStatus, hasAudio: Bool = true,
                         stored: TimeInterval? = nil) -> MeetingRecord {
        MeetingRecord(title: "Sync", createdAt: now.addingTimeInterval(-age), duration: 600, status: status,
                      hasAudio: hasAudio,
                      segments: status == .transcribed ? [TranscriptSegment(start: 0, end: 1, speaker: .me, text: "Sync")] : [],
                      audioStoredAt: stored.map { now.addingTimeInterval(-$0) })
    }
}

/// Records what retention asked the meetings store to do.
@MainActor
private final class FakeMeetingStore: MeetingStoring {
    private(set) var meetings: [MeetingRecord]
    private(set) var deletedAudio: [UUID] = []

    init(meetings: [MeetingRecord]) {
        self.meetings = meetings
    }

    func create(title: String) throws -> MeetingRecord {
        let record = MeetingRecord(title: title)
        meetings.insert(record, at: 0)
        return record
    }

    func update(_ meeting: MeetingRecord) {
        guard let index = meetings.firstIndex(where: { $0.id == meeting.id }) else { return }
        meetings[index] = meeting
    }

    func delete(id: UUID) {
        meetings.removeAll { $0.id == id }
    }

    func deleteAudio(id: UUID) {
        deletedAudio.append(id)
        guard let index = meetings.firstIndex(where: { $0.id == id }) else { return }
        meetings[index].hasAudio = false
    }

    var meetingsFolder: URL { FileManager.default.temporaryDirectory }
    func folder(for id: UUID) -> URL { FileManager.default.temporaryDirectory.appendingPathComponent(id.uuidString) }
    func microphoneAudioURL(for id: UUID) -> URL { folder(for: id).appendingPathComponent("mic.caf") }
    func systemAudioURL(for id: UUID) -> URL { folder(for: id).appendingPathComponent("system.caf") }
    func screenRecordingURL(for id: UUID) -> URL { folder(for: id).appendingPathComponent("screen.mov") }
    func transcriptURL(for id: UUID) -> URL { folder(for: id).appendingPathComponent("transcript.md") }
    func flush() throws {}
}
