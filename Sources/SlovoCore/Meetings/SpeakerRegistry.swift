import Foundation

/// Numbers separated voices and carries the user's names, colors and "This is me" over
/// re-runs. Pure.
///
/// Diarization clusters are local to one run, so a re-run cannot know that its cluster 2
/// is the person the user named «Аня». Speakers are therefore matched by when they
/// talk: the voice whose time overlaps a previous speaker's the most takes over that
/// speaker's id, number, name, color and «Я». A track's fallback speaker that the user
/// renamed or marked as «Я» passes its name and «Я» the same way to a voice, which gets
/// a number of its own.
public enum SpeakerRegistry {
    /// «Я» is set automatically in calls when one microphone voice holds this share.
    static let automaticMeShare = 0.8
    /// A re-run speaker takes over a previous one only above this share of its own time.
    static let minimumMatchShare = 0.3

    /// - Parameters:
    ///   - microphoneClusters: time ranges of each microphone cluster; nil when the track
    ///     was not separated.
    ///   - systemClusters: the same for system audio.
    ///   - previous: the meeting before this run, whose speakers are matched by overlap.
    /// - Returns: the speakers to store, and the speaker id of every cluster per track.
    public static func build(microphoneClusters: [Int: [Range<TimeInterval>]]?,
                             systemClusters: [Int: [Range<TimeInterval>]]?,
                             hasSystemTrack: Bool,
                             previous: MeetingRecord?) -> (speakers: [MeetingSpeaker], idForCluster: [MeetingTrack: [Int: String]]) {
        var highest = highestNumber(in: previous)
        var speakers: [MeetingSpeaker] = []
        var idForCluster: [MeetingTrack: [Int: String]] = [:]

        for track in MeetingTrack.allCases {
            guard let clusters = track == .microphone ? microphoneClusters : systemClusters else {
                // An unseparated track shows its fallback speaker through
                // `effectiveSpeakers`; only a stored one, which holds the user's name or
                // «Я», needs carrying over.
                let fallbackID = MeetingSpeaker.fallback(for: track).id
                if let kept = previous?.speakers.first(where: { $0.id == fallbackID && $0.track == track }) {
                    speakers.append(kept)
                }
                continue
            }

            let ordered = firstAppearanceOrder(clusters)
            let matches = matchPrevious(ordered: ordered, clusters: clusters, track: track, previous: previous)
            var ids: [Int: String] = [:]
            for cluster in ordered {
                let match = matches[cluster]
                if let match, let number = match.number {
                    speakers.append(MeetingSpeaker(id: match.id, track: track, number: number,
                                                   displayName: match.displayName, colorIndex: match.colorIndex,
                                                   isMe: match.isMe))
                    ids[cluster] = match.id
                } else {
                    // A voice matched to a fallback speaker cannot keep its id, which stands
                    // for a whole track, so it is numbered like a new voice.
                    highest += 1
                    let speaker = MeetingSpeaker(id: "s\(highest)", track: track, number: highest,
                                                 displayName: match?.displayName,
                                                 colorIndex: (highest - 1) % MeetingSpeaker.paletteSize,
                                                 isMe: match?.isMe ?? false)
                    speakers.append(speaker)
                    ids[cluster] = speaker.id
                }
            }
            idForCluster[track] = ids
        }

        // In person the laptop hears everyone equally, so only calls, where the user is
        // usually the one voice on the microphone, get «Я» automatically.
        if hasSystemTrack, !speakers.contains(where: \.isMe), let microphoneClusters,
           let dominant = dominantCluster(microphoneClusters),
           let id = idForCluster[.microphone]?[dominant],
           let index = speakers.firstIndex(where: { $0.id == id }) {
            speakers[index].isMe = true
        }

        return (speakers, idForCluster)
    }

