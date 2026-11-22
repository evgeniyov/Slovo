import Foundation

/// Who spoke a transcript segment: the microphone track or system audio.
///
/// This stays the stored form of a segment's track, so builds that know nothing about
/// separated speakers keep reading `meeting.json`. `MeetingTrack` is the same value
/// under the name the speaker model uses.
public enum Speaker: String, Codable, Sendable, CaseIterable {
    case me
    case others
}

/// The audio source of a segment. `TranscriptSegment.speaker` stays the stored form
/// (.me = microphone, .others = system) so older builds keep reading meeting.json.
public enum MeetingTrack: String, Codable, Sendable, CaseIterable {
    case microphone
    case system

    public init(_ speaker: Speaker) {
        switch speaker {
        case .me: self = .microphone
        case .others: self = .system
        }
    }

    public var speaker: Speaker {
        switch self {
        case .microphone: return .me
        case .system: return .others
        }
    }
}

/// Bounds for times read from `meeting.json`, which can have been edited by hand.
enum MeetingTime {
    /// About eleven years, beyond any recording. Every time within it converts to whole
    /// seconds, so formatting a hand-edited value cannot trap.
    static let maximumSeconds: TimeInterval = 359_999_999

    /// The time within 0...maximumSeconds; 0 for NaN and infinities.
    static func clamped(_ seconds: TimeInterval) -> TimeInterval {
        guard seconds.isFinite else { return 0 }
        return min(max(0, seconds), maximumSeconds)
    }
}

/// One person, or one unseparated track, in a meeting transcript.
public struct MeetingSpeaker: Codable, Hashable, Sendable, Identifiable {
    /// Speaker colors are indexes into a palette of this many entries.
    public static let paletteSize = 8
    /// The largest speaker number a file may hold. Real meetings stay far below it; the
    /// bound keeps a hand-edited number from overflowing when the next one is added.
    static let maximumNumber = 999_999

    /// "s1", "s2", … for separated voices; "me" or "others" for a track that was not
    /// separated. Stable within a meeting and never reused after a merge.
    public var id: String
    public var track: MeetingTrack
    /// N in «Спикер N»; nil for "me"/"others". Numbers never change after creation.
    public var number: Int?
    /// The user's name for this speaker; nil shows the default label. Trimmed,
    /// at most 40 characters, without line breaks, ":", "[", "]" or "★".
    public var displayName: String?
    /// 0..<8, fixed when the speaker is created; survives renames and re-runs.
    public var colorIndex: Int
    /// At most one speaker per meeting.
    public var isMe: Bool

    public init(id: String, track: MeetingTrack, number: Int? = nil, displayName: String? = nil,
                colorIndex: Int, isMe: Bool = false) {
        self.id = id
        self.track = track
        self.number = number
        self.displayName = displayName
        self.colorIndex = Self.clampedColor(colorIndex)
        self.isMe = isMe
    }

    /// The speaker of a track that was not separated: "me" with color 0 for the
    /// microphone, "others" with color 1 for system audio, so they look as before
    /// speaker separation existed.
    public static func fallback(for track: MeetingTrack) -> MeetingSpeaker {
        switch track {
        case .microphone: return MeetingSpeaker(id: "me", track: .microphone, colorIndex: 0)
        case .system: return MeetingSpeaker(id: "others", track: .system, colorIndex: 1)
        }
    }

    private enum CodingKeys: String, CodingKey {
        case id, track, number, displayName, colorIndex, isMe
    }

