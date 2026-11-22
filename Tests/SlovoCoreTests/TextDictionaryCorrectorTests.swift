import XCTest
@testable import SlovoCore

final class TextDictionaryCorrectorTests: XCTestCase {
    private let corrector = DictionaryCorrector()

    /// The Dictionary seed shipped with SlovoCore.
    private static let vocabulary: [String] = {
        guard let url = CoreStrings.resourceBundle.url(forResource: "vocabulary", withExtension: "txt"),
              let text = try? String(contentsOf: url, encoding: .utf8) else { return [] }
        return text.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
    }()

    private static let benchRoot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appendingPathComponent("Bench", isDirectory: true)

    override func setUpWithError() throws {
        XCTAssertFalse(Self.vocabulary.isEmpty, "vocabulary.txt is missing from SlovoCore resources")
    }

    // MARK: - Terms

    func testSpecExamplesBecomeTerms() {
        let cases = [
            ("кибернетес", "Kubernetes"),
            ("Гитхаб экшенс", "GitHub Actions"),
            ("юзер Айди", "user id"),
            ("постгресс", "Postgres"),
            ("докер", "Docker"),
            ("User Defolts", "UserDefaults")
        ]
        for (spoken, term) in cases {
            let result = corrector.correctTerms(spoken, terms: Self.vocabulary)
            XCTAssertEqual(result.text, term, spoken)
            XCTAssertEqual(result.count, 1, spoken)
        }
    }

    func testTermCorrectionsInSampleSentences() {
        let cases = [
            ("Учебный стенд на кибернетис готов. Открой отчёт в Гитхаб экшенс.",
             "Учебный стенд на Kubernetes готов. Открой отчёт в GitHub Actions."),
            ("Демо выдаёт Джисон с полем юзер Айди.",
             "Демо выдаёт JSON с полем user id."),
            ("Запусти Doker образ рядом с Post Gress Local.", "Запусти Docker образ рядом с Postgres Local."),
            ("Для примера прочитай настройки из User Defolts.",
             "Для примера прочитай настройки из UserDefaults."),
            ("Для каталога включи эсленд и Pretter, потом запусти проверку.",
             "Для каталога включи ESLint и Prettier, потом запусти проверку."),
            ("У демо сломался Сигнинг. В списке нет сертификата девелопера Айди.",
             "У демо сломался signing. В списке нет сертификата Developer ID."),
            ("Покажи интерактивный каталог на верселе после урока.", "Покажи интерактивный каталог на Vercel после урока.")
        ]
        for (transcript, expected) in cases {
            XCTAssertEqual(corrector.correctTerms(transcript, terms: Self.vocabulary).text, expected)
        }
    }

    func testOrdinaryRussianWordsStay() {
        // The spec's examples, then word forms that sit within the edit distance of a
        // seed term: версию and Vercel, скрыть and SQLite.
        let words = ["деплой", "логи", "модель", "проект", "версию", "версия", "версии", "версией", "вернул",
                     "скрыть", "скрыто", "скрытое", "месте", "мин", "май", "мая", "машин", "размер", "если",
                     "выше", "серия", "фоном", "релизу", "джин", "мастерская", "опция", "опыт", "рабочий",
                     "докладе", "полей", "телега", "свитер", "виски", "роутер", "сигнал", "тестовый"]
        for word in words {
            let result = corrector.correctTerms(word, terms: Self.vocabulary)
            XCTAssertEqual(result.text, word, word)
            XCTAssertEqual(result.count, 0, word)
        }
        let sentence = "Пусть модель стартует после открытия учебного примера."
        XCTAssertEqual(corrector.correctTerms(sentence, terms: Self.vocabulary).text, sentence)
    }

    func testCommonEnglishWordsStay() {
        let texts = ["makes", "reduce", "scanning", "developed", "remain", "mail", "caption", "Swift",
                     "the user wants", "Please review the sample menu before class."]
        for text in texts {
            XCTAssertEqual(corrector.correctTerms(text, terms: Self.vocabulary).text, text, text)
        }
    }

    func testPrepositionsAreNotSwallowed() {
        XCTAssertEqual(corrector.correctTerms("Обнови Pach Swift и подними версию", terms: Self.vocabulary).text,
                       "Обнови Pach Swift и подними версию")
        XCTAssertEqual(corrector.correctTerms("логи в гитхаб экшенс", terms: ["GitHub Actions"]).text,
                       "логи в GitHub Actions")
    }

    func testLongerTermWinsOverAShorterOne() {
        XCTAssertEqual(corrector.correctTerms("юзер дефолтс", terms: ["user id", "UserDefaults"]).text, "UserDefaults")
    }

