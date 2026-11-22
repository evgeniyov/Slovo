import Foundation
import XCTest
@testable import SlovoCore

/// `TranscriptMerger`: the first format's merging, timestamps and document, display lines
/// with speakers, the speaker filter, highlight anchoring and labels.
final class MeetingsMergerTests: XCTestCase {
    func testMergeOrdersByStartAndCollapsesConsecutiveSegmentsOfOneSpeaker() {
        let microphone = [
            TranscriptSegment(start: 0.8, end: 3, speaker: .me, text: "Hi everyone"),
            TranscriptSegment(start: 3.5, end: 6, speaker: .me, text: "let's start"),
            TranscriptSegment(start: 20, end: 22, speaker: .me, text: "Sounds good")
        ]
        let system = [
            TranscriptSegment(start: 12, end: 15, speaker: .others, text: "Hello"),
            TranscriptSegment(start: 7, end: 11, speaker: .others, text: "Morning"),
            TranscriptSegment(start: 30, end: 31, speaker: .others, text: "Bye")
        ]

        let merged = TranscriptMerger.merge(microphone: microphone, system: system)

        XCTAssertEqual(merged, [
            TranscriptSegment(start: 0.8, end: 6, speaker: .me, text: "Hi everyone let's start"),
            TranscriptSegment(start: 7, end: 15, speaker: .others, text: "Morning Hello"),
            TranscriptSegment(start: 20, end: 22, speaker: .me, text: "Sounds good"),
            TranscriptSegment(start: 30, end: 31, speaker: .others, text: "Bye")
        ])
    }

    func testMergeAssignsSpeakersByTrackDropsEmptyTextAndPutsMicrophoneFirstOnTies() {
        let microphone = [
            TranscriptSegment(start: 5, end: 6, speaker: .others, text: "  mine\nreally "),
            TranscriptSegment(start: 8, end: 9, speaker: .me, text: "   ")
        ]
        let system = [TranscriptSegment(start: 5, end: 7, speaker: .me, text: "theirs")]

        let merged = TranscriptMerger.merge(microphone: microphone, system: system)

        XCTAssertEqual(merged.map(\.speaker), [.me, .others])
        XCTAssertEqual(merged.map(\.text), ["mine really", "theirs"])
        XCTAssertEqual(TranscriptMerger.merge(microphone: [], system: []), [])
    }

    func testMergeKeepsTheLatestEndWhenCollapsingOverlaps() {
        let microphone = [
            TranscriptSegment(start: 0, end: 10, speaker: .me, text: "long"),
            TranscriptSegment(start: 2, end: 4, speaker: .me, text: "short")
        ]
        let merged = TranscriptMerger.merge(microphone: microphone, system: [])
        XCTAssertEqual(merged, [TranscriptSegment(start: 0, end: 10, speaker: .me, text: "long short")])
    }

    func testTimestamp() {
        XCTAssertEqual(TranscriptMerger.timestamp(0), "00:00")
        XCTAssertEqual(TranscriptMerger.timestamp(5.99), "00:05")
        XCTAssertEqual(TranscriptMerger.timestamp(65.9), "01:05")
        XCTAssertEqual(TranscriptMerger.timestamp(3599.9), "59:59")
        XCTAssertEqual(TranscriptMerger.timestamp(3600), "1:00:00")
        XCTAssertEqual(TranscriptMerger.timestamp(3723), "1:02:03")
        XCTAssertEqual(TranscriptMerger.timestamp(36_000), "10:00:00")
        XCTAssertEqual(TranscriptMerger.timestamp(-5), "00:00")
        XCTAssertEqual(TranscriptMerger.timestamp(.nan), "00:00")
        XCTAssertEqual(TranscriptMerger.timestamp(.infinity), "00:00")
        // Beyond Int, as a hand-edited meeting.json can hold: clamped instead of trapping.
        XCTAssertEqual(TranscriptMerger.timestamp(1e20), "99999:59:59")
    }