    /// An unknown track or an id that is not "me", "others" or "s<number>" throws, so the
    /// record drops this speaker instead of the whole file; every other field falls back
    /// to a sensible value. Ids reach transcripts, exports and Claude as they are, so
    /// only the forms Slovo writes are accepted.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        guard Self.isValidID(id) else {
            throw DecodingError.dataCorruptedError(forKey: .id, in: container, debugDescription: "unexpected speaker id")
        }
        track = try container.decode(MeetingTrack.self, forKey: .track)
        number = (try? container.decodeIfPresent(Int.self, forKey: .number)).flatMap(Self.validNumber)
        displayName = try? container.decodeIfPresent(String.self, forKey: .displayName)
        let storedColor = try? container.decodeIfPresent(Int.self, forKey: .colorIndex)
        let defaultColor = number.map { $0 - 1 } ?? Self.fallback(for: track).colorIndex
        colorIndex = Self.clampedColor(storedColor ?? defaultColor)
        isMe = (try? container.decodeIfPresent(Bool.self, forKey: .isMe)) ?? false
    }

    /// Wraps any integer into the palette, negative values included.
    static func clampedColor(_ value: Int) -> Int {
        ((value % paletteSize) + paletteSize) % paletteSize
    }

    /// "me", "others", or "s" and a number from 1 to `maximumNumber` without leading zeros.
    static func isValidID(_ id: String) -> Bool {
        if id == "me" || id == "others" {
            return true
        }
        let digits = id.utf8.dropFirst()
        return id.utf8.first == UInt8(ascii: "s")
            && (1...String(maximumNumber).count).contains(digits.count)
            && digits.first != UInt8(ascii: "0")
            && digits.allSatisfy { $0 >= UInt8(ascii: "0") && $0 <= UInt8(ascii: "9") }
    }

    /// The number when it is one a speaker can have.
    static func validNumber(_ value: Int) -> Int? {
        (1...maximumNumber).contains(value) ? value : nil
    }
}

/// How an important moment was marked.
public enum HighlightSource: String, Codable, Sendable {
    /// The shortcut, button or menu item while recording.
    case recording
    /// A star on a transcript line afterwards.
    case transcript
}

/// A moment the user marked as important.
public struct MeetingHighlight: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    /// Seconds on the tracks' shared 16 kHz timeline.
    public var time: TimeInterval
    /// Optional, at most 500 characters, one line.
    public var note: String?
    public var source: HighlightSource
    public var createdAt: Date

    public init(id: UUID = UUID(), time: TimeInterval, note: String? = nil, source: HighlightSource,
                createdAt: Date = Date()) {
        self.id = id
        self.time = time
        self.note = note
        self.source = source
        self.createdAt = createdAt
    }

    private enum CodingKeys: String, CodingKey {
        case id, time, note, source, createdAt
    }

    /// A highlight without an id or a usable time throws and is dropped by the record;
    /// an unknown source reads as `.transcript`.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        let time = try container.decode(TimeInterval.self, forKey: .time)
        guard time.isFinite else {
            throw DecodingError.dataCorruptedError(forKey: .time, in: container, debugDescription: "time is not finite")
        }
        self.time = MeetingTime.clamped(time)
        note = try? container.decodeIfPresent(String.self, forKey: .note)
        let rawSource = try? container.decodeIfPresent(String.self, forKey: .source)
        source = rawSource.flatMap(HighlightSource.init(rawValue:)) ?? .transcript
        createdAt = (try? container.decodeIfPresent(Date.self, forKey: .createdAt)) ?? Date(timeIntervalSinceReferenceDate: 0)
    }
}

/// The outcome of speaker separation for one meeting.
public struct MeetingDiarization: Codable, Hashable, Sendable {
    public enum State: String, Codable, Sendable {
        case applied
        case off
        case modelMissing
        case unsupportedSystem
        case failed
    }

    public var state: State
    /// The number of people requested for this run; nil means automatic.
    public var requestedSpeakers: Int?
    /// Separated speakers found per track, keyed by MeetingTrack raw value.
    public var speakersPerTrack: [String: Int]
    /// "pyannote-community-1-coreml@1ed7a66"
    public var model: String?
    /// The tuning used, for example "step=0.1 minSeg=0 threshold=0.775 zeroVote=on".
    public var configuration: String?
    /// Localized; set for .failed.
    public var message: String?
    public var processedAt: Date?

