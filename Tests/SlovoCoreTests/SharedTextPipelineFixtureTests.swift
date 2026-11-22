import Foundation
import XCTest
@testable import SlovoCore

/// Runs every case of `Shared/fixtures/text-pipeline.json` through the text pipeline with
/// the case's fixed word lexicon instead of the system spell checker, so the expected
/// output depends on nothing but the file. The Windows port runs the same file.
///
/// `SLOVO_REGENERATE_FIXTURES=1 swift test --filter SharedTextPipelineFixtureTests`
/// rewrites every case's `expected` from the current pipeline instead of checking it;
/// review the diff before committing it.
final class SharedTextPipelineFixtureTests: XCTestCase {
    static let fixtureURL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appendingPathComponent("Shared/fixtures/text-pipeline.json")

    func testEveryCaseProducesTheExpectedResult() throws {
        let data = try Data(contentsOf: Self.fixtureURL)
        var fixture = try JSONDecoder().decode(PipelineFixture.self, from: data)
        XCTAssertFalse(fixture.format.isEmpty, "the fixture documents its format under _format")
        XCTAssertGreaterThanOrEqual(fixture.cases.count, 80)

        let regenerate = ProcessInfo.processInfo.environment["SLOVO_REGENERATE_FIXTURES"] == "1"
        var names = Set<String>()
        for index in fixture.cases.indices {
            let fixtureCase = fixture.cases[index]
            XCTAssertTrue(names.insert(fixtureCase.name).inserted, "duplicate case name: \(fixtureCase.name)")
            let actual = try PipelineFixture.Expected(run(fixtureCase))
            if regenerate {
                fixture.cases[index].expected = actual
                continue
            }
            guard let expected = fixtureCase.expected else {
                XCTFail("\(fixtureCase.name): no expected result")
                continue
            }
            XCTAssertEqual(actual, expected, fixtureCase.name)
        }
        if regenerate {
            try fixture.serialized().write(to: Self.fixtureURL, options: .atomic)
        }
    }

    /// The fixed lexicon follows the rules the fixture's `_format` describes.
    func testFixedLexiconRules() {
        let lexicon = WordLexicon.fixed(["ru": ["дом", "Москва"], "en": []])
        XCTAssertTrue(lexicon.hasLanguage("ru"))
        XCTAssertTrue(lexicon.hasLanguage("en"))
        XCTAssertFalse(lexicon.hasLanguage("de"))
        XCTAssertTrue(lexicon.isCorrect("дом", "ru"))
        XCTAssertTrue(lexicon.isCorrect("Дом", "ru"))
        XCTAssertTrue(lexicon.isCorrect("ДОМ", "ru"))
        XCTAssertTrue(lexicon.isCorrect("Москва", "ru"))
        XCTAssertFalse(lexicon.isCorrect("москва", "ru"))
        XCTAssertFalse(lexicon.isCorrect("дом", "en"))
        XCTAssertFalse(lexicon.isCorrect("дом", "de"))
    }

    private func run(_ fixtureCase: PipelineFixture.Case) throws -> TextPipelineResult {
        let context = fixtureCase.context
        let dictionary = try context.dictionary.map { item -> DictionaryItem in
            guard let kind = DictionaryItem.Kind(rawValue: item.kind) else {
                throw FixtureError("\(fixtureCase.name): unknown dictionary kind \(item.kind)")
            }
            return DictionaryItem(kind: kind, text: item.text, replacement: item.replacement ?? "",
                                  createdAt: Date(timeIntervalSince1970: 0))
        }
        let snippets = context.snippets.map {
            Snippet(trigger: $0.trigger, expansion: $0.expansion, createdAt: Date(timeIntervalSince1970: 0))
        }
        var style: StylePreset?
        if let name = context.style {
            guard let preset = StylePreset(rawValue: name) else {
                throw FixtureError("\(fixtureCase.name): unknown style \(name)")
            }
            style = preset
        }
        guard let languageMode = LanguageMode(rawValue: context.languageMode) else {
            throw FixtureError("\(fixtureCase.name): unknown language mode \(context.languageMode)")
        }
        let pipelineContext = TextPipelineContext(
            dictionary: dictionary, snippets: snippets,
            cleanup: AutoCleanupOptions(removeFillers: context.cleanup.removeFillers,
                                        removeRepetitions: context.cleanup.removeRepetitions),
            style: style, languageMode: languageMode)
        let pipeline = TextPipeline(stages: TextPipeline.standardStages(lexicon: .fixed(fixtureCase.lexicon)))
        return pipeline.process(fixtureCase.input, context: pipelineContext)
    }
}

