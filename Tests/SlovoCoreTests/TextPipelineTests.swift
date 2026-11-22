import XCTest
@testable import SlovoCore

final class TextPipelineTests: XCTestCase {
    private let pipeline = TextPipeline()

    private let dictionary: [DictionaryItem] = ["Kubernetes", "GitHub Actions", "Docker", "Postgres", "macOS"]
        .map { DictionaryItem.term($0) } + [.replacement(from: "слово", to: "Slovo")]

    func testStandardStagesRunInTheSpecOrder() {
        let names = TextPipeline.standardStages.map { String(describing: type(of: $0)) }
        XCTAssertEqual(names, ["SnippetExpander", "DictionaryCorrector", "FillerCleaner", "StyleFormatter"])
    }

    func testRussianDictationGoesThroughEveryStage() {
        let context = TextPipelineContext(dictionary: dictionary, style: .formal)
        let result = pipeline.process("э, пример на кибернетес готов, открой отчёт в гитхаб экшенс", context: context)
        XCTAssertEqual(result.text, "Пример на Kubernetes готов, открой отчёт в GitHub Actions.")
        XCTAssertEqual(result.termFixesApplied, 2)
        XCTAssertEqual(result.replacementsApplied, 0)
        XCTAssertEqual(result.fixesCount, 2)
    }

    func testEnglishDictation() {
        let context = TextPipelineContext(dictionary: dictionary, style: .casual, languageMode: .english)
        let result = pipeline.process("um, so the the doker image is ready.", context: context)
        XCTAssertEqual(result.text, "So the Docker image is ready")
        XCTAssertEqual(result.fixesCount, 1)
    }

    func testMixedDictationInVeryCasual() {
        let context = TextPipelineContext(dictionary: dictionary, style: .veryCasual)
        let result = pipeline.process("Запусти докер контейнер с постгресс. Потом обнови макос.", context: context)
        XCTAssertEqual(result.text, "запусти Docker контейнер с Postgres потом обнови macOS")
        XCTAssertEqual(result.termFixesApplied, 3)
    }

    func testReplacementsCountAsFixes() {
        let result = pipeline.process("Открой слово и проверь", context: TextPipelineContext(dictionary: dictionary))
        XCTAssertEqual(result.text, "Открой Slovo и проверь")
        XCTAssertEqual(result.replacementsApplied, 1)
        XCTAssertEqual(result.fixesCount, 1)
    }

    func testSnippetExpansionIsLeftAloneByLaterStages() {
        let snippet = Snippet(trigger: "подпись", expansion: "с уважением, докер-команда\nmacos.dev")
        let context = TextPipelineContext(dictionary: dictionary, snippets: [snippet], style: .formal)
        let result = pipeline.process("спасибо, э, подпись", context: context)
        XCTAssertEqual(result.text, "Спасибо, с уважением, докер-команда\nmacos.dev")
        XCTAssertEqual(result.snippetsApplied, 1)
        XCTAssertEqual(result.fixesCount, 0)
    }

    func testTextAroundAnExpansionIsStillProcessed() {
        let snippet = Snippet(trigger: "мой адрес", expansion: "ул. Примерная, 1")
        let context = TextPipelineContext(dictionary: dictionary, snippets: [snippet], style: .casual)
        let result = pipeline.process("отправь докер мой адрес и и кибернетес.", context: context)
        XCTAssertEqual(result.text, "Отправь Docker ул. Примерная, 1 и Kubernetes")
        XCTAssertEqual(result.snippetsApplied, 1)
        XCTAssertEqual(result.termFixesApplied, 2)
    }

    func testWithoutStyleOrCleanupTheEngineTextStays() {
        let context = TextPipelineContext(dictionary: dictionary, cleanup: .off)
        let result = pipeline.process("ну э кибернетес ,да", context: context)
        XCTAssertEqual(result.text, "ну э Kubernetes ,да")
        XCTAssertEqual(result.fixesCount, 1)
    }

    func testEmptyDictationStaysEmpty() {
        let context = TextPipelineContext(dictionary: dictionary, style: .formal)
        XCTAssertEqual(pipeline.process("", context: context), TextPipelineResult(text: ""))
        XCTAssertEqual(pipeline.process("э, ммм", context: context).text, "")
    }
}