    public init(state: State, requestedSpeakers: Int? = nil, speakersPerTrack: [String: Int] = [:],
                model: String? = nil, configuration: String? = nil, message: String? = nil, processedAt: Date? = nil) {
        self.state = state
        self.requestedSpeakers = requestedSpeakers
        self.speakersPerTrack = speakersPerTrack
        self.model = model
        self.configuration = configuration
        self.message = message
        self.processedAt = processedAt
    }

    private enum CodingKeys: String, CodingKey {
        case state, requestedSpeakers, speakersPerTrack, model, configuration, message, processedAt
    }

    /// Every field is optional on disk; an unknown state reads as `.off`.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let rawState = try? container.decodeIfPresent(String.self, forKey: .state)
        state = rawState.flatMap(State.init(rawValue:)) ?? .off
        requestedSpeakers = try? container.decodeIfPresent(Int.self, forKey: .requestedSpeakers)
        speakersPerTrack = (try? container.decodeIfPresent([String: Int].self, forKey: .speakersPerTrack)) ?? [:]
        model = try? container.decodeIfPresent(String.self, forKey: .model)
        configuration = try? container.decodeIfPresent(String.self, forKey: .configuration)
        message = try? container.decodeIfPresent(String.self, forKey: .message)
        processedAt = try? container.decodeIfPresent(Date.self, forKey: .processedAt)
    }
}

/// One piece of a meeting transcript with times relative to the recording start.
public struct TranscriptSegment: Codable, Hashable, Sendable {
    public var start: TimeInterval
    public var end: TimeInterval
    /// The track the segment came from.
    public var speaker: Speaker
    public var text: String
    /// The separated speaker; nil means the fallback speaker of the segment's track.
    public var speakerID: String?

    public init(start: TimeInterval, end: TimeInterval, speaker: Speaker, text: String, speakerID: String? = nil) {
        self.start = start
        self.end = end
        self.speaker = speaker
        self.text = text
        self.speakerID = speakerID
    }

    private enum CodingKeys: String, CodingKey {
        case start, end, speaker, text, speakerID
    }

    /// The keys every build writes stay required; `speakerID` is optional. Times are
    /// clamped to `MeetingTime`.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        start = MeetingTime.clamped(try container.decode(TimeInterval.self, forKey: .start))
        end = MeetingTime.clamped(try container.decode(TimeInterval.self, forKey: .end))
        speaker = try container.decode(Speaker.self, forKey: .speaker)
        text = try container.decode(String.self, forKey: .text)
        speakerID = try? container.decodeIfPresent(String.self, forKey: .speakerID)
    }
}

/// Where a meeting's audio was recorded.
public enum MeetingOrigin: String, Codable, Sendable {
    /// A clip whose own metadata says it was recorded on a Mac. Slovo's own recordings
    /// store no source at all, which says the same thing about every meeting made here.
    case thisMac
    case phone
}

/// Where a meeting came from, kept only when it is worth saying: a clip collected from a
/// phone names the phone, so a transcript made on the move is not mistaken for one
/// recorded at the desk.
public struct MeetingSource: Codable, Hashable, Sendable {
    /// Longer than any name a phone gives itself; the value reaches the interface as it is.
    static let maximumDeviceCharacters = 60

    public var origin: MeetingOrigin
    /// What the device calls itself, "Demo Phone"; nil when it did not say.
    public var device: String?

    public init(origin: MeetingOrigin, device: String? = nil) {
        self.origin = origin
        self.device = device.flatMap(Self.sanitizedDevice)
    }

    private enum CodingKeys: String, CodingKey {
        case origin, device
    }

    /// An origin a later build added throws, so the record simply has no source instead of
    /// claiming the wrong one.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let rawOrigin = try container.decode(String.self, forKey: .origin)
        guard let origin = MeetingOrigin(rawValue: rawOrigin) else {
            throw DecodingError.dataCorruptedError(forKey: .origin, in: container,
                                                   debugDescription: "unexpected meeting origin")
        }
        self.origin = origin
        device = (try? container.decodeIfPresent(String.self, forKey: .device)).flatMap(Self.sanitizedDevice)
    }

    /// Trimmed to one line of at most 60 characters; nil when nothing is left. The name is
    /// written by a device outside this Mac and is shown without further checking.
    static func sanitizedDevice(_ name: String) -> String? {
        let single = TranscriptMerger.normalizedText(name)
        let kept = String(single.prefix(maximumDeviceCharacters)).trimmingCharacters(in: .whitespaces)
        return kept.isEmpty ? nil : kept
    }
}

