import XCTest
@testable import SlovoCore

/// Sentence case must not break links, addresses, file names or brand casing, and
/// Formal ends every text with a period.
final class FixTextStyleTests: XCTestCase {
    private let formatter = StyleFormatter()

    func testLinksAddressesAndInnerCapitalsKeepTheirCase() {
        XCTAssertEqual(format("https://github.com/foo это ссылка", .casual), "https://github.com/foo это ссылка")
        XCTAssertEqual(format("ivan@example.com мой адрес", .casual), "ivan@example.com мой адрес")
        XCTAssertEqual(format("iPhone сломался.", .casual), "iPhone сломался")
        XCTAssertEqual(format("готово. eBay и iPad на месте", .formal), "Готово. eBay и iPad на месте.")
        XCTAssertEqual(format("src/main.swift открыт", .formal), "src/main.swift открыт.")
    }

    func testFileAndDomainNamesKeepTheirCase() {
        XCTAssertEqual(format("github.com это сайт", .casual), "github.com это сайт")
        XCTAssertEqual(format("готово. index.html лежит в корне", .formal), "Готово. index.html лежит в корне.")
        XCTAssertEqual(format("т.е. завтра", .formal), "Т.е. завтра.")
    }

    func testLowercaseTermsKeepTheirCaseAtSentenceStart() {
        XCTAssertEqual(format("npm install и всё", .formal, terms: ["npm"]), "npm install и всё.")
        XCTAssertEqual(format("npm install и всё", .casual, terms: ["npm"]), "npm install и всё")
        let context = TextPipelineContext(dictionary: DictionarySeed.bundledTerms().map { DictionaryItem.term($0) },
                                          style: .formal)
        XCTAssertEqual(TextPipeline().process("npm install и всё", context: context).text, "npm install и всё.")
    }

    func testFormalAddsAPeriodAfterPercentAndPlus() {
        XCTAssertEqual(format("рост на 5%", .formal), "Рост на 5%.")
        XCTAssertEqual(format("используй C++", .formal), "Используй C++.")
    }

    func testEmoticonAtTheEndIsNotAColon() {
        XCTAssertEqual(format("смайлик :)", .formal), "Смайлик :)")
        XCTAssertEqual(format("смайлик :)", .casual), "Смайлик :)")
        XCTAssertEqual(format("список дел:", .formal), "Список дел.")
    }

    private func format(_ text: String, _ style: StylePreset, terms: [String] = []) -> String {
        formatter.format(text, style: style, protectedTerms: terms)
    }
}
