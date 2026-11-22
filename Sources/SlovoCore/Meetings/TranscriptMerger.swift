import Foundation

/// Merges the "Me" and "Others" tracks into one transcript. Pure.
public enum TranscriptMerger {
    /// Orders segments of both tracks by start time and collapses consecutive
    /// segments of the same speaker. Microphone segments are always "Me" and system
    /// audio segments "Others"; segments without text are dropped. On equal start
    /// times the microphone comes first.
    public static func merge(microphone: [TranscriptSegment], system: [TranscriptSegment]) -> [TranscriptSegment] {
        let tagged = microphone.map { segment -> TranscriptSegment in
            var copy = segment
            copy.speaker = .me
            return copy
        } + system.map { segment -> TranscriptSegment in
            var copy = segment
            copy.speaker = .others
            return copy
        }

        let ordered = tagged.enumerated()
            .compactMap { offset, segment -> (offset: Int, segment: TranscriptSegment)? in
                let text = normalizedText(segment.text)
                guard !text.isEmpty else { return nil }
                var copy = segment
                copy.text = text
                return (offset, copy)
            }
            .sorted { left, right in
                left.segment.start == right.segment.start
                    ? left.offset < right.offset
                    : left.segment.start < right.segment.start
            }
            .map(\.segment)

        var merged: [TranscriptSegment] = []
        for segment in ordered {
            if var last = merged.last, last.speaker == segment.speaker {
                last.end = max(last.end, segment.end)
                last.text += " " + segment.text
                merged[merged.count - 1] = last
            } else {
                merged.append(segment)
            }
        }
        return merged
    }

    /// `mm:ss`, or `h:mm:ss` from one hour on. Negative, non-finite and absurdly large
    /// times, which only a hand-edited file holds, are clamped to `MeetingTime`.
    public static func timestamp(_ seconds: TimeInterval) -> String {
        let total = Int(MeetingTime.clamped(seconds).rounded(.down))
        let hours = total / 3600
        let minutes = (total % 3600) / 60
        let rest = total % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, rest)
            : String(format: "%02d:%02d", minutes, rest)
    }

    /// The localized name of a speaker.
    public static func speakerName(_ speaker: Speaker, language: AppLanguage? = nil) -> String {
        switch speaker {
        case .me: return CoreStrings.string("meetings.speaker.me", table: .meetings, language: language)
        case .others: return CoreStrings.string("meetings.speaker.others", table: .meetings, language: language)
        }
    }

    /// One transcript line: `[mm:ss] Me: text`.
    public static func line(for segment: TranscriptSegment, language: AppLanguage? = nil) -> String {
        "[\(timestamp(segment.start))] \(speakerName(segment.speaker, language: language)): \(normalizedText(segment.text))"
    }

    /// The transcript as plain text, one line per segment, for copying.
    public static func plainText(segments: [TranscriptSegment], language: AppLanguage? = nil) -> String {
        let resolved = language ?? AppLanguage.saved()
        return segments.map { line(for: $0, language: resolved) }.joined(separator: "\n")
    }

    /// The `transcript.md` document: a title line, then `[mm:ss] Me: …` lines with
    /// speaker names in the given language.
    public static func markdown(title: String, date: Date, segments: [TranscriptSegment],
                                language: AppLanguage? = nil) -> String {
        let resolved = language ?? AppLanguage.saved()
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: resolved.resolvedCode())
        formatter.dateStyle = .long
        formatter.timeStyle = .short

        var document = "# \(normalizedText(title))\n\n\(formatter.string(from: date))\n\n"
        if segments.isEmpty {
            document += CoreStrings.string("meetings.transcript.empty", table: .meetings, language: resolved) + "\n"
        } else {
            // Blank lines keep every speaker turn its own Markdown paragraph.
            document += segments.map { line(for: $0, language: resolved) }.joined(separator: "\n\n") + "\n"
        }
        return document
    }

    /// Collapses runs of whitespace and line breaks, so every segment stays on one line.
    static func normalizedText(_ text: String) -> String {
        text.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).joined(separator: " ")
    }
}