/// What Claude wrote about a meeting after reading it: a short summary, the decisions it
/// found and the tasks it found, with who wrote it and when.
///
/// This is Claude's account of the meeting, never a record of what was said. It is kept
/// apart from the segments and shown under its author's name, because a summary can be
/// wrong in ways a transcript cannot: it can leave things out, merge two people into one
/// or state something nobody said.
///
/// The text comes from outside Slovo, so it is held to the same rules as the text the
/// server hands out: one line per value, no control characters, and nothing that could
/// close the untrusted wrapper or come back as a transcript line when `transcript.md` is
/// parsed to recover a meeting. `MCPSanitizer` is the same cleaner used on the way out;
/// here it also decides what a hand-edited file is allowed to hold.
public struct MeetingNotes: Codable, Hashable, Sendable {
    public static let maximumSummaryCharacters = 2_000
    public static let maximumItemCharacters = 500
    public static let maximumItems = 50
    public static let maximumAuthorCharacters = 60
    /// The author of notes whose own name was lost or empty.
    public static let defaultAuthor = "Claude"

    /// A few sentences about the meeting; empty when only lists were written.
    public var summary: String
    /// What was decided, one decision per item.
    public var decisions: [String]
    /// What someone is to do, one task per item.
    public var tasks: [String]
    /// Who wrote this, as the writer named itself.
    public var author: String
    public var writtenAt: Date

    public init(summary: String = "", decisions: [String] = [], tasks: [String] = [],
                author: String = MeetingNotes.defaultAuthor, writtenAt: Date = Date()) {
        self.summary = Self.sanitized(summary, limit: Self.maximumSummaryCharacters) ?? ""
        self.decisions = Self.sanitizedItems(decisions)
        self.tasks = Self.sanitizedItems(tasks)
        self.author = Self.sanitized(author, limit: Self.maximumAuthorCharacters) ?? Self.defaultAuthor
        self.writtenAt = writtenAt
    }

    /// True when nothing was written; such notes are not worth keeping or showing.
    public var isEmpty: Bool {
        summary.isEmpty && decisions.isEmpty && tasks.isEmpty
    }

    private enum CodingKeys: String, CodingKey {
        case summary, decisions, tasks, author, writtenAt
    }

    /// Every field is optional on disk: notes written by a later build, or edited by hand,
    /// lose what cannot be read instead of damaging the meeting around them.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let summary = (try? container.decodeIfPresent(String.self, forKey: .summary)) ?? nil
        let decisions = (try? container.decodeIfPresent([String].self, forKey: .decisions)) ?? nil
        let tasks = (try? container.decodeIfPresent([String].self, forKey: .tasks)) ?? nil
        let author = (try? container.decodeIfPresent(String.self, forKey: .author)) ?? nil
        self.summary = summary.flatMap { Self.sanitized($0, limit: Self.maximumSummaryCharacters) } ?? ""
        self.decisions = Self.sanitizedItems(decisions ?? [])
        self.tasks = Self.sanitizedItems(tasks ?? [])
        self.author = author.flatMap { Self.sanitized($0, limit: Self.maximumAuthorCharacters) } ?? Self.defaultAuthor
        writtenAt = (try? container.decodeIfPresent(Date.self, forKey: .writtenAt))
            ?? Date(timeIntervalSinceReferenceDate: 0)
    }

    /// Cleaned, on one line, and at most `limit` characters; nil when nothing is left.
    static func sanitized(_ text: String, limit: Int) -> String? {
        let single = TranscriptMerger.normalizedText(MCPSanitizer.sanitize(text))
        let kept = String(single.prefix(limit)).trimmingCharacters(in: .whitespaces)
        return kept.isEmpty ? nil : kept
    }

    /// Empty items drop out and the rest are capped, so a list can never grow without bound.
    static func sanitizedItems(_ items: [String]) -> [String] {
        items.prefix(maximumItems).compactMap { sanitized($0, limit: maximumItemCharacters) }
    }
}

