import XCTest
@testable import SlovoCore

/// Auto cleanup removes fillers and stutters only; code punctuation, dotfiles, emoticons
/// and deliberate repeats stay.
final class FixTextCleanupTests: XCTestCase {
    private let cleaner = FillerCleaner()

    func testCodePunctuationAndDotfilesStay() {
        let cases = [
            "вызови функцию foo() и всё", "верни пустой массив [] из функции",
            "Добавь в .gitignore папку build", "создай файл .env и положи туда ключ",
            "вызови init() и map {} потом"
        ]
        for text in cases {
            XCTAssertEqual(cleaner.clean(text, options: .default), text)
        }
    }

    func testBracketsThatHeldOnlyAFillerGo() {
        XCTAssertEqual(cleaner.clean("Скажи (э) привет", options: .default), "Скажи привет")
        XCTAssertEqual(cleaner.clean("Erm (um) okay", options: .default), "Okay")
        XCTAssertEqual(cleaner.clean("вызови foo() [эм] и всё", options: .default), "вызови foo() и всё")
    }

    func testSentencePeriodsStillCloseUp() {
        XCTAssertEqual(cleaner.clean("Готово . Файл .env готов .", options: .default), "Готово. Файл .env готов.")
    }

    func testEmoticonsKeepTheirSpace() {
        for text in ["Смайлик :) и всё", "Ок ;) пока", "Грустно :( очень", "Супер :D да", "Смайлик :-)"] {
            XCTAssertEqual(cleaner.clean(text, options: .default), text)
        }
        XCTAssertEqual(cleaner.clean("Список дел : купить хлеб", options: .default), "Список дел: купить хлеб")
        XCTAssertEqual(cleaner.clean("Встреча в 10:30 ( или позже )", options: .default), "Встреча в 10:30 (или позже)")
    }

    func testOnlyWhitespaceSeparatedRepeatsCollapse() {
        XCTAssertEqual(cleaner.clean("Нет, нет, нет!", options: .default), "Нет, нет, нет!")
        XCTAssertEqual(cleaner.clean("Да — да, конечно", options: .default), "Да — да, конечно")
        XCTAssertEqual(cleaner.clean("я я думаю", options: .default), "я думаю")
        XCTAssertEqual(cleaner.clean("the the plan", options: .default), "the plan")
    }

    func testGrammaticalEnglishDoublesStay() {
        XCTAssertEqual(cleaner.clean("I had had enough.", options: .default), "I had had enough.")
        XCTAssertEqual(cleaner.clean("I know that that is true.", options: .default), "I know that that is true.")
    }
}
