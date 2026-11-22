import Foundation

/// The user's corrections to a meeting: names, "This is me", merges, line reassignment and
/// important moments. Each function returns the edited record, which the app saves with
/// `MeetingStoring.update`. Pure.
///
/// Unknown ids leave the meeting unchanged: the screen can act on a record that changed
/// underneath it, and a no-op is safer than an alert about something the user cannot see.
public enum MeetingEdits {
    static let maximumNameCharacters = 40
    static let maximumNoteCharacters = 500

    /// An empty or nil name resets the default label.
    public static func renameSpeaker(_ id: String, to name: String?, in meeting: MeetingRecord) -> MeetingRecord {
        let newName = name.flatMap(sanitizedName)
        var edited = meeting
        guard let index = storedIndex(of: id, in: &edited, addingFallback: newName != nil) else { return meeting }
        edited.speakers[index].displayName = newName
        return edited
    }

    /// Clears `isMe` on every other speaker.
    public static func markAsMe(_ id: String, in meeting: MeetingRecord) -> MeetingRecord {
        var edited = meeting
        guard let index = storedIndex(of: id, in: &edited, addingFallback: true) else { return meeting }
        for other in edited.speakers.indices {
            edited.speakers[other].isMe = other == index
        }
        return edited
    }

    /// Moves the source's segments to the target and removes the source; only within one
    /// track, otherwise throws `meetings.error.mergeAcrossTracks`. The target keeps its
    /// name, color and number, and becomes «Я» when the source was.
    public static func mergeSpeaker(_ source: String, into target: String, in meeting: MeetingRecord) throws -> MeetingRecord {
        guard source != target,
              let sourceSpeaker = meeting.effectiveSpeakers.first(where: { $0.id == source }),
              let targetSpeaker = meeting.speaker(id: target) else {
            return meeting
        }
        guard sourceSpeaker.track == targetSpeaker.track else { throw mergeAcrossTracks() }

        var edited = meeting
        let storedTarget = segmentSpeakerID(for: targetSpeaker)
        for index in meeting.segments.indices where meeting.speakerID(for: meeting.segments[index]) == source {
            edited.segments[index].speakerID = storedTarget
        }
        edited.speakers.removeAll { $0.id == source }
        // The source's number stays used, so a later run never gives it to another voice
        // and references to it in an exported transcript keep meaning this person.
        if sourceSpeaker.number != nil {
            edited.highestSpeakerNumber = SpeakerRegistry.highestNumber(keeping: meeting, with: [])
        }
        if sourceSpeaker.isMe, let index = storedIndex(of: target, in: &edited, addingFallback: true) {
            edited.speakers[index].isMe = true
        }
        return edited
    }

    /// Only within the line's track, otherwise throws `meetings.error.mergeAcrossTracks`.
    public static func reassignLine(_ line: TranscriptLine, to speakerID: String, in meeting: MeetingRecord) throws -> MeetingRecord {
        guard let target = meeting.speaker(id: speakerID) else { return meeting }
        guard target.track == line.track else { throw mergeAcrossTracks() }
        var edited = meeting
        let stored = segmentSpeakerID(for: target)
        for index in line.segmentIndices where meeting.segments.indices.contains(index) {
            guard MeetingTrack(meeting.segments[index].speaker) == line.track else { continue }
            edited.segments[index].speakerID = stored
        }
        return edited
    }

    /// Removes every highlight anchored to the line, or adds a `.transcript` one at its start.
    public static func toggleHighlight(for line: TranscriptLine, in meeting: MeetingRecord, now: Date) -> MeetingRecord {
        // The line may come from a filtered list or an older render; anchors are looked up
        // again on the meeting as it is now.
        let current = TranscriptMerger.lines(for: meeting).first { $0.segmentIndices == line.segmentIndices }
        let existing = Set(meeting.highlights.map(\.id))
        let anchored = Set(current?.highlightIDs ?? line.highlightIDs).intersection(existing)

        var edited = meeting
        if anchored.isEmpty {
            edited.highlights.append(MeetingHighlight(time: line.start, source: .transcript, createdAt: now))
        } else {
            edited.highlights.removeAll { anchored.contains($0.id) }
        }
        return edited
    }

