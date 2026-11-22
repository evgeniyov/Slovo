import Foundation
import XCTest
@testable import SlovoCore

/// `SpeechStatsCalculator`: talk time and shares, turns and pace, speech against silence,
/// and the pauses in between.
final class MeetingsSpeechStatsTests: XCTestCase {
    private func meeting(duration: TimeInterval, segments: [TranscriptSegment],
                         speakers: [MeetingSpeaker] = []) -> MeetingRecord {
        MeetingRecord(title: "Планёрка", duration: duration, status: .transcribed, hasSystemAudio: true,
                      segments: segments, speakers: speakers)
    }

    private var threeSpeakers: [MeetingSpeaker] {
        [MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0),
         MeetingSpeaker(id: "s2", track: .microphone, number: 2, colorIndex: 1),
         MeetingSpeaker(id: "s3", track: .microphone, number: 3, colorIndex: 2)]
    }

    func testSharesOfEverySpeakerAddUpToTheWholeConversation() {
        let segments = [
            TranscriptSegment(start: 0, end: 30, speaker: .me, text: "первый", speakerID: "s1"),
            TranscriptSegment(start: 30, end: 50, speaker: .me, text: "второй", speakerID: "s2"),
            TranscriptSegment(start: 50, end: 60, speaker: .me, text: "третий", speakerID: "s3")
        ]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 60, segments: segments,
                                                              speakers: threeSpeakers))

        XCTAssertEqual(stats.speakers.map(\.speakerID), ["s1", "s2", "s3"])
        XCTAssertEqual(stats.speakers.map(\.speakingSeconds), [30, 20, 10])
        XCTAssertEqual(stats.speakers.reduce(0) { $0 + $1.share }, 1, accuracy: 0.000_001)
        XCTAssertEqual(stats.speakers[0].share, 0.5, accuracy: 0.000_001)
        XCTAssertEqual(stats.speechSeconds, 60, accuracy: 0.001)
        XCTAssertEqual(stats.speechShare, 1, accuracy: 0.000_001)
        XCTAssertEqual(stats.silenceSeconds, 0, accuracy: 0.001)
        XCTAssertEqual(stats.pauseSeconds, SpeechStatsCalculator.defaultPauseSeconds)
    }

    /// The loudest speaker leads even when the file lists them in another order.
    func testSpeakersAreOrderedByTalkTime() {
        let segments = [
            TranscriptSegment(start: 0, end: 5, speaker: .me, text: "коротко", speakerID: "s1"),
            TranscriptSegment(start: 10, end: 70, speaker: .me, text: "длинно", speakerID: "s3"),
            TranscriptSegment(start: 80, end: 100, speaker: .me, text: "средне", speakerID: "s2")
        ]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 100, segments: segments,
                                                              speakers: threeSpeakers))

        XCTAssertEqual(stats.speakers.map(\.speakerID), ["s3", "s2", "s1"])
    }

    func testSingleSpeakerTakesTheWholeShareAndAShortNoteReportsNoPace() {
        let segments = [TranscriptSegment(start: 0.5, end: 4.5, speaker: .me, text: "Купить билеты на среду")]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 5, segments: segments))

        XCTAssertEqual(stats.speakers.count, 1)
        XCTAssertEqual(stats.speakers[0].speakerID, "me")
        XCTAssertEqual(stats.speakers[0].share, 1, accuracy: 0.000_001)
        XCTAssertEqual(stats.speakers[0].turns, 1)
        XCTAssertEqual(stats.speakers[0].words, 4)
        XCTAssertNil(stats.speakers[0].wordsPerMinute, "four seconds is too little speech for a pace")
        XCTAssertEqual(stats.pauseCount, 0)
        XCTAssertEqual(stats.longestPauseSeconds, 0)
    }

    /// Two people talking at once fill one stretch of the recording, but each keeps their
    /// own speaking time: the two questions have two different answers.
    func testOverlappingVoicesCountOnceInSpeechAndKeepTheirOwnTime() {
        let segments = [
            TranscriptSegment(start: 0, end: 60, speaker: .me, text: "я говорю"),
            TranscriptSegment(start: 30, end: 90, speaker: .others, text: "и я говорю")
        ]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 120, segments: segments))

        XCTAssertEqual(stats.speakers.map(\.speakingSeconds), [60, 60])
        XCTAssertEqual(stats.speakers.reduce(0) { $0 + $1.share }, 1, accuracy: 0.000_001)
        XCTAssertEqual(stats.speechSeconds, 90, accuracy: 0.001)
        XCTAssertEqual(stats.silenceSeconds, 30, accuracy: 0.001)
        XCTAssertEqual(stats.speechShare, 0.75, accuracy: 0.000_001)
    }

    func testOverlappingAndZeroLengthSegmentsOfOneSpeakerAreNotCountedTwice() {
        let segments = [
            TranscriptSegment(start: 0, end: 20, speaker: .me, text: "первая"),
            TranscriptSegment(start: 10, end: 15, speaker: .me, text: "внутри первой"),
            TranscriptSegment(start: 20, end: 20, speaker: .me, text: "мгновение")
        ]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 30, segments: segments))

        XCTAssertEqual(stats.speakers[0].speakingSeconds, 20, accuracy: 0.001)
        XCTAssertEqual(stats.speakers[0].turns, 1)
        XCTAssertEqual(stats.speakers[0].words, 4)
        XCTAssertEqual(stats.speechSeconds, 20, accuracy: 0.001)
    }

    func testMeetingWithoutSegmentsHasNoSpeakersAndNoNumbers() {
        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 600, segments: []))

        XCTAssertTrue(stats.isEmpty)
        XCTAssertEqual(stats.speakers, [])
        XCTAssertEqual(stats.speechSeconds, 0)
        XCTAssertEqual(stats.speechShare, 0)
        XCTAssertEqual(stats.totalSeconds, 600)
        XCTAssertEqual(stats.silenceSeconds, 600)
        XCTAssertEqual(stats.pauseCount, 0)
        XCTAssertEqual(stats.longestPauseSeconds, 0)

        let blank = meeting(duration: 60, segments: [TranscriptSegment(start: 0, end: 10, speaker: .me, text: "   ")])
        XCTAssertTrue(SpeechStatsCalculator.compute(for: blank).isEmpty)
    }

    func testTurnsEndAtAnotherVoiceOrAtALongerSilenceOfTheSameSpeaker() {
        let segments = [
            TranscriptSegment(start: 0, end: 10, speaker: .me, text: "начинаю", speakerID: "s1"),
            TranscriptSegment(start: 12, end: 20, speaker: .me, text: "и продолжаю", speakerID: "s1"),
            TranscriptSegment(start: 40, end: 50, speaker: .me, text: "отвечаю", speakerID: "s2"),
            TranscriptSegment(start: 52, end: 60, speaker: .me, text: "снова я", speakerID: "s1"),
            TranscriptSegment(start: 80, end: 90, speaker: .me, text: "и ещё раз", speakerID: "s1")
        ]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 90, segments: segments,
                                                              speakers: threeSpeakers))

        let first = stats.speakers.first { $0.speakerID == "s1" }
        XCTAssertEqual(first?.turns, 3, "a two second gap keeps a turn, twenty seconds ends it")
        XCTAssertEqual(first?.speakingSeconds, 36)
        XCTAssertEqual(first?.averageTurnSeconds, 12)
        XCTAssertEqual(stats.speakers.first { $0.speakerID == "s2" }?.turns, 1)
        XCTAssertEqual(stats.pauseCount, 2)
        XCTAssertEqual(stats.longestPauseSeconds, 20, accuracy: 0.001)
    }

    /// The quiet before the meeting gets going and after it ends is not a pause in the
    /// conversation, only silence.
    func testQuietBeforeTheFirstWordAndAfterTheLastIsNotAPause() {
        let segments = [TranscriptSegment(start: 300, end: 310, speaker: .me, text: "единственная фраза")]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 600, segments: segments))

        XCTAssertEqual(stats.pauseCount, 0)
        XCTAssertEqual(stats.longestPauseSeconds, 0)
        XCTAssertEqual(stats.silenceSeconds, 590, accuracy: 0.001)
    }

    func testPaceCountsWordsAndKeepsHyphenatedRussianWordsWhole() {
        // Ten words: the dash between spaces is punctuation, «кое-что» and «что-то» one
        // word each. Splitting on whitespace would find eleven.
        let text = "Кое-что мы успели, а что-то нет — переносим на следующую неделю"
        let segments = [TranscriptSegment(start: 0, end: 30, speaker: .me, text: text)]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 30, segments: segments))

        XCTAssertEqual(SpeechStatsCalculator.wordCount(text), 10)
        XCTAssertEqual(stats.speakers[0].words, 10)
        XCTAssertEqual(stats.speakers[0].wordsPerMinute ?? 0, 20, accuracy: 0.000_001)
    }

    /// A meeting whose speakers were never separated has the fallback voice of each track,
    /// and a stored speaker who never said anything gets no row at all.
    func testUnseparatedTracksUseTheirFallbackSpeakersAndSilentSpeakersAreLeftOut() {
        let stored = [MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0)]
        let segments = [
            TranscriptSegment(start: 0, end: 20, speaker: .me, text: "мой голос"),
            TranscriptSegment(start: 25, end: 45, speaker: .others, text: "их голос")
        ]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 45, segments: segments, speakers: stored))

        XCTAssertEqual(Set(stats.speakers.map(\.speakerID)), ["me", "others"])
        XCTAssertFalse(stats.speakers.contains { $0.speakerID == "s1" })
    }

    /// A duration that stops before the last word, which only a hand-edited file has,
    /// would otherwise make speech more than the whole recording.
    func testTimelineFallsBackToTheLastWordWhenTheStoredDurationEndsBeforeIt() {
        let segments = [TranscriptSegment(start: 0, end: 40, speaker: .me, text: "говорю дольше, чем записано")]

        let stats = SpeechStatsCalculator.compute(for: meeting(duration: 0, segments: segments))

        XCTAssertEqual(stats.totalSeconds, 40)
        XCTAssertEqual(stats.speechSeconds, 40, accuracy: 0.001)
        XCTAssertEqual(stats.speechShare, 1, accuracy: 0.000_001)
        XCTAssertEqual(stats.silenceSeconds, 0, accuracy: 0.001)
    }
}
