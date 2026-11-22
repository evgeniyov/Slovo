import Foundation

/// Which lines of a transcript `get_transcript` shows, and the page size.
struct MCPTranscriptSelection: Equatable {
    static let defaultMaxChars = 16_000

    /// Sorted speaker ids; nil shows every speaker.
    var speakerIDs: [String]?
    /// A line is shown when it starts at or after `from` and before `to`.
    var from: TimeInterval?
    var to: TimeInterval?
    var maxChars: Int

    init(speakerIDs: [String]? = nil, from: TimeInterval? = nil, to: TimeInterval? = nil, maxChars: Int) {
        self.speakerIDs = speakerIDs
        self.from = from
        self.to = to
        self.maxChars = maxChars
    }

    /// Ties a cursor to the arguments it was made for.
    var filterHash: String {
        let speakers = speakerIDs.map { $0.sorted().joined(separator: ",") } ?? "*"
        let start = from.map { "\($0)" } ?? "-"
        let end = to.map { "\($0)" } ?? "-"
        return MCPHash.hex("get_transcript|\(speakers)|\(start)|\(end)|\(maxChars)")
    }
}

/// Where the next transcript page starts. Not signed: a changed cursor can only move
/// within text the same arguments already allow reading.
struct MCPTranscriptCursor: Equatable {
    /// The lowercase meeting id.
    var meeting: String
    /// The next line, 0-based within the selection.
    var index: Int
    /// UTF-16 offset inside that line's text when a long line continues.
    var offset: Int
    var fingerprint: String
    var filter: String

    init(meeting: String, index: Int, offset: Int, fingerprint: String, filter: String) {
        self.meeting = meeting
        self.index = index
        self.offset = offset
        self.fingerprint = fingerprint
        self.filter = filter
    }

    init?(encoded text: String) {
        guard let object = MCPBase64URL.object(text, keys: ["m", "i", "c", "f", "q"]),
              let rawMeeting = object["m"]?.stringValue, let meeting = UUID(uuidString: rawMeeting),
              case let .integer(index)? = object["i"], index >= 0,
              case let .integer(offset)? = object["c"], offset >= 0,
              let fingerprint = object["f"]?.stringValue, let filter = object["q"]?.stringValue else { return nil }
        self.meeting = meeting.uuidString.lowercased()
        self.index = index
        self.offset = offset
        self.fingerprint = fingerprint
        self.filter = filter
    }

    var encoded: String {
        let object: JSONValue = [
            "m": .string(meeting), "i": .integer(index), "c": .integer(offset),
            "f": .string(fingerprint), "q": .string(filter)
        ]
        return MCPBase64URL.encode(object.serialized())
    }
}

/// Renders transcript lines and cuts them into pages that stay within `max_chars`.
enum MCPTranscriptPager {
    struct RenderedLine {
        let line: TranscriptLine
        /// `[mm:ss] ★ Label: text`, followed by `  ↳ note: …` lines for anchored notes.
        let text: String
    }

    static let speakersLineCharacters = 1_000
    /// The speakers line gives way down to this much when a small `max_chars` needs the room.
    static let minimumSpeakersLineCharacters = 40
    /// Room kept for the "Lines a–b of n" line, which is written after the body is chosen.
    static let linesLineReserve = 120
    /// Room kept for the cursor and the sentence after it.
    static let footerReserve = 360
    /// Every page shows at least this much text, so a huge header cannot stall paging.
    static let minimumBodyCharacters = 400

    static func render(_ view: MCPMeetingView, selection: MCPTranscriptSelection) -> [RenderedLine] {
        let speakers = selection.speakerIDs.map(Set.init)
        var rendered: [RenderedLine] = []
        for (index, line) in view.lines.enumerated() {
            if let speakers, !speakers.contains(line.speakerID) { continue }
            if let from = selection.from, line.start < from { continue }
            if let to = selection.to, line.start >= to { continue }
            let anchored = view.highlightsByLine[index] ?? []
            var text = "[\(TranscriptMerger.timestamp(line.start))] " + (anchored.isEmpty ? "" : "★ ")
                + view.label(forSpeaker: line.speakerID) + ": " + line.text
            for highlight in anchored {
                if let note = highlight.note, !note.isEmpty {
                    text += "\n" + MCPText.noteMarker + note
                }
            }
            rendered.append(RenderedLine(line: line, text: text))
        }
        return rendered
    }

