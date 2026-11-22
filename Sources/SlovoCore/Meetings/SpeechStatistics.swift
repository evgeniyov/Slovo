import Foundation

/// How much of a meeting one voice said.
///
/// Derived from the transcript every time it is shown and never stored, so a meeting
/// recorded long before these numbers existed has them the moment it is opened.
public struct SpeakerSpeechStats: Hashable, Sendable, Identifiable {
    public var speakerID: String
    /// Seconds this speaker was speaking. Segments of one speaker that overlap count
    /// once, so a run that cut a phrase in two cannot inflate the total.
    public var speakingSeconds: TimeInterval
    /// This speaker's part of all the speaking in the meeting, 0...1. The shares add up
    /// to 1, so they answer "who talked how much" even when two voices ran over each
    /// other; how much of the recording was speech at all is `SpeechStats.speechShare`.
    public var share: Double
    /// Times the speaker took the floor: a run of their own segments, ended by another
    /// voice or by a silence of their own longer than the pause threshold.
    public var turns: Int
    /// `speakingSeconds` over `turns`, so the numbers on one row stay consistent:
    /// speaking time is turns times the average.
    public var averageTurnSeconds: TimeInterval
    public var words: Int
    /// Words over speaking minutes, or nil when so little was said that a rate would
    /// describe where the segments were cut rather than the speaker.
    public var wordsPerMinute: Double?

    public var id: String { speakerID }

    public init(speakerID: String, speakingSeconds: TimeInterval = 0, share: Double = 0, turns: Int = 0,
                averageTurnSeconds: TimeInterval = 0, words: Int = 0, wordsPerMinute: Double? = nil) {
        self.speakerID = speakerID
        self.speakingSeconds = speakingSeconds
        self.share = share
        self.turns = turns
        self.averageTurnSeconds = averageTurnSeconds
        self.words = words
        self.wordsPerMinute = wordsPerMinute
    }
}

/// A meeting's speech in numbers: who spoke how much, and how much of the recording was
/// speech at all.
public struct SpeechStats: Hashable, Sendable {
    /// Only speakers who said something, the longest speaking time first; ties keep the
    /// meeting's own speaker order.
    public var speakers: [SpeakerSpeechStats]
    /// The stretch speech and silence divide: the recording's duration, or the end of the
    /// last word when a hand-edited duration stops before it.
    public var totalSeconds: TimeInterval
    /// Seconds with at least one voice speaking; two voices at once count once.
    public var speechSeconds: TimeInterval
    /// `speechSeconds` of `totalSeconds`, 0...1.
    public var speechShare: Double
    /// Silences between the first and the last word longer than `pauseSeconds`. The quiet
    /// before a meeting gets going and after it ends is not a pause in the conversation.
    public var pauseCount: Int
    public var longestPauseSeconds: TimeInterval
    /// The silence a gap has to beat to count as a pause.
    public var pauseSeconds: TimeInterval

    public var silenceSeconds: TimeInterval { max(0, totalSeconds - speechSeconds) }

    /// True when nothing was said at all: no transcript yet, or only empty segments.
    public var isEmpty: Bool { speakers.isEmpty }

    public init(speakers: [SpeakerSpeechStats] = [], totalSeconds: TimeInterval = 0,
                speechSeconds: TimeInterval = 0, speechShare: Double = 0, pauseCount: Int = 0,
                longestPauseSeconds: TimeInterval = 0, pauseSeconds: TimeInterval = 0) {
        self.speakers = speakers
        self.totalSeconds = totalSeconds
        self.speechSeconds = speechSeconds
        self.speechShare = speechShare
        self.pauseCount = pauseCount
        self.longestPauseSeconds = longestPauseSeconds
        self.pauseSeconds = pauseSeconds
    }
}

/// Derives who spoke how much, at what pace, and where a meeting went quiet, from the
/// segments and speakers it already has. Pure.
public enum SpeechStatsCalculator {
    /// A silence has to beat this to be worth calling a pause, and it ends the turn of the
    /// speaker before it. Shorter gaps are the breathing inside one thought: the transcript
    /// itself keeps a speaker's line together across three seconds.
    public static let defaultPauseSeconds: TimeInterval = 5
    /// Under this much speech, words per minute says more about where the segments were cut
    /// than about the speaker, so no pace is reported. A note dictated into the phone in a
    /// few seconds is the case this exists for.
    public static let minimumPaceSeconds: TimeInterval = 10

    public static func compute(for meeting: MeetingRecord,
                               pauseSeconds: TimeInterval = defaultPauseSeconds) -> SpeechStats {
        let spoken = ordered(meeting)
        let totalSeconds = max(MeetingTime.clamped(meeting.duration), spoken.last?.end ?? 0)
        guard !spoken.isEmpty else {
            return SpeechStats(totalSeconds: totalSeconds, pauseSeconds: pauseSeconds)
        }

        let speech = merged(spoken.map { (start: $0.start, end: $0.end) })
        let speechSeconds = min(totalSeconds, speech.reduce(0) { $0 + ($1.end - $1.start) })
        var pauseCount = 0
        var longestPause: TimeInterval = 0
        for (previous, next) in zip(speech, speech.dropFirst()) {
            let gap = next.start - previous.end
            guard gap > pauseSeconds else { continue }
            pauseCount += 1
            longestPause = max(longestPause, gap)
        }

        return SpeechStats(speakers: speakers(spoken, in: meeting, pauseSeconds: pauseSeconds),
                           totalSeconds: totalSeconds,
                           speechSeconds: speechSeconds,
                           speechShare: totalSeconds > 0 ? speechSeconds / totalSeconds : 0,
                           pauseCount: pauseCount,
                           longestPauseSeconds: longestPause,
                           pauseSeconds: pauseSeconds)
    }

