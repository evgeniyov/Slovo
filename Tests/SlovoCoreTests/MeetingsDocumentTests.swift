import Foundation
import XCTest
@testable import SlovoCore

/// `transcript.md`: the first format's document for meetings without speakers or moments,
/// the second format in both languages, the recovery parser, and `MeetingStore` keeping
/// the file in step with transcribed meetings.
@MainActor
final class MeetingsDocumentTests: XCTestCase {
    private var temporary: URL!
    private var directories: AppDirectories!

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoMeetingsDocumentTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - First format

    func testFirstFormatRecordGivesTheFirstFormatDocumentByteForByte() {
        let record = MeetingRecord(title: "Коворк 15 сентября", createdAt: Self.createdAt, duration: 754.5,
                                   status: .transcribed, hasSystemAudio: true, segments: Self.firstFormatSegments,
                                   formatVersion: 1)
        var withStoredFallback = record
        withStoredFallback.speakers = [MeetingSpeaker.fallback(for: .system)]

        for language in [AppLanguage.en, .ru] {
            let expected = TranscriptMerger.markdown(title: record.title, date: record.createdAt,
                                                     segments: Self.firstFormatSegments, language: language)
            XCTAssertEqual(TranscriptMerger.markdown(meeting: record, language: language), expected, "\(language)")
            XCTAssertEqual(TranscriptMerger.markdown(meeting: withStoredFallback, language: language), expected, "\(language)")
        }
    }

    func testClipSegmentsWithoutSpeakersKeepTheFirstFormatLayout() {
        let record = MeetingRecord(title: "Созвон", createdAt: Self.createdAt, status: .transcribed, hasSystemAudio: true,
                                   segments: [
                                       TranscriptSegment(start: 0, end: 2, speaker: .me, text: "a"),
                                       TranscriptSegment(start: 3, end: 5, speaker: .me, text: "b"),
                                       TranscriptSegment(start: 6, end: 8, speaker: .others, text: "c")
                                   ])
        let expected = TranscriptMerger.markdown(title: "Созвон", date: Self.createdAt, segments: [
            TranscriptSegment(start: 0, end: 5, speaker: .me, text: "a b"),
            TranscriptSegment(start: 6, end: 8, speaker: .others, text: "c")
        ], language: .en)
        XCTAssertEqual(TranscriptMerger.markdown(meeting: record, language: .en), expected)

        let renamed = MeetingEdits.renameSpeaker("others", to: "Команда", in: record)
        XCTAssertTrue(TranscriptMerger.markdown(meeting: renamed, language: .en).contains("\n## Transcript\n"))
        var marked = record
        marked.highlights = [MeetingHighlight(time: 1, source: .recording)]
        XCTAssertTrue(TranscriptMerger.markdown(meeting: marked, language: .en).contains("\n## Important moments\n"))
    }

    // MARK: - Second format

    func testSecondFormatInRussian() {
        let expected = """
            # Коворк 15 сентября

            \(Self.dateText(language: "ru")) · 1:04:12

            Участники: Я, Аня, Спикер 3

            ## Важные моменты

            - [12:40] Аня: «Давайте к пятнице соберём прототип и покажем его команде.» — дедлайн

            ## Расшифровка

            [00:04] Я: Всем привет, начнём с планов на неделю.

            [12:38] ★ Аня: Давайте к пятнице соберём прототип и покажем его команде.

            [13:20] Спикер 3: Согласен.

            """
        XCTAssertEqual(TranscriptMerger.markdown(meeting: Self.coworkRecord(), language: .ru), expected)
    }

    func testSecondFormatInEnglishListsMomentsWithoutALineToo() {
        var record = Self.coworkRecord()
        record.highlights.append(MeetingHighlight(time: 3000, source: .recording,
                                                  createdAt: Date(timeIntervalSinceReferenceDate: 779_003_000)))
        let expected = """
            # Коворк 15 сентября

            \(Self.dateText(language: "en")) · 1:04:12

            Participants: Me, Аня, Speaker 3

            ## Important moments

            - [12:40] Аня: «Давайте к пятнице соберём прототип и покажем его команде.» — дедлайн
            - [50:00]

            ## Transcript

            [00:04] Me: Всем привет, начнём с планов на неделю.

            [12:38] ★ Аня: Давайте к пятнице соберём прототип и покажем его команде.

            [13:20] Speaker 3: Согласен.

            """
        XCTAssertEqual(TranscriptMerger.markdown(meeting: record, language: .en), expected)
    }

