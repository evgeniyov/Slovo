import Foundation
import XCTest
@testable import SlovoCore

/// `MeetingEdits`: names, «Это я», merges, line reassignment, stars, notes and deletion.
final class MeetingsEditsTests: XCTestCase {
    // MARK: - Names

    func testRenameTrimsResetsAndIgnoresUnknownSpeakers() {
        let meeting = fixture()
        let renamed = MeetingEdits.renameSpeaker("s2", to: "  Аня  ", in: meeting)
        XCTAssertEqual(renamed.speaker(id: "s2")?.displayName, "Аня")
        XCTAssertEqual(renamed.speakers.count, 3)
        XCTAssertEqual(renamed.speaker(id: "s2")?.number, 2)
        XCTAssertEqual(renamed.speaker(id: "s2")?.colorIndex, 1)

        XCTAssertNil(MeetingEdits.renameSpeaker("s2", to: "", in: renamed).speaker(id: "s2")?.displayName)
        XCTAssertNil(MeetingEdits.renameSpeaker("s2", to: nil, in: renamed).speaker(id: "s2")?.displayName)
        XCTAssertNil(MeetingEdits.renameSpeaker("s2", to: " :[]★ ", in: renamed).speaker(id: "s2")?.displayName)
        XCTAssertEqual(MeetingEdits.renameSpeaker("s9", to: "Никто", in: meeting), meeting)
    }

    func testSanitizedNameRemovesWhatWouldBreakTranscriptLines() {
        XCTAssertEqual(MeetingEdits.sanitizedName("Аня: [PM]\n★ Иванова"), "Аня PM Иванова")
        XCTAssertEqual(MeetingEdits.sanitizedName("  Анна   Мария  "), "Анна Мария")
        XCTAssertEqual(MeetingEdits.sanitizedName(String(repeating: "я", count: 50)), String(repeating: "я", count: 40))
        XCTAssertEqual(MeetingEdits.sanitizedName(String(repeating: "a", count: 39) + " bcd"), String(repeating: "a", count: 39))
        XCTAssertNil(MeetingEdits.sanitizedName("   "))
        XCTAssertNil(MeetingEdits.sanitizedName(":[]★\r\n"))
    }

    func testRenamingAFallbackSpeakerStoresItFirst() {
        let call = MeetingRecord(title: "Звонок", status: .transcribed, hasSystemAudio: true, segments: [
            TranscriptSegment(start: 0, end: 3, speaker: .me, text: "Алло"),
            TranscriptSegment(start: 4, end: 6, speaker: .others, text: "Слышу")
        ])

        let renamed = MeetingEdits.renameSpeaker("others", to: "Команда дизайна", in: call)

        XCTAssertEqual(renamed.speakers, [MeetingSpeaker(id: "others", track: .system, displayName: "Команда дизайна",
                                                         colorIndex: 1)])
        XCTAssertEqual(renamed.effectiveSpeakers.map(\.id), ["me", "others"])
        XCTAssertEqual(MeetingEdits.renameSpeaker("others", to: nil, in: call), call)

        let microphoneOnly = MeetingRecord(title: "Заметка", status: .transcribed,
                                           segments: [TranscriptSegment(start: 0, end: 3, speaker: .me, text: "Один")])
        XCTAssertEqual(MeetingEdits.renameSpeaker("others", to: "Кто-то", in: microphoneOnly), microphoneOnly)
    }

    func testMarkAsMeKeepsOneMe() {
        let first = MeetingEdits.markAsMe("s1", in: fixture())
        XCTAssertEqual(first.speakers.map(\.isMe), [true, false, false])
        let second = MeetingEdits.markAsMe("s3", in: first)
        XCTAssertEqual(second.speakers.map(\.isMe), [false, false, true])
        XCTAssertEqual(MeetingEdits.markAsMe("s9", in: second), second)

        var withFallback = second
        withFallback.segments.append(TranscriptSegment(start: 30, end: 32, speaker: .me, text: "Без спикера"))
        let fallbackMe = MeetingEdits.markAsMe("me", in: withFallback)
        XCTAssertEqual(fallbackMe.speakers.map(\.id), ["s1", "s2", "s3", "me"])
        XCTAssertEqual(fallbackMe.speakers.map(\.isMe), [false, false, false, true])
    }

    // MARK: - Merge and reassign