    func testExactTermsAreNotCountedAndCasingIsRestored() {
        XCTAssertEqual(corrector.correctTerms("Запусти Docker", terms: ["Docker"]).count, 0)
        let result = corrector.correctTerms("обновил macos и redis", terms: ["macOS", "Redis"])
        XCTAssertEqual(result.text, "обновил macOS и Redis")
        XCTAssertEqual(result.count, 2)
    }

    func testFuzzyCorrectionPreservesNumbersInTermsAndIdentifiers() {
        let corrector = DictionaryCorrector(lexicon: .fixed(["ru": ["обнови"], "en": []]))
        let terms = ["Kubernetes", "Package.swift", "macOS 27"]
        for text in ["Обнови Kubernetes2", "Обнови Package.swift6", "Обнови macOS 26", "Обнови макос 2.7"] {
            let result = corrector.correctTerms(text, terms: terms)
            XCTAssertEqual(result.text, text)
            XCTAssertEqual(result.count, 0)
        }
        XCTAssertEqual(corrector.correctTerms("Обнови макос 27", terms: terms).text, "Обнови macOS 27")
        let hyphenated = corrector.correctTerms("Обнови макос 27-2", terms: ["macOS 27.2"])
        XCTAssertEqual(hyphenated.text, "Обнови макос 27-2")
        XCTAssertEqual(hyphenated.count, 0)
        XCTAssertEqual(corrector.correctTerms("Обнови whisper4", terms: ["Whisper-4"]).text, "Обнови Whisper-4")
    }

    func testPunctuationSplitsWindows() {
        XCTAssertEqual(corrector.correctTerms("гитхаб, экшенс", terms: ["GitHub Actions"]).count, 0)
    }

    func testShortTermsAndThresholds() {
        XCTAssertEqual(corrector.correctTerms("апи и сиай", terms: ["API", "CI"]).count, 0)
        let strict = DictionaryCorrector(maximumDistance: 0)
        XCTAssertEqual(strict.correctTerms("кибернетес и докер", terms: ["Kubernetes", "Docker"]).text, "кибернетес и Docker")
        let longTermsOnly = DictionaryCorrector(minimumTermLetters: 7)
        XCTAssertEqual(longTermsOnly.correctTerms("кибернетес и докер", terms: ["Kubernetes", "Docker"]).text,
                       "Kubernetes и докер")
        XCTAssertEqual(corrector.maximumDistance, 0.34)
        XCTAssertEqual(corrector.minimumTermLetters, 4)
    }

    func testTermsWithoutLatinLettersAreIgnored() {
        XCTAssertEqual(corrector.correctTerms("кибернетес", terms: ["кубернетес"]).count, 0)
    }

    // MARK: - Replacements

    func testReplacementsAreWholeWordCaseInsensitiveAndCounted() {
        let items: [DictionaryItem] = [
            .replacement(from: "гит хаб", to: "GitHub"),
            .replacement(from: "ё-моё", to: "ого"),
            .term("Docker"),
            .replacement(from: "кот", to: "кошка")
        ]
        let result = corrector.applyReplacements("Открой Гит   Хаб, котик, кот и ЕЖ. Е-мое!", items: items)
        XCTAssertEqual(result.text, "Открой GitHub, котик, кошка и ЕЖ. ого!")
        XCTAssertEqual(result.count, 3)
    }

    func testCapitalizedMatchOfLowercaseTriggerKeepsItsCapital() {
        let items = [DictionaryItem.replacement(from: "комит", to: "коммит")]
        XCTAssertEqual(corrector.applyReplacements("Комит для примера в main", items: items).text, "Коммит для примера в main")
    }

    func testTriggerWrittenWithCapitalsKeepsTheReplacementCase() {
        let items: [DictionaryItem] = [.replacement(from: "Bb", to: "bb"), .replacement(from: "BUILD BOT", to: "build bot")]
        let result = corrector.applyReplacements("Bb, BB и BUILD BOT", items: items)
        XCTAssertEqual(result.text, "bb, bb и build bot")
        XCTAssertEqual(result.count, 3)
    }

    func testReplacementOutputIsNotScannedAgain() {
        let items = [DictionaryItem.replacement(from: "a", to: "a a")]
        let result = corrector.applyReplacements("a b", items: items)
        XCTAssertEqual(result.text, "a a b")
        XCTAssertEqual(result.count, 1)
    }

    func testLongerReplacementWinsAndUnchangedTextIsNotCounted() {
        let items: [DictionaryItem] = [.replacement(from: "пул", to: "pool"), .replacement(from: "пул реквест", to: "pull request"),
                                       .replacement(from: "Slovo", to: "Slovo")]
        let result = corrector.applyReplacements("Открой пул реквест в Slovo", items: items)
        XCTAssertEqual(result.text, "Открой pull request в Slovo")
        XCTAssertEqual(result.count, 1)
    }

    // MARK: - Stage

