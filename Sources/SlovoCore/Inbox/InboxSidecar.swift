import Foundation

/// The `.json` a phone writes next to a clip in the inbox.
///
/// The phone writes it once the audio is complete, so a sidecar next to a clip also means
/// the clip is finished. With it the meeting keeps the phone's own time, name and marked
/// moments instead of the moment the file happened to reach the Mac.
///
/// Reading is defensive throughout: a sidecar from a newer phone build, or one cut off
/// half-written, may cost the extra data but never the clip itself.
struct InboxSidecar: Equatable, Sendable {
    /// What the phone was recording. A meeting is separated into speakers like one
    /// recorded here; a note is one voice.
    enum Kind: String, Sendable {
        case note
        case meeting
    }

    /// A moment marked on the phone while it was recording.
    struct Moment: Equatable, Sendable {
        /// Seconds from the start of the clip.
        var time: TimeInterval
        var note: String?
    }

    static let fileExtension = "json"
    /// Longer than any name typed on a phone. A title also becomes the `# heading` of
    /// `transcript.md`, so it is kept to one line.
    static let maximumTitleCharacters = 200

    var kind: Kind
    /// When the phone started recording; nil when it did not say, or said something that
    /// is not a date.
    var recordedAt: Date?
    var device: String?
    var title: String?
    var moments: [Moment]

    init(kind: Kind = .note, recordedAt: Date? = nil, device: String? = nil, title: String? = nil,
         moments: [Moment] = []) {
        self.kind = kind
        self.recordedAt = recordedAt
        self.device = device
        self.title = title
        self.moments = moments
    }

    /// The sidecar that belongs to a clip: `slovo-20260916-125458.m4a` → `…-125458.json`.
    static func url(for clip: URL) -> URL {
        clip.deletingPathExtension().appendingPathExtension(fileExtension)
    }

    /// Nil when the file cannot be read or is not a JSON object; anything inside it that
    /// does not fit the contract is dropped on its own.
    static func read(from url: URL) -> InboxSidecar? {
        guard let data = try? Data(contentsOf: url),
              let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            return nil
        }
        return InboxSidecar(object)
    }

    /// Reads the fields of format 1. `version` is not checked and unknown keys are ignored:
    /// the format grows by adding keys, and a phone one release ahead should still import.
    /// `durationMs` is ignored too — the decoded audio is what every later step measures.
    init(_ object: [String: Any]) {
        kind = (object["kind"] as? String).flatMap(Kind.init(rawValue:)) ?? .note
        recordedAt = (object["recordedAt"] as? String).flatMap(Self.date(from:))
        device = (object["device"] as? String).flatMap(MeetingSource.sanitizedDevice)
        title = (object["title"] as? String).flatMap(Self.sanitizedTitle)
        moments = ((object["highlights"] as? [Any]) ?? []).compactMap(Self.moment(from:))
    }

    // MARK: - Fields

    /// ISO 8601 with the phone's offset, with or without fractional seconds.
    private static func date(from text: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        if let date = formatter.date(from: text) {
            return date
        }
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: text)
    }

    private static func sanitizedTitle(_ title: String) -> String? {
        let kept = String(TranscriptMerger.normalizedText(title).prefix(maximumTitleCharacters))
            .trimmingCharacters(in: .whitespaces)
        return kept.isEmpty ? nil : kept
    }

    /// A moment without a usable position is dropped rather than placed at the start,
    /// where it would point at speech nobody marked.
    private static func moment(from element: Any) -> Moment? {
        guard let object = element as? [String: Any], let milliseconds = object["atMs"] as? NSNumber else {
            return nil
        }
        let seconds = milliseconds.doubleValue / 1000
        guard seconds.isFinite, seconds >= 0 else { return nil }
        return Moment(time: MeetingTime.clamped(seconds),
                      note: MeetingEdits.sanitizedNote(object["note"] as? String))
    }
}