    func testMarkdownInEnglish() {
        let segments = [
            TranscriptSegment(start: 5, end: 8, speaker: .me, text: "Hello there"),
            TranscriptSegment(start: 3725, end: 3730, speaker: .others, text: "Hi")
        ]
        let document = TranscriptMerger.markdown(title: "Weekly\nsync", date: Date(timeIntervalSince1970: 1_789_000_000),
                                                 segments: segments, language: .en)
        let lines = document.components(separatedBy: "\n")

        XCTAssertEqual(lines.first, "# Weekly sync")
        XCTAssertTrue(document.contains("[00:05] Me: Hello there\n\n[1:02:05] Others: Hi\n"), document)
        XCTAssertTrue(document.contains("2026"), document)
        XCTAssertTrue(document.hasSuffix("Others: Hi\n"))
    }

    func testMarkdownInRussianUsesRussianSpeakerNames() {
        let segments = [
            TranscriptSegment(start: 0, end: 2, speaker: .me, text: "Привет"),
            TranscriptSegment(start: 61, end: 63, speaker: .others, text: "Здравствуйте")
        ]
        let document = TranscriptMerger.markdown(title: "Планёрка", date: Date(), segments: segments, language: .ru)
        XCTAssertTrue(document.hasPrefix("# Планёрка\n\n"))
        XCTAssertTrue(document.contains("[00:00] Я: Привет\n\n[01:01] Собеседники: Здравствуйте\n"), document)
    }

    func testMarkdownWithoutSpeechSaysSo() {
        let english = TranscriptMerger.markdown(title: "Empty", date: Date(), segments: [], language: .en)
        XCTAssertTrue(english.hasSuffix("No speech was recognized.\n"), english)
        let russian = TranscriptMerger.markdown(title: "Пусто", date: Date(), segments: [], language: .ru)
        XCTAssertTrue(russian.hasSuffix("Речь не распознана.\n"), russian)
    }

    func testPlainTextHasOneLinePerSegment() {
        let segments = [
            TranscriptSegment(start: 1, end: 2, speaker: .me, text: "One"),
            TranscriptSegment(start: 3, end: 4, speaker: .others, text: "Two")
        ]
        XCTAssertEqual(TranscriptMerger.plainText(segments: segments, language: .en), "[00:01] Me: One\n[00:03] Others: Two")
    }

    // MARK: - Display lines

    func testLinesJoinOneSpeakerAcrossGapsOfAtMostThreeSeconds() {
        let record = meeting([
            TranscriptSegment(start: 0, end: 2, speaker: .me, text: "a", speakerID: "s1"),
            TranscriptSegment(start: 5, end: 7, speaker: .me, text: " b ", speakerID: "s1"),
            TranscriptSegment(start: 10.5, end: 12, speaker: .me, text: "c", speakerID: "s1")
        ], speakers: [speaker1])

        let lines = TranscriptMerger.lines(for: record)

        XCTAssertEqual(lines, [
            TranscriptLine(id: 0, start: 0, end: 7, speakerID: "s1", track: .microphone, text: "a b", segmentIndices: [0, 1]),
            TranscriptLine(id: 1, start: 10.5, end: 12, speakerID: "s1", track: .microphone, text: "c", segmentIndices: [2])
        ])
    }

    func testLinesStayAtMostSixtySecondsLong() {
        let record = meeting([
            TranscriptSegment(start: 0, end: 20, speaker: .me, text: "a", speakerID: "s1"),
            TranscriptSegment(start: 20, end: 40, speaker: .me, text: "b", speakerID: "s1"),
            TranscriptSegment(start: 40, end: 60, speaker: .me, text: "c", speakerID: "s1"),
            TranscriptSegment(start: 60, end: 61, speaker: .me, text: "d", speakerID: "s1")
        ], speakers: [speaker1])

        let lines = TranscriptMerger.lines(for: record)

        XCTAssertEqual(lines.map(\.text), ["a b c", "d"])
        XCTAssertEqual(lines.map(\.segmentIndices), [[0, 1, 2], [3]])
        XCTAssertEqual(lines.first?.end, 60)
    }

