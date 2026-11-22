import Foundation
import XCTest
@testable import SlovoCore

/// `ClipPlanner`: padding, splitting long turns at quiet points, dropped silence,
/// unattributed speech, ordering, and assigning existing segments to new turns.
final class MeetingsClipPlannerTests: XCTestCase {
    private let rate = 16_000

    func testTurnsArePaddedUpToTheMiddleOfEachGapAndUncoveredSpeechGoesToTheNearestTurn() {
        let turns = [
            SpeakerTurn(start: 1.0, end: 5.0, cluster: 0),
            SpeakerTurn(start: 5.2, end: 9.0, cluster: 1),
            SpeakerTurn(start: 12.0, end: 14.0, cluster: 0)
        ]
        let clips = ClipPlanner.plan(turns: turns, speech: [0.8..<9.6, 11.9..<14.05], trackDuration: 14.05)

        // 0.8–0.85 is uncovered for less than 0.3 s and stays out; 9.15–9.6 becomes a clip
        // of the turn that ended at 9.0.
        assertClips(clips, [(0.85, 5.1, 0), (5.1, 9.15, 1), (9.15, 9.6, 1), (11.85, 14.05, 0)])
    }

    func testPaddingNeverLeavesTheTrack() {
        let clips = ClipPlanner.plan(turns: [SpeakerTurn(start: 0.05, end: 3.0, cluster: 2)], speech: [],
                                     trackDuration: 3.1)
        assertClips(clips, [(0, 3.1, 2)])
    }

    func testTrackWithoutTurnsKeepsItsSpeechRangesUnassigned() {
        let clips = ClipPlanner.plan(turns: [], speech: [5..<7, 1..<3, 7..<9], trackDuration: 10)
        assertClips(clips, [(1, 3, nil), (5, 7, nil), (7, 9, nil)])
    }

    func testLongTurnWithoutAnEnvelopeIsCutIntoEqualPieces() {
        let clips = ClipPlanner.plan(turns: [SpeakerTurn(start: 0, end: 45, cluster: 1)], speech: [0..<45], trackDuration: 45)
        assertClips(clips, [(0, 15, 1), (15, 30, 1), (30, 45, 1)])
    }

    func testLongTurnIsSplitAtAQuietMomentIntoPiecesOfAtMostTwentySeconds() throws {
        let samples = tone(17) + silence(0.3) + tone(8)
        let segmenter = SpeechSegmenter()
        let speech = segmenter.segments(for: samples).map { $0.start..<$0.end }

        let clips = ClipPlanner.plan(turns: [SpeakerTurn(start: 0, end: 25.3, cluster: 0)], speech: speech,
                                     trackDuration: 25.3, envelope: envelope(samples), segmenter: segmenter)

        XCTAssertEqual(clips.count, 2)
        let first = try XCTUnwrap(clips.first)
        let second = try XCTUnwrap(clips.last)
        XCTAssertEqual(first.range.lowerBound, 0, accuracy: 1e-9)
        XCTAssertEqual(first.range.upperBound, 17.15, accuracy: 0.1)
        XCTAssertEqual(second.range.lowerBound, first.range.upperBound, accuracy: 1e-9)
        XCTAssertEqual(second.range.upperBound, 25.3, accuracy: 1e-6)
        XCTAssertTrue(clips.allSatisfy { $0.range.upperBound - $0.range.lowerBound <= 20 + 1e-9 })
        XCTAssertEqual(clips.map(\.cluster), [0, 0])
    }

    func testSilentPiecesOfALongTurnAreDropped() throws {
        let samples = tone(17) + silence(8.3)
        let segmenter = SpeechSegmenter()
        let speech = segmenter.segments(for: samples).map { $0.start..<$0.end }

        let clips = ClipPlanner.plan(turns: [SpeakerTurn(start: 0, end: 25.3, cluster: 3)], speech: speech,
                                     trackDuration: 25.3, envelope: envelope(samples), segmenter: segmenter)

        XCTAssertEqual(clips.count, 1)
        let clip = try XCTUnwrap(clips.first)
        XCTAssertEqual(clip.range.lowerBound, 0, accuracy: 1e-9)
        XCTAssertGreaterThan(clip.range.upperBound, 17)
        XCTAssertLessThanOrEqual(clip.range.upperBound, 20 + 1e-9)
        XCTAssertEqual(clip.cluster, 3)
    }

