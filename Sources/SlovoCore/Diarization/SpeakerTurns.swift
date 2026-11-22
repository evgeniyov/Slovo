import Foundation

/// Cleans up raw diarization turns before clips are planned. Pure.
public enum SpeakerTurns {
    /// Shorter pieces left after clipping hold no word worth a transcript line.
    static let minimumTurnSeconds: TimeInterval = 0.25
    /// Same-speaker turns closer than this are one utterance with a breath in between.
    static let mergeGapSeconds: TimeInterval = 0.8
    /// A cluster under both limits is a stray label rather than a person: a cough, a
    /// laugh or a few frames the clustering could not place.
    static let tinyClusterSeconds: TimeInterval = 3
    static let tinyClusterShare = 0.02

    /// Absorbs floating-point noise in durations that are exactly on a limit.
    private static let tolerance: TimeInterval = 1e-9

    /// Clamps turns to the track, clips overlaps, drops turns under 0.25 s, merges
    /// same-cluster turns separated by less than 0.8 s, folds tiny clusters into their
    /// neighbours unless `hint.exactly` is set, and renumbers clusters by first appearance.
    public static func normalize(_ turns: [SpeakerTurn], trackDuration: TimeInterval,
                                 hint: SpeakerCountHint) -> [SpeakerTurn] {
        let limit = trackDuration.isFinite ? max(0, trackDuration) : .infinity
        var kept = clippedToOneSpeaker(clamped(turns, limit: limit))
            .filter { $0.end - $0.start >= minimumTurnSeconds - tolerance }
        kept = mergingClose(kept)
        // With an exact count the user told us how many people there are, so a quiet
        // person must keep their own cluster.
        if hint.exactly == nil {
            kept = foldingTinyClusters(kept)
        }
        return renumbered(kept)
    }

    /// Finite turns inside `0...limit`, sorted by start.
    private static func clamped(_ turns: [SpeakerTurn], limit: TimeInterval) -> [SpeakerTurn] {
        turns.compactMap { turn -> SpeakerTurn? in
            guard turn.start.isFinite, turn.end.isFinite, turn.cluster >= 0 else { return nil }
            let start = min(max(turn.start, 0), limit)
            let end = min(max(turn.end, 0), limit)
            return end > start ? SpeakerTurn(start: start, end: end, cluster: turn.cluster) : nil
        }
        .sorted { ($0.start, $0.end, $0.cluster) < ($1.start, $1.end, $1.cluster) }
    }

    /// Gives overlapping time to the turn that started first, because a transcript line
    /// has one speaker.
    private static func clippedToOneSpeaker(_ sorted: [SpeakerTurn]) -> [SpeakerTurn] {
        var result: [SpeakerTurn] = []
        var coveredUntil = -TimeInterval.infinity
        for var turn in sorted {
            if turn.start < coveredUntil {
                turn.start = coveredUntil
            }
            guard turn.end > turn.start else { continue }
            coveredUntil = max(coveredUntil, turn.end)
            result.append(turn)
        }
        return result
    }

    private static func mergingClose(_ sorted: [SpeakerTurn]) -> [SpeakerTurn] {
        var result: [SpeakerTurn] = []
        for turn in sorted {
            if let last = result.last, last.cluster == turn.cluster, turn.start - last.end < mergeGapSeconds {
                result[result.count - 1].end = max(last.end, turn.end)
            } else {
                result.append(turn)
            }
        }
        return result
    }

    private static func foldingTinyClusters(_ sorted: [SpeakerTurn]) -> [SpeakerTurn] {
        let totalSpeech = sorted.reduce(0) { $0 + ($1.end - $1.start) }
        var speechByCluster: [Int: TimeInterval] = [:]
        for turn in sorted {
            speechByCluster[turn.cluster, default: 0] += turn.end - turn.start
        }
        let tiny = Set(speechByCluster.compactMap { cluster, seconds in
            seconds < tinyClusterSeconds && seconds < tinyClusterShare * totalSpeech ? cluster : nil
        })
        guard !tiny.isEmpty, tiny.count < speechByCluster.count else { return sorted }

        let anchors = sorted.filter { !tiny.contains($0.cluster) }
        let reassigned = sorted.map { turn -> SpeakerTurn in
            guard tiny.contains(turn.cluster), let nearest = nearest(to: turn, among: anchors) else { return turn }
            return SpeakerTurn(start: turn.start, end: turn.end, cluster: nearest.cluster)
        }
        return mergingClose(reassigned)
    }

    /// The anchor with the smallest gap to `turn`; the earlier one wins a tie, since a
    /// short interjection more often answers the previous speaker.
    private static func nearest(to turn: SpeakerTurn, among anchors: [SpeakerTurn]) -> SpeakerTurn? {
        var best: SpeakerTurn?
        var bestGap = TimeInterval.infinity
        for anchor in anchors {
            let gap = max(0, anchor.start - turn.end, turn.start - anchor.end)
            if gap < bestGap {
                bestGap = gap
                best = anchor
            }
        }
        return best
    }

    private static func renumbered(_ sorted: [SpeakerTurn]) -> [SpeakerTurn] {
        var numbers: [Int: Int] = [:]
        var result: [SpeakerTurn] = []
        result.reserveCapacity(sorted.count)
        for turn in sorted {
            let number: Int
            if let existing = numbers[turn.cluster] {
                number = existing
            } else {
                number = numbers.count
                numbers[turn.cluster] = number
            }
            result.append(SpeakerTurn(start: turn.start, end: turn.end, cluster: number))
        }
        return result
    }
}