    func testLinesAreSortedWithMicrophoneFirstOnTiesAndSkipEmptyText() {
        let record = meeting([
            TranscriptSegment(start: 5, end: 6, speaker: .others, text: "theirs"),
            TranscriptSegment(start: 5, end: 7, speaker: .me, text: "mine", speakerID: "s1"),
            TranscriptSegment(start: 0, end: 1, speaker: .me, text: "first", speakerID: "s1"),
            TranscriptSegment(start: 2, end: 3, speaker: .me, text: "  ", speakerID: "s1")
        ], speakers: [speaker1])

        let lines = TranscriptMerger.lines(for: record)

        XCTAssertEqual(lines.map(\.text), ["first", "mine", "theirs"])
        XCTAssertEqual(lines.map(\.id), [0, 1, 2])
        XCTAssertEqual(lines.map(\.speakerID), ["s1", "s1", "others"])
        XCTAssertEqual(lines.map(\.track), [.microphone, .microphone, .system])
        XCTAssertEqual(lines.map(\.segmentIndices), [[2], [1], [0]])
    }

    func testSpeakerFilterAppliesAfterMerging() {
        let record = meeting([
            TranscriptSegment(start: 0, end: 2, speaker: .me, text: "a", speakerID: "s1"),
            TranscriptSegment(start: 2.5, end: 3, speaker: .me, text: "b", speakerID: "s2"),
            TranscriptSegment(start: 3.5, end: 5, speaker: .me, text: "c", speakerID: "s1")
        ], speakers: [speaker1, speaker2])

        XCTAssertEqual(TranscriptMerger.lines(for: record).map(\.text), ["a", "b", "c"])
        let filtered = TranscriptMerger.lines(for: record, speakerFilter: ["s1"])
        XCTAssertEqual(filtered.map(\.text), ["a", "c"])
        XCTAssertEqual(filtered.map(\.id), [0, 1])
        XCTAssertEqual(TranscriptMerger.lines(for: record, speakerFilter: []), [])
    }

    // MARK: - Highlights

    func testHighlightAnchorsToTheContainingLineThenLooksBackTenThenAheadFiveSeconds() {
        let lines = [
            line(0, 0, 10),
            line(1, 20, 30),
            line(2, 50, 60)
        ]
        XCTAssertEqual(TranscriptMerger.highlightLineIndex(moment(5), in: lines), 0)
        XCTAssertEqual(TranscriptMerger.highlightLineIndex(moment(20), in: lines), 1)
        XCTAssertEqual(TranscriptMerger.highlightLineIndex(moment(35), in: lines), 1)
        XCTAssertEqual(TranscriptMerger.highlightLineIndex(moment(40), in: lines), 1)
        XCTAssertEqual(TranscriptMerger.highlightLineIndex(moment(45), in: lines), 2)
        XCTAssertNil(TranscriptMerger.highlightLineIndex(moment(44), in: lines))
        XCTAssertNil(TranscriptMerger.highlightLineIndex(moment(71), in: lines))
        XCTAssertNil(TranscriptMerger.highlightLineIndex(moment(.nan), in: lines))
        XCTAssertNil(TranscriptMerger.highlightLineIndex(moment(5), in: []))
    }

    func testHighlightInOverlappingLinesGoesToTheLineThatStartedLast() {
        let lines = [
            line(0, 0, 10),
            TranscriptLine(id: 1, start: 5, end: 7, speakerID: "others", track: .system, text: "…", segmentIndices: [1])
        ]
        XCTAssertEqual(TranscriptMerger.highlightLineIndex(moment(6), in: lines), 1)
        XCTAssertEqual(TranscriptMerger.highlightLineIndex(moment(8), in: lines), 0)
    }