public enum MeetingStatus: String, Codable, Sendable {
    case recording
    /// Audio is on disk, no transcript yet.
    case recorded
    case transcribing
    case transcribed
    /// Transcription failed or was cancelled; see `MeetingRecord.errorMessage`.
    case failed
}

/// A meeting as stored in `Meetings/<id>/meeting.json`.
///
/// Decoding is tolerant, because a file that fails to decode is moved aside as
/// damaged: keys added after the first format are optional, unknown enum values fall
/// back instead of throwing, and a malformed segment, speaker or highlight is dropped on
/// its own. Encoding keeps the synthesized key names.
public struct MeetingRecord: Codable, Hashable, Sendable, Identifiable {
    /// The format new builds write. Files without `formatVersion` are format 1: one
    /// merged segment per speaker turn and no speakers, highlights or diarization.
    public static let currentFormatVersion = 2

    public var id: UUID
    public var title: String
    public var createdAt: Date
    public var duration: TimeInterval
    public var status: MeetingStatus
    /// Whether `system.caf` was recorded.
    public var hasSystemAudio: Bool
    /// False once retention or the user removed `mic.caf` and `system.caf`.
    public var hasAudio: Bool
    /// Whether `screen.mov` was recorded. False for every meeting stored before screen
    /// recording existed, and once retention or the user removed the file.
    public var hasScreenRecording: Bool
    public var engineID: EngineID?
    public var segments: [TranscriptSegment]
    /// Localized message for `.failed`.
    public var errorMessage: String?
    /// Speakers the transcript refers to; fallbacks of unseparated tracks may be absent
    /// (see `effectiveSpeakers`).
    public var speakers: [MeetingSpeaker]
    public var highlights: [MeetingHighlight]
    /// Nil for meetings processed before speaker separation existed.
    public var diarization: MeetingDiarization?
    /// Where the audio came from; nil for a meeting recorded on this Mac and for every
    /// meeting stored before this was written.
    public var source: MeetingSource?
    /// When the audio became this Mac's to keep, which is the age retention counts; nil for
    /// a meeting recorded here and for every meeting stored before this was written, whose
    /// `createdAt` is that same instant. A clip collected from a phone keeps the time it was
    /// recorded in `createdAt`, and that can be days before the audio ever arrived here.
    public var audioStoredAt: Date?
    public var formatVersion: Int
    /// The highest «Спикер N» number the meeting has used, kept so a number removed by a
    /// merge is never given to another voice by a later run; nil until one is recorded.
    public var highestSpeakerNumber: Int?
    /// What Claude wrote about this meeting; nil until it writes something, and for every
    /// meeting stored before this existed. Never part of the transcript.
    public var claudeNotes: MeetingNotes?

    public init(id: UUID = UUID(), title: String, createdAt: Date = Date(), duration: TimeInterval = 0,
                status: MeetingStatus = .recording, hasSystemAudio: Bool = false, hasAudio: Bool = true,
                hasScreenRecording: Bool = false,
                engineID: EngineID? = nil, segments: [TranscriptSegment] = [], errorMessage: String? = nil,
                speakers: [MeetingSpeaker] = [], highlights: [MeetingHighlight] = [],
                diarization: MeetingDiarization? = nil, source: MeetingSource? = nil,
                audioStoredAt: Date? = nil, formatVersion: Int = MeetingRecord.currentFormatVersion,
                highestSpeakerNumber: Int? = nil, claudeNotes: MeetingNotes? = nil) {
        self.id = id
        self.title = title
        self.createdAt = createdAt
        self.duration = duration
        self.status = status
        self.hasSystemAudio = hasSystemAudio
        self.hasAudio = hasAudio
        self.hasScreenRecording = hasScreenRecording
        self.engineID = engineID
        self.segments = segments
        self.errorMessage = errorMessage
        self.speakers = speakers
        self.highlights = highlights
        self.diarization = diarization
        self.source = source
        self.audioStoredAt = audioStoredAt
        self.formatVersion = formatVersion
        self.highestSpeakerNumber = highestSpeakerNumber
        self.claudeNotes = claudeNotes
    }

