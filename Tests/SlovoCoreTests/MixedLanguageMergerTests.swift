import XCTest
@testable import SlovoCore

/// Runs every case of `Shared/fixtures/mixed-merge.json`, which the Windows app runs too, and
/// checks the pieces the merge is built from.
final class MixedLanguageMergerTests: XCTestCase {
    struct Fixture: Decodable {
        struct Case: Decodable {
            let name: String
            let russian: String
            let helper: String
            let expected: String
            let replacements: Int
            let needsHelper: Bool
        }

        let lexicon: [String: [String]]
        let cases: [Case]
    }

    static let fixtureURL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appendingPathComponent("Shared/fixtures/mixed-merge.json")

    func testEveryFixtureCase() throws {
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: Self.fixtureURL))
        XCTAssertGreaterThanOrEqual(fixture.cases.count, 20)
        let merger = MixedLanguageMerger(lexicon: .fixed(fixture.lexicon))
        for item in fixture.cases {
            let result = merger.merge(russian: item.russian, helper: item.helper)
            XCTAssertEqual(result.text, item.expected, item.name)
            XCTAssertEqual(result.replacements, item.replacements, item.name)
            XCTAssertEqual(merger.needsHelper(item.russian), item.needsHelper, item.name)
        }
    }

    func testSequenceMatcherMatchesDifflib() {
        // difflib.SequenceMatcher(None, "abxcd", "abcd").get_opcodes() and .ratio()
        let matcher = SequenceMatcher(Array("abxcd"), Array("abcd"))
        XCTAssertEqual(matcher.opcodes().map(\.tag), [.equal, .delete, .equal])
        XCTAssertEqual(matcher.ratio(), 2.0 * 4 / 9, accuracy: 1e-12)
        // difflib.SequenceMatcher(None, "qabxcd", "abycdf").get_opcodes()
        let example = SequenceMatcher(Array("qabxcd"), Array("abycdf")).opcodes()
        XCTAssertEqual(example.map(\.tag), [.delete, .equal, .replace, .equal, .insert])
        XCTAssertEqual(example.map { [$0.aStart, $0.aEnd, $0.bStart, $0.bEnd] },
                       [[0, 1, 0, 0], [1, 3, 0, 2], [3, 4, 2, 3], [4, 6, 3, 5], [6, 6, 5, 6]])
    }

    func testSoundKeysMeetAcrossScripts() {
        XCTAssertEqual(MixedLanguageMerger.sound("Makos"), MixedLanguageMerger.sound("macOS"))
        XCTAssertEqual(MixedLanguageMerger.sound("мейн"), MixedLanguageMerger.sound("main"))
        XCTAssertEqual(MixedLanguageMerger.sound("Pod"), MixedLanguageMerger.sound("под"))
    }

    func testHelperKeepsLatinLabelsAndRejectsMixedScriptReplacements() {
        let merger = MixedLanguageMerger(lexicon: .fixed([:]))
        let result = merger.merge(russian: "Vendor A и Makos.", helper: "Vendоr А и Maкos.")
        XCTAssertEqual(result.text, "Vendor A и Makos.")
        XCTAssertEqual(result.replacements, 0)
        XCTAssertEqual(merger.merge(russian: "Vendor A.", helper: "Vendor А.").text, "Vendor A.")
        XCTAssertEqual(merger.merge(russian: "A beta.", helper: "А beta.").text, "A beta.")
    }

    func testHelperKeepsRepeatedLetterAbbreviations() {
        let merger = MixedLanguageMerger(lexicon: .fixed(["ru": ["В", "панели", "появится", "раздел"]]))
        let result = merger.merge(russian: "В панели BB появится раздел.",
                                  helper: "В панели BP появится раздел.")
        XCTAssertEqual(result.text, "В панели BB появится раздел.")
        XCTAssertEqual(result.replacements, 0)
        XCTAssertEqual(merger.merge(russian: "На стороне VB.", helper: "На стороне BB.").text, "На стороне BB.")
        XCTAssertEqual(merger.merge(russian: "Убери AP.", helper: "Убери API.").text, "Убери API.")
        XCTAssertEqual(merger.merge(russian: "Стоит би.", helper: "Стоит BI.").text, "Стоит BI.")
    }

    func testHelperCanRestoreHyphenatedWords() {
        let merger = MixedLanguageMerger(lexicon: .fixed([:]))
        XCTAssertEqual(merger.merge(russian: "Tshort.", helper: "T-shirt.").text, "T-shirt.")
        XCTAssertEqual(merger.merge(russian: "X Ray.", helper: "X-Ray.").text, "X-Ray.")
    }

    func testHelperCanRestoreSingleLetterPrepositionInRussian() {
        let merger = MixedLanguageMerger(lexicon: .fixed([:]))
        let result = merger.merge(russian: "Я был V офисе.", helper: "Я был в офисе.")
        XCTAssertEqual(result.text, "Я был в офисе.")
        XCTAssertEqual(result.replacements, 1)
    }

    func testSingleLetterPrepositionIgnoresEnglishAfterItAndAcrossSentences() {
        let merger = MixedLanguageMerger(lexicon: .fixed([:]))
        XCTAssertEqual(merger.merge(russian: "Это V GitHub.", helper: "Это в GitHub.").text, "Это в GitHub.")
        XCTAssertEqual(merger.merge(russian: "Открой Finder. V офисе.", helper: "Открой Finder. В офисе.").text,
                       "Открой Finder. В офисе.")
    }

    func testWithoutARussianDictionaryOnlyLatinGarbleIsReplaced() {
        let merger = MixedLanguageMerger(lexicon: .fixed([:]))
        let result = merger.merge(russian: "Сделай ребейс в версии Makos.", helper: "Сделай rebase в версии macOS.")
        XCTAssertEqual(result.text, "Сделай ребейс в версии macOS.")
        XCTAssertEqual(result.replacements, 1)
    }
}
