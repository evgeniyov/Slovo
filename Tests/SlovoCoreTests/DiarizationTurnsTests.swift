import XCTest
@testable import SlovoCore

final class DiarizationTurnsTests: XCTestCase {
    private func turn(_ start: TimeInterval, _ end: TimeInterval, _ cluster: Int) -> SpeakerTurn {
        SpeakerTurn(start: start, end: end, cluster: cluster)
    }

    func testEmptyAndInvalidTurnsGiveNothing() {
        XCTAssertEqual(SpeakerTurns.normalize([], trackDuration: 60, hint: .automatic), [])
        let invalid = [turn(.nan, 3, 0), turn(1, .infinity, 0), turn(5, 4, 0), turn(1, 2, -1)]
        XCTAssertEqual(SpeakerTurns.normalize(invalid, trackDuration: 60, hint: .automatic), [])
    }

    func testTurnsAreClampedToTheTrack() {
        let turns = [turn(-1, 2, 0), turn(9, 12, 1), turn(11, 14, 0)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 10, hint: .automatic),
                       [turn(0, 2, 0), turn(9, 10, 1)])
    }

    func testTurnsAreSortedAndOverlapGoesToTheEarlierTurn() {
        let turns = [turn(4, 8, 1), turn(0, 5, 0), turn(1, 3, 1), turn(8.5, 12, 0)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 60, hint: .automatic),
                       [turn(0, 5, 0), turn(5, 8, 1), turn(8.5, 12, 0)])
    }

    func testTurnsShorterThanAQuarterSecondAreDropped() {
        let turns = [turn(0, 4, 0), turn(4.5, 4.7, 1), turn(6, 6.25, 1), turn(8, 12, 0)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 60, hint: SpeakerCountHint(exactly: 2)),
                       [turn(0, 4, 0), turn(6, 6.25, 1), turn(8, 12, 0)])
    }

    func testClippingCanLeaveATurnTooShortToKeep() {
        let turns = [turn(0, 5, 0), turn(4.9, 5.1, 1), turn(7, 10, 1)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 60, hint: SpeakerCountHint(exactly: 2)),
                       [turn(0, 5, 0), turn(7, 10, 1)])
    }

    func testSameSpeakerTurnsCloserThanPointEightSecondsMerge() {
        let turns = [turn(0, 2, 0), turn(2.75, 4, 0), turn(4.5, 9, 1), turn(9.9, 12, 1), turn(13, 16, 0)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 60, hint: .automatic),
                       [turn(0, 4, 0), turn(4.5, 9, 1), turn(9.9, 12, 1), turn(13, 16, 0)])
    }

    func testTinyClusterJoinsTheNearestTurnAndMergesWithIt() {
        // Cluster 1 holds 1 s of 61 s: under 3 s and under 2%.
        let turns = [turn(0, 30, 0), turn(30.5, 31.5, 1), turn(40, 70, 2)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 80, hint: .automatic),
                       [turn(0, 31.5, 0), turn(40, 70, 1)])
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 80, hint: SpeakerCountHint(maximum: 3)),
                       [turn(0, 31.5, 0), turn(40, 70, 1)])
    }

    func testTinyClusterTurnsAreReassignedOneByOne() {
        let turns = [turn(0, 20, 0), turn(21, 21.5, 2), turn(30, 50, 1), turn(58, 58.5, 2), turn(60, 80, 0)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 90, hint: .automatic),
                       [turn(0, 20, 0), turn(21, 21.5, 0), turn(30, 50, 1), turn(58, 58.5, 0), turn(60, 80, 0)])
    }

    func testAnExactHintKeepsTinyClusters() {
        let turns = [turn(0, 30, 0), turn(30.5, 31.5, 1), turn(40, 70, 2)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 80, hint: SpeakerCountHint(exactly: 3)),
                       [turn(0, 30, 0), turn(30.5, 31.5, 1), turn(40, 70, 2)])
    }

    func testAClusterWithThreeSecondsOrAShareOfTwoPercentIsNotTiny() {
        // 3 s of 63 s: not under 3 s.
        let longEnough = [turn(0, 30, 0), turn(31, 34, 1), turn(40, 70, 2)]
        XCTAssertEqual(SpeakerTurns.normalize(longEnough, trackDuration: 80, hint: .automatic).map(\.cluster), [0, 1, 2])
        // 1 s of 21 s is almost 5%.
        let largeShare = [turn(0, 10, 0), turn(11, 12, 1), turn(20, 30, 0)]
        XCTAssertEqual(SpeakerTurns.normalize(largeShare, trackDuration: 40, hint: .automatic).map(\.cluster), [0, 1, 0])
    }

    func testAOneSpeakerTrackKeepsItsOnlyCluster() {
        let turns = [turn(0, 1, 4), turn(3, 4, 4)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 10, hint: .automatic),
                       [turn(0, 1, 0), turn(3, 4, 0)])
    }

    func testClustersAreRenumberedByFirstAppearance() {
        let turns = [turn(0, 5, 5), turn(6, 11, 2), turn(12, 17, 5), turn(18, 23, 7)]
        XCTAssertEqual(SpeakerTurns.normalize(turns, trackDuration: 30, hint: .automatic).map(\.cluster), [0, 1, 0, 2])
    }
}