    // MARK: - Speakers

    private static func speakers(_ spoken: [Spoken], in meeting: MeetingRecord,
                                 pauseSeconds: TimeInterval) -> [SpeakerSpeechStats] {
        var intervals: [String: [(start: TimeInterval, end: TimeInterval)]] = [:]
        var words: [String: Int] = [:]
        var turns: [String: Int] = [:]
        // A turn runs until another voice speaks or its own speaker falls silent for
        // longer than the threshold; `runEnd` is how far the turn has reached so far.
        var currentSpeaker: String?
        var runEnd: TimeInterval = 0
        for item in spoken {
            if currentSpeaker != item.speakerID || item.start - runEnd > pauseSeconds {
                turns[item.speakerID, default: 0] += 1
                currentSpeaker = item.speakerID
                runEnd = item.end
            } else {
                runEnd = max(runEnd, item.end)
            }
            intervals[item.speakerID, default: []].append((start: item.start, end: item.end))
            words[item.speakerID, default: 0] += item.words
        }

        let speaking = intervals.mapValues { ranges in
            merged(ranges).reduce(0) { $0 + ($1.end - $1.start) }
        }
        let totalSpeaking = speaking.values.reduce(0, +)
        let order = meeting.effectiveSpeakers.map(\.id)
        return speaking.keys
            .map { id -> SpeakerSpeechStats in
                let seconds = speaking[id] ?? 0
                let count = turns[id] ?? 0
                let said = words[id] ?? 0
                return SpeakerSpeechStats(
                    speakerID: id,
                    speakingSeconds: seconds,
                    share: totalSpeaking > 0 ? seconds / totalSpeaking : 0,
                    turns: count,
                    averageTurnSeconds: count > 0 ? seconds / Double(count) : 0,
                    words: said,
                    wordsPerMinute: seconds >= minimumPaceSeconds ? Double(said) / (seconds / 60) : nil)
            }
            .sorted { left, right in
                guard left.speakingSeconds == right.speakingSeconds else {
                    return left.speakingSeconds > right.speakingSeconds
                }
                let leftOrder = order.firstIndex(of: left.speakerID) ?? order.count
                let rightOrder = order.firstIndex(of: right.speakerID) ?? order.count
                return leftOrder == rightOrder ? left.speakerID < right.speakerID : leftOrder < rightOrder
            }
    }

    // MARK: - Segments

    /// One transcribed segment reduced to what the numbers need.
    private struct Spoken {
        let offset: Int
        let speakerID: String
        let track: MeetingTrack
        let start: TimeInterval
        let end: TimeInterval
        let words: Int
    }

    /// The segments that carry speech, in the order they were said: by start, the
    /// microphone first on ties, as the transcript itself is ordered. Times are clamped,
    /// because `meeting.json` can have been edited by hand.
    private static func ordered(_ meeting: MeetingRecord) -> [Spoken] {
        meeting.segments.enumerated()
            .compactMap { offset, segment -> Spoken? in
                let text = TranscriptMerger.normalizedText(segment.text)
                guard !text.isEmpty else { return nil }
                let start = MeetingTime.clamped(segment.start)
                return Spoken(offset: offset,
                              speakerID: meeting.speakerID(for: segment),
                              track: MeetingTrack(segment.speaker),
                              start: start,
                              end: max(start, MeetingTime.clamped(segment.end)),
                              words: wordCount(text))
            }
            .sorted { left, right in
                guard left.start == right.start else { return left.start < right.start }
                let leftTrack = left.track == .microphone ? 0 : 1
                let rightTrack = right.track == .microphone ? 0 : 1
                return leftTrack == rightTrack ? left.offset < right.offset : leftTrack < rightTrack
            }
    }

    /// A word is a run of letters or digits; a hyphen or apostrophe between two of them
    /// stays inside it, so «кое-что» and "Let's" count once each and a dash standing
    /// between spaces counts for nothing. Counting whitespace-separated pieces instead
    /// would score Russian punctuation as words and make every pace too fast.
    static func wordCount(_ text: String) -> Int {
        TextScanning.words(in: Array(text), joiners: TextScanning.wordJoiners).count
    }

    /// The intervals sorted and merged, overlapping and touching ones becoming one.
    /// Segments of zero length carry words but no time, so they drop out here.
    static func merged(_ intervals: [(start: TimeInterval, end: TimeInterval)])
        -> [(start: TimeInterval, end: TimeInterval)] {
        var result: [(start: TimeInterval, end: TimeInterval)] = []
        for interval in intervals.sorted(by: { $0.start < $1.start }) where interval.end > interval.start {
            if let last = result.last, interval.start <= last.end {
                result[result.count - 1].end = max(last.end, interval.end)
            } else {
                result.append(interval)
            }
        }
        return result
    }
}
