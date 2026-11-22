import XCTest
@testable import SlovoCore

final class TextFillerCleanerTests: XCTestCase {
    private let cleaner = FillerCleaner()

    func testRussianFillersGoWithTheirCommas() {
        XCTAssertEqual(cleaner.clean("Я э проверю, горит ли ммм лампа", options: .default), "Я проверю, горит ли лампа")
        XCTAssertEqual(cleaner.clean("Спасибо, э, до встречи", options: .default), "Спасибо, до встречи")
        XCTAssertEqual(cleaner.clean("Э, давай начнём. Ээ, итак", options: .default), "Давай начнём. Итак")
        XCTAssertEqual(cleaner.clean("Ээээ, эмм, ну ладно", options: .default), "Ну ладно")
        XCTAssertEqual(cleaner.clean("Ну, э-э... давай", options: .default), "Ну... давай")
        XCTAssertEqual(cleaner.clean("Давай, э.", options: .default), "Давай.")
    }

    func testEnglishFillersIncludingDrawnOutOnes() {
        XCTAssertEqual(cleaner.clean("Um, I think uhh the lamp is ready, hmm.", options: .default), "I think the lamp is ready.")
        XCTAssertEqual(cleaner.clean("Sure, uh, sounds good", options: .default), "Sure, sounds good")
        XCTAssertEqual(cleaner.clean("Erm (um) okay", options: .default), "Okay")
    }

    func testWordsThatOnlyLookLikeFillersStay() {
        XCTAssertEqual(cleaner.clean("5 м от дома, мм, hm, umbrella, ум", options: .default), "5 м от дома, мм, hm, umbrella, ум")
    }

    func testImmediateRepetitionsCollapse() {
        XCTAssertEqual(cleaner.clean("Я я думаю думаю что в общем в общем да", options: .default), "Я думаю что в общем да")
        // Repeats set off by punctuation are emphasis, not a stutter.
        XCTAssertEqual(cleaner.clean("Я думаю, думаю", options: .default), "Я думаю, думаю")
        XCTAssertEqual(cleaner.clean("the the the plan", options: .default), "the plan")
    }

    func testRepetitionsAcrossSentencesAndNumbersStay() {
        XCTAssertEqual(cleaner.clean("Нет. Нет, так нельзя", options: .default), "Нет. Нет, так нельзя")
        XCTAssertEqual(cleaner.clean("Build 2 2 times", options: .default), "Build 2 2 times")
        XCTAssertEqual(cleaner.clean("да\nда", options: .default), "да\nда")
    }

    func testOptionsAreIndependent() {
        let text = "Ну э ну ну ладно"
        XCTAssertEqual(cleaner.clean(text, options: AutoCleanupOptions(removeFillers: true, removeRepetitions: false)),
                       "Ну ну ну ладно")
        XCTAssertEqual(cleaner.clean(text, options: AutoCleanupOptions(removeFillers: false, removeRepetitions: true)),
                       "Ну э ну ладно")
        XCTAssertEqual(cleaner.clean(text, options: .default), "Ну ладно")
        XCTAssertEqual(cleaner.clean("  так ,  э  ", options: .off), "  так ,  э  ")
    }

    func testSpacingAroundPunctuation() {
        XCTAssertEqual(cleaner.clean("  Привет ,мир !  Как дела ?Файл Package.swift ( черновик )  готов", options: .default),
                       "Привет, мир! Как дела? Файл Package.swift (черновик) готов")
        XCTAssertEqual(cleaner.clean("Цена 1,5 , время 10:30 ,,  ок ,.", options: .default), "Цена 1,5, время 10:30, ок.")
        XCTAssertEqual(cleaner.clean("строка  \n  вторая", options: .default), "строка\nвторая")
    }

    func testSplitEllipsisBoundariesBecomeOneEllipsis() {
        XCTAssertEqual(cleaner.clean("Смотрю. ...дальше. …ещё", options: .default), "Смотрю... дальше... ещё")
        XCTAssertEqual(cleaner.clean("Готово. ...", options: .default), "Готово...")
        XCTAssertEqual(cleaner.clean(#"Готово. ..."нет""#, options: .default), #"Готово... "нет""#)
        XCTAssertEqual(cleaner.clean("Сказал. ...`value`", options: .default), "Сказал... `value`")
        XCTAssertEqual(cleaner.clean("Сказал. ...«нет»", options: .default), "Сказал... «нет»")
        XCTAssertEqual(cleaner.clean("Сказал. …(тише)", options: .default), "Сказал... (тише)")
        XCTAssertEqual(cleaner.clean("Готово. ...— нет", options: .default), "Готово... — нет")
        let input = "Экскурсию перенесли. ...по этой причине."
        XCTAssertEqual(TextPipeline().process(input, context: .init(style: .formal)).text,
                       "Экскурсию перенесли... По этой причине.")
        let inlineCode = "Смотри «`'a. ...b'`»."
        XCTAssertEqual(cleaner.clean(inlineCode, options: .default), inlineCode)
        XCTAssertEqual(cleaner.clean("Версия 2.1. Массив 1...3. Уже... здесь.", options: .default),
                       "Версия 2.1. Массив 1...3. Уже... здесь.")
        let snippet = Snippet(trigger: "пример кода", expansion: #""a. ...b""#)
        let context = TextPipelineContext(snippets: [snippet])
        XCTAssertEqual(TextPipeline().process("Выведи пример кода", context: context).text, #"Выведи "a. ...b""#)
    }

    func testDetachedHesitationsGoWithTheirPunctuation() {
        XCTAssertEqual(cleaner.clean("А-а. Давай, а-а-а, обсудим. М-м. Готово.", options: .default),
                       "Давай, обсудим. Готово.")
    }

    func testParenthesizedAndBracketedWordsStay() {
        for text in ["Обозначение (А-А) на схеме.", "Обозначение [А-А] на схеме.", "`f(а-а)`"] {
            XCTAssertEqual(cleaner.clean(text, options: .default), text)
        }
    }

    func testInterjectionsAndHesitationsWithinPhrasesStay() {
        let text = "А-а, понял. А-а! Это м-м-м вкусно. Он сказал а-а-а протяжно."
        XCTAssertEqual(cleaner.clean(text, options: .default), text)
        let repetitionsOnly = AutoCleanupOptions(removeFillers: false, removeRepetitions: true)
        XCTAssertEqual(cleaner.clean("А-а. Дальше.", options: repetitionsOnly), "А-а. Дальше.")
    }

    func testFillerListsMatchTheSpec() {
        XCTAssertEqual(FillerCleaner.russianFillers, ["э", "ээ", "эм", "ммм"])
        XCTAssertEqual(FillerCleaner.englishFillers, ["um", "uh", "erm", "hmm"])
    }
}