    /// The speakers an earlier run found on a track that this run did not separate: the
    /// separation was turned off, the model is missing, it failed, or the track had too
    /// little speech this time. Without this a full re-run folds every line of that track
    /// back into «Я» or «Собеседники» and the names the user gave are gone for good.
    ///
    /// Each new segment goes to the earlier speaker whose lines overlap it the most in
    /// time, else to the one whose line is nearest (`ClipPlanner.assign` over the earlier
    /// lines as turns).
    ///
    /// - Returns: nil when `previous` has no numbered speaker on the track, since a track's
    ///   fallback speaker needs no carrying over; otherwise the speaker id for each segment
    ///   in order (nil for the fallback), and the earlier speakers that received segments.
    static func carryOver(track: MeetingTrack, segments: [TranscriptSegment], previous: MeetingRecord?)
        -> (speakerIDs: [String?], speakers: [MeetingSpeaker])? {
        guard let previous else { return nil }
        let earlier = previous.segments.filter {
            MeetingTrack($0.speaker) == track && $0.start.isFinite && $0.end.isFinite
        }
        let earlierIDs = earlier.map { previous.speakerID(for: $0) }
        let numbered = Set(previous.speakers.filter { $0.track == track && $0.number != nil }.map(\.id))
        guard earlierIDs.contains(where: numbered.contains) else { return nil }

        var clusterOfID: [String: Int] = [:]
        var idOfCluster: [String] = []
        var turns: [SpeakerTurn] = []
        for (segment, id) in zip(earlier, earlierIDs) {
            let cluster: Int
            if let known = clusterOfID[id] {
                cluster = known
            } else {
                cluster = idOfCluster.count
                clusterOfID[id] = cluster
                idOfCluster.append(id)
            }
            turns.append(SpeakerTurn(start: segment.start, end: max(segment.start, segment.end), cluster: cluster))
        }

        let fallbackID = MeetingSpeaker.fallback(for: track).id
        let speakerIDs = ClipPlanner.assign(segments: segments, turns: turns).map { cluster -> String? in
            guard let cluster, idOfCluster.indices.contains(cluster) else { return nil }
            let id = idOfCluster[cluster]
            return id == fallbackID ? nil : id
        }
        let used = Set(speakerIDs.compactMap { $0 })
        let speakers = previous.speakers.filter { $0.track == track && used.contains($0.id) }
        return (speakerIDs, speakers)
    }

    // MARK: - Matching

    /// One-to-one, greedy by the seconds of overlap, largest first.
    private static func matchPrevious(ordered: [Int], clusters: [Int: [Range<TimeInterval>]], track: MeetingTrack,
                                      previous: MeetingRecord?) -> [Int: MeetingSpeaker] {
        guard let previous else { return [:] }
        // A fallback speaker stands for everyone its segments show, not one voice, so it
        // takes part only when it holds the user's work: a name or «Я». Its time is the time
        // of those segments, and the voice that overlaps it the most receives that work.
        let fallbackID = MeetingSpeaker.fallback(for: track).id
        let candidates = previous.speakers.filter { speaker in
            guard speaker.track == track else { return false }
            if speaker.number != nil {
                return true
            }
            let named = !(speaker.displayName.map(TranscriptMerger.normalizedText) ?? "").isEmpty
            return speaker.id == fallbackID && (named || speaker.isMe)
        }
        guard !candidates.isEmpty else { return [:] }

        let candidateTimes = candidates.map { candidate in
            ClipPlanner.union(previous.segments
                .filter { previous.speakerID(for: $0) == candidate.id }
                .compactMap { segment -> Range<TimeInterval>? in
                    segment.end > segment.start ? segment.start..<segment.end : nil
                })
        }

        var pairs: [(overlap: TimeInterval, newOrder: Int, candidateOrder: Int)] = []
        for (newOrder, cluster) in ordered.enumerated() {
            let times = ClipPlanner.union(clusters[cluster] ?? [])
            let total = length(of: times)
            guard total > 0 else { continue }
            for (candidateOrder, candidateTime) in candidateTimes.enumerated() {
                let shared = overlap(times, candidateTime)
                if shared > 0, shared >= minimumMatchShare * total {
                    pairs.append((shared, newOrder, candidateOrder))
                }
            }
        }
        pairs.sort { left, right in
            if left.overlap != right.overlap { return left.overlap > right.overlap }
            if left.newOrder != right.newOrder { return left.newOrder < right.newOrder }
            return left.candidateOrder < right.candidateOrder
        }

        var result: [Int: MeetingSpeaker] = [:]
        var taken: Set<Int> = []
        for pair in pairs {
            let cluster = ordered[pair.newOrder]
            guard result[cluster] == nil, !taken.contains(pair.candidateOrder) else { continue }
            result[cluster] = candidates[pair.candidateOrder]
            taken.insert(pair.candidateOrder)
        }
        return result
    }

