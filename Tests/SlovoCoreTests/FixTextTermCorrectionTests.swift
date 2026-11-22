import AppKit
import XCTest
@testable import SlovoCore

/// Term correction must leave real Russian and English words alone, keep the user's
/// explicit replacements, and still turn transliterated terms into their spelling.
final class FixTextTermCorrectionTests: XCTestCase {
    private let corrector = DictionaryCorrector()
    private let vocabulary = DictionarySeed.bundledTerms()

    override func setUpWithError() throws {
        XCTAssertFalse(vocabulary.isEmpty, "the Dictionary seed is missing")
        let languages = Set(NSSpellChecker.shared.availableLanguages)
        try XCTSkipUnless(languages.contains("ru") && languages.contains("en"),
                          "The system spell checker has no Russian or English dictionary")
    }

    // MARK: - Russian words

    func testOrdinaryRussianSentencesStay() {
        let sentences = [
            "Я кладу ключи на стол.", "Мы нашли клад в саду.", "Радиус круга равен пяти.", "Купи редис на рынке.",
            "Отправь телеграмму бабушке.", "Телеграф работает.", "Его друг кубинец.", "Мистер Смит пришёл.",
            "Это ребус.", "Норберт Винер был кибернетик.", "Макс, привет!", "Мы были в Версале."
        ]
        for sentence in sentences {
            let result = corrector.correctTerms(sentence, terms: vocabulary)
            XCTAssertEqual(result.text, sentence)
            XCTAssertEqual(result.count, 0, sentence)
        }
    }

    func testOrdinaryRussianWordFormsStay() {
        let words = ["кладу", "клад", "клады", "кладёт", "радиус", "радиусы", "редис", "редиса", "редиской",
                     "телеграмма", "телеграмму", "телеграммы", "телеграф", "кубинец", "кубинцы", "кибернетик",
                     "кибернетика", "мистер", "ребус", "ребусы", "макс"]
        for word in words {
            XCTAssertEqual(corrector.correctTerms(word, terms: vocabulary).text, word, word)
        }
    }

    func testTransliteratedTermsAreStillCorrected() {
        let cases = [
            ("кибернетес", "Kubernetes"), ("Гитхаб экшенс", "GitHub Actions"), ("юзер Айди", "user id"),
            ("постгресс", "Postgres"), ("докер", "Docker"), ("User Defolts", "UserDefaults"),
            ("Запусти докер контейнер", "Запусти Docker контейнер"),
            ("Задиплой фронт энд на верселе", "Задиплой фронт энд на Vercel"),
            ("Настрой Эсленд и Преттер", "Настрой ESLint и Prettier"),
            ("Нужно сравнить Гигам и Виспер", "Нужно сравнить GigaAM и Whisper"),
            ("Повесь запись на правый опшен", "Повесь запись на правый Option"),
            ("Скинь телеграм ссылку", "Скинь Telegram ссылку"),
            ("Закашируй ответы в Редис", "Закашируй ответы в Redis"),
            ("Обнови минимальную версию макоз", "Обнови минимальную версию macOS")
        ]
        for (spoken, expected) in cases {
            XCTAssertEqual(corrector.correctTerms(spoken, terms: vocabulary).text, expected, spoken)
        }
    }

    // MARK: - English words

    func testCommonEnglishWordsKeepTheirSpelling() {
        let sentences = ["Choose an option.", "Speak in a whisper.", "Make it prettier.", "A telegram arrived.",
                         "Master of Science.", "Jason is here.", "The cloud is down.", "Measure the radius.",
                         "Rebase the branch.", "A case in point.", "Read me a story."]
        for sentence in sentences {
            let result = corrector.correctTerms(sentence, terms: vocabulary)
            XCTAssertEqual(result.text, sentence)
            XCTAssertEqual(result.count, 0, sentence)
        }
    }

