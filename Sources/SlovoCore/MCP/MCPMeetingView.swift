import Foundation

/// One meeting prepared for output: every string taken from the meeting sanitized and
/// capped, speaker labels in the interface language, display lines and highlight anchors
/// computed once.
final class MCPMeetingView {
    static let maximumTitleCharacters = 200
    static let maximumNameCharacters = 80
    static let maximumNoteCharacters = 500
    static let maximumLineExcerptCharacters = 240

    let archived: ArchivedMeeting
    let language: AppLanguage
    /// The sanitized title; computed eagerly because every list shows it.
    let title: String
    private var labels: [String: String] = [:]

    init(_ archived: ArchivedMeeting, language: AppLanguage) {
        self.archived = archived
        self.language = language
        self.title = Self.singleLine(archived.record.title, limit: Self.maximumTitleCharacters)
    }

    /// The full lowercase id, the form results use.
    var id: String {
        archived.record.id.uuidString.lowercased()
    }

    /// The first 8 characters of the id, accepted wherever an id is.
    var shortID: String {
        String(id.prefix(8))
    }

    /// The record with sanitized text, names and notes; built only when lines are needed.
    lazy var record: MeetingRecord = Self.sanitized(archived.record, title: title)

    lazy var speakers: [MeetingSpeaker] = archived.record.effectiveSpeakers.map(Self.sanitizedSpeaker)

    lazy var lines: [TranscriptLine] = TranscriptMerger.lines(for: record)

    /// Highlights in timeline order.
    lazy var highlights: [MeetingHighlight] = record.highlights.sorted { left, right in
        (left.time, left.createdAt.timeIntervalSinceReferenceDate, left.id.uuidString)
            < (right.time, right.createdAt.timeIntervalSinceReferenceDate, right.id.uuidString)
    }

    /// The display line each anchored highlight belongs to.
    lazy var anchors: [UUID: Int] = {
        var result: [UUID: Int] = [:]
        for highlight in highlights {
            if let index = TranscriptMerger.highlightLineIndex(highlight, in: lines), lines.indices.contains(index) {
                result[highlight.id] = index
            }
        }
        return result
    }()

    lazy var highlightsByLine: [Int: [MeetingHighlight]] = {
        var result: [Int: [MeetingHighlight]] = [:]
        for highlight in highlights {
            if let index = anchors[highlight.id] {
                result[index, default: []].append(highlight)
            }
        }
        return result
    }()

    /// Identifies the version of the file the meeting came from, for transcript cursors.
    lazy var fingerprint: String =
        MCPHash.hex("\(archived.recordModified.timeIntervalSinceReferenceDate)|\(archived.recordSize)")

    func label(for speaker: MeetingSpeaker) -> String {
        if let cached = labels[speaker.id] {
            return cached
        }
        let label = TranscriptMerger.speakerLabel(speaker, in: archived.record, language: language)
        labels[speaker.id] = label
        return label
    }

    func label(forSpeaker id: String) -> String {
        if let speaker = speakers.first(where: { $0.id == id }) {
            return label(for: speaker)
        }
        if let speaker = archived.record.speaker(id: id) {
            return label(for: Self.sanitizedSpeaker(speaker))
        }
        return id
    }

    /// The anchored line of a highlight, if any.
    func line(of highlight: MeetingHighlight) -> TranscriptLine? {
        anchors[highlight.id].map { lines[$0] }
    }

    static func sanitized(_ source: MeetingRecord, title: String) -> MeetingRecord {
        var record = source
        record.title = title
        record.errorMessage = nil
        // Claude's own notes are dropped here, because everything rendered from this record
        // goes inside the untrusted-transcript wrapper, whose notice says the text is
        // recognized speech. A retelling served under that notice would come back as a
        // quotation of words nobody said. `MCPResources` says outside the wrapper that the
        // notes exist.
        record.claudeNotes = nil
        record.segments = source.segments.map { segment in
            var copy = segment
            copy.text = MCPSanitizer.sanitize(segment.text)
            return copy
        }
        record.speakers = source.speakers.map(sanitizedSpeaker)
        record.highlights = source.highlights.map { highlight in
            var copy = highlight
            copy.note = highlight.note.flatMap { note in
                let clean = singleLine(note, limit: maximumNoteCharacters)
                return clean.isEmpty ? nil : clean
            }
            return copy
        }
        return record
    }

    static func sanitizedSpeaker(_ speaker: MeetingSpeaker) -> MeetingSpeaker {
        var copy = speaker
        copy.displayName = speaker.displayName.flatMap { name in
            let clean = singleLine(name, limit: maximumNameCharacters)
            return clean.isEmpty ? nil : clean
        }
        return copy
    }

