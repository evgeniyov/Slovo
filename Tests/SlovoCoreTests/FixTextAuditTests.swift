import AppKit
import XCTest
@testable import SlovoCore

/// Fixes from the text pipeline audit: ordinary Russian words that sound like English
/// terms, abbreviations written with a space, fillers the engine wrote as sentences,
/// links in spacing cleanup, snippet trigger punctuation, and the injectable lexicon.
final class FixTextAuditTests: XCTestCase {
    private let vocabulary = DictionarySeed.bundledTerms()

    // MARK: - Real Russian words and English terms

    func testMasterIsNotInTheSeedVocabulary() {
        XCTAssertFalse(vocabulary.contains { $0.lowercased() == "master" })
        XCTAssertTrue(vocabulary.contains("Telegram"))
    }

    func testOrdinaryWordsThatSoundLikeTermsStayWithTheSystemSpellChecker() throws {
        let languages = Set(NSSpellChecker.shared.availableLanguages)
        try XCTSkipUnless(languages.contains("ru") && languages.contains("en"),
                          "The system spell checker has no Russian or English dictionary")
        let terms = vocabulary + ["master"]
        let corrector = DictionaryCorrector()
        for sentence in ["Пришёл мастер и починил кран", "Мастер сказал, что всё готово", "Роман «Мастер и Маргарита» прочитал",
                         "Нет новых телеграмм", "Мастер спорта"] {
            let result = corrector.correctTerms(sentence, terms: terms)
            XCTAssertEqual(result.text, sentence)
            XCTAssertEqual(result.count, 0, sentence)
        }
        XCTAssertEqual(corrector.correctTerms("Скинь телеграм ссылку", terms: vocabulary).text, "Скинь Telegram ссылку")
        XCTAssertEqual(corrector.correctTerms("Запусти докер", terms: vocabulary).text, "Запусти Docker")
    }

    func testARealWordNeedsABorrowedNameOrANameInMidSentence() {
        let lexicon = WordLexicon.fixed([
            "ru": ["мастер", "телеграмм", "телеграм", "докер", "редис", "руби", "гигам", "и", "в", "пришёл", "нет", "новых"],
            "en": ["master", "telegram", "docker", "ruby"]
        ])
        let corrector = DictionaryCorrector(lexicon: lexicon)
        let cases: [(String, [String], String)] = [
            ("пришёл мастер", ["master"], "пришёл мастер"),
            ("роман «Мастер и Маргарита»", ["master"], "роман «Мастер и Маргарита»"),
            ("нет новых телеграмм", ["Telegram"], "нет новых телеграмм"),
            ("в телеграм", ["Telegram"], "в Telegram"),
            ("запусти докер", ["Docker"], "запусти Docker"),
            // Ruby is an English word and «руби» is not on the borrowed list: it stays.
            ("руби дрова", ["Ruby"], "руби дрова"),
            ("положи в Редис", ["Redis"], "положи в Redis"),
            ("купи редис", ["Redis"], "купи редис"),
            // A doubled letter in the term does not block a name: «Гигам» is GigaAM.
            ("сравни Гигам и Whisper", ["GigaAM"], "сравни GigaAM и Whisper")
        ]
        for (text, terms, expected) in cases {
            XCTAssertEqual(corrector.correctTerms(text, terms: terms).text, expected, text)
        }
    }

    // MARK: - Abbreviations

    func testSpacedAbbreviationsDoNotStartSentences() {
        let formatter = StyleFormatter(lexicon: .fixed([:]))
        let formal: [(String, String)] = [
            ("приходи завтра, т. е. в среду", "Приходи завтра, т. е. в среду."),
            ("купи хлеб, молоко и т. д. потом приходи", "Купи хлеб, молоко и т. д. потом приходи."),
            ("фрукты и т. п. бери всё", "Фрукты и т. п. бери всё."),
            ("в 2025 г. мы запустили продукт", "В 2025 г. мы запустили продукт."),
            ("встретимся в 5 ч. вечера", "Встретимся в 5 ч. вечера."),
            ("цена 1 000,50 руб. за штуку", "Цена 1 000,50 руб. за штуку."),
            ("Apple Inc. announced it", "Apple Inc. announced it."),
            // The engine's own capital still starts a sentence.
            ("купи хлеб и т. д. Потом обсудим", "Купи хлеб и т. д. Потом обсудим."),
            ("давай созвонимся завтра. в 10 удобно", "Давай созвонимся завтра. В 10 удобно."),
            ("ок. увидимся", "Ок. Увидимся.")
        ]
        for (text, expected) in formal {
            XCTAssertEqual(formatter.format(text, style: .formal, protectedTerms: []), expected, text)
        }
    }

    func testAbbreviationPeriodsAreNotDropped() {
        let formatter = StyleFormatter(lexicon: .fixed([:]))
        XCTAssertEqual(formatter.format("купи хлеб и т. д.", style: .casual, protectedTerms: []), "Купи хлеб и т. д.")
        XCTAssertEqual(formatter.format("вышел в 2025 г.", style: .casual, protectedTerms: []), "Вышел в 2025 г.")
        XCTAssertEqual(formatter.format("цена 100 руб.", style: .casual, protectedTerms: []), "Цена 100 руб.")
        XCTAssertEqual(formatter.format("это план Б.", style: .casual, protectedTerms: []), "Это план Б")
        XCTAssertEqual(formatter.format("Купи хлеб и т. д. Потом приходи.", style: .veryCasual, protectedTerms: []),
                       "купи хлеб и т. д. потом приходи")
        XCTAssertEqual(formatter.format("Приходи в 5 ч. вечером. Жду.", style: .veryCasual, protectedTerms: []),
                       "приходи в 5 ч. вечером жду")
    }

