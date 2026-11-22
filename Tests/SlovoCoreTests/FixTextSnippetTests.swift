import XCTest
@testable import SlovoCore

/// Snippet triggers match whole words only, hyphenated compounds included, and an
/// expansion's own final mark is not doubled by the engine's.
final class FixTextSnippetTests: XCTestCase {
    private let expander = SnippetExpander()

    func testTriggerDoesNotMatchInsideHyphenatedWords() {
        let address = Snippet(trigger: "адрес", expansion: "Примерск, Примерная 1")
        XCTAssertEqual(expander.expand("IP-адрес сервера", snippets: [address]).text, "IP-адрес сервера")
        XCTAssertEqual(expander.expand("IP-адрес сервера", snippets: [address]).count, 0)
        let test = Snippet(trigger: "тест", expansion: "TEST-EXP")
        XCTAssertEqual(expander.expand("Запусти тест и бета-тест", snippets: [test]).text, "Запусти TEST-EXP и бета-тест")
    }

    func testHyphenatedTriggerStillMatches() {
        let snippet = Snippet(trigger: "кое-что важное", expansion: "!!")
        XCTAssertEqual(expander.expand("скажи кое-что важное сейчас", snippets: [snippet]).text, "скажи !! сейчас")
    }

    func testFinalMarkIsNotDoubled() {
        let signature = Snippet(trigger: "подпись", expansion: "с уважением, иван.")
        XCTAssertEqual(expander.expand("подпись. спасибо", snippets: [signature]).text, "с уважением, иван. спасибо")
        let question = Snippet(trigger: "как дела", expansion: "Как у тебя дела?")
        XCTAssertEqual(expander.expand("Привет, как дела? Жду", snippets: [question]).text, "Привет, Как у тебя дела? Жду")

        let context = TextPipelineContext(snippets: [signature], style: .formal)
        let result = TextPipeline().process("подпись. спасибо", context: context)
        XCTAssertFalse(result.text.contains(".."), result.text)
        XCTAssertEqual(result.text, "с уважением, иван. Спасибо.")
    }

    func testDifferentMarkAfterTheExpansionStays() {
        let signature = Snippet(trigger: "подпись", expansion: "иван.")
        XCTAssertEqual(expander.expand("подпись, спасибо", snippets: [signature]).text, "иван., спасибо")
    }
}