/// One display line of a transcript: consecutive segments of one speaker.
public struct TranscriptLine: Equatable, Sendable, Identifiable {
    /// The index in the lines array.
    public var id: Int
    public var start: TimeInterval
    public var end: TimeInterval
    public var speakerID: String
    public var track: MeetingTrack
    public var text: String
    /// Indexes into `MeetingRecord.segments`.
    public var segmentIndices: [Int]
    /// Highlights anchored to this line.
    public var highlightIDs: [UUID]

    public init(id: Int, start: TimeInterval, end: TimeInterval, speakerID: String, track: MeetingTrack,
                text: String, segmentIndices: [Int], highlightIDs: [UUID] = []) {
        self.id = id
        self.start = start
        self.end = end
        self.speakerID = speakerID
        self.track = track
        self.text = text
        self.segmentIndices = segmentIndices
        self.highlightIDs = highlightIDs
    }
}

// Speakers and important moments. New builds store one segment per transcribed clip;
// lines exist only for display and for `transcript.md`.
extension TranscriptMerger {
    /// Consecutive segments of one speaker stay on one line across pauses up to this long.
    static let lineJoinGapSeconds: TimeInterval = 3
    static let maximumLineSeconds: TimeInterval = 60
    /// A moment pressed this soon after a line ended belongs to that line.
    static let highlightLookBackSeconds: TimeInterval = 10
    /// A moment pressed this soon before a line started belongs to that line.
    static let highlightLookAheadSeconds: TimeInterval = 5
    static let highlightExcerptCharacters = 120
    static let highlightMark = "★"

    /// Display lines: segments sorted by start (microphone first on ties), consecutive
    /// segments of one speaker joined while the gap is at most 3 s and the line at most
    /// 60 s long. The speaker filter applies after merging, and highlights are anchored
    /// before filtering, so a moment never jumps to another speaker's line.
    public static func lines(for meeting: MeetingRecord, speakerFilter: Set<String>? = nil) -> [TranscriptLine] {
        filtered(anchoredLines(for: meeting), speakerFilter: speakerFilter)
    }

    /// Every line of the meeting with its highlights.
    static func anchoredLines(for meeting: MeetingRecord) -> [TranscriptLine] {
        var lines = mergedLines(for: meeting)
        for highlight in orderedHighlights(meeting.highlights) {
            if let index = highlightLineIndex(highlight, in: lines) {
                lines[index].highlightIDs.append(highlight.id)
            }
        }
        return lines
    }

    private static func mergedLines(for meeting: MeetingRecord) -> [TranscriptLine] {
        let ordered = meeting.segments.enumerated()
            .compactMap { offset, segment -> (offset: Int, segment: TranscriptSegment, text: String)? in
                let text = normalizedText(segment.text)
                return text.isEmpty ? nil : (offset, segment, text)
            }
            .sorted { left, right in
                guard left.segment.start == right.segment.start else { return left.segment.start < right.segment.start }
                let leftTrack = MeetingTrack(left.segment.speaker) == .microphone ? 0 : 1
                let rightTrack = MeetingTrack(right.segment.speaker) == .microphone ? 0 : 1
                return leftTrack == rightTrack ? left.offset < right.offset : leftTrack < rightTrack
            }

        var lines: [TranscriptLine] = []
        for (index, segment, text) in ordered {
            let speakerID = meeting.speakerID(for: segment)
            let track = MeetingTrack(segment.speaker)
            let end = max(segment.start, segment.end)
            if var last = lines.last, last.speakerID == speakerID, last.track == track,
               segment.start - last.end <= lineJoinGapSeconds,
               max(last.end, end) - last.start <= maximumLineSeconds {
                last.end = max(last.end, end)
                last.text += " " + text
                last.segmentIndices.append(index)
                lines[lines.count - 1] = last
            } else {
                lines.append(TranscriptLine(id: lines.count, start: segment.start, end: end, speakerID: speakerID,
                                            track: track, text: text, segmentIndices: [index]))
            }
        }
        return lines
    }