    /// Sanitized, on one line with single spaces, and at most `limit` characters.
    static func singleLine(_ text: String, limit: Int) -> String {
        let clean = MCPSanitizer.sanitize(text).split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
        return capped(clean, limit)
    }

    /// At most `limit` characters, the last one an ellipsis when text was cut.
    static func capped(_ text: String, _ limit: Int) -> String {
        guard text.count > limit else { return text }
        return String(text.prefix(max(0, limit - 1))) + "…"
    }
}

/// Dates as tools accept and report them, in the Mac's time zone.
struct MCPDates {
    let now: Date
    let timeZone: TimeZone
    let calendar: Calendar

    init(now: Date, timeZone: TimeZone) {
        self.now = now
        self.timeZone = timeZone
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        self.calendar = calendar
    }

    /// ISO 8601 with the local offset, for example `2026-09-15T11:02:07+03:00`.
    func iso(_ date: Date) -> String {
        let parts = calendar.dateComponents([.year, .month, .day, .hour, .minute, .second], from: date)
        let offset = timeZone.secondsFromGMT(for: date)
        let magnitude = abs(offset)
        return String(format: "%04ld-%02ld-%02ldT%02ld:%02ld:%02ld%@%02ld:%02ld",
                      parts.year ?? 0, parts.month ?? 0, parts.day ?? 0, parts.hour ?? 0, parts.minute ?? 0,
                      parts.second ?? 0, offset < 0 ? "-" : "+", magnitude / 3600, (magnitude % 3600) / 60)
    }

    /// A long date and short time in the interface language, for prompt texts.
    func localized(_ date: Date, language: AppLanguage) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: language.resolvedCode())
        formatter.timeZone = timeZone
        formatter.dateStyle = .long
        formatter.timeStyle = .short
        return formatter.string(from: date)
    }

    /// `today`, `yesterday`, `Nd` (1–365 days back from now), `YYYY-MM-DD` (local midnight)
    /// or an ISO 8601 date-time, local unless it names an offset. Nil for anything else,
    /// including dates the calendar would roll over, such as February 30.
    func parse(_ text: String) -> Date? {
        switch text {
        case "today": return calendar.startOfDay(for: now)
        case "yesterday": return calendar.date(byAdding: .day, value: -1, to: calendar.startOfDay(for: now))
        default: break
        }
        let bytes = Array(text.utf8)
        if bytes.last == UInt8(ascii: "d") {
            guard bytes.count >= 2, bytes.count <= 4, bytes.first != UInt8(ascii: "0"),
                  let days = number(bytes, 0..<(bytes.count - 1)), (1...365).contains(days) else { return nil }
            return calendar.date(byAdding: .day, value: -days, to: now)
        }
        guard bytes.count >= 10, let year = number(bytes, 0..<4), bytes[4] == UInt8(ascii: "-"),
              let month = number(bytes, 5..<7), bytes[7] == UInt8(ascii: "-"), let day = number(bytes, 8..<10),
              (1...12).contains(month), (1...31).contains(day) else { return nil }
        var components = DateComponents(year: year, month: month, day: day)
        var zone = timeZone
        if bytes.count > 10 {
            guard bytes[10] == UInt8(ascii: "T"), bytes.count >= 16, let hour = number(bytes, 11..<13),
                  bytes[13] == UInt8(ascii: ":"), let minute = number(bytes, 14..<16),
                  hour <= 23, minute <= 59 else { return nil }
            var cursor = 16
            var second = 0
            if cursor < bytes.count, bytes[cursor] == UInt8(ascii: ":") {
                guard let value = number(bytes, 17..<19), value <= 59 else { return nil }
                second = value
                cursor = 19
            }
            if cursor < bytes.count {
                if bytes[cursor] == UInt8(ascii: "Z") {
                    zone = TimeZone(secondsFromGMT: 0) ?? timeZone
                    cursor += 1
                } else if bytes[cursor] == UInt8(ascii: "+") || bytes[cursor] == UInt8(ascii: "-") {
                    guard let hours = number(bytes, (cursor + 1)..<(cursor + 3)), bytes.count > cursor + 3,
                          bytes[cursor + 3] == UInt8(ascii: ":"),
                          let minutes = number(bytes, (cursor + 4)..<(cursor + 6)),
                          hours <= 18, minutes <= 59 else { return nil }
                    let sign = bytes[cursor] == UInt8(ascii: "-") ? -1 : 1
                    guard let offsetZone = TimeZone(secondsFromGMT: sign * (hours * 3600 + minutes * 60)) else {
                        return nil
                    }
                    zone = offsetZone
                    cursor += 6
                } else {
                    return nil
                }
            }
            guard cursor == bytes.count else { return nil }
            components.hour = hour
            components.minute = minute
            components.second = second
        }
        var zoned = calendar
        zoned.timeZone = zone
        guard let date = zoned.date(from: components) else { return nil }
        let check = zoned.dateComponents([.year, .month, .day], from: date)
        guard check.year == year, check.month == month, check.day == day else { return nil }
        return date
    }

    private func number(_ bytes: [UInt8], _ range: Range<Int>) -> Int? {
        guard range.lowerBound >= 0, range.upperBound <= bytes.count, !range.isEmpty else { return nil }
        var value = 0
        for index in range {
            let byte = bytes[index]
            guard byte >= UInt8(ascii: "0"), byte <= UInt8(ascii: "9") else { return nil }
            value = value * 10 + Int(byte - UInt8(ascii: "0"))
        }
        return value
    }
}