    /// The highest speaker number the meeting has used, so new speakers never take the
    /// number of a merged one and exported timecoded references stay valid.
    ///
    /// A merge removes the source speaker, so stored numbers alone can miss it: runs and
    /// merges record the highest number in `MeetingRecord.highestSpeakerNumber`, and for
    /// meetings saved before that the ids segments carry and the counts of the last run
    /// cover the usual cases.
    static func highestNumber(in meeting: MeetingRecord?) -> Int {
        guard let meeting else { return 0 }
        // Counts are bounded before adding, so a hand-edited file cannot overflow the sum.
        let counts = meeting.diarization?.speakersPerTrack.values.map { min(max(0, $0), MeetingSpeaker.maximumNumber) } ?? []
        var highest = max(min(counts.reduce(0, +), MeetingSpeaker.maximumNumber), meeting.highestSpeakerNumber ?? 0)
        for speaker in meeting.speakers {
            highest = max(highest, speaker.number ?? 0, number(fromID: speaker.id) ?? 0)
        }
        for segment in meeting.segments {
            if let id = segment.speakerID, let value = number(fromID: id) {
                highest = max(highest, value)
            }
        }
        return highest
    }

    /// What `MeetingRecord.highestSpeakerNumber` becomes when `speakers` replace the
    /// speakers of `meeting`: never lower than before; nil while no number was ever used.
    static func highestNumber(keeping meeting: MeetingRecord?, with speakers: [MeetingSpeaker]) -> Int? {
        let highest = max(highestNumber(in: meeting), speakers.compactMap(\.number).max() ?? 0)
        return highest > 0 ? highest : nil
    }

    private static func number(fromID id: String) -> Int? {
        guard id.hasPrefix("s"), let value = Int(id.dropFirst()) else { return nil }
        return MeetingSpeaker.validNumber(value)
    }

    // MARK: - Time

    private static func firstAppearanceOrder(_ clusters: [Int: [Range<TimeInterval>]]) -> [Int] {
        clusters
            .compactMap { cluster, ranges -> (cluster: Int, start: TimeInterval)? in
                guard let start = ranges.map(\.lowerBound).min() else { return nil }
                return (cluster, start)
            }
            .sorted { $0.start == $1.start ? $0.cluster < $1.cluster : $0.start < $1.start }
            .map(\.cluster)
    }

    private static func dominantCluster(_ clusters: [Int: [Range<TimeInterval>]]) -> Int? {
        let times = clusters.mapValues { length(of: ClipPlanner.union($0)) }
        let total = times.values.reduce(0, +)
        guard total > 0,
              let best = times.max(by: { $0.value == $1.value ? $0.key > $1.key : $0.value < $1.value }),
              best.value >= automaticMeShare * total else {
            return nil
        }
        return best.key
    }

    private static func length(of ranges: [Range<TimeInterval>]) -> TimeInterval {
        ranges.reduce(0) { $0 + ($1.upperBound - $1.lowerBound) }
    }

    /// Seconds shared by two sorted, disjoint range lists.
    private static func overlap(_ left: [Range<TimeInterval>], _ right: [Range<TimeInterval>]) -> TimeInterval {
        var total: TimeInterval = 0
        var leftIndex = 0
        var rightIndex = 0
        while leftIndex < left.count, rightIndex < right.count {
            let a = left[leftIndex]
            let b = right[rightIndex]
            let shared = min(a.upperBound, b.upperBound) - max(a.lowerBound, b.lowerBound)
            if shared > 0 {
                total += shared
            }
            if a.upperBound < b.upperBound {
                leftIndex += 1
            } else {
                rightIndex += 1
            }
        }
        return total
    }
}