    func testMergeMovesSegmentsWithinATrackAndRefusesAcrossTracks() throws {
        let meeting = fixture()
        let named = MeetingEdits.renameSpeaker("s1", to: "Аня", in: meeting)

        let merged = try MeetingEdits.mergeSpeaker("s2", into: "s1", in: named)

        XCTAssertEqual(merged.segments.map(\.speakerID), ["s1", "s1", "s3", "s1"])
        XCTAssertEqual(merged.speakers.map(\.id), ["s1", "s3"])
        XCTAssertEqual(merged.speaker(id: "s1")?.displayName, "Аня")
        XCTAssertEqual(merged.speaker(id: "s1")?.number, 1)
        XCTAssertEqual(merged.speaker(id: "s1")?.colorIndex, 0)
        XCTAssertEqual(merged.segments.map(\.text), meeting.segments.map(\.text))
        XCTAssertEqual(TranscriptMerger.lines(for: merged).map(\.text), ["Привет Начнём", "Слышно?", "Да"])

        XCTAssertThrowsError(try MeetingEdits.mergeSpeaker("s3", into: "s1", in: meeting)) { error in
            XCTAssertEqual(error as? SlovoError,
                           SlovoError(CoreStrings.string("meetings.error.mergeAcrossTracks", table: .meetings)))
        }
        XCTAssertEqual(try MeetingEdits.mergeSpeaker("s1", into: "s1", in: meeting), meeting)
        XCTAssertEqual(try MeetingEdits.mergeSpeaker("s9", into: "s1", in: meeting), meeting)
    }

    func testMergeCarriesMeAndWorksWithFallbackSpeakers() throws {
        let meWasSecond = MeetingEdits.markAsMe("s2", in: fixture())
        let merged = try MeetingEdits.mergeSpeaker("s2", into: "s1", in: meWasSecond)
        XCTAssertEqual(merged.speakers.map(\.id), ["s1", "s3"])
        XCTAssertEqual(merged.speakers.map(\.isMe), [true, false])

        let mixed = MeetingRecord(title: "Коворк", status: .transcribed, segments: [
            TranscriptSegment(start: 0, end: 5, speaker: .me, text: "Без спикера"),
            TranscriptSegment(start: 6, end: 9, speaker: .me, text: "Первый", speakerID: "s1")
        ], speakers: [MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0)])

        let intoNumbered = try MeetingEdits.mergeSpeaker("me", into: "s1", in: mixed)
        XCTAssertEqual(intoNumbered.segments.map(\.speakerID), ["s1", "s1"])
        XCTAssertEqual(intoNumbered.effectiveSpeakers.map(\.id), ["s1"])