private struct FixtureError: Error, CustomStringConvertible {
    let description: String

    init(_ description: String) {
        self.description = description
    }
}

/// `Shared/fixtures/text-pipeline.json`.
struct PipelineFixture: Codable {
    var format: [String]
    var version: Int
    var cases: [Case]

    enum CodingKeys: String, CodingKey {
        case format = "_format"
        case version
        case cases
    }

    struct Case: Codable {
        var name: String
        var input: String
        var context: Context
        var lexicon: [String: [String]]
        var expected: Expected?
    }

    struct Context: Codable {
        var dictionary: [Item]
        var snippets: [SnippetItem]
        var cleanup: Cleanup
        /// "formal", "casual", "veryCasual" or null.
        var style: String?
        var languageMode: String

        enum CodingKeys: String, CodingKey {
            case dictionary, snippets, cleanup, style, languageMode
        }

        func encode(to encoder: Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            try container.encode(dictionary, forKey: .dictionary)
            try container.encode(snippets, forKey: .snippets)
            try container.encode(cleanup, forKey: .cleanup)
            // An explicit null, so every case spells out every field.
            try container.encode(style, forKey: .style)
            try container.encode(languageMode, forKey: .languageMode)
        }
    }

    struct Item: Codable {
        /// "term" or "replacement".
        var kind: String
        var text: String
        var replacement: String?
    }

    struct SnippetItem: Codable {
        var trigger: String
        var expansion: String
    }

    struct Cleanup: Codable {
        var removeFillers: Bool
        var removeRepetitions: Bool
    }

    struct Expected: Codable, Equatable {
        var text: String
        var snippetsApplied: Int
        var replacementsApplied: Int
        var termFixesApplied: Int

        init(_ result: TextPipelineResult) {
            text = result.text
            snippetsApplied = result.snippetsApplied
            replacementsApplied = result.replacementsApplied
            termFixesApplied = result.termFixesApplied
        }
    }

    /// The file's text: `_format` and `version` first, then one case per block with one
    /// field per line in a fixed order, so diffs stay readable.
    func serialized() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        func json<T: Encodable>(_ value: T) throws -> String {
            String(decoding: try encoder.encode(value), as: UTF8.self)
        }
        var lines = ["{", "  \"_format\": ["]
        for (index, line) in format.enumerated() {
            lines.append("    " + (try json(line)) + (index < format.count - 1 ? "," : ""))
        }
        lines.append("  ],")
        lines.append("  \"version\": \(version),")
        lines.append("  \"cases\": [")
        for (index, fixtureCase) in cases.enumerated() {
            lines.append("    {")
            lines.append("      \"name\": \(try json(fixtureCase.name)),")
            lines.append("      \"input\": \(try json(fixtureCase.input)),")
            lines.append("      \"context\": \(try json(fixtureCase.context)),")
            lines.append("      \"lexicon\": \(try json(fixtureCase.lexicon)),")
            lines.append("      \"expected\": \(try json(fixtureCase.expected))")
            lines.append("    }" + (index < cases.count - 1 ? "," : ""))
        }
        lines.append("  ]")
        lines.append("}")
        return Data((lines.joined(separator: "\n") + "\n").utf8)
    }
}