    static func filtered(_ lines: [TranscriptLine], speakerFilter: Set<String>?) -> [TranscriptLine] {
        guard let speakerFilter else { return lines }
        return lines.filter { speakerFilter.contains($0.speakerID) }.enumerated().map { index, line in
            var copy = line
            copy.id = index
            return copy
        }
    }

    private static func orderedHighlights(_ highlights: [MeetingHighlight]) -> [MeetingHighlight] {
        highlights.sorted { left, right in
            if left.time != right.time { return left.time < right.time }
            if left.createdAt != right.createdAt { return left.createdAt < right.createdAt }
            return left.id.uuidString < right.id.uuidString
        }
    }

    /// `displayName`; else «Я»/Me when `isMe`; else «Спикер N»/Speaker N when numbered;
    /// else the track's «Я»/Me or «Собеседники»/Others.
    public static func speakerLabel(_ speaker: MeetingSpeaker, language: AppLanguage? = nil) -> String {
        if let name = speaker.displayName.map(normalizedText), !name.isEmpty {
            return name
        }
        if speaker.isMe {
            return speakerName(.me, language: language)
        }
        if let number = speaker.number {
            return CoreStrings.format("meetings.speaker.numbered", table: .meetings, language: language, number)
        }
        return speakerName(speaker.track.speaker, language: language)
    }

    /// Whether a speaker is the person who recorded: marked «Это я», or the microphone's
    /// fallback while no speaker is marked. Microphone speech was «Я» before speakers
    /// were separated, and stays so until the user picks someone else.
    public static func isMe(_ speaker: MeetingSpeaker, in meeting: MeetingRecord) -> Bool {
        if speaker.isMe {
            return true
        }
        return speaker.id == MeetingSpeaker.fallback(for: .microphone).id && speaker.track == .microphone
            && !meeting.speakers.contains(where: \.isMe)
    }

    /// The label a speaker has in this meeting. Like `speakerLabel(_:language:)`, except
    /// that an unnamed, unnumbered microphone speaker who is not «Я» reads «Микрофон»:
    /// once another speaker is marked «Это я», the microphone's own name «Я» would
    /// otherwise appear twice.
    public static func speakerLabel(_ speaker: MeetingSpeaker, in meeting: MeetingRecord,
                                    language: AppLanguage? = nil) -> String {
        var shown = speaker
        shown.isMe = isMe(speaker, in: meeting)
        let hasName = !(shown.displayName.map(normalizedText) ?? "").isEmpty
        if !shown.isMe, !hasName, shown.number == nil, shown.track == .microphone {
            return CoreStrings.string("meetings.speaker.microphone", table: .meetings, language: language)
        }
        return speakerLabel(shown, language: language)
    }