    func testStageAddsToTheCounters() {
        var result = TextPipelineResult(text: "докер и кибернетес", replacementsApplied: 1, termFixesApplied: 2)
        let context = TextPipelineContext(dictionary: [.term("Docker"), .term("Kubernetes"), .replacement(from: "и", to: "and")])
        corrector.apply(to: &result, context: context)
        XCTAssertEqual(result.text, "Docker and Kubernetes")
        XCTAssertEqual(result.replacementsApplied, 2)
        XCTAssertEqual(result.termFixesApplied, 4)
        XCTAssertEqual(result.fixesCount, 6)
    }

    // MARK: - Bench data

    /// The corrector must not change a single Cyrillic word of the user's reference
    /// texts; they are written correctly, so any change is a false positive.
    func testBenchReferencesKeepEveryCyrillicWord() throws {
        let references = try benchReferences()
        XCTAssertFalse(references.isEmpty)
        for (clip, reference) in references {
            let corrected = corrector.correctTerms(reference, terms: Self.vocabulary).text
            XCTAssertEqual(cyrillicWords(corrected), cyrillicWords(reference), clip)
            XCTAssertEqual(corrected, reference, clip)
        }
    }

    /// Saved engine transcripts: English terms of the references that end up written
    /// in Latin letters, before and after correction.
    func testBenchTranscriptsGainLatinTerms() throws {
        let references = try benchReferences()
        let resultsFolder = Self.benchRoot.appendingPathComponent("results", isDirectory: true)
        let files = (try? FileManager.default.contentsOfDirectory(at: resultsFolder, includingPropertiesForKeys: nil)) ?? []
        let runs = files.filter { $0.pathExtension == "json" }.sorted { $0.lastPathComponent < $1.lastPathComponent }
        guard !runs.isEmpty else { throw XCTSkip("No saved engine results in \(resultsFolder.path)") }

        var totalBefore = 0
        var totalAfter = 0
        var totalTerms = 0
        for file in runs {
            let object = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any]
            let engine = object?["engine"] as? String ?? file.lastPathComponent
            let clips = object?["clips"] as? [[String: Any]] ?? []
            var before = 0
            var after = 0
            for clip in clips {
                guard let id = clip["clip"] as? String, let text = clip["text"] as? String,
                      let reference = references[id] else { continue }
                let corrected = corrector.correctTerms(text, terms: Self.vocabulary).text
                let kept = latinTermsKept(reference: reference, hypothesis: text)
                before += kept.kept
                after += latinTermsKept(reference: reference, hypothesis: corrected).kept
                totalTerms += kept.total
            }
            XCTContext.runActivity(named: "\(engine): Latin terms \(before) → \(after)") { _ in }
            XCTAssertGreaterThanOrEqual(after, before, engine)
            totalBefore += before
            totalAfter += after
        }
        XCTContext.runActivity(named: "All engines: Latin terms \(totalBefore) → \(totalAfter) of \(totalTerms)") { _ in }
        // Measured on the saved runs: 125 → 206 of 345. The bound leaves room for
        // re-recorded results while still failing if correction stops working.
        XCTAssertGreaterThanOrEqual(totalAfter - totalBefore, (totalTerms - totalBefore) / 4)
    }

    // MARK: - Helpers

    private func benchReferences() throws -> [String: String] {
        let clips = Self.benchRoot.appendingPathComponent("clips", isDirectory: true)
        guard let files = try? FileManager.default.contentsOfDirectory(at: clips, includingPropertiesForKeys: nil) else {
            throw XCTSkip("No bench clips in \(clips.path)")
        }
        var references: [String: String] = [:]
        for file in files where file.pathExtension == "txt" {
            let text = try String(contentsOf: file, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty {
                references[file.deletingPathExtension().lastPathComponent] = text
            }
        }
        return references
    }

    private func cyrillicWords(_ text: String) -> [String] {
        let characters = Array(text)
        return TextScanning.words(in: characters)
            .map { String(characters[$0]) }
            .filter { $0.contains(where: TextScanning.isCyrillic) }
    }

    private func normalizedWords(_ text: String) -> [String] {
        let characters = Array(TextScanning.fold(text))
        return TextScanning.words(in: characters).map { String(characters[$0]) }
    }

    private func latinTermsKept(reference: String, hypothesis: String) -> (kept: Int, total: Int) {
        let isLatin = { (word: String) in word.unicodeScalars.allSatisfy { Transliterator.isCompactScalar($0) }
            && word.unicodeScalars.contains { (0x61...0x7A).contains($0.value) } }
        let terms = normalizedWords(reference).filter(isLatin)
        var available: [String: Int] = [:]
        for word in normalizedWords(hypothesis) {
            available[word, default: 0] += 1
        }
        var kept = 0
        for term in terms where available[term, default: 0] > 0 {
            available[term, default: 0] -= 1
            kept += 1
        }
        return (kept, terms.count)
    }
}
