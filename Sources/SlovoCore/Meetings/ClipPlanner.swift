import Foundation

/// A stretch of one track transcribed as one clip.
public struct PlannedClip: Equatable, Sendable {
    public var range: Range<TimeInterval>
    /// The diarization cluster the clip belongs to; nil on a track without turns.
    public var cluster: Int?

    public init(range: Range<TimeInterval>, cluster: Int?) {
        self.range = range
        self.cluster = cluster
    }
}

/// Turns speaker turns and energy-detected speech into transcription clips of at most
/// `maxSeconds`, so every clip has one speaker and no detected speech is left out. Pure.
///
/// Speech engines return only text, so the speaker of every word is decided before
/// recognition: each clip is one turn of one person, and a line's timestamp is its
/// clip's start.
public enum ClipPlanner {
    /// Audio kept on each side of a turn, so soft word onsets and endings survive.
    static let turnPaddingSeconds: TimeInterval = 0.15
    /// Speech the diarizer did not attribute becomes a clip of its own from this length.
    static let minimumUncoveredSeconds: TimeInterval = 0.3

    /// Clips sorted by start, never overlapping. A track without turns gets its speech
    /// ranges unassigned. Without the track's energy envelope, turns longer than
    /// `maxSeconds` are cut into equal pieces.
    public static func plan(turns: [SpeakerTurn], speech: [Range<TimeInterval>], trackDuration: TimeInterval,
                            maxSeconds: Double = 20) -> [PlannedClip] {
        plan(turns: turns, speech: speech, trackDuration: trackDuration, maxSeconds: maxSeconds) { range in
            evenPieces(of: range, maxSeconds: maxSeconds)
        }
    }

    /// The pipeline's form: long turns are cut at the quietest moments of the track's
    /// envelope, pieces without sound are dropped, and uncovered stretches that hold only
    /// padding silence do not become clips.
    static func plan(turns: [SpeakerTurn], speech: [Range<TimeInterval>], trackDuration: TimeInterval,
                     maxSeconds: Double = 20, envelope: EnergyEnvelope, segmenter: SpeechSegmenter) -> [PlannedClip] {
        var splitter = segmenter
        splitter.maxSeconds = maxSeconds
        return plan(turns: turns, speech: speech, trackDuration: trackDuration, maxSeconds: maxSeconds) { range in
            quietPieces(of: range, envelope: envelope, segmenter: splitter)
        }
    }

    /// The cluster of each existing segment, in order: the one with the largest time
    /// overlap, else the nearest turn's; nil when there are no turns.
    public static func assign(segments: [TranscriptSegment], turns: [SpeakerTurn]) -> [Int?] {
        let valid = turns.filter { $0.start.isFinite && $0.end.isFinite && $0.end >= $0.start }
        guard !valid.isEmpty else { return segments.map { _ in nil } }
        return segments.map { segment -> Int? in
            let start = segment.start.isFinite ? segment.start : 0
            let end = segment.end.isFinite ? max(start, segment.end) : start
            var overlap: [Int: TimeInterval] = [:]
            for turn in valid {
                let shared = min(end, turn.end) - max(start, turn.start)
                if shared > 0 {
                    overlap[turn.cluster, default: 0] += shared
                }
            }
            // Equal overlaps go to the lower cluster, so the result does not depend on
            // dictionary order.
            if let best = overlap.max(by: { $0.value == $1.value ? $0.key > $1.key : $0.value < $1.value }) {
                return best.key
            }
            return nearestCluster(to: start..<end, among: valid)
        }
    }

    // MARK: - Planning