    func testSpeakerFilterNarrowsParticipantsMomentsAndLines() {
        var record = Self.coworkRecord()
        record.highlights.append(MeetingHighlight(time: 3000, source: .recording))

        let anya = TranscriptMerger.markdown(meeting: record, language: .ru, speakerFilter: ["s2"])
        XCTAssertTrue(anya.contains("\nУчастники: Аня\n"), anya)
        XCTAssertTrue(anya.contains("- [12:40] Аня: «"), anya)
        XCTAssertFalse(anya.contains("- [50:00]"), anya)
        XCTAssertTrue(anya.hasSuffix("## Расшифровка\n\n[12:38] ★ Аня: Давайте к пятнице соберём прототип и покажем его команде.\n"),
                      anya)

        let third = TranscriptMerger.markdown(meeting: record, language: .ru, speakerFilter: ["s3"])
        XCTAssertFalse(third.contains("## Важные моменты"), third)
        XCTAssertTrue(third.hasSuffix("[13:20] Спикер 3: Согласен.\n"), third)
    }

    func testEmptySecondFormatDocumentSaysNoSpeech() {
        let record = MeetingRecord(title: "Тишина", createdAt: Self.createdAt, status: .transcribed,
                                   speakers: [MeetingSpeaker(id: "s1", track: .microphone, number: 1, displayName: "Аня",
                                                             colorIndex: 0)])
        let document = TranscriptMerger.markdown(meeting: record, language: .en)
        XCTAssertTrue(document.hasSuffix("\n\n## Transcript\n\nNo speech was recognized.\n"), document)
        XCTAssertFalse(document.contains("Participants"), document)
    }

    func testExcerptIsCutAtOneHundredTwentyCharacters() {
        XCTAssertEqual(TranscriptMerger.excerpt(String(repeating: "ы", count: 130)), String(repeating: "ы", count: 120) + "…")
        XCTAssertEqual(TranscriptMerger.excerpt(String(repeating: "ы", count: 120)), String(repeating: "ы", count: 120))
    }

    // MARK: - Recovery parser

    func testRecoveryParserRoundTripsSecondFormatDocuments() {
        let original = Self.coworkRecord()
        for language in [AppLanguage.en, .ru] {
            let document = TranscriptMerger.markdown(meeting: original, language: language)
            let parsed = MeetingStore.parseTranscript(document)

            XCTAssertEqual(parsed.title, "Коворк 15 сентября", "\(language)")
            XCTAssertEqual(parsed.segments.map(\.start), [4, 758, 800], "\(language)")
            XCTAssertEqual(parsed.segments.map(\.text), original.segments.map(\.text), "\(language)")
            XCTAssertEqual(parsed.segments.map(\.speaker), [.me, .me, .me], "\(language)")
            XCTAssertEqual(parsed.segments.map(\.speakerID), [nil, "s4", "s3"], "\(language)")
            XCTAssertEqual(parsed.speakers, [
                MeetingSpeaker(id: "s4", track: .microphone, number: 4, displayName: "Аня", colorIndex: 3),
                MeetingSpeaker(id: "s3", track: .microphone, number: 3, colorIndex: 2)
            ], "\(language)")
            XCTAssertEqual(parsed.highlights.map(\.time), [758], "\(language)")
            XCTAssertEqual(parsed.highlights.map(\.source), [.transcript], "\(language)")
            XCTAssertEqual(MeetingStore.parseTranscript(document).highlights.map(\.id), parsed.highlights.map(\.id),
                           "the same document gives the same moment ids")

            let recovered = MeetingRecord(title: parsed.title ?? "", createdAt: original.createdAt,
                                          duration: original.duration, status: .transcribed, segments: parsed.segments,
                                          speakers: parsed.speakers, highlights: parsed.highlights)
            let rendered = TranscriptMerger.markdown(meeting: recovered, language: language)
            let heading = "## " + CoreStrings.string("meetings.transcript.heading", table: .meetings, language: language) + "\n\n"
            XCTAssertEqual(rendered.components(separatedBy: heading).last, document.components(separatedBy: heading).last,
                           "\(language)")
        }
    }

