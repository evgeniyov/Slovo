import Foundation
import XCTest
@testable import SlovoCore

/// Runs every `Shared/fixtures/meetings/<name>.meeting.json` through
/// `TranscriptMerger.markdown` in both languages and through the recovery parser, and
/// compares the results with the files next to it: `<name>.<en|ru>.md` byte for byte and
/// `<name>.<en|ru>.parsed.json` as decoded values. The Windows port runs the same files,
/// so both apps write the same `transcript.md` and read the same one back.
///
/// Dates are shown in a fixed time zone, UTC+03:00, so the documents do not depend on the
/// computer they are made on; the Windows test passes the same zone.
///
/// `SLOVO_REGENERATE_FIXTURES=1 swift test --filter SharedMeetingFixture` rewrites the
/// expected files from the current code instead of checking them; review the diff before
/// committing it.
final class SharedMeetingFixtureTests: XCTestCase {
    static let folder = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appendingPathComponent("Shared/fixtures/meetings")

    static let timeZone = TimeZone(secondsFromGMT: 3 * 3600)

    /// What `MeetingStore.parseTranscript` returns, in a form JSON can hold.
    struct Parsed: Codable, Equatable {
        var title: String?
        var segments: [TranscriptSegment]
        var speakers: [MeetingSpeaker]
        var highlights: [MeetingHighlight]
    }

    private static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }

    func testEveryMeetingGivesTheExpectedDocumentsAndReadsBack() throws {
        let regenerate = ProcessInfo.processInfo.environment["SLOVO_REGENERATE_FIXTURES"] == "1"
        let saved = NSTimeZone.default
        NSTimeZone.default = try XCTUnwrap(Self.timeZone)
        defer { NSTimeZone.default = saved }

        let names = try FileManager.default.contentsOfDirectory(atPath: Self.folder.path)
            .filter { $0.hasSuffix(".meeting.json") }
            .sorted()
        XCTAssertGreaterThanOrEqual(names.count, 4, "the fixture holds at least four meetings")

        for name in names {
            let base = String(name.dropLast(".meeting.json".count))
            let data = try Data(contentsOf: Self.folder.appendingPathComponent(name))
            let record = try JSONDecoder().decode(MeetingRecord.self, from: data)
            for language in [AppLanguage.en, .ru] {
                let document = TranscriptMerger.markdown(meeting: record, language: language)
                let parsedValue = MeetingStore.parseTranscript(document)
                let parsed = Parsed(title: parsedValue.title, segments: parsedValue.segments,
                                    speakers: parsedValue.speakers, highlights: parsedValue.highlights)
                let documentURL = Self.folder.appendingPathComponent("\(base).\(language.rawValue).md")
                let parsedURL = Self.folder.appendingPathComponent("\(base).\(language.rawValue).parsed.json")
                if regenerate {
                    try Data(document.utf8).write(to: documentURL, options: .atomic)
                    var json = try Self.encoder.encode(parsed)
                    json.append(0x0A)
                    try json.write(to: parsedURL, options: .atomic)
                    continue
                }
                let expected = try Data(contentsOf: documentURL)
                XCTAssertEqual(Data(document.utf8), expected, "\(base) \(language.rawValue):\n\(document)")
                let expectedParsed = try JSONDecoder().decode(Parsed.self, from: Data(contentsOf: parsedURL))
                XCTAssertEqual(parsed, expectedParsed, "\(base) \(language.rawValue)")
            }
        }
    }
}