    private static func plan(turns: [SpeakerTurn], speech: [Range<TimeInterval>], trackDuration: TimeInterval,
                             maxSeconds: Double,
                             split: (Range<TimeInterval>) -> [Range<TimeInterval>]) -> [PlannedClip] {
        let latestEnd = (turns.map(\.end) + speech.map(\.upperBound)).filter(\.isFinite).max() ?? 0
        let limit = trackDuration.isFinite && trackDuration > 0 ? trackDuration : max(0, latestEnd)
        let speechUnion = union(speech.compactMap { clamped($0, limit: limit) })
        let ordered = exclusiveTurns(turns, limit: limit)

        guard !ordered.isEmpty else {
            return speechUnion.flatMap { range in
                piecesWithin(range, maxSeconds: maxSeconds, split: split).map { PlannedClip(range: $0, cluster: nil) }
            }
        }

        // Padding stops at the middle of the gap to the neighbouring turn, whoever speaks
        // there, so padded turns never overlap.
        var padded: [PlannedClip] = []
        for (index, turn) in ordered.enumerated() {
            var lower = max(0, turn.start - turnPaddingSeconds)
            if index > 0 {
                lower = max(lower, (ordered[index - 1].end + turn.start) / 2)
            }
            var upper = min(limit, turn.end + turnPaddingSeconds)
            if index + 1 < ordered.count {
                upper = min(upper, (turn.end + ordered[index + 1].start) / 2)
            }
            guard upper > lower else { continue }
            padded.append(PlannedClip(range: lower..<upper, cluster: turn.cluster))
        }

        var clips: [PlannedClip] = []
        for clip in padded {
            if clip.range.upperBound - clip.range.lowerBound > maxSeconds {
                clips += split(clip.range).map { PlannedClip(range: $0, cluster: clip.cluster) }
            } else {
                clips.append(clip)
            }
        }

        // Words the diarizer missed still reach the transcript, under the speaker whose
        // turn is closest.
        for piece in subtract(padded.map(\.range), from: speechUnion)
        where piece.upperBound - piece.lowerBound >= minimumUncoveredSeconds {
            let cluster = nearestCluster(to: piece, among: ordered)
            clips += split(piece).map { PlannedClip(range: $0, cluster: cluster) }
        }

        return nonOverlapping(clips)
    }

    /// Turns clamped to the track, sorted by start, with any overlap clipped to the
    /// earlier turn, as one speaker at a time needs.
    private static func exclusiveTurns(_ turns: [SpeakerTurn], limit: TimeInterval) -> [SpeakerTurn] {
        var clampedTurns: [SpeakerTurn] = []
        for turn in turns where turn.start.isFinite && turn.end.isFinite {
            let start: TimeInterval = min(max(0, turn.start), limit)
            let end: TimeInterval = min(max(0, turn.end), limit)
            if end > start {
                clampedTurns.append(SpeakerTurn(start: start, end: end, cluster: turn.cluster))
            }
        }
        let sorted = clampedTurns.sorted { left, right in
            left.start == right.start ? left.end < right.end : left.start < right.start
        }
        var result: [SpeakerTurn] = []
        for turn in sorted {
            var current = turn
            if let last = result.last, current.start < last.end {
                current.start = last.end
            }
            if current.end > current.start {
                result.append(current)
            }
        }
        return result
    }

    private static func piecesWithin(_ range: Range<TimeInterval>, maxSeconds: Double,
                                     split: (Range<TimeInterval>) -> [Range<TimeInterval>]) -> [Range<TimeInterval>] {
        range.upperBound - range.lowerBound > maxSeconds ? split(range) : [range]
    }

    private static func nonOverlapping(_ clips: [PlannedClip]) -> [PlannedClip] {
        let sorted = clips.sorted { $0.range.lowerBound < $1.range.lowerBound }
        var result: [PlannedClip] = []
        for clip in sorted {
            var current = clip
            if let last = result.last, current.range.lowerBound < last.range.upperBound {
                guard current.range.upperBound > last.range.upperBound else { continue }
                current.range = last.range.upperBound..<current.range.upperBound
            }
            result.append(current)
        }
        return result
    }

