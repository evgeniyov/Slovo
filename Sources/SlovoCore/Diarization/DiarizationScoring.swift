import FluidAudio
import Foundation

/// Diarization error rate for the bench, so `slovo-bench` reaches FluidAudio's scorer
/// only through SlovoCore.
///
/// The scorer follows NIST md-eval and pyannote.metrics: overlapping reference speech
/// counts, and the collar removes frames around every reference boundary. FluidAudio's
/// published numbers ignore overlap, so reports state which rules they used.
public enum DiarizationScoring {
    /// One labelled stretch of speech in a reference or a hypothesis.
    public struct Segment: Equatable, Sendable {
        public var speaker: String
        public var start: TimeInterval
        public var end: TimeInterval

        public init(speaker: String, start: TimeInterval, end: TimeInterval) {
            self.speaker = speaker
            self.start = start
            self.end = end
        }
    }

    /// Frame-wise scores with the optimal one-to-one speaker mapping.
    public struct Score: Equatable, Sendable {
        /// (miss + false alarm + confusion) / reference speech.
        public var der: Double
        /// Seconds of reference speech with no hypothesis speaker.
        public var miss: Double
        /// Seconds of hypothesis speech with no reference speaker.
        public var falseAlarm: Double
        /// Seconds attributed to the wrong speaker under the mapping.
        public var confusion: Double
        /// Scored reference speech in seconds, collars excluded.
        public var referenceSpeechSeconds: Double
        /// Hypothesis label → reference label.
        public var mapping: [String: String]

        public init(der: Double, miss: Double, falseAlarm: Double, confusion: Double,
                    referenceSpeechSeconds: Double, mapping: [String: String]) {
            self.der = der
            self.miss = miss
            self.falseAlarm = falseAlarm
            self.confusion = confusion
            self.referenceSpeechSeconds = referenceSpeechSeconds
            self.mapping = mapping
        }
    }

    /// Scores on a 10 ms grid; `collar` is the full width excluded around each
    /// reference boundary.
    public static func der(reference: [Segment], hypothesis: [Segment], collar: TimeInterval) -> Score {
        // The scorer traps on a negative collar, so bad input is corrected instead.
        let safeCollar = collar.isFinite ? max(0, collar) : 0
        let result = DiarizationDER.compute(ref: scorable(reference), hyp: scorable(hypothesis),
                                            frameStep: 0.01, collar: safeCollar)
        return Score(der: result.der, miss: result.miss, falseAlarm: result.falseAlarm,
                     confusion: result.confusion, referenceSpeechSeconds: result.totalRefSpeech,
                     mapping: result.mapping)
    }

    private static func scorable(_ segments: [Segment]) -> [DERSpeakerSegment] {
        segments.compactMap { segment in
            guard segment.start.isFinite, segment.end.isFinite else { return nil }
            let start = max(0, segment.start)
            guard segment.end > start else { return nil }
            return DERSpeakerSegment(speaker: segment.speaker, start: start, end: segment.end)
        }
    }
}