    func testLinesCarryTheirHighlightsAndTheFilterDoesNotMoveThem() {
        let early = moment(1)
        let late = moment(3.2)
        let lost = moment(100)
        let record = meeting([
            TranscriptSegment(start: 0, end: 2, speaker: .me, text: "a", speakerID: "s1"),
            TranscriptSegment(start: 3, end: 4, speaker: .me, text: "b", speakerID: "s2")
        ], speakers: [speaker1, speaker2], highlights: [late, lost, early])

        let lines = TranscriptMerger.lines(for: record)
        XCTAssertEqual(lines.map(\.highlightIDs), [[early.id], [late.id]])
        XCTAssertEqual(TranscriptMerger.lines(for: record, speakerFilter: ["s2"]).map(\.highlightIDs), [[late.id]])
    }

    func testPlainTextOfAMeetingShowsNamesAndStars() {
        var anya = speaker1
        anya.displayName = "Аня"
        let record = meeting([
            TranscriptSegment(start: 0, end: 2, speaker: .me, text: "a", speakerID: "s1"),
            TranscriptSegment(start: 10, end: 12, speaker: .me, text: "b", speakerID: "s2")
        ], speakers: [anya, speaker2], highlights: [moment(1)])

        XCTAssertEqual(TranscriptMerger.plainText(meeting: record, language: .ru), "[00:00] ★ Аня: a\n[00:10] Спикер 2: b")
        XCTAssertEqual(TranscriptMerger.plainText(meeting: record, language: .en, speakerFilter: ["s2"]), "[00:10] Speaker 2: b")
    }

    func testPlainTextOfAFirstFormatMeetingMatchesTheSegmentForm() {
        let segments = [
            TranscriptSegment(start: 1, end: 2, speaker: .me, text: "One"),
            TranscriptSegment(start: 3, end: 4, speaker: .others, text: "Two")
        ]
        let record = meeting(segments)
        XCTAssertEqual(TranscriptMerger.plainText(meeting: record, language: .en),
                       TranscriptMerger.plainText(segments: segments, language: .en))
    }

    // MARK: - Labels

    func testSpeakerLabelsInEnglishAndRussian() {
        let numbered = MeetingSpeaker(id: "s2", track: .system, number: 2, colorIndex: 1)
        XCTAssertEqual(TranscriptMerger.speakerLabel(numbered, language: .en), "Speaker 2")
        XCTAssertEqual(TranscriptMerger.speakerLabel(numbered, language: .ru), "Спикер 2")

        var me = numbered
        me.isMe = true
        XCTAssertEqual(TranscriptMerger.speakerLabel(me, language: .en), "Me")
        XCTAssertEqual(TranscriptMerger.speakerLabel(me, language: .ru), "Я")

        var named = me
        named.displayName = "Аня"
        XCTAssertEqual(TranscriptMerger.speakerLabel(named, language: .en), "Аня")
        XCTAssertEqual(TranscriptMerger.speakerLabel(named, language: .ru), "Аня")

        var blank = MeetingSpeaker.fallback(for: .system)
        blank.displayName = "   "
        XCTAssertEqual(TranscriptMerger.speakerLabel(blank, language: .en), "Others")
        XCTAssertEqual(TranscriptMerger.speakerLabel(blank, language: .ru), "Собеседники")
        XCTAssertEqual(TranscriptMerger.speakerLabel(MeetingSpeaker.fallback(for: .microphone), language: .en), "Me")
    }