    func testRecoveryParserSkipsEverythingButTranscriptLines() {
        let text = """
            # Заметки

            Participants: Me, Олег
            - [00:10] Олег: «пункт списка»
            ## Transcript
            [00:05] Олег: Первая строка
            [1:00:07] ★ Speaker 2: Вторая: с двоеточием
            [aa:bb] Me: не время
            [00:09] \(String(repeating: "д", count: 41)): слишком длинное имя
            [00:12]Others: без пробела
            просто текст
            [00:20] Olga Speaker: ещё
            """

        let parsed = MeetingStore.parseTranscript(text)

        XCTAssertEqual(parsed.title, "Заметки")
        XCTAssertEqual(parsed.segments.map(\.text), ["Первая строка", "Вторая: с двоеточием", "без пробела", "ещё"])
        XCTAssertEqual(parsed.segments.map(\.start), [5, 3607, 12, 20])
        XCTAssertEqual(parsed.segments.map(\.speaker), [.me, .me, .others, .me])
        XCTAssertEqual(parsed.segments.map(\.speakerID), ["s3", "s2", nil, "s4"])
        XCTAssertEqual(parsed.speakers.map(\.id), ["s3", "s2", "s4"])
        XCTAssertEqual(parsed.speakers.map(\.displayName), ["Олег", nil, "Olga Speaker"])
        XCTAssertEqual(parsed.highlights.map(\.time), [3607])
    }

    // MARK: - Store