    // MARK: - Fillers written as sentences

    func testAFillerSentenceLeavesNoStrayPunctuation() {
        let cleaner = FillerCleaner()
        let cases = [
            ("Э. Давай начнём", "Давай начнём"), ("Хорошо. Э. Давай начнём", "Хорошо. Давай начнём"),
            ("Так. Эм. Что дальше?", "Так. Что дальше?"), ("Hmm? What?", "What?"), ("Uh! Wow", "Wow"),
            ("Um… okay", "Okay"), ("Ээ... давай", "Давай"), ("Давай. Э.", "Давай."), ("Э. Э. Давай", "Давай"),
            ("Ммм. Вкусно", "Вкусно"),
            // Unchanged behaviour next to commas and mid-sentence.
            ("Давай, э.", "Давай."), ("Ну, э-э... давай", "Ну... давай"), ("Э, давай", "Давай")
        ]
        for (text, expected) in cases {
            XCTAssertEqual(cleaner.clean(text, options: .default), expected, text)
        }
        let result = TextPipeline(stages: TextPipeline.standardStages(lexicon: .fixed([:])))
            .process("Хорошо. Э. Давай начнём", context: TextPipelineContext(style: .formal))
        XCTAssertEqual(result.text, "Хорошо. Давай начнём.")
    }

    // MARK: - Links in spacing cleanup

    func testSpacingLeavesLinksAddressesAndPathsWhole() {
        let cleaner = FillerCleaner()
        for text in ["Открой https://github.com/foo/bar?Page=2 пожалуйста", "Ссылка example.com/a,b готова",
                     "Напиши на mailto:ivan@example.com?Subject=Hi сегодня", "Путь ~/Documents/a;b тут",
                     "Сайт www.example.com?Ref=1 работает"] {
            XCTAssertEqual(cleaner.clean(text, options: .default), text, text)
        }
        XCTAssertEqual(cleaner.clean("да,нет и Готово!Проверишь", options: .default), "да, нет и Готово! Проверишь")
    }

    // MARK: - Snippet triggers

    func testSnippetTriggersFoldApostrophesAndTreatHyphensAsBreaks() {
        let expander = SnippetExpander()
        XCTAssertEqual(expander.expand("OK let's go now", snippets: [Snippet(trigger: "let’s go", expansion: "LG")]).text, "OK LG now")
        XCTAssertEqual(expander.expand("OK let’s go now", snippets: [Snippet(trigger: "let's go", expansion: "LG")]).text, "OK LG now")
        XCTAssertEqual(expander.expand("Мой IP-адрес сломался", snippets: [Snippet(trigger: "IP адрес", expansion: "192.0.2.1")]).text,
                       "Мой 192.0.2.1 сломался")
        XCTAssertEqual(expander.expand("Пиши на e-mail", snippets: [Snippet(trigger: "e mail", expansion: "me@example.com")]).text,
                       "Пиши на me@example.com")
        // Part of a hyphenated compound is still not a trigger.
        let address = Snippet(trigger: "адрес", expansion: "A")
        XCTAssertEqual(expander.expand("IP-адрес и адрес-строка и адрес", snippets: [address]).text, "IP-адрес и адрес-строка и A")
    }

    func testReplacementsFoldTheTypographicApostrophe() {
        let result = DictionaryCorrector(lexicon: .fixed([:]))
            .applyReplacements("It’s done", items: [.replacement(from: "it's done", to: "Done")])
        XCTAssertEqual(result.text, "Done")
    }

    // MARK: - Injectable lexicon

    func testStyleFormatterAsksTheInjectedLexicon() {
        let knowsRebase = StyleFormatter(lexicon: .fixed(["en": ["rebase"]]))
        let knowsNothing = StyleFormatter(lexicon: .fixed(["en": []]))
        XCTAssertEqual(knowsRebase.format("rebase готов", style: .formal, protectedTerms: ["rebase"]), "Rebase готов.")
        XCTAssertEqual(knowsNothing.format("rebase готов", style: .formal, protectedTerms: ["rebase"]), "rebase готов.")
    }

    func testVeryCasualKeepsProperNounsTheLexiconKnowsOnlyCapitalized() {
        let formatter = StyleFormatter(lexicon: .fixed(["ru": ["Москва", "большая", "маша", "пришла"], "en": ["London", "is", "big"]]))
        XCTAssertEqual(formatter.format("Москва большая. Маша пришла.", style: .veryCasual, protectedTerms: []),
                       "Москва большая маша пришла")
        XCTAssertEqual(formatter.format("London is big.", style: .veryCasual, protectedTerms: []), "London is big")
        // Without a dictionary nothing counts as a known proper noun.
        XCTAssertEqual(StyleFormatter(lexicon: .fixed([:])).format("Москва большая.", style: .veryCasual, protectedTerms: []),
                       "москва большая")
    }
}