    private enum CodingKeys: String, CodingKey {
        case id, title, createdAt, duration, status, hasSystemAudio, hasAudio, engineID, segments, errorMessage
        case speakers, highlights, diarization, source, audioStoredAt, formatVersion, highestSpeakerNumber
        case claudeNotes, hasScreenRecording
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        title = try container.decode(String.self, forKey: .title)
        createdAt = try container.decode(Date.self, forKey: .createdAt)
        duration = MeetingTime.clamped(try container.decode(TimeInterval.self, forKey: .duration))
        hasSystemAudio = try container.decode(Bool.self, forKey: .hasSystemAudio)
        hasAudio = try container.decode(Bool.self, forKey: .hasAudio)
        hasScreenRecording = (try? container.decodeIfPresent(Bool.self, forKey: .hasScreenRecording)) ?? false
        // One segment that cannot be read (a track a later build added, a hand edit) costs
        // that segment, not the meeting: a file that fails to decode is moved aside, and
        // every other line, name and moment would go with it. The key itself stays required.
        segments = try container.decode([Lossy<TranscriptSegment>].self, forKey: .segments).compactMap(\.value)
        errorMessage = try? container.decodeIfPresent(String.self, forKey: .errorMessage)

        // An engine a later build added reads as unknown rather than damaging the file.
        let rawEngine = try? container.decodeIfPresent(String.self, forKey: .engineID)
        engineID = rawEngine.flatMap(EngineID.init(rawValue:))

        let rawStatus = try container.decode(String.self, forKey: .status)
        if let known = MeetingStatus(rawValue: rawStatus) {
            status = known
        } else if hasAudio {
            status = .recorded
        } else {
            status = segments.isEmpty ? .failed : .transcribed
        }

        let decodedSpeakers = (try? container.decodeIfPresent([Lossy<MeetingSpeaker>].self, forKey: .speakers))?
            .compactMap(\.value) ?? []
        speakers = Self.consistentSpeakers(decodedSpeakers)
        let decodedHighlights = (try? container.decodeIfPresent([Lossy<MeetingHighlight>].self, forKey: .highlights))?
            .compactMap(\.value) ?? []
        highlights = Self.uniqueByID(decodedHighlights)
        diarization = try? container.decodeIfPresent(MeetingDiarization.self, forKey: .diarization)
        source = try? container.decodeIfPresent(MeetingSource.self, forKey: .source)
        audioStoredAt = try? container.decodeIfPresent(Date.self, forKey: .audioStoredAt)
        formatVersion = (try? container.decodeIfPresent(Int.self, forKey: .formatVersion)) ?? 1
        highestSpeakerNumber = (try? container.decodeIfPresent(Int.self, forKey: .highestSpeakerNumber))
            .flatMap(MeetingSpeaker.validNumber)
        // Notes that say nothing are the same as no notes, so nothing empty reaches the card.
        claudeNotes = (try? container.decodeIfPresent(MeetingNotes.self, forKey: .claudeNotes))
            .flatMap { $0.isEmpty ? nil : $0 }
    }

    // MARK: - Speakers

    /// The stored speakers plus the fallback speaker of each track whose segments do
    /// not name a stored speaker of that track, microphone speakers first. Within a
    /// track the stored order is kept.
    public var effectiveSpeakers: [MeetingSpeaker] {
        var result = speakers
        for track in MeetingTrack.allCases {
            let fallback = MeetingSpeaker.fallback(for: track)
            guard !result.contains(where: { $0.id == fallback.id }) else { continue }
            let usesFallback = segments.contains { segment in
                MeetingTrack(segment.speaker) == track && speakerID(for: segment) == fallback.id
            }
            if usesFallback {
                result.append(fallback)
            }
        }
        return result.enumerated()
            .sorted { left, right in
                let leftTrack = Self.trackOrder(left.element.track)
                let rightTrack = Self.trackOrder(right.element.track)
                return leftTrack == rightTrack ? left.offset < right.offset : leftTrack < rightTrack
            }
            .map(\.element)
    }