    /// The meeting line and the speakers line that start every transcript Claude reads.
    static func headerLines(_ view: MCPMeetingView, dates: MCPDates,
                            speakersLimit: Int = speakersLineCharacters) -> [String] {
        let source = view.archived.record
        let title = MCPText.titleLine(title: view.title, created: dates.iso(source.createdAt),
                                      duration: TranscriptMerger.timestamp(source.duration), id: view.id)
        let speakers = view.speakers
            .map { speaker in "\(speaker.id) = \(view.label(for: speaker))" + (speaker.isMe ? MCPText.slovoUserMark : "") }
            .joined(separator: " · ")
        let speakersText = speakers.isEmpty ? MCPText.noSpeakers : MCPMeetingView.capped(speakers, speakersLimit)
        return [title, MCPText.speakersPrefix + speakersText]
    }

    static func page(view: MCPMeetingView, selection: MCPTranscriptSelection, rendered: [RenderedLine], start: Int,
                     offset: Int, changed: Bool, dates: MCPDates) -> (text: String, next: MCPTranscriptCursor?) {
        var notices: [String] = []
        if changed {
            notices.append(MCPText.meetingChanged)
        }
        if offset > 0, start < rendered.count {
            notices.append(MCPText.lineContinues(start + 1))
        }
        func fixedCharacters(_ header: [String]) -> Int {
            (header + notices).reduce(0) { $0 + $1.utf16.count + 1 }
                + MCPText.transcriptNotice.utf16.count + 2
                + MCPText.transcriptOpenTag.utf16.count + 1 + 1
                + MCPText.transcriptCloseTag.utf16.count + 2
        }
        // The whole page stays within `max_chars`: when a long speakers line would leave the
        // body less than its minimum, the speakers line is cut instead of the page overflowing.
        let room = selection.maxChars - linesLineReserve - footerReserve - minimumBodyCharacters
        var speakersLimit = speakersLineCharacters
        var header = headerLines(view, dates: dates, speakersLimit: speakersLimit)
        var fixed = fixedCharacters(header)
        while fixed > room, speakersLimit > minimumSpeakersLineCharacters {
            speakersLimit = max(minimumSpeakersLineCharacters, speakersLimit - (fixed - room))
            header = headerLines(view, dates: dates, speakersLimit: speakersLimit)
            fixed = fixedCharacters(header)
        }
        let budget = max(minimumBodyCharacters, selection.maxChars - fixed - linesLineReserve - footerReserve)

        var pieces: [String] = []
        var used = 0
        var index = start
        var pieceOffset = offset
        var cutOffset: Int?
        while index < rendered.count {
            let full = rendered[index].text
            let remainder = full[characterIndex(in: full, utf16Offset: pieceOffset)...]
            let separator = pieces.isEmpty ? 0 : 1
            let length = remainder.utf16.count
            if used + separator + length <= budget {
                pieces.append(String(remainder))
                used += separator + length
                index += 1
                pieceOffset = 0
                continue
            }
            // Whole lines only, unless one line alone is longer than a page.
            if pieces.isEmpty {
                let cut = cutIndex(in: remainder, limit: budget)
                pieces.append(String(remainder[..<cut]))
                cutOffset = full.utf16.distance(from: full.startIndex, to: cut)
            }
            break
        }

        let linesLine: String
        if rendered.isEmpty {
            linesLine = MCPText.noLinesMatch
        } else if start >= rendered.count {
            linesLine = MCPText.noLinesAfter(total: rendered.count)
        } else {
            let last = cutOffset == nil ? index - 1 : index
            linesLine = MCPText.linesRange(first: start + 1, last: last + 1, total: rendered.count,
                                           from: TranscriptMerger.timestamp(rendered[start].line.start),
                                           to: TranscriptMerger.timestamp(rendered[last].line.end))
        }

        var next: MCPTranscriptCursor?
        if cutOffset != nil || index < rendered.count {
            next = MCPTranscriptCursor(meeting: view.id, index: index, offset: cutOffset ?? 0,
                                       fingerprint: view.fingerprint, filter: selection.filterHash)
        }

        var text = header.joined(separator: "\n") + "\n" + linesLine + "\n"
        for notice in notices {
            text += notice + "\n"
        }
        text += MCPText.transcriptNotice + "\n\n" + MCPText.transcriptOpenTag + "\n"
        if !pieces.isEmpty {
            text += pieces.joined(separator: "\n") + "\n"
        }
        text += MCPText.transcriptCloseTag + "\n\n"
        if let next {
            text += MCPText.nextCursorLine(next.encoded) + "\n"
            text += cutOffset == nil ? MCPText.continueWithLines(from: index + 1) : MCPText.continueWithRestOfLine(index + 1)
        } else {
            text += MCPText.endOfTranscript
        }
        return (text, next)
    }