    /// The cluster of the turn whose nearest edge is closest to the range; on a tie the
    /// earlier turn, since unattributed speech most often trails a turn.
    private static func nearestCluster(to range: Range<TimeInterval>, among turns: [SpeakerTurn]) -> Int? {
        var best: (distance: TimeInterval, cluster: Int)?
        for turn in turns {
            let distance: TimeInterval
            if range.upperBound <= turn.start {
                distance = turn.start - range.upperBound
            } else if range.lowerBound >= turn.end {
                distance = range.lowerBound - turn.end
            } else {
                distance = 0
            }
            if best == nil || distance < (best?.distance ?? .infinity) {
                best = (distance, turn.cluster)
            }
        }
        return best?.cluster
    }

    // MARK: - Splitting

    private static func evenPieces(of range: Range<TimeInterval>, maxSeconds: Double) -> [Range<TimeInterval>] {
        let length = range.upperBound - range.lowerBound
        guard maxSeconds > 0, length > maxSeconds else { return [range] }
        let count = Int((length / maxSeconds).rounded(.up))
        let step = length / Double(count)
        return (0..<count).map { index in
            let lower = range.lowerBound + step * Double(index)
            let upper = index == count - 1 ? range.upperBound : range.lowerBound + step * Double(index + 1)
            return lower..<upper
        }
    }

    private static func quietPieces(of range: Range<TimeInterval>, envelope: EnergyEnvelope,
                                    segmenter: SpeechSegmenter) -> [Range<TimeInterval>] {
        let rate = Double(envelope.sampleRate)
        let frameLength = envelope.frameLength
        let firstSample = max(0, Int((range.lowerBound * rate).rounded(.down)))
        let endSample = max(firstSample, Int((range.upperBound * rate).rounded(.up)))
        let frames = (firstSample / frameLength)..<((endSample + frameLength - 1) / frameLength)
        // Envelope frames are 25 ms, so piece edges are clamped back to the range.
        return segmenter.segments(for: envelope, within: frames).compactMap { segment in
            let lower = max(range.lowerBound, segment.start)
            let upper = min(range.upperBound, segment.end)
            return upper > lower ? lower..<upper : nil
        }
    }

    // MARK: - Range arithmetic

    private static func clamped(_ range: Range<TimeInterval>, limit: TimeInterval) -> Range<TimeInterval>? {
        guard range.lowerBound.isFinite, range.upperBound.isFinite else { return nil }
        let lower = min(max(0, range.lowerBound), limit)
        let upper = min(max(0, range.upperBound), limit)
        return upper > lower ? lower..<upper : nil
    }

    /// Sorted, disjoint ranges covering the same time. Ranges that only touch stay apart,
    /// so the segmenter's consecutive pieces are not joined into one over-long range.
    static func union(_ ranges: [Range<TimeInterval>]) -> [Range<TimeInterval>] {
        var result: [Range<TimeInterval>] = []
        for range in ranges.sorted(by: { $0.lowerBound < $1.lowerBound }) where !range.isEmpty {
            if let last = result.last, range.lowerBound < last.upperBound {
                result[result.count - 1] = last.lowerBound..<max(last.upperBound, range.upperBound)
            } else {
                result.append(range)
            }
        }
        return result
    }

    /// The parts of `ranges` (sorted and disjoint) that `covered` does not cover.
    private static func subtract(_ covered: [Range<TimeInterval>], from ranges: [Range<TimeInterval>]) -> [Range<TimeInterval>] {
        let cover = union(covered)
        var result: [Range<TimeInterval>] = []
        var coverIndex = 0
        for range in ranges {
            var cursor = range.lowerBound
            while coverIndex < cover.count, cover[coverIndex].upperBound <= cursor {
                coverIndex += 1
            }
            var index = coverIndex
            while index < cover.count, cover[index].lowerBound < range.upperBound {
                if cover[index].lowerBound > cursor {
                    result.append(cursor..<cover[index].lowerBound)
                }
                cursor = max(cursor, cover[index].upperBound)
                index += 1
            }
            if cursor < range.upperBound {
                result.append(cursor..<range.upperBound)
            }
        }
        return result
    }
}