/// `since` inclusive, `until` exclusive.
struct MCPDateRange {
    var since: Date?
    var until: Date?

    func contains(_ date: Date) -> Bool {
        if let since, date < since { return false }
        if let until, date >= until { return false }
        return true
    }
}

/// A stable hash for cursors. Swift's `Hasher` is seeded per process, and cursors must
/// survive a restart of the server between pages.
enum MCPHash {
    static func hex(_ text: String) -> String {
        var hash: UInt64 = 0xCBF2_9CE4_8422_2325
        for byte in text.utf8 {
            hash ^= UInt64(byte)
            hash = hash &* 0x0000_0100_0000_01B3
        }
        return String(format: "%016llx", hash)
    }
}

/// Cursors travel as unpadded base64url JSON.
enum MCPBase64URL {
    static func encode(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    static func decode(_ text: String) -> Data? {
        guard !text.isEmpty, text.count % 4 != 1,
              text.unicodeScalars.allSatisfy({ $0.isASCII && (CharacterSet.alphanumerics.contains($0) || $0 == "-" || $0 == "_") })
        else { return nil }
        var base64 = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        return Data(base64Encoded: base64)
    }

    /// The object inside a cursor when it has exactly these keys.
    static func object(_ text: String, keys: Set<String>) -> [String: JSONValue]? {
        guard let data = decode(text), let value = try? JSONValue(parsing: data), let object = value.objectValue,
              Set(object.keys) == keys else { return nil }
        return object
    }
}

/// Where the next `list_meetings` page starts: after the meeting with this date and id,
/// in newest-first order, so meetings added or deleted meanwhile do not shift pages.
struct MCPListCursor: Equatable {
    /// `createdAt` in seconds since 2001, the precision `meeting.json` stores.
    var time: TimeInterval
    /// The canonical id string, which breaks ties in list order.
    var id: String
    var filter: String

    init(after archived: ArchivedMeeting, filter: String) {
        time = archived.record.createdAt.timeIntervalSinceReferenceDate
        id = archived.record.id.uuidString
        self.filter = filter
    }

    init?(encoded text: String) {
        guard let object = MCPBase64URL.object(text, keys: ["k", "t", "i", "q"]), object["k"] == .string("m"),
              let time = object["t"]?.doubleValue, time.isFinite,
              let rawID = object["i"]?.stringValue, let id = UUID(uuidString: rawID),
              let filter = object["q"]?.stringValue else { return nil }
        self.time = time
        self.id = id.uuidString
        self.filter = filter
    }

    var encoded: String {
        let object: JSONValue = ["k": "m", "t": .number(time), "i": .string(id), "q": .string(filter)]
        return MCPBase64URL.encode(object.serialized())
    }

    /// True when the meeting comes after the cursor position.
    func precedes(_ archived: ArchivedMeeting) -> Bool {
        let meetingTime = archived.record.createdAt.timeIntervalSinceReferenceDate
        return meetingTime < time || (meetingTime == time && archived.record.id.uuidString > id)
    }
}

/// Localized messages for tool errors and prompts.
enum MCPMessages {
    static func disabled(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.disabled", table: .integration, language: language)
    }

    static func notFound(_ reference: String, _ language: AppLanguage) -> String {
        CoreStrings.format("integration.mcp.notFound", table: .integration, language: language, reference)
    }

    static func ambiguous(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.ambiguous", table: .integration, language: language)
    }

    static func noTranscript(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.noTranscript", table: .integration, language: language)
    }

    static func stillProcessing(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.stillProcessing", table: .integration, language: language)
    }

    static func badCursor(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.badCursor", table: .integration, language: language)
    }

    static func badArgument(_ detail: String, _ language: AppLanguage) -> String {
        CoreStrings.format("integration.mcp.badArgument", table: .integration, language: language, detail)
    }

    static func noMeetings(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.noMeetings", table: .integration, language: language)
    }