    static func pageCount(view: MCPMeetingView, selection: MCPTranscriptSelection, rendered: [RenderedLine],
                          dates: MCPDates) -> Int {
        var pages = 0
        var start = 0
        var offset = 0
        while true {
            pages += 1
            let next = page(view: view, selection: selection, rendered: rendered, start: start, offset: offset,
                            changed: false, dates: dates).next
            guard let next, pages < 100_000 else { return pages }
            start = next.index
            offset = next.offset
        }
    }

    /// Seconds, `mm:ss` or `h:mm:ss`; nil for anything else or a negative value.
    static func timelinePosition(_ value: JSONValue) -> TimeInterval? {
        switch value {
        case let .integer(number):
            return number >= 0 ? TimeInterval(number) : nil
        case let .number(number):
            return number.isFinite && number >= 0 ? number : nil
        case let .string(raw):
            let text = raw.trimmingCharacters(in: .whitespaces)
            guard !text.isEmpty, text.count <= 16 else { return nil }
            let digits = CharacterSet(charactersIn: "0123456789")
            if text.unicodeScalars.allSatisfy({ digits.contains($0) || $0 == "." }) {
                return Double(text).flatMap { $0.isFinite ? $0 : nil }
            }
            let parts = text.split(separator: ":", omittingEmptySubsequences: false)
            guard (2...3).contains(parts.count),
                  parts.allSatisfy({ !$0.isEmpty && $0.unicodeScalars.allSatisfy(digits.contains) }),
                  parts.dropFirst().allSatisfy({ $0.count == 2 }) else { return nil }
            let numbers = parts.compactMap { Int($0) }
            guard numbers.count == parts.count, numbers.dropFirst().allSatisfy({ $0 < 60 }) else { return nil }
            return TimeInterval(numbers.reduce(0) { $0 * 60 + $1 })
        case .null, .bool, .array, .object:
            return nil
        }
    }

    /// The last character boundary at or before a UTF-16 offset.
    static func characterIndex(in text: String, utf16Offset: Int) -> String.Index {
        guard utf16Offset > 0 else { return text.startIndex }
        var used = 0
        var index = text.startIndex
        while index < text.endIndex {
            let width = text[index].utf16.count
            if used + width > utf16Offset { break }
            used += width
            index = text.index(after: index)
        }
        return index
    }

    /// Where to cut a line longer than a page: after the last whitespace that fits, or at
    /// the last fitting character when one word is longer than the page. Always cuts
    /// after at least one character, so paging moves forward.
    static func cutIndex(in text: Substring, limit: Int) -> String.Index {
        var used = 0
        var index = text.startIndex
        var lastBreak: String.Index?
        while index < text.endIndex {
            let character = text[index]
            let width = character.utf16.count
            if used + width > limit { break }
            used += width
            index = text.index(after: index)
            if character.isWhitespace {
                lastBreak = index
            }
        }
        if index == text.startIndex, index < text.endIndex {
            return text.index(after: index)
        }
        if index < text.endIndex, let lastBreak {
            return lastBreak
        }
        return index
    }
}