    /// The `transcript.md` document, also used for Export and by the MCP server. A meeting
    /// with only unrenamed fallback speakers and no highlights gives the first format's
    /// document; otherwise the document adds the date with the duration, participants,
    /// important moments and headings.
    public static func markdown(meeting: MeetingRecord, language: AppLanguage? = nil,
                                speakerFilter: Set<String>? = nil) -> String {
        let resolved = language ?? AppLanguage.saved()
        let all = anchoredLines(for: meeting)
        let visible = filtered(all, speakerFilter: speakerFilter)

        if hasOnlyFirstFormatContent(meeting) {
            let segments = visible.map {
                TranscriptSegment(start: $0.start, end: $0.end, speaker: $0.track.speaker, text: $0.text)
            }
            return markdown(title: meeting.title, date: meeting.createdAt, segments: segments, language: resolved)
        }

        var document = "# \(normalizedText(meeting.title))\n\n"
        var dateLine = documentDateFormatter(resolved).string(from: meeting.createdAt)
        if meeting.duration.isFinite, meeting.duration > 0 {
            dateLine += " · " + timestamp(meeting.duration)
        }
        document += dateLine + "\n\n"

        let shownSpeakers = Set(visible.map(\.speakerID))
        let participants = meeting.effectiveSpeakers
            .filter { shownSpeakers.contains($0.id) }
            .map { speakerLabel($0, in: meeting, language: resolved) }
        if !participants.isEmpty {
            document += CoreStrings.format("meetings.transcript.participants", table: .meetings, language: resolved,
                                           participants.joined(separator: ", ")) + "\n\n"
        }

        if let notes = meeting.claudeNotes, !notes.isEmpty {
            document += claudeNotes(notes, language: resolved)
        }

        let moments = highlightItems(meeting: meeting, lines: all, speakerFilter: speakerFilter, language: resolved)
        if !moments.isEmpty {
            document += "## " + CoreStrings.string("meetings.transcript.highlightsHeading", table: .meetings,
                                                   language: resolved) + "\n\n"
            document += moments.joined(separator: "\n") + "\n\n"
        }

        document += "## " + CoreStrings.string("meetings.transcript.heading", table: .meetings, language: resolved) + "\n\n"
        if visible.isEmpty {
            document += CoreStrings.string("meetings.transcript.empty", table: .meetings, language: resolved) + "\n"
        } else {
            // Blank lines keep every line its own Markdown paragraph.
            document += visible.map { text(of: $0, in: meeting, language: resolved) }.joined(separator: "\n\n") + "\n"
        }
        return document
    }

    /// The transcript lines as plain text for copying, with ★ on lines that have highlights.
    public static func plainText(meeting: MeetingRecord, language: AppLanguage? = nil,
                                 speakerFilter: Set<String>? = nil) -> String {
        let resolved = language ?? AppLanguage.saved()
        return lines(for: meeting, speakerFilter: speakerFilter)
            .map { text(of: $0, in: meeting, language: resolved) }
            .joined(separator: "\n")
    }

    /// The line a highlight belongs to: the line containing its time, else the last line
    /// that ended within 10 s before it (people press after hearing the important part),
    /// else the next line starting within 5 s after it; nil when none is that close.
    ///
    /// When lines of both tracks contain the time, the one that started last wins: it is
    /// the most recent thing said, and a star put on a line lands at that line's start, so
    /// toggling it finds the same line again.
    public static func highlightLineIndex(_ highlight: MeetingHighlight, in lines: [TranscriptLine]) -> Int? {
        let time = highlight.time
        guard time.isFinite else { return nil }

        var containing: Int?
        for (index, line) in lines.enumerated() where line.start <= time && time <= line.end {
            if let current = containing, lines[current].start > line.start { continue }
            containing = index
        }
        if let containing { return containing }

        var before: Int?
        for (index, line) in lines.enumerated() where line.end < time && time - line.end <= highlightLookBackSeconds {
            if let current = before, lines[current].end > line.end { continue }
            before = index
        }
        if let before { return before }

        var after: Int?
        for (index, line) in lines.enumerated() where line.start > time && line.start - time <= highlightLookAheadSeconds {
            if let current = after, lines[current].start <= line.start { continue }
            after = index
        }
        return after
    }

    // MARK: - Document parts