        let intoFallback = try MeetingEdits.mergeSpeaker("s1", into: "me", in: mixed)
        XCTAssertEqual(intoFallback.segments.map(\.speakerID), [nil, nil])
        XCTAssertEqual(intoFallback.speakers, [])
        XCTAssertEqual(intoFallback.effectiveSpeakers.map(\.id), ["me"])
    }

    func testReassignLineOnlyWithinItsTrack() throws {
        let meeting = fixture()
        let lines = TranscriptMerger.lines(for: meeting)
        XCTAssertEqual(lines.count, 4)

        let reassigned = try MeetingEdits.reassignLine(lines[1], to: "s1", in: meeting)
        XCTAssertEqual(reassigned.segments.map(\.speakerID), ["s1", "s1", "s3", "s1"])
        XCTAssertEqual(reassigned.speakers, meeting.speakers)

        XCTAssertThrowsError(try MeetingEdits.reassignLine(lines[1], to: "s3", in: meeting)) { error in
            XCTAssertEqual(error as? SlovoError,
                           SlovoError(CoreStrings.string("meetings.error.mergeAcrossTracks", table: .meetings)))
        }

        let toFallback = try MeetingEdits.reassignLine(lines[2], to: "others", in: meeting)
        XCTAssertEqual(toFallback.segments.map(\.speakerID), ["s1", "s2", nil, "s1"])
        XCTAssertEqual(toFallback.effectiveSpeakers.map(\.id), ["s1", "s2", "s3", "others"])
        XCTAssertEqual(try MeetingEdits.reassignLine(lines[1], to: "s9", in: meeting), meeting)
    }

    // MARK: - Moments

    func testToggleHighlightAddsAtTheLineStartAndRemovesEveryAnchoredMoment() throws {
        let meeting = fixture()
        let now = Date(timeIntervalSinceReferenceDate: 779_100_000)
        let lines = TranscriptMerger.lines(for: meeting)

        let starred = MeetingEdits.toggleHighlight(for: lines[1], in: meeting, now: now)

        let star = try XCTUnwrap(starred.highlights.first)
        XCTAssertEqual(starred.highlights.count, 1)
        XCTAssertEqual(star.time, 6)
        XCTAssertEqual(star.source, .transcript)
        XCTAssertEqual(star.createdAt, now)
        XCTAssertNil(star.note)
        let starredLines = TranscriptMerger.lines(for: starred)
        XCTAssertEqual(starredLines.map(\.highlightIDs), [[], [star.id], [], []])
        // A line rendered before the star was added still finds it.
        XCTAssertEqual(MeetingEdits.toggleHighlight(for: lines[1], in: starred, now: now).highlights, [])
        XCTAssertEqual(MeetingEdits.toggleHighlight(for: starredLines[1], in: starred, now: now).highlights, [])

        var marked = meeting
        let during = MeetingHighlight(time: 7, source: .recording, createdAt: now)
        let later = MeetingHighlight(time: 9.5, note: "важно", source: .recording, createdAt: now)
        let elsewhere = MeetingHighlight(time: 12, source: .recording, createdAt: now)
        marked.highlights = [during, elsewhere, later]
        let cleared = MeetingEdits.toggleHighlight(for: TranscriptMerger.lines(for: marked)[1], in: marked, now: now)
        XCTAssertEqual(cleared.highlights, [elsewhere])
    }

    func testNotesAreOneLineOfAtMostFiveHundredCharacters() {
        var meeting = fixture()
        let moment = MeetingHighlight(time: 7, source: .recording)
        meeting.highlights = [moment]

        let noted = MeetingEdits.setHighlightNote(moment.id, note: "  важно\nочень  ", in: meeting)
        XCTAssertEqual(noted.highlights.first?.note, "важно очень")
        XCTAssertNil(MeetingEdits.setHighlightNote(moment.id, note: " \n ", in: noted).highlights.first?.note)
        XCTAssertNil(MeetingEdits.setHighlightNote(moment.id, note: nil, in: noted).highlights.first?.note)
        let long = MeetingEdits.setHighlightNote(moment.id, note: String(repeating: "ж", count: 600), in: meeting)
        XCTAssertEqual(long.highlights.first?.note?.count, 500)
        XCTAssertEqual(MeetingEdits.setHighlightNote(UUID(), note: "x", in: meeting), meeting)
    }

    func testDeleteHighlight() {
        var meeting = fixture()
        let first = MeetingHighlight(time: 1, source: .transcript)
        let second = MeetingHighlight(time: 12, source: .recording)
        meeting.highlights = [first, second]

        XCTAssertEqual(MeetingEdits.deleteHighlight(first.id, in: meeting).highlights, [second])
        XCTAssertEqual(MeetingEdits.deleteHighlight(UUID(), in: meeting), meeting)
    }

    // MARK: - Fixture

    /// Two microphone voices and one remote voice: s1 0–5, s2 6–10, s3 11–15, s1 16–20.
    private func fixture() -> MeetingRecord {
        MeetingRecord(
            title: "Созвон", createdAt: Date(timeIntervalSinceReferenceDate: 779_000_000), status: .transcribed,
            hasSystemAudio: true, hasAudio: true, engineID: .parakeet,
            segments: [
                TranscriptSegment(start: 0, end: 5, speaker: .me, text: "Привет", speakerID: "s1"),
                TranscriptSegment(start: 6, end: 10, speaker: .me, text: "Начнём", speakerID: "s2"),
                TranscriptSegment(start: 11, end: 15, speaker: .others, text: "Слышно?", speakerID: "s3"),
                TranscriptSegment(start: 16, end: 20, speaker: .me, text: "Да", speakerID: "s1")
            ],
            speakers: [
                MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0),
                MeetingSpeaker(id: "s2", track: .microphone, number: 2, colorIndex: 1),
                MeetingSpeaker(id: "s3", track: .system, number: 3, colorIndex: 2)
            ])
    }
}
