import Foundation

/// One stretch of one speaker's speech in a reference or a hypothesis.
public struct SpeakerSegment: Codable, Equatable, Sendable {
    public var speaker: String
    public var start: Double
    public var end: Double

    public init(speaker: String, start: Double, end: Double) {
        self.speaker = speaker
        self.start = start
        self.end = end
    }

    public var duration: Double {
        max(0, end - start)
    }
}

public struct AnnotationError: LocalizedError, Equatable {
    public let message: String

    public init(_ message: String) {
        self.message = message
    }

    public var errorDescription: String? { message }
}

/// NIST Rich Transcription Time Marked files, the exchange format of diarization
/// scorers: one `SPEAKER` line per segment, times in seconds.
public enum RTTM {
    /// `SPEAKER <file> 1 <start> <duration> <NA> <NA> <speaker> <NA> <NA>` per segment,
    /// sorted by start. Whitespace inside names becomes `_`, since fields are
    /// whitespace-separated.
    public static func write(_ segments: [SpeakerSegment], file: String) -> String {
        let fileID = field(file)
        return segments
            .filter { $0.end > $0.start }
            .sorted { ($0.start, $0.end, $0.speaker) < ($1.start, $1.end, $1.speaker) }
            .map { segment in
                "SPEAKER \(fileID) 1 \(seconds(segment.start)) \(seconds(segment.duration)) <NA> <NA> "
                    + "\(field(segment.speaker)) <NA> <NA>\n"
            }
            .joined()
    }

    /// Segments of every file in the text, keyed by file id, in file order. Blank lines,
    /// `;;` comments and other record types are skipped.
    public static func parse(_ text: String) throws -> [String: [SpeakerSegment]] {
        var files: [String: [SpeakerSegment]] = [:]
        for (index, line) in text.split(whereSeparator: \.isNewline).enumerated() {
            let fields = line.split(whereSeparator: \.isWhitespace).map(String.init)
            guard let type = fields.first, type == "SPEAKER" else { continue }
            guard fields.count >= 8, let start = Double(fields[3]), let duration = Double(fields[4]),
                  start.isFinite, duration.isFinite, start >= 0, duration >= 0 else {
                throw AnnotationError("RTTM line \(index + 1) is malformed: \(line)")
            }
            files[fields[1], default: []].append(SpeakerSegment(speaker: fields[7], start: start, end: start + duration))
        }
        return files
    }

    /// Millisecond precision without a locale, so files compare byte for byte.
    private static func seconds(_ value: Double) -> String {
        String(format: "%.3f", value)
    }

    private static func field(_ value: String) -> String {
        let cleaned = value.split(whereSeparator: \.isWhitespace).joined(separator: "_")
        return cleaned.isEmpty ? "<NA>" : cleaned
    }
}

/// The hand-written truth for a real recording: `start,end,name` per line, times in
/// seconds (`12.5`) or clock form (`1:02`, `1:02:03.5`). A header line, blank lines and
/// lines starting with `#` are skipped; the name is everything after the second comma.
public enum TruthCSV {
    public static func parse(_ text: String) throws -> [SpeakerSegment] {
        var segments: [SpeakerSegment] = []
        for (index, rawLine) in text.split(whereSeparator: \.isNewline).enumerated() {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, !line.hasPrefix("#") else { continue }
            let parts = line.split(separator: ",", maxSplits: 2, omittingEmptySubsequences: false)
                .map { $0.trimmingCharacters(in: .whitespaces) }
            if index == 0, parts.count == 3, time(parts[0]) == nil, time(parts[1]) == nil {
                continue
            }
            guard parts.count == 3, let start = time(parts[0]), let end = time(parts[1]), end > start,
                  !parts[2].isEmpty else {
                throw AnnotationError("Truth line \(index + 1) should read start,end,name: \(line)")
            }
            segments.append(SpeakerSegment(speaker: parts[2], start: start, end: end))
        }
        return segments.sorted { ($0.start, $0.end) < ($1.start, $1.end) }
    }

    /// Seconds from `ss.s`, `mm:ss.s` or `h:mm:ss.s`; nil when unreadable.
    public static func time(_ text: String) -> Double? {
        let parts = text.split(separator: ":", omittingEmptySubsequences: false)
        guard (1...3).contains(parts.count) else { return nil }
        var total = 0.0
        for (index, part) in parts.enumerated() {
            guard let value = Double(part), value.isFinite, value >= 0 else { return nil }
            // Minutes and seconds after a colon stay below 60.
            if index > 0, value >= 60 {
                return nil
            }
            total = total * 60 + value
        }
        return total
    }
}
