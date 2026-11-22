import Foundation
import XCTest
@testable import SlovoCore

/// `SpeakerRegistry`: numbering, colors, fallbacks, automatic «Я» and carrying the user's
/// work over re-runs.
final class MeetingsSpeakerRegistryTests: XCTestCase {
    func testMicrophoneClustersAreNumberedByFirstAppearanceThenSystemClustersContinue() {
        let built = SpeakerRegistry.build(microphoneClusters: [3: [10..<20], 1: [0..<5, 30..<40]],
                                          systemClusters: [0: [5..<9]], hasSystemTrack: true, previous: nil)

        XCTAssertEqual(built.speakers, [
            MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0),
            MeetingSpeaker(id: "s2", track: .microphone, number: 2, colorIndex: 1),
            MeetingSpeaker(id: "s3", track: .system, number: 3, colorIndex: 2)
        ])
        XCTAssertEqual(built.idForCluster[.microphone], [1: "s1", 3: "s2"])
        XCTAssertEqual(built.idForCluster[.system], [0: "s3"])
    }

    func testColorsFollowNumbersAndWrapAroundThePalette() {
        var clusters: [Int: [Range<TimeInterval>]] = [:]
        for index in 0..<9 {
            clusters[index] = [Double(index * 10)..<Double(index * 10 + 5)]
        }
        let built = SpeakerRegistry.build(microphoneClusters: clusters, systemClusters: nil, hasSystemTrack: false,
                                          previous: nil)
        XCTAssertEqual(built.speakers.map(\.number), Array(1...9))
        XCTAssertEqual(built.speakers.map(\.colorIndex), [0, 1, 2, 3, 4, 5, 6, 7, 0])
    }

    func testUnseparatedTracksKeepOnlyAStoredFallback() {
        let plain = SpeakerRegistry.build(microphoneClusters: nil, systemClusters: [0: [0..<5]], hasSystemTrack: true,
                                          previous: nil)
        XCTAssertEqual(plain.speakers.map(\.id), ["s1"])
        XCTAssertEqual(plain.speakers.first?.track, .system)
        XCTAssertNil(plain.idForCluster[.microphone])

        let renamedOthers = MeetingSpeaker(id: "others", track: .system, displayName: "Команда", colorIndex: 1)
        let previous = MeetingRecord(title: "Звонок", status: .transcribed, hasSystemAudio: true,
                                     segments: [TranscriptSegment(start: 0, end: 5, speaker: .others, text: "Алло")],
                                     speakers: [renamedOthers])
        let kept = SpeakerRegistry.build(microphoneClusters: [0: [0..<10]], systemClusters: nil, hasSystemTrack: true,
                                         previous: previous)
        XCTAssertEqual(kept.speakers, [
            MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0, isMe: true),
            renamedOthers
        ])
    }

    func testMeIsAutomaticOnlyInCallsAndFromEightyPercent() {
        func me(_ clusters: [Int: [Range<TimeInterval>]], call: Bool) -> [Bool] {
            SpeakerRegistry.build(microphoneClusters: clusters, systemClusters: nil, hasSystemTrack: call, previous: nil)
                .speakers.map(\.isMe)
        }
        XCTAssertEqual(me([0: [0..<85], 1: [85..<100]], call: true), [true, false])
        XCTAssertEqual(me([0: [0..<80], 1: [80..<100]], call: true), [true, false])
        XCTAssertEqual(me([0: [0..<79], 1: [79..<100]], call: true), [false, false])
        XCTAssertEqual(me([0: [0..<20], 1: [20..<100]], call: true), [false, true])
        XCTAssertEqual(me([0: [0..<100]], call: false), [false])
    }

    /// A call recorded with the microphone off: nothing on the microphone track can hold
    /// most of the speech, so nobody becomes «Я» by the call rule.
    func testASilentMicrophoneTrackGetsNoAutomaticMe() {
        let system: [Int: [Range<TimeInterval>]] = [0: [0..<30], 1: [30..<40]]
        for microphone in [nil, [:], [0: []], [0: [5..<5]]] as [[Int: [Range<TimeInterval>]]?] {
            let built = SpeakerRegistry.build(microphoneClusters: microphone, systemClusters: system,
                                              hasSystemTrack: true, previous: nil)
            XCTAssertFalse(built.speakers.contains(where: \.isMe), "\(String(describing: microphone))")
        }
        let silent = SpeakerRegistry.build(microphoneClusters: [:], systemClusters: system, hasSystemTrack: true,
                                           previous: nil)
        XCTAssertEqual(silent.speakers.map(\.track), [.system, .system])
    }

    func testReRunKeepsIDNumberNameColorAndMeOfMatchedSpeakers() {
        let previous = MeetingRecord(
            title: "Коворк", status: .transcribed,
            segments: [
                TranscriptSegment(start: 0, end: 10, speaker: .me, text: "a", speakerID: "s1"),
                TranscriptSegment(start: 10, end: 20, speaker: .me, text: "b", speakerID: "s2"),
                TranscriptSegment(start: 20, end: 30, speaker: .me, text: "c", speakerID: "s1")
            ],
            speakers: [
                MeetingSpeaker(id: "s1", track: .microphone, number: 1, displayName: "Аня", colorIndex: 6),
                MeetingSpeaker(id: "s2", track: .microphone, number: 2, colorIndex: 1, isMe: true)
            ])

        let built = SpeakerRegistry.build(microphoneClusters: [0: [10..<20], 1: [0..<10, 20..<30]], systemClusters: nil,
                                          hasSystemTrack: true, previous: previous)

        XCTAssertEqual(built.speakers, previous.speakers)
        XCTAssertEqual(built.idForCluster[.microphone], [1: "s1", 0: "s2"])
    }

    func testPreviousMeChoiceWinsOverTheAutomaticOne() {
        let previous = MeetingRecord(
            title: "Звонок", status: .transcribed, hasSystemAudio: true,
            segments: [
                TranscriptSegment(start: 0, end: 85, speaker: .me, text: "a", speakerID: "s1"),
                TranscriptSegment(start: 85, end: 100, speaker: .me, text: "b", speakerID: "s2")
            ],
            speakers: [
                MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0),
                MeetingSpeaker(id: "s2", track: .microphone, number: 2, colorIndex: 1, isMe: true)
            ])
        let built = SpeakerRegistry.build(microphoneClusters: [0: [0..<85], 1: [85..<100]], systemClusters: nil,
                                          hasSystemTrack: true, previous: previous)
        XCTAssertEqual(built.speakers.map(\.isMe), [false, true])
    }

    func testNewSpeakersGetNumbersAboveTheHighestEverUsed() {
        let previous = MeetingRecord(
            title: "Коворк", status: .transcribed, hasSystemAudio: true,
            segments: [
                TranscriptSegment(start: 0, end: 10, speaker: .me, text: "a", speakerID: "s1"),
                TranscriptSegment(start: 20, end: 30, speaker: .me, text: "b", speakerID: "s4")
            ],
            speakers: [
                MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0),
                MeetingSpeaker(id: "s4", track: .microphone, number: 4, colorIndex: 3)
            ])

        let built = SpeakerRegistry.build(microphoneClusters: [0: [0..<10], 1: [20..<30], 2: [40..<50]],
                                          systemClusters: [0: [60..<70]], hasSystemTrack: true, previous: previous)

        XCTAssertEqual(built.speakers.map(\.id), ["s1", "s4", "s5", "s6"])
        XCTAssertEqual(built.speakers.map(\.number), [1, 4, 5, 6])
        XCTAssertEqual(built.speakers.map(\.colorIndex), [0, 3, 4, 5])
        XCTAssertEqual(built.speakers.map(\.track), [.microphone, .microphone, .microphone, .system])
    }

    func testMergedAwayNumbersAreNotReusedAfterTheLastRun() {
        let previous = MeetingRecord(
            title: "Коворк", status: .transcribed,
            segments: [TranscriptSegment(start: 0, end: 30, speaker: .me, text: "a", speakerID: "s1")],
            speakers: [MeetingSpeaker(id: "s1", track: .microphone, number: 1, colorIndex: 0)],
            diarization: MeetingDiarization(state: .applied, speakersPerTrack: ["microphone": 3]))
        let built = SpeakerRegistry.build(microphoneClusters: [0: [0..<30], 1: [40..<50]], systemClusters: nil,
                                          hasSystemTrack: false, previous: previous)
        XCTAssertEqual(built.speakers.map(\.id), ["s1", "s4"])
    }

    func testNumbersRemovedByMergesAreNeverGivenToNewVoices() throws {
        /// Stores a run's result the way `MeetingTranscriber` does.
        func stored(_ clusters: [Int: [Range<TimeInterval>]], over meeting: MeetingRecord) -> MeetingRecord {
            let built = SpeakerRegistry.build(microphoneClusters: clusters, systemClusters: nil, hasSystemTrack: false,
                                              previous: meeting)
            var record = meeting
            record.segments = clusters
                .flatMap { cluster, ranges in
                    ranges.map { TranscriptSegment(start: $0.lowerBound, end: $0.upperBound, speaker: .me, text: "c\(cluster)",
                                                   speakerID: built.idForCluster[.microphone]?[cluster]) }
                }
                .sorted { $0.start < $1.start }
            record.highestSpeakerNumber = SpeakerRegistry.highestNumber(keeping: meeting, with: built.speakers)
            record.speakers = built.speakers
            record.diarization = MeetingDiarization(state: .applied, speakersPerTrack: ["microphone": built.speakers.count])
            return record
        }

        var meeting = stored([0: [0..<10], 1: [10..<20], 2: [20..<30]], over: MeetingRecord(title: "Коворк", status: .transcribed))
        XCTAssertEqual(meeting.speakers.map(\.id), ["s1", "s2", "s3"])
        meeting = try MeetingEdits.mergeSpeaker("s3", into: "s2", in: meeting)
        meeting = stored([0: [0..<10], 1: [10..<30], 2: [40..<50]], over: meeting)
        XCTAssertEqual(meeting.speakers.map(\.id), ["s1", "s2", "s4"])
        // After this merge no speaker, segment or run count names 4 any more.
        meeting = try MeetingEdits.mergeSpeaker("s4", into: "s1", in: meeting)
        XCTAssertEqual(meeting.highestSpeakerNumber, 4)

        let built = SpeakerRegistry.build(microphoneClusters: [0: [0..<10, 40..<50], 1: [10..<30], 2: [60..<70]],
                                          systemClusters: nil, hasSystemTrack: false, previous: meeting)
        XCTAssertEqual(built.speakers.map(\.id), ["s1", "s2", "s5"])
    }

    func testMatchingIsOneToOneAndNeedsThirtyPercentOfTheNewSpeakersTime() {
        let previous = MeetingRecord(
            title: "Коворк", status: .transcribed,
            segments: [TranscriptSegment(start: 0, end: 10, speaker: .me, text: "a", speakerID: "s1")],
            speakers: [MeetingSpeaker(id: "s1", track: .microphone, number: 1, displayName: "Аня", colorIndex: 0)])

        let split = SpeakerRegistry.build(microphoneClusters: [0: [0..<6], 1: [6..<10]], systemClusters: nil,
                                          hasSystemTrack: false, previous: previous)
        XCTAssertEqual(split.speakers.map(\.id), ["s1", "s2"])
        XCTAssertEqual(split.speakers.map(\.displayName), ["Аня", nil])

        let barelyOverlapping = SpeakerRegistry.build(microphoneClusters: [0: [8..<18]], systemClusters: nil,
                                                      hasSystemTrack: false, previous: previous)
        XCTAssertEqual(barelyOverlapping.speakers.map(\.id), ["s2"])
        XCTAssertNil(barelyOverlapping.speakers.first?.displayName)
    }

    func testFallbackSpeakersAreNeverMatchedToVoices() {
        let previous = MeetingRecord(title: "Коворк", status: .transcribed,
                                     segments: [TranscriptSegment(start: 0, end: 100, speaker: .me, text: "a")])
        let built = SpeakerRegistry.build(microphoneClusters: [0: [0..<90], 1: [90..<100]], systemClusters: nil,
                                          hasSystemTrack: false, previous: previous)
        XCTAssertEqual(built.speakers.map(\.id), ["s1", "s2"])
        XCTAssertEqual(built.speakers.map(\.isMe), [false, false])
    }

    func testRenamedFallbackSpeakersPassTheirNameAndMeToTheVoiceThatOverlapsThemMost() {
        // Transcribed before the speaker model was downloaded, then renamed.
        let previous = MeetingRecord(
            title: "Звонок", status: .transcribed, hasSystemAudio: true,
            segments: [
                TranscriptSegment(start: 0, end: 10, speaker: .me, text: "a"),
                TranscriptSegment(start: 10, end: 20, speaker: .others, text: "b"),
                TranscriptSegment(start: 20, end: 24, speaker: .others, text: "c")
            ],
            speakers: [
                MeetingSpeaker(id: "me", track: .microphone, displayName: "Михаил", colorIndex: 0, isMe: true),
                MeetingSpeaker(id: "others", track: .system, displayName: "Аня", colorIndex: 1)
            ])

        let built = SpeakerRegistry.build(microphoneClusters: [0: [0..<10]], systemClusters: [0: [20..<24], 1: [10..<20]],
                                          hasSystemTrack: true, previous: previous)

        XCTAssertEqual(built.speakers, [
            MeetingSpeaker(id: "s1", track: .microphone, number: 1, displayName: "Михаил", colorIndex: 0, isMe: true),
            MeetingSpeaker(id: "s2", track: .system, number: 2, displayName: "Аня", colorIndex: 1),
            MeetingSpeaker(id: "s3", track: .system, number: 3, colorIndex: 2)
        ])
        XCTAssertEqual(built.idForCluster[.system], [1: "s2", 0: "s3"])

        // A stored fallback without a name or «Я» has nothing to pass on.
        var reset = previous
        reset.speakers = [MeetingSpeaker(id: "others", track: .system, displayName: " ", colorIndex: 1)]
        let plain = SpeakerRegistry.build(microphoneClusters: nil, systemClusters: [0: [10..<24]], hasSystemTrack: true,
                                          previous: reset)
        XCTAssertEqual(plain.speakers, [MeetingSpeaker(id: "s1", track: .system, number: 1, colorIndex: 0)])
    }

    func testSpeakersOfTheOtherTrackAreNotMatched() {
        let previous = MeetingRecord(
            title: "Звонок", status: .transcribed, hasSystemAudio: true,
            segments: [TranscriptSegment(start: 0, end: 10, speaker: .others, text: "a", speakerID: "s1")],
            speakers: [MeetingSpeaker(id: "s1", track: .system, number: 1, displayName: "Олег", colorIndex: 0)])
        let built = SpeakerRegistry.build(microphoneClusters: [0: [0..<10]], systemClusters: nil, hasSystemTrack: true,
                                          previous: previous)
        XCTAssertEqual(built.speakers.map(\.id), ["s2"])
        XCTAssertEqual(built.speakers.first?.track, .microphone)
    }
}