    static func tooManyRequests(_ seconds: Int, _ language: AppLanguage) -> String {
        CoreStrings.format("integration.mcp.tooManyRequests", table: .integration, language: language, seconds)
    }

    static func damaged(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.damaged", table: .integration, language: language)
    }

    static func writeDisabled(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.writeDisabled", table: .integration, language: language)
    }

    static func writeFailed(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.mcp.writeFailed", table: .integration, language: language)
    }

    static func slovoUser(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.prompt.slovoUser", table: .integration, language: language)
    }

    static func tasksPrompt(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.prompt.tasks", table: .integration, language: language)
    }

    static func shortSummaryPrompt(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.prompt.summary.short", table: .integration, language: language)
    }

    static func fullSummaryPrompt(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.prompt.summary.full", table: .integration, language: language)
    }

    static func pagingPrompt(_ language: AppLanguage) -> String {
        CoreStrings.string("integration.prompt.paging", table: .integration, language: language)
    }
}

/// Search matching: both sides lowercased, ё folded to е, diacritics folded and runs of
/// whitespace collapsed.
enum MCPSearch {
    static func normalized(_ text: String) -> String {
        fastNormalized(text) ?? slowNormalized(text)
    }

    /// Query words with punctuation trimmed from their edges.
    static func words(_ query: String) -> [String] {
        normalized(query).split(separator: " ")
            .map { $0.trimmingCharacters(in: CharacterSet.punctuationCharacters.union(.symbols)) }
            .filter { !$0.isEmpty }
    }

    static func matches(_ normalizedText: String, words: [String]) -> Bool {
        words.allSatisfy { normalizedText.contains($0) }
    }

    /// About `radius` characters on each side of the first match, with ellipses where the
    /// text was cut; the start of the text when nothing matches.
    static func snippet(_ text: String, words: [String], radius: Int = 120) -> String {
        let characters = Array(text)
        var folded: [Character] = []
        var origins: [Int] = []
        var lastWasSpace = true
        for (offset, character) in characters.enumerated() {
            if character.isWhitespace {
                if !lastWasSpace {
                    folded.append(" ")
                    origins.append(offset)
                    lastWasSpace = true
                }
                continue
            }
            lastWasSpace = false
            for piece in normalized(String(character)) {
                folded.append(piece)
                origins.append(offset)
            }
        }
        let haystack = String(folded)
        var first: (position: Int, length: Int)?
        for word in words {
            guard let range = haystack.range(of: word, options: .literal) else { continue }
            let position = haystack.distance(from: haystack.startIndex, to: range.lowerBound)
            if first == nil || position < (first?.position ?? 0) {
                first = (position, word.count)
            }
        }
        guard let match = first, !origins.isEmpty else {
            return MCPMeetingView.capped(text, radius * 2)
        }
        let start = origins[min(match.position, origins.count - 1)]
        let end = origins[min(match.position + max(0, match.length - 1), origins.count - 1)] + 1
        let lower = max(0, start - radius)
        let upper = min(characters.count, end + radius)
        var result = String(characters[lower..<upper])
        if lower > 0 { result = "…" + result }
        if upper < characters.count { result += "…" }
        return result
    }

    /// Foundation's folding for anything outside the common Latin and Cyrillic letters.
    static func slowNormalized(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
            .lowercased()
            .replacingOccurrences(of: "ё", with: "е")
            .split(whereSeparator: { $0.isWhitespace })
            .joined(separator: " ")
    }

    /// The same result as `slowNormalized` for ASCII, the basic Cyrillic letters and common
    /// punctuation, without Foundation's per-string folding cost, because search reads every
    /// line of every meeting. Nil when the text holds anything else.
    static func fastNormalized(_ text: String) -> String? {
        var output = String.UnicodeScalarView()
        output.reserveCapacity(text.unicodeScalars.count)
        var pendingSpace = false
        for scalar in text.unicodeScalars {
            let value = scalar.value
            let mapped: UInt32
            switch value {
            case 0x20, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0xA0, 0x2000...0x200A, 0x202F:
                pendingSpace = !output.isEmpty
                continue
            case 0x41...0x5A: mapped = value + 0x20
            case 0x21...0x7E: mapped = value
            case 0x0401, 0x0451: mapped = 0x0435 // Ё ё → е
            case 0x0419, 0x0439: mapped = 0x0438 // Й й → и, as diacritic folding does
            case 0x0410...0x042F: mapped = value + 0x20
            case 0x0430...0x044F: mapped = value
            case 0xAB, 0xBB, 0x2010...0x2027, 0x2030...0x205E: mapped = value
            default: return nil
            }
            if pendingSpace {
                output.append(" ")
                pendingSpace = false
            }
            guard let result = Unicode.Scalar(mapped) else { return nil }
            output.append(result)
        }
        return String(output)
    }
}
