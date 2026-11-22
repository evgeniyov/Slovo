import XCTest
@testable import SlovoCore

final class TextSnippetExpanderTests: XCTestCase {
    private let expander = SnippetExpander()
    private let address = Snippet(trigger: "мой адрес", expansion: "ул. Примерная, 1")

    func testWholeDictationIsReplacedWithoutEnginePunctuation() {
        let result = expander.expand("Мой адрес.", snippets: [address])
        XCTAssertEqual(result.text, "ул. Примерная, 1")
        XCTAssertEqual(result.count, 1)
    }

    func testTriggerInsideDictationKeepsSurroundingText() {
        let result = expander.expand("Отправь мой адрес, пожалуйста.", snippets: [address])
        XCTAssertEqual(result.text, "Отправь ул. Примерная, 1, пожалуйста.")
        XCTAssertEqual(result.count, 1)
    }

    func testMatchingIgnoresCasePunctuationAndYo() {
        let snippet = Snippet(trigger: "Всё, готово!", expansion: "Готово ✅")
        XCTAssertEqual(expander.expand("все готово", snippets: [snippet]).text, "Готово ✅")
        XCTAssertEqual(expander.expand("Короче, ВСЕ — готово, иду", snippets: [snippet]).text, "Короче, Готово ✅, иду")
    }

    func testEveryOccurrenceIsCounted() {
        let snippet = Snippet(trigger: "my email", expansion: "me@example.com")
        let result = expander.expand("My email, and again my email", snippets: [snippet])
        XCTAssertEqual(result.text, "me@example.com, and again me@example.com")
        XCTAssertEqual(result.count, 2)
    }

    func testLongerTriggerWins() {
        let short = Snippet(trigger: "адрес", expansion: "A")
        let long = Snippet(trigger: "рабочий адрес", expansion: "B")
        XCTAssertEqual(expander.expand("мой рабочий адрес и адрес", snippets: [short, long]).text, "мой B и A")
    }

    func testWholeWordsOnly() {
        let snippet = Snippet(trigger: "кот", expansion: "🐈")
        XCTAssertEqual(expander.expand("котик и кот", snippets: [snippet]).text, "котик и 🐈")
    }

    func testNothingToExpand() {
        XCTAssertEqual(expander.expand("текст", snippets: [Snippet(trigger: " , ", expansion: "x")]).count, 0)
        XCTAssertEqual(expander.expand("текст", snippets: []).text, "текст")
        XCTAssertEqual(expander.expand("", snippets: [address]).text, "")
    }

    func testStageAddsToTheCounter() {
        var result = TextPipelineResult(text: "мой адрес и мой адрес", snippetsApplied: 1)
        expander.apply(to: &result, context: TextPipelineContext(snippets: [address]))
        XCTAssertEqual(result.text, "ул. Примерная, 1 и ул. Примерная, 1")
        XCTAssertEqual(result.snippetsApplied, 3)
        XCTAssertEqual(result.fixesCount, 0)
    }
}
