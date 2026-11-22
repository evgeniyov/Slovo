import XCTest
@testable import SlovoCore

final class DictionarySuggestionsTests: XCTestCase {
    private let lexicon = WordLexicon.fixed(["ru": ["в", "наш", "мастер", "книга"], "en": ["cloud", "code", "worker"]])

    private func find(_ history: [String], terms: [String] = [], dictionary: [DictionaryItem] = []) -> [DictionarySuggestion] {
        DictionarySuggestions.find(in: history, dictionary: dictionary, bundledTerms: terms, lexicon: lexicon)
    }

    func testCountsDistinctDictationsAndReturnsShortExcerpts() throws {
        let history = [
            String(repeating: "в ", count: 80) + "с гитхабом, гитхабом сегодня.",
            "с Гитхабом завтра",
            "с гитхабом вчера",
        ]
        XCTAssertTrue(find(Array(history.prefix(2)), terms: ["GitHub"]).isEmpty)

        let output = find(history, terms: ["GitHub"])
        XCTAssertEqual(output.count, 1)
        let suggestion = try XCTUnwrap(output.first)
        XCTAssertEqual(suggestion.from, "гитхабом")
        XCTAssertEqual(suggestion.to, "GitHub")
        XCTAssertEqual(suggestion.occurrences, 3)
        XCTAssertEqual(suggestion.examples.count, 2)
        XCTAssertTrue(suggestion.examples[0].hasPrefix("…"))
        XCTAssertTrue(suggestion.examples[0].contains("с гитхабом"))
        XCTAssertEqual(suggestion.examples[1], history[1])
    }

    func testFindsTwoWordRunsAcrossSpacesButNotPunctuation() {
        let history = ["нова грид", "Нова   грид", "нова грид.", "нова, грид", "нова\nгрид"]
        let output = find(history, terms: ["NovaGrid"])
        XCTAssertEqual(output.map(\.from), ["нова грид"])
        XCTAssertEqual(output.map(\.to), ["NovaGrid"])
        XCTAssertEqual(output.map(\.occurrences), [3])
    }

    func testUsesAnAttestedHistorySpellingAndKeepsAmbiguousOrUnattestedForms() {
        let history = Array(repeating: "новагрид конибор воркр", count: 3) + ["NovaGrid.", "NovaGrid!"]
            + ["Worker.", "Worker!", "worker?", "worker"]
        let output = find(history)
        XCTAssertEqual(output.map(\.from), ["воркр", "новагрид"])
        XCTAssertEqual(output.map(\.to), ["Worker", "NovaGrid"])
        XCTAssertTrue(find(Array(repeating: "Novagrd", count: 3), terms: ["NovaGrid", "NovaGrad"]).isEmpty)
        XCTAssertTrue(find(Array(repeating: "новагрид", count: 3) + ["NovaGrid"]).isEmpty)
    }

    func testUsesDictionaryTermsAsTargetsWhenTheCurrentTermCorrectionDoesNotCoverTheForm() {
        let output = find(Array(repeating: "клот", count: 3), dictionary: [.term("Claude")])
        XCTAssertEqual(output.map(\.from), ["клот"])
        XCTAssertEqual(output.map(\.to), ["Claude"])
    }

    func testSkipsFormsTheDictionaryAlreadyCorrectsInContext() {
        let history = Array(repeating: "Посмотри read me перед отправкой.", count: 3)
        let dictionary = [DictionaryItem.term("README")]
        XCTAssertTrue(DictionarySuggestions.find(in: history, dictionary: dictionary, bundledTerms: [], lexicon: .system)
            .filter { $0.to == "README" }.isEmpty)
    }

    func testLeavesOrdinaryWordsAndExistingRulesAlone() {
        XCTAssertTrue(find(Array(repeating: "наш мастер Cloud Code книга", count: 3),
                           terms: ["master", "Claude Code", "Kniga"]).isEmpty)
        XCTAssertTrue(find(Array(repeating: "гитхаб", count: 3), dictionary: [.term("GitHub")]).isEmpty)
        XCTAssertTrue(find(Array(repeating: "с гитхабом", count: 3), terms: ["GitHub"],
                           dictionary: [.replacement(from: "С    Гитхабом", to: "с GitHub")]).isEmpty)
        XCTAssertTrue(find(Array(repeating: "гитхабом", count: 3), terms: ["GitHub"],
                           dictionary: [.replacement(from: "Гитхабом", to: "GitHub")]).isEmpty)
    }

    func testDoesNotSwapVersionsOrLatinWordsForTheirSoundalikes() {
        XCTAssertTrue(find(Array(repeating: "Tool27 Cloud", count: 3), terms: ["Tool28", "Claude"]).isEmpty)
    }

    func testDismissalSurvivesReopeningSettingsAndUsesReplacementNormalization() throws {
        let suite = "DictionarySuggestionsTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let suggestion = try XCTUnwrap(find(Array(repeating: "Нова грид", count: 3), terms: ["NovaGrid"]).first)
        let settings = AppSettings(defaults: defaults)
        XCTAssertTrue(settings.dismissedDictionarySuggestions.isEmpty)
        settings.dismissedDictionarySuggestions.insert(suggestion.id)
        let reopened = AppSettings(defaults: try XCTUnwrap(UserDefaults(suiteName: suite)))
        XCTAssertEqual(reopened.dismissedDictionarySuggestions, ["нова грид"])
    }
}