    /// A stored speaker, or the fallback speaker when the id is "me" or "others".
    public func speaker(id: String) -> MeetingSpeaker? {
        if let stored = speakers.first(where: { $0.id == id }) {
            return stored
        }
        return MeetingTrack.allCases.map(MeetingSpeaker.fallback(for:)).first { $0.id == id }
    }

    /// The id of the speaker a segment is shown under: its `speakerID` when that names a
    /// stored speaker of the same track, otherwise the fallback of its track.
    public func speakerID(for segment: TranscriptSegment) -> String {
        let track = MeetingTrack(segment.speaker)
        if let id = segment.speakerID, speakers.contains(where: { $0.id == id && $0.track == track }) {
            return id
        }
        return MeetingSpeaker.fallback(for: track).id
    }

    /// Seconds of transcribed speech shown under a speaker.
    public func talkTime(of speakerID: String) -> TimeInterval {
        segments.reduce(0) { total, segment in
            self.speakerID(for: segment) == speakerID ? total + max(0, segment.end - segment.start) : total
        }
    }

    private static func trackOrder(_ track: MeetingTrack) -> Int {
        MeetingTrack.allCases.firstIndex(of: track) ?? 0
    }

    /// Keeps the first speaker of each id and at most one `isMe`, so views that key rows
    /// by id and "This is me" stay consistent even after a hand-edited file.
    private static func consistentSpeakers(_ speakers: [MeetingSpeaker]) -> [MeetingSpeaker] {
        var seenMe = false
        return uniqueByID(speakers).map { speaker in
            var copy = speaker
            if copy.isMe {
                copy.isMe = !seenMe
                seenMe = true
            }
            return copy
        }
    }

    private static func uniqueByID<Element: Identifiable>(_ elements: [Element]) -> [Element] {
        var seen: Set<Element.ID> = []
        return elements.filter { seen.insert($0.id).inserted }
    }
}

/// Decodes an array element without failing the array.
private struct Lossy<Value: Decodable>: Decodable {
    let value: Value?

    init(from decoder: Decoder) throws {
        value = try? Value(from: decoder)
    }
}

/// What the meeting recorder is doing.
public enum MeetingRecorderState: Equatable, Sendable {
    case idle
    /// `startedAt` is `ProcessInfo.systemUptime` at the start.
    case recording(meetingID: UUID, startedAt: TimeInterval)
    case stopping
    /// The message is already localized.
    case failed(String)
}

/// Whether system audio is actually arriving.
public enum SystemAudioStatus: Equatable, Sendable {
    /// The setting is off or no meeting is recording.
    case off
    case capturing
    /// The tap delivered only zeros for `SystemAudioTap.silenceTimeout` (8 s): System Audio
    /// Recording is most likely denied.
    case silentPermissionLikelyDenied
    /// The tap could not be created; the message is already localized.
    case unavailable(String)
}

/// Whether the microphone is still being recorded.
public enum MicrophoneStatus: Equatable, Sendable {
    case capturing
    /// No microphone could be opened for longer than the restart budget. The meeting
    /// keeps recording and the capture keeps trying, so this clears by itself when a
    /// device comes back.
    case lost
}

/// Whether a meeting recording is also capturing the screen right now.
public enum ScreenRecordingStatus: Equatable, Sendable {
    /// The setting is off, no meeting is recording, or the meeting itself never asked
    /// for the screen.
    case off
    case capturing
    /// The screen is off-limits for the moment: the screen is locked. Capture resumes
    /// on unlock without ending the meeting.
    case pausedScreenLocked
    /// The stream stopped (a display disconnected, the permission was revoked) and one
    /// restart already failed; the track is done for the rest of this meeting.
    case lost
    /// Never started: no permission, no display, or the writer could not be created.
    /// The message is already localized.
    case unavailable(String)
}