    func testMicrophoneFallbackIsMeUntilAnotherSpeakerIsMarked() throws {
        let call = MeetingRecord(title: "Звонок", createdAt: Date(timeIntervalSinceReferenceDate: 779_000_000),
                                 status: .transcribed, hasSystemAudio: true, segments: [
                                     TranscriptSegment(start: 0, end: 3, speaker: .me, text: "Алло"),
                                     TranscriptSegment(start: 4, end: 6, speaker: .others, text: "Слышу")
                                 ])
        let microphone = MeetingSpeaker.fallback(for: .microphone)
        XCTAssertTrue(TranscriptMerger.isMe(microphone, in: call))
        XCTAssertFalse(TranscriptMerger.isMe(MeetingSpeaker.fallback(for: .system), in: call))
        XCTAssertEqual(call.effectiveSpeakers.map { TranscriptMerger.speakerLabel($0, in: call, language: .ru) },
                       ["Я", "Собеседники"])

        // «Это я» on «Собеседники» leaves one «Я».
        let marked = MeetingEdits.markAsMe("others", in: call)
        XCTAssertEqual(marked.effectiveSpeakers.map(\.id), ["me", "others"])
        XCTAssertEqual(marked.effectiveSpeakers.map { TranscriptMerger.isMe($0, in: marked) }, [false, true])
        XCTAssertEqual(marked.effectiveSpeakers.map { TranscriptMerger.speakerLabel($0, in: marked, language: .ru) },
                       ["Микрофон", "Я"])
        XCTAssertEqual(marked.effectiveSpeakers.map { TranscriptMerger.speakerLabel($0, in: marked, language: .en) },
                       ["Microphone", "Me"])
        XCTAssertEqual(TranscriptMerger.plainText(meeting: marked, language: .en),
                       "[00:00] Microphone: Алло\n[00:04] Me: Слышу")
        XCTAssertTrue(TranscriptMerger.markdown(meeting: marked, language: .en).contains("Participants: Microphone, Me"))

        // A name still wins, and a stored microphone speaker follows the same rule.
        let named = MeetingEdits.renameSpeaker("me", to: "Переговорка", in: marked)
        let namedMicrophone = try XCTUnwrap(named.speaker(id: "me"))
        XCTAssertEqual(TranscriptMerger.speakerLabel(namedMicrophone, in: named, language: .ru), "Переговорка")
        let reset = MeetingEdits.renameSpeaker("me", to: nil, in: named)
        let storedMicrophone = try XCTUnwrap(reset.speaker(id: "me"))
        XCTAssertEqual(TranscriptMerger.speakerLabel(storedMicrophone, in: reset, language: .ru), "Микрофон")
        let back = MeetingEdits.markAsMe("me", in: reset)
        XCTAssertEqual(back.effectiveSpeakers.map { TranscriptMerger.speakerLabel($0, in: back, language: .ru) },
                       ["Я", "Собеседники"])

        // Separated microphone: speech no voice was assigned to stops reading «Я» once a voice is.
        let mixed = meeting([
            TranscriptSegment(start: 0, end: 5, speaker: .me, text: "Первый", speakerID: "s1"),
            TranscriptSegment(start: 9, end: 12, speaker: .me, text: "Без спикера")
        ], speakers: [speaker1, speaker2])
        XCTAssertEqual(mixed.effectiveSpeakers.map { TranscriptMerger.speakerLabel($0, in: mixed, language: .en) },
                       ["Speaker 1", "Speaker 2", "Me"])
        let mixedMarked = MeetingEdits.markAsMe("s1", in: mixed)
        XCTAssertEqual(mixedMarked.effectiveSpeakers.map { TranscriptMerger.speakerLabel($0, in: mixedMarked, language: .en) },
                       ["Me", "Speaker 2", "Microphone"])
    }

    // MARK: - Helpers

    private let speaker1 = MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0)
    private let speaker2 = MeetingSpeaker(id: "s2", track: .microphone, number: 2, colorIndex: 1)

    private func meeting(_ segments: [TranscriptSegment], speakers: [MeetingSpeaker] = [],
                         highlights: [MeetingHighlight] = []) -> MeetingRecord {
        MeetingRecord(title: "Test", createdAt: Date(timeIntervalSinceReferenceDate: 779_000_000), status: .transcribed,
                      segments: segments, speakers: speakers, highlights: highlights)
    }

    private func line(_ id: Int, _ start: TimeInterval, _ end: TimeInterval) -> TranscriptLine {
        TranscriptLine(id: id, start: start, end: end, speakerID: "s1", track: .microphone, text: "…", segmentIndices: [id])
    }

    private func moment(_ time: TimeInterval) -> MeetingHighlight {
        MeetingHighlight(time: time, source: .recording, createdAt: Date(timeIntervalSinceReferenceDate: 779_000_000))
    }
}
