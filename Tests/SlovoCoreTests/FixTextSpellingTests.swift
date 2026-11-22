import os
import XCTest
@testable import SlovoCore

/// The word lexicon behind term correction: the system spell checker's answers, and
/// what correction does when a dictionary is missing.
final class FixTextSpellingTests: XCTestCase {
    private let vocabulary = DictionarySeed.bundledTerms()

    func testSystemSpellingAnswersTheSameFromManyThreads() throws {
        try XCTSkipUnless(SystemSpelling.hasLanguage("ru") && SystemSpelling.hasLanguage("en"),
                          "The system spell checker has no Russian or English dictionary")
        let words: [(word: String, language: String, correct: Bool)] = [
            ("дом", "ru", true), ("окно", "ru", true), ("кладу", "ru", true), ("редиской", "ru", true),
            ("кибернетис", "ru", false), ("постгресс", "ru", false), ("эсленд", "ru", false), ("опшен", "ru", false),
            ("whisper", "en", true), ("window", "en", true), ("kubernetes", "en", false), ("defolts", "en", false)
        ]
        let wrong = OSAllocatedUnfairLock(initialState: [String]())
        DispatchQueue.concurrentPerform(iterations: 96) { iteration in
            let item = words[iteration % words.count]
            if SystemSpelling.isCorrect(item.word, language: item.language) != item.correct {
                wrong.withLock { $0.append(item.word) }
            }
        }
        XCTAssertEqual(wrong.withLock { $0 }, [])
        XCTAssertFalse(SystemSpelling.hasLanguage("xx"))
        XCTAssertFalse(SystemSpelling.isCorrect("дом", language: "xx"))
        XCTAssertFalse(SystemSpelling.isCorrect("", language: "ru"))
    }

    func testWithoutDictionariesShortNearWordsStayAndSpecExamplesStillWork() {
        let corrector = DictionaryCorrector(lexicon: .missing)
        for text in ["кладу", "клад", "радиус", "ребус", "The cloud is down.", "Measure the radius.", "Choose an option."] {
            XCTAssertEqual(corrector.correctTerms(text, terms: vocabulary).text, text, text)
        }
        let cases = [("кибернетес", "Kubernetes"), ("Гитхаб экшенс", "GitHub Actions"), ("юзер Айди", "user id"),
                     ("постгресс", "Postgres"), ("докер", "Docker"), ("User Defolts", "UserDefaults"),
                     ("send json", "send JSON")]
        for (spoken, expected) in cases {
            XCTAssertEqual(corrector.correctTerms(spoken, terms: vocabulary).text, expected, spoken)
        }
    }

    func testTheLexiconDecidesWhatIsARussianWord() {
        let known = WordLexicon(hasLanguage: { _ in true }, isCorrect: { word, language in
            language == "ru" ? ["кибернетес", "редис", "Редис", "докер"].contains(word) : word == "docker"
        })
        let corrector = DictionaryCorrector(lexicon: known)
        XCTAssertEqual(corrector.correctTerms("кибернетес", terms: ["Kubernetes"]).text, "кибернетес")
        // Sounds exactly like Redis, which is not an English word: only a name in mid-sentence is the term.
        XCTAssertEqual(corrector.correctTerms("купи редис", terms: ["Redis"]).text, "купи редис")
        XCTAssertEqual(corrector.correctTerms("положи в Редис", terms: ["Redis"]).text, "положи в Redis")
        XCTAssertEqual(corrector.correctTerms("Редис упал", terms: ["Redis"]).text, "Редис упал")
        // Docker is an English word, so «докер» is that word borrowed.
        XCTAssertEqual(corrector.correctTerms("запусти докер", terms: ["Docker"]).text, "запусти Docker")
    }
}

extension WordLexicon {
    /// A system with no spelling dictionaries at all.
    static let missing = WordLexicon(hasLanguage: { _ in false }, isCorrect: { _, _ in false })
}