    func testUncoveredSilenceIsNotAClipWhenTheEnvelopeIsKnown() {
        // Loud 1–5 s. The detected speech range runs to 5.6 s, so 5.15–5.6 is left
        // uncovered by the padded turn, but it holds no sound.
        let samples = silence(1) + tone(4) + silence(3)
        let turns = [SpeakerTurn(start: 1, end: 5, cluster: 0)]

        let withEnvelope = ClipPlanner.plan(turns: turns, speech: [0.8..<5.6], trackDuration: 8,
                                            envelope: envelope(samples), segmenter: SpeechSegmenter())
        assertClips(withEnvelope, [(0.85, 5.15, 0)])

        let withoutEnvelope = ClipPlanner.plan(turns: turns, speech: [0.8..<5.6], trackDuration: 8)
        assertClips(withoutEnvelope, [(0.85, 5.15, 0), (5.15, 5.6, 0)])
    }

    func testClipsNeverOverlapEvenForOverlappingTurns() {
        let turns = [
            SpeakerTurn(start: 4, end: 8, cluster: 1),
            SpeakerTurn(start: 0, end: 5, cluster: 0),
            SpeakerTurn(start: 6, end: 7, cluster: 2),
            SpeakerTurn(start: .nan, end: 3, cluster: 4)
        ]
        let clips = ClipPlanner.plan(turns: turns, speech: [0..<9], trackDuration: 9)
        XCTAssertFalse(clips.isEmpty)
        for (left, right) in zip(clips, clips.dropFirst()) {
            XCTAssertLessThanOrEqual(left.range.upperBound, right.range.lowerBound + 1e-12, "\(clips)")
        }
        XCTAssertFalse(clips.contains { $0.cluster == 2 || $0.cluster == 4 }, "\(clips)")
    }

    func testAssignUsesTheLargestOverlapThenTheNearestTurn() {
        let turns = [
            SpeakerTurn(start: 0, end: 5, cluster: 0),
            SpeakerTurn(start: 5, end: 11, cluster: 1),
            SpeakerTurn(start: 15, end: 16, cluster: 2)
        ]
        let segments = [
            segment(0, 4),
            segment(4, 10),
            segment(12, 13),
            segment(17, 18),
            segment(4.5, 5.5),
            segment(20, 20)
        ]

        XCTAssertEqual(ClipPlanner.assign(segments: segments, turns: turns), [0, 1, 1, 2, 0, 2])
        XCTAssertEqual(ClipPlanner.assign(segments: segments, turns: []), [nil, nil, nil, nil, nil, nil])
        XCTAssertEqual(ClipPlanner.assign(segments: [], turns: turns), [])
    }

    // MARK: - Helpers

    private func segment(_ start: TimeInterval, _ end: TimeInterval) -> TranscriptSegment {
        TranscriptSegment(start: start, end: end, speaker: .me, text: "…")
    }

    private func assertClips(_ clips: [PlannedClip], _ expected: [(Double, Double, Int?)],
                             file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(clips.count, expected.count, "\(clips)", file: file, line: line)
        for (clip, value) in zip(clips, expected) {
            XCTAssertEqual(clip.range.lowerBound, value.0, accuracy: 1e-6, "\(clips)", file: file, line: line)
            XCTAssertEqual(clip.range.upperBound, value.1, accuracy: 1e-6, "\(clips)", file: file, line: line)
            XCTAssertEqual(clip.cluster, value.2, "\(clips)", file: file, line: line)
        }
    }

    private func envelope(_ samples: [Float]) -> EnergyEnvelope {
        var envelope = EnergyEnvelope(sampleRate: rate)
        samples.withUnsafeBufferPointer { envelope.append($0) }
        envelope.finish()
        return envelope
    }

    private func tone(_ seconds: Double) -> [Float] {
        (0..<Int(seconds * Double(rate))).map { Float(0.2 * sin(2 * Double.pi * 220 * Double($0) / Double(rate))) }
    }

    private func silence(_ seconds: Double) -> [Float] {
        [Float](repeating: 0, count: Int(seconds * Double(rate)))
    }
}