    public static func setHighlightNote(_ id: UUID, note: String?, in meeting: MeetingRecord) -> MeetingRecord {
        guard let index = meeting.highlights.firstIndex(where: { $0.id == id }) else { return meeting }
        var edited = meeting
        edited.highlights[index].note = sanitizedNote(note)
        return edited
    }

    public static func deleteHighlight(_ id: UUID, in meeting: MeetingRecord) -> MeetingRecord {
        var edited = meeting
        edited.highlights.removeAll { $0.id == id }
        return edited
    }

    /// Adds a highlight at `time`, marked as if a transcript line were starred rather than
    /// marked live while recording. A highlight already sitting at that exact time with the
    /// same note is reused instead of duplicated, so marking the same moment twice changes
    /// nothing.
    public static func markHighlight(at time: TimeInterval, note: String?, in meeting: MeetingRecord, now: Date)
        -> (record: MeetingRecord, highlight: MeetingHighlight, created: Bool) {
        let clampedTime = MeetingTime.clamped(time)
        let cleanNote = sanitizedNote(note)
        if let existing = meeting.highlights.first(where: { $0.time == clampedTime && $0.note == cleanNote }) {
            return (meeting, existing, false)
        }
        var edited = meeting
        let highlight = MeetingHighlight(time: clampedTime, note: cleanNote, source: .transcript, createdAt: now)
        edited.highlights.append(highlight)
        return (edited, highlight, true)
    }

    /// Removes the highlight with this id; `removed` is false, and the meeting unchanged,
    /// when none has it.
    public static func unmarkHighlight(_ id: UUID, in meeting: MeetingRecord) -> (record: MeetingRecord, removed: Bool) {
        guard meeting.highlights.contains(where: { $0.id == id }) else { return (meeting, false) }
        return (deleteHighlight(id, in: meeting), true)
    }

    /// Trimmed, at most 40 characters, without line breaks, ":", "[", "]" or "★"; nil when
    /// nothing is left. Those characters would break the `[mm:ss] ★ Name: text` lines of
    /// `transcript.md` that the recovery parser reads back.
    public static func sanitizedName(_ name: String) -> String? {
        let forbidden: Set<Character> = [":", "[", "]", "★"]
        let kept = String(name.compactMap { character -> Character? in
            if character.isNewline { return " " }
            return forbidden.contains(character) ? nil : character
        })
        return limited(TranscriptMerger.normalizedText(kept), to: maximumNameCharacters)
    }

    /// One line of at most 500 characters; nil when empty.
    static func sanitizedNote(_ note: String?) -> String? {
        guard let note else { return nil }
        return limited(TranscriptMerger.normalizedText(note), to: maximumNoteCharacters)
    }

    // MARK: - Private

    private static func limited(_ text: String, to characters: Int) -> String? {
        let result = String(text.prefix(characters)).trimmingCharacters(in: .whitespaces)
        return result.isEmpty ? nil : result
    }

    /// The index of a stored speaker. A fallback speaker the transcript shows is stored
    /// first when `addingFallback`, so its name or «Я» has somewhere to live.
    private static func storedIndex(of id: String, in meeting: inout MeetingRecord, addingFallback: Bool) -> Int? {
        if let index = meeting.speakers.firstIndex(where: { $0.id == id }) {
            return index
        }
        guard addingFallback,
              let fallback = MeetingTrack.allCases.map(MeetingSpeaker.fallback(for:)).first(where: { $0.id == id }),
              meeting.effectiveSpeakers.contains(where: { $0.id == id }) else {
            return nil
        }
        meeting.speakers.append(fallback)
        return meeting.speakers.count - 1
    }

    /// Segments name a fallback speaker by leaving `speakerID` empty, which keeps them
    /// readable by builds that know nothing about speakers.
    private static func segmentSpeakerID(for speaker: MeetingSpeaker) -> String? {
        speaker.id == MeetingSpeaker.fallback(for: speaker.track).id ? nil : speaker.id
    }

    private static func mergeAcrossTracks() -> SlovoError {
        SlovoError(CoreStrings.string("meetings.error.mergeAcrossTracks", table: .meetings))
    }
}