/// What a screen recording captures: the whole main display, whose audio is whatever
/// `system.caf` already records; or one app's windows, whose audio replaces `system.caf`
/// with just that app's sound instead of everyone else's.
public enum ScreenCaptureTarget: Equatable, Sendable {
    case display
    case application(bundleID: String)

    /// Nil for the whole display; the bundle identifier, empty string included, for an
    /// app — the shape `AppSettings.screenCaptureApp` stores in `UserDefaults`.
    public var storedBundleID: String? {
        if case let .application(bundleID) = self { return bundleID }
        return nil
    }

    /// `.display` for an empty or missing stored value, `.application` otherwise.
    public static func from(storedBundleID: String?) -> ScreenCaptureTarget {
        guard let storedBundleID, !storedBundleID.isEmpty else { return .display }
        return .application(bundleID: storedBundleID)
    }
}

/// The tracks a meeting recording is putting sound on right now, as the recording panel
/// and the meeting card say it.
///
/// This is the live state, not `MeetingRecord.hasSystemAudio`: that flag describes the
/// files a finished meeting kept, and a record is created without it before any capture
/// has started.
public enum MeetingRecordingTracks: Equatable, Sendable {
    case microphoneAndSystem
    case microphoneOnly
    case systemOnly
    /// Neither track has sound going to it: the microphone is switched off or lost, and
    /// system audio is off or failed. The timer still runs and both files keep the timeline.
    /// Not `none`, which an optional of this type would confuse with `Optional.none`.
    case neither

    /// A system track counts while its tap is open, including the silent stretch that may
    /// mean a denied permission: the panel warns about that separately, and a call where
    /// nobody has spoken yet looks exactly the same.
    public init(microphoneOn: Bool, microphoneStatus: MicrophoneStatus, systemAudioStatus: SystemAudioStatus) {
        let microphone = microphoneOn && microphoneStatus == .capturing
        let system: Bool
        switch systemAudioStatus {
        case .capturing, .silentPermissionLikelyDenied: system = true
        case .off, .unavailable: system = false
        }
        switch (microphone, system) {
        case (true, true): self = .microphoneAndSystem
        case (true, false): self = .microphoneOnly
        case (false, true): self = .systemOnly
        case (false, false): self = .neither
        }
    }
}

/// What a meeting run is doing now; shown with TranscriptionProgress.
public enum MeetingProcessingPhase: Equatable, Sendable {
    case preparing
    case loadingSpeakerModel
    case separatingSpeakers(MeetingTrack, fraction: Double)
    case transcribing
}

/// Progress of one meeting's processing run.
public struct TranscriptionProgress: Equatable, Sendable {
    public var completedSegments: Int
    public var totalSegments: Int
    public var phase: MeetingProcessingPhase
    /// From 0 to 1 over the whole run, speaker separation included; drives the
    /// progress bar.
    public var overallFraction: Double

    /// Without `overallFraction` the run is only transcribing, and the overall
    /// fraction is the share of segments done.
    public init(completedSegments: Int, totalSegments: Int, phase: MeetingProcessingPhase = .transcribing,
                overallFraction: Double? = nil) {
        self.completedSegments = completedSegments
        self.totalSegments = totalSegments
        self.phase = phase
        let segmentFraction = Self.fraction(completed: completedSegments, total: totalSegments)
        let overall = overallFraction ?? segmentFraction
        self.overallFraction = overall.isFinite ? min(1, max(0, overall)) : 0
    }

    /// From 0 to 1; zero while the segment count is unknown.
    public var fraction: Double {
        Self.fraction(completed: completedSegments, total: totalSegments)
    }

    private static func fraction(completed: Int, total: Int) -> Double {
        total > 0 ? Double(completed) / Double(total) : 0
    }
}
