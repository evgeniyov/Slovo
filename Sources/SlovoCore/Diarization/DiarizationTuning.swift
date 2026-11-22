import FluidAudio
import Foundation

/// Every value that shapes a diarization run, in one place. The evaluation confirms or
/// replaces the standard set before release, and `description` is what a meeting
/// records in `MeetingDiarization.configuration`.
public struct DiarizationTuning: Equatable, Sendable, CustomStringConvertible {
    /// Segmentation window step as a share of the 10 s window.
    public var segmentationStepRatio: Double
    /// Speech shorter than this gets no embedding of its own.
    public var minimumSegmentSeconds: Double
    /// Re-embeds frames without an embedding vote instead of giving them to the first cluster.
    public var zeroVoteReembed: Bool
    public var zeroVoteMinimumSeconds: Double
    /// Euclidean cut in (0, 2]; larger merges more voices.
    public var clusteringThreshold: Double
    public var excludeOverlap: Bool
    /// One speaker at a time, as transcript lines need.
    public var exclusiveSegments: Bool

    /// A track with less energy-detected speech than this is not separated.
    public static let minimumTrackSpeechSeconds: TimeInterval = 5.0

    /// The values the meeting pipeline uses.
    ///
    /// Step 0.1 with no minimum segment scored best on VoxConverse (13.89% against 15.07%
    /// at the library's 0.2 and 1 s) and keeps sub-second answers; it is about twice as
    /// slow, which does not matter after a meeting. Threshold 0.775 is the current
    /// meaning of the value behind FluidAudio's published AMI result. Zero-vote
    /// re-embedding counters the pull of vote-less frames toward the first speaker.
    public static let standard = DiarizationTuning(segmentationStepRatio: 0.1, minimumSegmentSeconds: 0.0,
                                                   zeroVoteReembed: true, zeroVoteMinimumSeconds: 0.4,
                                                   clusteringThreshold: 0.775, excludeOverlap: true,
                                                   exclusiveSegments: true)

    public init(segmentationStepRatio: Double, minimumSegmentSeconds: Double, zeroVoteReembed: Bool,
                zeroVoteMinimumSeconds: Double, clusteringThreshold: Double, excludeOverlap: Bool,
                exclusiveSegments: Bool) {
        self.segmentationStepRatio = segmentationStepRatio
        self.minimumSegmentSeconds = minimumSegmentSeconds
        self.zeroVoteReembed = zeroVoteReembed
        self.zeroVoteMinimumSeconds = zeroVoteMinimumSeconds
        self.clusteringThreshold = clusteringThreshold
        self.excludeOverlap = excludeOverlap
        self.exclusiveSegments = exclusiveSegments
    }

    /// Parses the bench's `step=0.1,minSeg=0,zeroVote=on,threshold=0.775` (commas or
    /// spaces); keys left out keep the standard values. Nil for an unknown key or value.
    ///
    /// Also accepts `zeroVoteMin`, `excludeOverlap` and `exclusive`, and the form
    /// `description` prints, so a recorded configuration parses back.
    public init?(configuration: String) {
        var tuning = DiarizationTuning.standard
        let items = configuration.split(whereSeparator: { $0 == "," || $0.isWhitespace })
        for item in items {
            let pair = item.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard pair.count == 2 else { return nil }
            let value = String(pair[1])
            switch pair[0].lowercased() {
            case "step":
                guard let number = Self.decimal(value), number > 0, number <= 1 else { return nil }
                tuning.segmentationStepRatio = number
            case "minseg":
                guard let number = Self.decimal(value), number >= 0 else { return nil }
                tuning.minimumSegmentSeconds = number
            case "threshold":
                guard let number = Self.decimal(value), number > 0, number <= 2 else { return nil }
                tuning.clusteringThreshold = number
            case "zerovote":
                guard let flag = Self.flag(value) else { return nil }
                tuning.zeroVoteReembed = flag
            case "zerovotemin":
                guard let number = Self.decimal(value), number >= 0 else { return nil }
                tuning.zeroVoteMinimumSeconds = number
            case "excludeoverlap":
                guard let flag = Self.flag(value) else { return nil }
                tuning.excludeOverlap = flag
            case "exclusive":
                guard let flag = Self.flag(value) else { return nil }
                tuning.exclusiveSegments = flag
            default:
                return nil
            }
        }
        self = tuning
    }

    /// "step=0.1 minSeg=0 threshold=0.775 zeroVote=on"
    public var description: String {
        "step=\(Self.number(segmentationStepRatio)) minSeg=\(Self.number(minimumSegmentSeconds)) "
            + "threshold=\(Self.number(clusteringThreshold)) zeroVote=\(zeroVoteReembed ? "on" : "off")"
    }

    /// FluidAudio's configuration for one run: the library defaults with these values
    /// set, then the count constraint of the hint. A count below one is no constraint.
    func offlineConfig(for hint: SpeakerCountHint) throws -> OfflineDiarizerConfig {
        var config = OfflineDiarizerConfig.default
        config.segmentation.stepRatio = segmentationStepRatio
        config.embedding.minSegmentDurationSeconds = minimumSegmentSeconds
        config.embedding.excludeOverlap = excludeOverlap
        config.clustering.threshold = clusteringThreshold
        config.zeroVoteReembed = OfflineDiarizerConfig.ZeroVoteReembed(enabled: zeroVoteReembed,
                                                                      minDurationSeconds: zeroVoteMinimumSeconds)
        config.postProcessing.exclusiveSegments = exclusiveSegments
        if let exactly = hint.exactly, exactly > 0 {
            config = config.withSpeakers(exactly: exactly)
        } else if let maximum = hint.maximum, maximum > 0 {
            config = config.withSpeakers(max: maximum)
        }
        try config.validate()
        return config
    }

    /// Shortest form without a locale, so the text is the same on every Mac.
    private static func number(_ value: Double) -> String {
        String(format: "%g", value)
    }

    /// `Double(_:)` always reads a dot, whatever the locale.
    private static func decimal(_ text: String) -> Double? {
        guard let value = Double(text), value.isFinite else { return nil }
        return value
    }

    private static func flag(_ text: String) -> Bool? {
        switch text.lowercased() {
        case "on", "true", "yes", "1": return true
        case "off", "false", "no", "0": return false
        default: return nil
        }
    }
}