    /// Claude's own account of the meeting, under its own heading and its author's name and
    /// date, before the moments and the transcript. A reader of the document must never
    /// take it for something that was said, and the recovery parser skips it: headings and
    /// list items are ignored, and the text of a note can never start like a `[mm:ss]` line.
    private static func claudeNotes(_ notes: MeetingNotes, language: AppLanguage) -> String {
        var section = "## " + CoreStrings.string("meetings.transcript.claudeHeading", table: .meetings,
                                                 language: language) + "\n\n"
        section += CoreStrings.format("meetings.transcript.claudeWritten", table: .meetings, language: language,
                                      notes.author, documentDateFormatter(language).string(from: notes.writtenAt))
            + "\n\n"
        if !notes.summary.isEmpty {
            section += notes.summary + "\n\n"
        }
        if !notes.decisions.isEmpty {
            section += CoreStrings.string("meetings.transcript.claudeDecisions", table: .meetings,
                                          language: language) + "\n\n"
            section += notes.decisions.map { "- " + $0 }.joined(separator: "\n") + "\n\n"
        }
        if !notes.tasks.isEmpty {
            section += CoreStrings.string("meetings.transcript.claudeTasks", table: .meetings,
                                          language: language) + "\n\n"
            section += notes.tasks.map { "- " + $0 }.joined(separator: "\n") + "\n\n"
        }
        return section
    }

    /// True when nothing in the meeting needs the second format: every speaker shown is an
    /// unrenamed fallback, no moment is marked and Claude wrote nothing. Such meetings keep
    /// today's document, which older builds and scripts already read.
    static func hasOnlyFirstFormatContent(_ meeting: MeetingRecord) -> Bool {
        guard meeting.highlights.isEmpty, meeting.claudeNotes == nil else { return false }
        return meeting.effectiveSpeakers.allSatisfy { speaker in
            speaker.id == MeetingSpeaker.fallback(for: speaker.track).id && speaker.number == nil && !speaker.isMe
                && (speaker.displayName.map(normalizedText) ?? "").isEmpty
        }
    }

    /// `[mm:ss] ★ Name: text`, the star only on lines with highlights.
    static func text(of line: TranscriptLine, in meeting: MeetingRecord, language: AppLanguage) -> String {
        let mark = line.highlightIDs.isEmpty ? "" : highlightMark + " "
        return "[\(timestamp(line.start))] \(mark)\(label(of: line, in: meeting, language: language)): \(line.text)"
    }

    static func label(of line: TranscriptLine, in meeting: MeetingRecord, language: AppLanguage) -> String {
        meeting.speaker(id: line.speakerID).map { speakerLabel($0, in: meeting, language: language) }
            ?? speakerName(line.track.speaker, language: language)
    }

    /// The start of a line for the list of moments, with an ellipsis when it was cut.
    static func excerpt(_ text: String) -> String {
        guard text.count > highlightExcerptCharacters else { return text }
        return String(text.prefix(highlightExcerptCharacters)).trimmingCharacters(in: .whitespaces) + "…"
    }

    /// `- [mm:ss] Name: «excerpt» — note` for each moment in time order. With a speaker
    /// filter only moments on the chosen speakers' lines are listed.
    private static func highlightItems(meeting: MeetingRecord, lines: [TranscriptLine], speakerFilter: Set<String>?,
                                       language: AppLanguage) -> [String] {
        var lineOfHighlight: [UUID: Int] = [:]
        for (index, line) in lines.enumerated() {
            for id in line.highlightIDs {
                lineOfHighlight[id] = index
            }
        }
        return orderedHighlights(meeting.highlights).compactMap { highlight -> String? in
            let index = lineOfHighlight[highlight.id]
            if let speakerFilter {
                guard let index, speakerFilter.contains(lines[index].speakerID) else { return nil }
            }
            var item = "- [\(timestamp(highlight.time))]"
            if let index {
                let line = lines[index]
                item += " \(label(of: line, in: meeting, language: language)): «\(excerpt(line.text))»"
            }
            if let note = highlight.note.map(normalizedText), !note.isEmpty {
                item += " — " + note
            }
            return item
        }
    }

    private static func documentDateFormatter(_ language: AppLanguage) -> DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: language.resolvedCode())
        formatter.dateStyle = .long
        formatter.timeStyle = .short
        return formatter
    }
}
