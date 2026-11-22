import XCTest
@testable import SlovoCore

final class TextStyleFormatterTests: XCTestCase {
    private let formatter = StyleFormatter()

    func testFormalCapitalizesSentencesAndAddsFinalPeriod() {
        XCTAssertEqual(format("давай созвонимся завтра. в 10 удобно", .formal), "Давай созвонимся завтра. В 10 удобно.")
        XCTAssertEqual(format("готово! проверишь?", .formal), "Готово! Проверишь?")
        XCTAssertEqual(format("список дел:", .formal), "Список дел.")
        XCTAssertEqual(format("он сказал «привет»", .formal), "Он сказал «привет».")
        XCTAssertEqual(format("see you soon.", .formal), "See you soon.")
    }

    func testFormalCapitalizesAfterAnEllipsisInsideQuotes() {
        XCTAssertEqual(format("«Он замолчал…» потом встал.", .formal), "«Он замолчал…» Потом встал.")
    }

    func testFormalKeepsInnerCasingAndLowercaseTerms() {
        XCTAssertEqual(format("macOS открыла окно, GitHub Actions вывел журнал", .formal, terms: ["macOS", "GitHub Actions"]),
                       "macOS открыла окно, GitHub Actions вывел журнал.")
        XCTAssertEqual(format("rebase готов", .formal, terms: ["rebase"]), "Rebase готов.")
        XCTAssertEqual(format("то есть т.е. завтра", .formal), "То есть т.е. завтра.")
    }

    func testCasualDropsOnlyTheLastPeriod() {
        XCTAssertEqual(format("ок. увидимся завтра.", .casual), "Ок. Увидимся завтра")
        XCTAssertEqual(format("Серьёзно?", .casual), "Серьёзно?")
        XCTAssertEqual(format("ну ладно...", .casual), "Ну ладно...")
        XCTAssertEqual(format("то есть т.е.", .casual), "То есть т.е.")
        XCTAssertEqual(format("открой Package.swift.", .casual), "Открой Package.swift")
    }

    func testVeryCasualLowercasesSentenceStartsAndDropsPeriods() {
        XCTAssertEqual(format("Музей открылся. Заглянем туда завтра.", .veryCasual), "музей открылся заглянем туда завтра")
        XCTAssertEqual(format("Готово! Проверишь?", .veryCasual), "готово! проверишь?")
        XCTAssertEqual(format("Sounds good. See you tomorrow.", .veryCasual), "sounds good see you tomorrow")
        XCTAssertEqual(format("Скажи «Привет.» Ладно", .veryCasual), "скажи «Привет» ладно")
    }

    func testVeryCasualKeepsTermsAndNames() {
        let text = "Docker готов. API доступен. Маша сказала, что Маша проверит. User id в примере. Telegram открыт. I think so."
        XCTAssertEqual(format(text, .veryCasual, terms: ["Docker", "user id"]),
                       "Docker готов API доступен Маша сказала, что Маша проверит user id в примере Telegram открыт I think so")
        XCTAssertEqual(format("GitHub упал.", .veryCasual), "GitHub упал")
    }

    func testTextWithoutWordsIsUntouched() {
        for style in StylePreset.allCases {
            XCTAssertEqual(format("", style), "")
            XCTAssertEqual(format(" … ", style), " … ")
        }
    }

    func testStageUsesTheContextStyle() {
        var result = TextPipelineResult(text: "привет")
        formatter.apply(to: &result, context: TextPipelineContext())
        XCTAssertEqual(result.text, "привет")
        formatter.apply(to: &result, context: TextPipelineContext(style: .formal))
        XCTAssertEqual(result.text, "Привет.")
    }

    func testReplacementTargetsKeepTheirCasingInVeryCasual() {
        var result = TextPipelineResult(text: "Slovo готово.")
        let context = TextPipelineContext(dictionary: [.replacement(from: "слово", to: "Slovo")], style: .veryCasual)
        formatter.apply(to: &result, context: context)
        XCTAssertEqual(result.text, "Slovo готово")
    }

    private func format(_ text: String, _ style: StylePreset, terms: [String] = []) -> String {
        formatter.format(text, style: style, protectedTerms: terms)
    }
}
