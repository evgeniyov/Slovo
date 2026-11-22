import FluidAudio
import XCTest
@testable import SlovoCore

final class DiarizationTuningTests: XCTestCase {
    func testStandardValues() {
        let tuning = DiarizationTuning.standard
        XCTAssertEqual(tuning.segmentationStepRatio, 0.1)
        XCTAssertEqual(tuning.minimumSegmentSeconds, 0)
        XCTAssertTrue(tuning.zeroVoteReembed)
        XCTAssertEqual(tuning.zeroVoteMinimumSeconds, 0.4)
        XCTAssertEqual(tuning.clusteringThreshold, 0.775)
        XCTAssertTrue(tuning.excludeOverlap)
        XCTAssertTrue(tuning.exclusiveSegments)
        XCTAssertEqual(DiarizationTuning.minimumTrackSpeechSeconds, 5)
        XCTAssertEqual(tuning.description, "step=0.1 minSeg=0 threshold=0.775 zeroVote=on")
    }

    func testConfigurationStringsParse() throws {
        let bench = try XCTUnwrap(DiarizationTuning(configuration: "step=0.2,minSeg=1.0,zeroVote=off,threshold=0.6"))
        XCTAssertEqual(bench.segmentationStepRatio, 0.2)
        XCTAssertEqual(bench.minimumSegmentSeconds, 1)
        XCTAssertFalse(bench.zeroVoteReembed)
        XCTAssertEqual(bench.clusteringThreshold, 0.6)
        XCTAssertEqual(bench.description, "step=0.2 minSeg=1 threshold=0.6 zeroVote=off")

        XCTAssertEqual(DiarizationTuning(configuration: DiarizationTuning.standard.description), .standard)
        XCTAssertEqual(DiarizationTuning(configuration: ""), .standard)
        XCTAssertEqual(DiarizationTuning(configuration: "threshold=0.85")?.segmentationStepRatio, 0.1)

        let extra = try XCTUnwrap(DiarizationTuning(configuration: "zeroVoteMin=0.8 excludeOverlap=false exclusive=off"))
        XCTAssertEqual(extra.zeroVoteMinimumSeconds, 0.8)
        XCTAssertFalse(extra.excludeOverlap)
        XCTAssertFalse(extra.exclusiveSegments)
    }

    func testBadConfigurationStringsAreRejected() {
        for text in ["speed=2", "step", "step=", "step=fast", "step=0", "step=1.5", "threshold=2.5",
                     "threshold=0", "minSeg=-1", "zeroVote=maybe", "step=0,1", "threshold=nan"] {
            XCTAssertNil(DiarizationTuning(configuration: text), text)
        }
    }

    func testAutomaticConfigCarriesEveryValue() throws {
        let config = try DiarizationTuning.standard.offlineConfig(for: .automatic)
        XCTAssertEqual(config.segmentation.stepRatio, 0.1)
        XCTAssertEqual(config.embedding.minSegmentDurationSeconds, 0)
        XCTAssertTrue(config.embedding.excludeOverlap)
        XCTAssertTrue(config.zeroVoteReembed.enabled)
        XCTAssertEqual(config.zeroVoteReembed.minDurationSeconds, 0.4)
        XCTAssertEqual(config.clustering.threshold, 0.775)
        XCTAssertTrue(config.postProcessing.exclusiveSegments)
        XCTAssertNil(config.clustering.numSpeakers)
        XCTAssertNil(config.clustering.minSpeakers)
        XCTAssertNil(config.clustering.maxSpeakers)
        XCTAssertNoThrow(try config.validate())
    }

    func testExactHintSetsTheSpeakerCount() throws {
        let config = try DiarizationTuning.standard.offlineConfig(for: SpeakerCountHint(exactly: 3, maximum: 8))
        XCTAssertEqual(config.clustering.numSpeakers, 3)
        XCTAssertNil(config.clustering.minSpeakers)
        XCTAssertNil(config.clustering.maxSpeakers)
        XCTAssertEqual(config.clustering.threshold, 0.775)
        XCTAssertNoThrow(try config.validate())
    }

    func testMaximumHintSetsTheUpperBound() throws {
        let config = try DiarizationTuning.standard.offlineConfig(for: SpeakerCountHint(maximum: 4))
        XCTAssertEqual(config.clustering.maxSpeakers, 4)
        XCTAssertNil(config.clustering.minSpeakers)
        XCTAssertNil(config.clustering.numSpeakers)
        XCTAssertEqual(config.segmentation.stepRatio, 0.1)
        XCTAssertNoThrow(try config.validate())
    }

    func testCountsBelowOneAreNoConstraint() throws {
        let config = try DiarizationTuning.standard.offlineConfig(for: SpeakerCountHint(exactly: 0, maximum: 0))
        XCTAssertNil(config.clustering.numSpeakers)
        XCTAssertNil(config.clustering.maxSpeakers)
    }

    func testInvalidTuningFailsValidation() {
        var tuning = DiarizationTuning.standard
        tuning.clusteringThreshold = 3
        XCTAssertThrowsError(try tuning.offlineConfig(for: .automatic))
    }
}