    func testNamesThatAreNotEnglishWordsGetTheirCasing() {
        let cases = [("restart redis", "restart Redis", 1), ("update macos", "update macOS", 1),
                     ("connect airpods", "connect AirPods", 1), ("send json", "send JSON", 1), ("run npm", "run npm", 0)]
        for (text, expected, count) in cases {
            let result = corrector.correctTerms(text, terms: vocabulary)
            XCTAssertEqual(result.text, expected, text)
            XCTAssertEqual(result.count, count, text)
        }
    }

    /// Engines write English names inside Russian speech in Latin letters, often split
    /// into dictionary words or with sentence casing.
    func testEnglishWordsInsideRussianSentencesStillBecomeTerms() {
        let cases = [
            ("Скинь telegram ссылку", "Скинь Telegram ссылку"),
            ("Скинь ссылку на новый билд в Test Flit", "Скинь ссылку на новый билд в TestFlight"),
            ("Этот in point отвечает ошибкой", "Этот endpoint отвечает ошибкой"),
            ("Добавь логирование через OS Loc.", "Добавь логирование через OSLog."),
            ("Этот апи возвращает Джин с пустым полем User Id.", "Этот апи возвращает Джин с пустым полем user id.")
        ]
        for (spoken, expected) in cases {
            XCTAssertEqual(corrector.correctTerms(spoken, terms: vocabulary).text, expected, spoken)
        }
        // A single real English word is still not swapped for a different term.
        for text in ["Задеплой всё в cloud", "Он получил Master of Science"] {
            XCTAssertEqual(corrector.correctTerms(text, terms: vocabulary).text, text, text)
        }
    }

    // MARK: - Replacements

    func testExplicitReplacementOutputIsNotRewrittenByTerms() {
        let items: [DictionaryItem] = [.replacement(from: "клауд", to: "cloud"), .replacement(from: "мистер", to: "Mister")]
            + vocabulary.map { DictionaryItem.term($0) }
        var result = TextPipelineResult(text: "положи в клауд, мистер Смит")
        DictionaryCorrector().apply(to: &result, context: TextPipelineContext(dictionary: items, cleanup: .off))
        XCTAssertEqual(result.text, "положи в cloud, Mister Смит")
        XCTAssertEqual(result.replacementsApplied, 2)
        XCTAssertEqual(result.termFixesApplied, 0)
    }

    func testTextAroundAReplacementIsStillCorrected() {
        let items: [DictionaryItem] = [.replacement(from: "клауд", to: "cloud"), .term("Docker")]
        var result = TextPipelineResult(text: "докер в клауд")
        DictionaryCorrector().apply(to: &result, context: TextPipelineContext(dictionary: items, cleanup: .off))
        XCTAssertEqual(result.text, "Docker в cloud")
        XCTAssertEqual(result.fixesCount, 2)
    }

    func testSpokenNamesBecomeTheirTermsInAnyCase() {
        let items: [DictionaryItem] = [.term("Claude"), .term("API")]
        for (spoken, written) in [
            ("спроси у клод что делать", "спроси у Claude что делать"),
            ("поговорил с Клодом вчера", "поговорил с Claude вчера"),
            ("у клода есть доступ", "у Claude есть доступ"),
            ("отправь через апи.", "отправь через API."),
        ] {
            var result = TextPipelineResult(text: spoken)
            DictionaryCorrector().apply(to: &result, context: TextPipelineContext(dictionary: items, cleanup: .off))
            XCTAssertEqual(result.text, written, spoken)
            XCTAssertEqual(result.termFixesApplied, 1, spoken)
        }
    }

    func testSpokenNamesNeedTheirTermAndAWholeWord() {
        var result = TextPipelineResult(text: "спроси у клод через апили")
        DictionaryCorrector().apply(to: &result, context: TextPipelineContext(dictionary: [.term("API")], cleanup: .off))
        XCTAssertEqual(result.text, "спроси у клод через апили")
    }
}