    func testStoreWritesTheTranscriptOnlyForTranscribedMeetings() throws {
        let store = MeetingStore(directories: directories)
        store.transcriptLanguage = { .ru }
        var meeting = try store.create(title: "Планёрка")
        let url = store.transcriptURL(for: meeting.id)
        try store.flush()
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))

        meeting.status = .recorded
        store.update(meeting)
        try store.flush()
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))

        meeting.status = .transcribed
        meeting.segments = [TranscriptSegment(start: 1, end: 4, speaker: .me, text: "Итоги пришлю вечером", speakerID: "s1")]
        meeting.speakers = [MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0)]
        store.update(meeting)
        try store.flush()
        let saved = try XCTUnwrap(store.meeting(id: meeting.id))
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), TranscriptMerger.markdown(meeting: saved, language: .ru))

        store.update(MeetingEdits.renameSpeaker("s1", to: "Аня", in: saved))
        try store.flush()
        XCTAssertTrue(try String(contentsOf: url, encoding: .utf8).contains("[00:01] Аня: Итоги пришлю вечером"))

        var reprocessing = try XCTUnwrap(store.meeting(id: meeting.id))
        reprocessing.status = .transcribing
        store.update(MeetingEdits.renameSpeaker("s1", to: "Борис", in: reprocessing))
        try store.flush()
        let kept = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(kept.contains("Аня"), kept)
        XCTAssertFalse(kept.contains("Борис"), kept)
    }

    func testDebouncedSaveWritesTheTranscriptAndUnchangedDocumentsAreLeftAlone() async throws {
        let store = MeetingStore(directories: directories)
        store.transcriptLanguage = { .en }
        var meeting = try store.create(title: "Draft")
        meeting.status = .transcribed
        meeting.segments = [TranscriptSegment(start: 0, end: 2, speaker: .me, text: "Hello")]
        store.update(meeting)
        let url = store.transcriptURL(for: meeting.id)

        for _ in 0..<60 where !FileManager.default.fileExists(atPath: url.path) {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertTrue(try String(contentsOf: url, encoding: .utf8).contains("[00:00] Me: Hello"))

        let past = Date(timeIntervalSinceReferenceDate: 700_000_000)
        try FileManager.default.setAttributes([.modificationDate: past], ofItemAtPath: url.path)
        store.deleteAudio(id: meeting.id)
        try store.flush()
        XCTAssertEqual(store.meeting(id: meeting.id)?.hasAudio, false)
        let modified = try FileManager.default.attributesOfItem(atPath: url.path)[.modificationDate] as? Date
        XCTAssertEqual(modified, past)
    }

    func testDamagedRecordComesBackFromASecondFormatTranscriptWithNamesAndMoments() throws {
        let id = UUID()
        let folder = directories.meetingFolder(id: id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let document = Data(TranscriptMerger.markdown(meeting: Self.coworkRecord(), language: .ru).utf8)
        let transcriptURL = folder.appendingPathComponent("transcript.md")
        try document.write(to: transcriptURL)
        try Data("{ broken".utf8).write(to: folder.appendingPathComponent("meeting.json"))

        let store = MeetingStore(directories: directories)

        let recovered = try XCTUnwrap(store.meeting(id: id))
        XCTAssertEqual(recovered.title, "Коворк 15 сентября")
        XCTAssertEqual(recovered.status, .transcribed)
        XCTAssertFalse(recovered.hasAudio)
        XCTAssertEqual(recovered.formatVersion, 1)
        XCTAssertEqual(recovered.speakers.map(\.displayName), ["Аня", nil])
        XCTAssertEqual(recovered.highlights.count, 1)
        let labels = TranscriptMerger.lines(for: recovered).map { TranscriptMerger.label(of: $0, in: recovered, language: .ru) }
        XCTAssertEqual(labels, ["Я", "Аня", "Спикер 3"])
        XCTAssertEqual(TranscriptMerger.lines(for: recovered).map(\.highlightIDs.count), [0, 1, 0])
        XCTAssertEqual(try Data(contentsOf: transcriptURL), document, "the transcript it was recovered from is kept")
        XCTAssertEqual(MeetingStore(directories: directories).meeting(id: id), recovered)
    }

    // MARK: - Fixtures

    private static let createdAt = Date(timeIntervalSinceReferenceDate: 779_000_000)

    private static let firstFormatSegments = [
        TranscriptSegment(start: 0.5, end: 12.5, speaker: .me, text: "Всем привет, начнём с планов на неделю."),
        TranscriptSegment(start: 13, end: 20, speaker: .others, text: "Давайте к пятнице соберём прототип."),
        TranscriptSegment(start: 21, end: 31.25, speaker: .me, text: "Договорились.")
    ]

    /// An in-person meeting of 1:04:12 with «Я», a renamed speaker, a numbered one and a
    /// moment marked two seconds into Аня's line.
    private static func coworkRecord() -> MeetingRecord {
        MeetingRecord(
            title: "Коворк 15 сентября", createdAt: createdAt, duration: 3852, status: .transcribed,
            hasSystemAudio: false, hasAudio: false, engineID: .parakeet,
            segments: [
                TranscriptSegment(start: 4, end: 8, speaker: .me, text: "Всем привет, начнём с планов на неделю.",
                                  speakerID: "s1"),
                TranscriptSegment(start: 758, end: 765, speaker: .me,
                                  text: "Давайте к пятнице соберём прототип и покажем его команде.", speakerID: "s2"),
                TranscriptSegment(start: 800, end: 802, speaker: .me, text: "Согласен.", speakerID: "s3")
            ],
            speakers: [
                MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0, isMe: true),
                MeetingSpeaker(id: "s2", track: .microphone, number: 2, displayName: "Аня", colorIndex: 1),
                MeetingSpeaker(id: "s3", track: .microphone, number: 3, colorIndex: 2)
            ],
            highlights: [
                MeetingHighlight(time: 760, note: "дедлайн", source: .recording,
                                 createdAt: Date(timeIntervalSinceReferenceDate: 779_000_760))
            ])
    }

    private static func dateText(language code: String) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: code)
        formatter.dateStyle = .long
        formatter.timeStyle = .short
        return formatter.string(from: createdAt)
    }
}
