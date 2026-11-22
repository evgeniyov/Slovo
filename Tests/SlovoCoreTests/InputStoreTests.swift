import Foundation
import XCTest
@testable import SlovoCore

/// JSON files, the debouncer and every store owned by the input module, against a
/// temporary directory and a throwaway UserDefaults suite.
@MainActor
final class InputStoreTests: XCTestCase {
    private var temporary = FileManager.default.temporaryDirectory
    private var directories = AppDirectories(base: FileManager.default.temporaryDirectory)
    private var suiteName = ""
    private var defaults = UserDefaults.standard

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoInputStoreTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
        suiteName = "slovo.tests.input.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - JSONFile

    func testJSONFileMissingThenRoundTrip() throws {
        let file = JSONFile<[Snippet]>(url: temporary.appendingPathComponent("nested/snippets.json"))
        guard case .missing = file.load() else { return XCTFail("Expected .missing") }

        let snippets = [Snippet(trigger: "мой адрес", expansion: "Примерск, ул. Примерная, 1",
                                createdAt: Date(timeIntervalSince1970: 1_789_000_000.123))]
        try file.save(snippets)
        guard case let .loaded(loaded) = file.load() else { return XCTFail("Expected .loaded") }
        XCTAssertEqual(loaded, snippets)

        // The atomic write leaves no temporary files next to the store.
        let siblings = try FileManager.default.contentsOfDirectory(atPath: file.url.deletingLastPathComponent().path)
        XCTAssertEqual(siblings, ["snippets.json"])
    }

    func testJSONFileMovesACorruptFileAside() throws {
        let url = temporary.appendingPathComponent("history.json")
        try Data("{ not json".utf8).write(to: url)
        let file = JSONFile<[HistoryEntry]>(url: url)

        guard case let .corrupt(movedTo) = file.load() else { return XCTFail("Expected .corrupt") }
        let backup = try XCTUnwrap(movedTo)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        XCTAssertTrue(backup.lastPathComponent.hasPrefix("history.corrupt-"))
        XCTAssertEqual(backup.pathExtension, "json")
        XCTAssertEqual(try String(contentsOf: backup, encoding: .utf8), "{ not json")
        guard case .missing = file.load() else { return XCTFail("Expected .missing after moving aside") }
    }

    func testJSONFileWrongShapeCountsAsCorrupt() throws {
        let url = temporary.appendingPathComponent("notes.json")
        try Data(#"{"entries": 5}"#.utf8).write(to: url)
        guard case .corrupt = JSONFile<[Note]>(url: url).load() else { return XCTFail("Expected .corrupt") }
    }

    func testCorruptBackupsNeverOverwriteEachOther() throws {
        let url = temporary.appendingPathComponent("dictionary.json")
        let file = JSONFile<[DictionaryItem]>(url: url)
        let date = Date(timeIntervalSince1970: 1_789_000_000)
        let first = file.corruptBackupURL(date: date)
        try Data("a".utf8).write(to: first)
        let second = file.corruptBackupURL(date: date)
        XCTAssertNotEqual(first, second)
        XCTAssertTrue(second.lastPathComponent.hasPrefix("dictionary.corrupt-"))
        XCTAssertTrue(second.lastPathComponent.hasSuffix("-2.json"))
    }

    func testJSONFileSaveFailureThrowsALocalizedError() throws {
        let blocker = temporary.appendingPathComponent("file")
        try Data("x".utf8).write(to: blocker)
        let file = JSONFile<[Note]>(url: blocker.appendingPathComponent("notes.json"))
        XCTAssertThrowsError(try file.save([])) { error in
            XCTAssertTrue(error is SlovoError)
            XCTAssertTrue(error.localizedDescription.contains("notes.json"))
        }
    }

    // MARK: - SaveDebouncer

    func testDebouncerCoalescesAndFlushes() async throws {
        var runs = 0
        let debouncer = SaveDebouncer(delay: .milliseconds(30)) { runs += 1 }
        debouncer.schedule()
        debouncer.schedule()
        debouncer.schedule()
        XCTAssertTrue(debouncer.isPending)
        try await Task.sleep(for: .milliseconds(200))
        XCTAssertEqual(runs, 1)
        XCTAssertFalse(debouncer.isPending)

        debouncer.flush()
        XCTAssertEqual(runs, 1, "flush without a pending action does nothing")

        debouncer.schedule()
        debouncer.flush()
        XCTAssertEqual(runs, 2)
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(runs, 2, "a flushed action does not run again")

        debouncer.schedule()
        debouncer.cancel()
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(runs, 2)
    }

    // MARK: - HistoryStore

    func testHistoryRoundTripKeepsNewestFirst() throws {
        let store = HistoryStore(directories: directories)
        XCTAssertTrue(store.entries.isEmpty)
        XCTAssertNil(store.loadWarning)
        let old = entry("старая запись", daysAgo: 3)
        let new = entry("новая запись", daysAgo: 0)
        let middle = entry("middle", daysAgo: 1)
        store.add(old)
        store.add(new)
        store.add(middle)
        XCTAssertEqual(store.entries.map(\.id), [new.id, middle.id, old.id])
        try store.flush()

        let reopened = HistoryStore(directories: directories)
        XCTAssertEqual(reopened.entries, [new, middle, old])
    }

    func testHistorySavesAfterTheDebounce() async throws {
        let store = HistoryStore(directories: directories)
        store.add(entry("hello", daysAgo: 0))
        XCTAssertFalse(FileManager.default.fileExists(atPath: directories.historyFile.path))
        try await Task.sleep(for: .milliseconds(900))
        XCTAssertTrue(FileManager.default.fileExists(atPath: directories.historyFile.path))
        XCTAssertEqual(HistoryStore(directories: directories).entries.count, 1)
    }

    func testHistoryAddWithExistingIDReplacesAndUpdateResorts() throws {
        let store = HistoryStore(directories: directories)
        var first = entry("one", daysAgo: 2)
        let second = entry("two", daysAgo: 1)
        store.add(first)
        store.add(second)
        first.text = "one, edited"
        store.add(first)
        XCTAssertEqual(store.entries.count, 2)
        XCTAssertEqual(store.entries.last?.text, "one, edited")

        first.date = Date()
        store.update(first)
        XCTAssertEqual(store.entries.map(\.id), [first.id, second.id])

        store.update(entry("unknown", daysAgo: 0))
        XCTAssertEqual(store.entries.count, 2)
    }

    func testHistoryDeleteRemovesAudioAndItsDayFolder() throws {
        let store = HistoryStore(directories: directories)
        let path = "2026-09-15/\(UUID().uuidString).wav"
        let audio = try makeRecording(path)
        var withAudio = entry("with audio", daysAgo: 0)
        withAudio.audioFileName = path
        store.add(withAudio)
        XCTAssertEqual(store.audioURL(for: withAudio)?.standardizedFileURL, audio.standardizedFileURL)

        store.delete(id: withAudio.id)
        XCTAssertTrue(store.entries.isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: audio.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: audio.deletingLastPathComponent().path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: directories.recordingsFolder.path))
    }

    func testHistoryRemoveAudioKeepsTheEntry() throws {
        let store = HistoryStore(directories: directories)
        let path = "2026-09-14/\(UUID().uuidString).wav"
        let otherPath = "2026-09-14/\(UUID().uuidString).wav"
        let audio = try makeRecording(path)
        let other = try makeRecording(otherPath)
        var withAudio = entry("keep me", daysAgo: 0)
        withAudio.audioFileName = path
        store.add(withAudio)

        store.removeAudio(for: withAudio.id)
        XCTAssertNil(store.entries.first?.audioFileName)
        XCTAssertFalse(FileManager.default.fileExists(atPath: audio.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: other.path), "the day folder still has another recording")
        XCTAssertNil(store.audioURL(for: withAudio))
    }

    func testHistoryAudioURLIsNilWhenTheFileIsGone() {
        let store = HistoryStore(directories: directories)
        var missing = entry("missing", daysAgo: 0)
        missing.audioFileName = "2026-01-01/nothing.wav"
        XCTAssertNil(store.audioURL(for: missing))
        XCTAssertNil(store.audioURL(for: entry("no audio", daysAgo: 0)))
    }

    func testHistoryNeverTouchesFilesOutsideRecordings() throws {
        let store = HistoryStore(directories: directories)
        let outside = temporary.appendingPathComponent("secret.txt")
        try Data("keep".utf8).write(to: outside)
        var hostile = entry("hostile", daysAgo: 0)
        hostile.audioFileName = "../secret.txt"
        store.add(hostile)
        XCTAssertNil(store.audioURL(for: hostile))
        store.removeAudio(for: hostile.id)
        store.delete(id: hostile.id)
        XCTAssertTrue(FileManager.default.fileExists(atPath: outside.path))
    }

    func testHistoryDeleteAllClearsEntriesAndRecordingsAndSaves() throws {
        let store = HistoryStore(directories: directories)
        let path = "2026-09-15/\(UUID().uuidString).wav"
        _ = try makeRecording(path)
        _ = try makeRecording("2026-09-01/orphan.wav")
        var withAudio = entry("a", daysAgo: 0)
        withAudio.audioFileName = path
        store.add(withAudio)
        store.add(entry("b", daysAgo: 1))

        store.deleteAll()
        XCTAssertTrue(store.entries.isEmpty)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: directories.recordingsFolder.path), [])
        XCTAssertTrue(HistoryStore(directories: directories).entries.isEmpty, "deleteAll saves at once")
    }

    func testHistorySearchIgnoresCaseAndYo() {
        let store = HistoryStore(directories: directories)
        let tree = entry("Купили ЁЛКУ на рынке", daysAgo: 0)
        let hedgehog = entry("ежик в тумане", daysAgo: 1)
        let english = entry("Deploy the Kubernetes cluster", daysAgo: 2)
        [tree, hedgehog, english].forEach(store.add)

        XCTAssertEqual(store.search("елку").map(\.id), [tree.id])
        XCTAssertEqual(store.search("Ёжик").map(\.id), [hedgehog.id])
        XCTAssertEqual(store.search("kubernetes").map(\.id), [english.id])
        XCTAssertEqual(store.search("  ").count, 3)
        XCTAssertEqual(store.search("").count, 3)
        XCTAssertTrue(store.search("postgres").isEmpty)
    }

    func testHistoryGroupsByDayNewestFirst() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "UTC"))
        let dayStart = Date(timeIntervalSince1970: 1_789_000_000 - 1_789_000_000.truncatingRemainder(dividingBy: 86_400))
        let morning = entry("morning", date: dayStart.addingTimeInterval(8 * 3600))
        let evening = entry("evening", date: dayStart.addingTimeInterval(20 * 3600))
        let yesterday = entry("yesterday", date: dayStart.addingTimeInterval(-3600))

        let groups = HistoryStore.groupedByDay([morning, yesterday, evening], calendar: calendar)
        XCTAssertEqual(groups.count, 2)
        XCTAssertEqual(groups[0].day, dayStart)
        XCTAssertEqual(groups[0].entries.map(\.id), [evening.id, morning.id])
        XCTAssertEqual(groups[1].day, dayStart.addingTimeInterval(-86_400))
        XCTAssertEqual(groups[1].entries.map(\.id), [yesterday.id])
        XCTAssertTrue(HistoryStore.groupedByDay([], calendar: calendar).isEmpty)
    }

    func testHistorySurvivesACorruptFile() throws {
        try Data("[{\"id\": 42}]".utf8).write(to: directories.historyFile)
        let store = HistoryStore(directories: directories)
        XCTAssertTrue(store.entries.isEmpty)
        let warning = try XCTUnwrap(store.loadWarning)
        XCTAssertTrue(warning.contains("history.json"))
        let backups = try FileManager.default.contentsOfDirectory(atPath: temporary.path)
            .filter { $0.hasPrefix("history.corrupt-") }
        XCTAssertEqual(backups.count, 1)

        store.add(entry("fresh start", daysAgo: 0))
        try store.flush()
        XCTAssertEqual(HistoryStore(directories: directories).entries.count, 1)
    }

    // MARK: - DictionaryStore

    func testDictionaryRoundTripAndKinds() throws {
        let store = DictionaryStore(directories: directories)
        var term = DictionaryItem.term("Kubernetes")
        let replacement = DictionaryItem.replacement(from: "гитхаб", to: "GitHub")
        store.add(term)
        store.add(replacement)
        XCTAssertEqual(store.terms.map(\.id), [term.id])
        XCTAssertEqual(store.replacements.map(\.id), [replacement.id])

        term.text = "Kubernetes (k8s)"
        store.update(term)
        store.add(term)
        XCTAssertEqual(store.items.count, 2)
        try store.flush()

        let reopened = DictionaryStore(directories: directories)
        XCTAssertEqual(reopened.items, [term, replacement])
        reopened.delete(id: replacement.id)
        try reopened.flush()
        XCTAssertEqual(DictionaryStore(directories: directories).items, [term])
    }

    func testDictionarySeedsOnceAndDeletedTermsStayDeleted() throws {
        let settings = AppSettings(defaults: defaults)
        let store = DictionaryStore(directories: directories)
        store.add(.term("docker"))
        store.seedIfNeeded(settings: settings, terms: ["Kubernetes", "Docker", " Postgres ", "", "kubernetes"])
        XCTAssertTrue(settings.dictionarySeeded)
        XCTAssertEqual(store.terms.map(\.text), ["docker", "Kubernetes", "Postgres"])

        let kubernetes = try XCTUnwrap(store.terms.first { $0.text == "Kubernetes" })
        store.delete(id: kubernetes.id)
        store.seedIfNeeded(settings: settings, terms: ["Kubernetes"])
        XCTAssertFalse(store.terms.contains { $0.text == "Kubernetes" })
        try store.flush()

        let reopened = DictionaryStore(directories: directories)
        XCTAssertEqual(reopened.terms.map(\.text), ["docker", "Postgres"])
    }

    func testAnOlderSeededDictionaryGetsOnlyTheNewTermsOnce() throws {
        let settings = AppSettings(defaults: defaults)
        let store = DictionaryStore(directories: directories)
        // Seeded by version 1: only later additions are eligible.
        store.add(.term("Kubernetes"))
        settings.dictionarySeeded = true
        settings.dictionarySeedVersion = 1
        store.seedIfNeeded(settings: settings, terms: ["Kubernetes", "Docker"])
        let newerTerms = ["iOS", "AppKit", "ChatGPT", "OpenAI", "DeepSeek", "VPN", "IP", "LAN", "sudo", "Mac", "MacBook"]
        XCTAssertEqual(store.terms.map(\.text), ["Kubernetes", "GitHub"] + newerTerms)
        XCTAssertEqual(settings.dictionarySeedVersion, DictionarySeed.latestVersion)

        // Deleting it afterwards is final.
        let github = try XCTUnwrap(store.terms.first { $0.text == "GitHub" })
        store.delete(id: github.id)
        store.seedIfNeeded(settings: settings, terms: ["Kubernetes", "Docker"])
        XCTAssertEqual(store.terms.map(\.text), ["Kubernetes"] + newerTerms)
    }

    func testDictionaryUpgradePreservesUserEntriesAndEnablesNewTerms() throws {
        let settings = AppSettings(defaults: defaults)
        let store = DictionaryStore(directories: directories)
        let term = DictionaryItem.term("openai")
        let replacement = DictionaryItem.replacement(from: "планёрка", to: "созвон")
        store.add(term)
        store.add(replacement)
        settings.dictionarySeeded = true
        settings.dictionarySeedVersion = 2
        // GitHub was removed from this dictionary after the version 2 seed.
        store.seedIfNeeded(settings: settings)

        let reopened = DictionaryStore(directories: directories)
        XCTAssertEqual(Array(reopened.items.prefix(2)), [term, replacement])
        XCTAssertEqual(reopened.terms.map(\.text),
                       ["openai", "iOS", "AppKit", "ChatGPT", "DeepSeek", "VPN", "IP", "LAN", "sudo", "Mac", "MacBook"])
        XCTAssertEqual(settings.dictionarySeedVersion, DictionarySeed.latestVersion)

        let lexicon = WordLexicon.fixed([
            "ru": ["проверь", "билд", "на", "спроси", "чат", "сравни", "и", "выбери", "другой", "кит"],
            "en": ["chat", "deep", "seek"]
        ])
        let pipeline = TextPipeline(stages: TextPipeline.standardStages(lexicon: lexicon))
        let context = TextPipelineContext(dictionary: reopened.items, cleanup: .off)
        let cases = [
            ("Проверь билд на айос.", "Проверь билд на iOS.", 1),
            ("Спроси чат гпт.", "Спроси ChatGPT.", 1),
            ("Сравни дипсик и аппкит.", "Сравни DeepSeek и AppKit.", 2),
            ("Выбери другой чат.", "Выбери другой чат.", 0),
        ]
        for (input, expected, count) in cases {
            let result = pipeline.process(input, context: context)
            XCTAssertEqual(result.text, expected, input)
            XCTAssertEqual(result.fixesCount, count, input)
        }

        let ios = try XCTUnwrap(reopened.terms.first { $0.text == "iOS" })
        reopened.delete(id: ios.id)
        try reopened.flush()
        reopened.seedIfNeeded(settings: settings)
        XCTAssertFalse(DictionaryStore(directories: directories).terms.contains { $0.text == "iOS" })
    }

    func testVersionFourSeedsExactReplacementsAndPreservesCustomRules() throws {
        let settings = AppSettings(defaults: defaults)
        let store = DictionaryStore(directories: directories)
        let personalTerm = DictionaryItem.term("CustomName")
        let personalReplacement = DictionaryItem.replacement(from: " Chat   GPT ", to: "my assistant")
        store.add(personalTerm)
        store.add(personalReplacement)
        settings.dictionarySeeded = true
        settings.dictionarySeedVersion = 3
        store.seedIfNeeded(settings: settings)

        let reopened = DictionaryStore(directories: directories)
        XCTAssertEqual(settings.dictionarySeedVersion, DictionarySeed.latestVersion)
        XCTAssertEqual(Array(reopened.items.prefix(2)), [personalTerm, personalReplacement])
        let pipeline = TextPipeline(stages: TextPipeline.standardStages(lexicon: .fixed([:])))
        let context = TextPipelineContext(dictionary: reopened.items, cleanup: .off)
        let examples = [
            ("В демо выбери клодо, Cloud cod или клот-код.", "В демо выбери Claude, Claude Code или Claude Code."),
            ("Для Makos и Macbook подойдут Vescode и Vic-Code.", "Для macOS и MacBook подойдут VS Code и VS Code."),
            ("Добавь в таблицу DeepSig, DepSik, чата GPT и дипсиком.", "Добавь в таблицу DeepSeek, DeepSeek, ChatGPT и DeepSeek."),
            ("Запиши Chat GPT и чат GPT.", "Запиши my assistant и ChatGPT."),
            ("У чата GPT-на есть демо и чат GPT-6.", "У ChatGPT есть демо и ChatGPT-6."),
            ("Карточка с гидхабом и с гитхабом, ниже гидхаб.", "Карточка с гидхабом и с GitHub, ниже GitHub."),
            ("Для списка комитов сначала закомить комиты, а затем отметь комит.", "Для списка коммитов сначала закоммитить коммиты, а затем отметь коммит."),
            ("Комит для примера в main", "Коммит для примера в main"),
            ("Воркер положил кодекс рядом, на мачте был клот.", "Воркер положил кодекс рядом, на мачте был клот."),
            ("Слева Google Cloud Code, справа другой чат.", "Слева Google Cloud Code, справа другой чат."),
        ]
        for (input, expected) in examples {
            XCTAssertEqual(pipeline.process(input, context: context).text, expected, input)
        }

        let makos = try XCTUnwrap(reopened.replacements.first { $0.text == "Makos" })
        reopened.delete(id: makos.id)
        try reopened.flush()
        reopened.seedIfNeeded(settings: settings)
        let afterDeletion = TextPipelineContext(dictionary: reopened.items, cleanup: .off)
        XCTAssertEqual(pipeline.process("Сборка под Makos.", context: afterDeletion).text, "Сборка под Makos.")
    }

    func testVersionFiveAddsTechnicalSpellingsAndPreservesCustomReplacement() throws {
        let settings = AppSettings(defaults: defaults)
        settings.dictionarySeeded = true
        settings.dictionarySeedVersion = 4
        let store = DictionaryStore(directories: directories)
        let custom = DictionaryItem.replacement(from: " ай   пи ", to: "мой адрес")
        store.add(custom)
        store.seedIfNeeded(settings: settings)
        store.seedIfNeeded(settings: settings)

        let reopened = DictionaryStore(directories: directories)
        XCTAssertEqual(settings.dictionarySeedVersion, 5)
        XCTAssertEqual(Set(reopened.terms.map(\.text)), ["VPN", "IP", "LAN", "sudo", "Mac", "MacBook"])
        XCTAssertEqual(reopened.replacements.filter { ReplacementRule.pattern(for: $0.text) == ReplacementRule.pattern(for: custom.text) },
                       [custom])
        let context = TextPipelineContext(dictionary: reopened.items, cleanup: .off)
        let pipeline = TextPipeline(stages: TextPipeline.standardStages(lexicon: .fixed([:])))
        let examples = [
            ("Включи ВПН, ви-пи-эн или ви пи эн.", "Включи VPN, VPN или VPN."),
            ("Проверь ай-пи и ай пи.", "Проверь IP и мой адрес."),
            ("На айпишнике есть правило.", "На IP-шнике есть правило."),
            ("Это лан-кабель.", "Это LAN-кабель."),
            ("Запусти СУДО и настрой си ай, си-ай и сиай.", "Запусти sudo и настрой CI, CI и CI."),
            ("Открой Xcod или икс код на маке.", "Открой Xcode или Xcode на Mac."),
            ("Возьми макбук, там есть драйвер.", "Возьми MacBook, там есть драйвер."),
            ("Подтяни с гетхаба.", "Подтяни с GitHub."),
            ("Занимается меш-сетью.", "Занимается mesh-сетью."),
            ("Там растёт мак. Лан, это гид, а тот маг.", "Там растёт мак. Лан, это гид, а тот маг."),
        ]
        for (input, expected) in examples {
            XCTAssertEqual(pipeline.process(input, context: context).text, expected, input)
        }
    }

    func testAFirstSeedIsAlreadyTheLatestVersion() {
        let settings = AppSettings(defaults: defaults)
        let store = DictionaryStore(directories: directories)
        store.seedIfNeeded(settings: settings, terms: ["Kubernetes"])
        XCTAssertEqual(settings.dictionarySeedVersion, DictionarySeed.latestVersion)
        XCTAssertEqual(store.terms.map(\.text), ["Kubernetes"])
        let context = TextPipelineContext(dictionary: store.items, cleanup: .off)
        XCTAssertEqual(TextPipeline().process("Сборка под Makos.", context: context).text, "Сборка под macOS.")
    }

    func testEveryAddedTermIsInTheBundledVocabulary() {
        let bundled = Set(DictionarySeed.bundledTerms())
        for addition in DictionarySeed.additions {
            XCTAssertGreaterThan(addition.version, 1)
            for term in addition.terms {
                XCTAssertTrue(bundled.contains(term), term)
            }
        }
    }

    func testDictionaryDoesNotMarkSeededWithoutTerms() {
        let settings = AppSettings(defaults: defaults)
        let store = DictionaryStore(directories: directories)
        store.seedIfNeeded(settings: settings, terms: [])
        XCTAssertFalse(settings.dictionarySeeded)
        XCTAssertTrue(store.items.isEmpty)
    }

    func testVocabularyParsingSkipsCommentsBlanksAndDuplicates() {
        let text = "# Комментарий\n\nKubernetes\n  GitHub Actions  \n#API\nkubernetes\r\nuser id\n"
        XCTAssertEqual(DictionarySeed.terms(fromVocabulary: text), ["Kubernetes", "GitHub Actions", "user id"])
    }

    func testBundledVocabularyIncludesTheBenchListAndVersionFiveTerms() throws {
        let terms = DictionarySeed.bundledTerms()
        XCTAssertTrue(terms.contains("Kubernetes"))
        XCTAssertTrue(terms.contains("GitHub Actions"))
        XCTAssertTrue(terms.contains("UserDefaults"))
        XCTAssertFalse(terms.contains { $0.hasPrefix("#") || $0.isEmpty })

        let bench = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Bench/vocabulary.txt")
        if let text = try? String(contentsOf: bench, encoding: .utf8) {
            XCTAssertEqual(terms, DictionarySeed.terms(fromVocabulary: text) + ["VPN", "IP", "LAN", "sudo", "Mac", "MacBook"])
        }
    }

    // MARK: - SnippetStore

    func testSnippetsRoundTripAndUpdateStamp() throws {
        let store = SnippetStore(directories: directories)
        let created = Date(timeIntervalSince1970: 1_700_000_000)
        var snippet = Snippet(trigger: "my email", expansion: "me@example.com", createdAt: created)
        store.add(snippet)
        store.add(Snippet(trigger: "адрес", expansion: "Москва"))
        XCTAssertEqual(store.snippets.count, 2)

        snippet.expansion = "me@example.org"
        store.update(snippet)
        let updated = try XCTUnwrap(store.snippets.first { $0.id == snippet.id })
        XCTAssertEqual(updated.expansion, "me@example.org")
        XCTAssertGreaterThan(updated.updatedAt, created)
        try store.flush()

        let reopened = SnippetStore(directories: directories)
        XCTAssertEqual(reopened.snippets, store.snippets)
        reopened.delete(id: snippet.id)
        XCTAssertEqual(reopened.snippets.map(\.trigger), ["адрес"])
    }

    func testSnippetsSurviveACorruptFile() throws {
        try Data([0xFF, 0xFE, 0x00]).write(to: directories.snippetsFile)
        let store = SnippetStore(directories: directories)
        XCTAssertTrue(store.snippets.isEmpty)
        XCTAssertNotNil(store.loadWarning)
    }

    // MARK: - NoteStore

    func testNotesCreateUpdateOrderAndRoundTrip() throws {
        let store = NoteStore(directories: directories)
        let first = store.create(text: "Shopping list")
        let second = store.create(text: "Ideas")
        XCTAssertEqual(store.notes.map(\.id), [second.id, first.id])

        var edited = first
        edited.text = "Shopping list\nmilk"
        store.update(edited)
        XCTAssertEqual(store.notes.map(\.id), [first.id, second.id])
        XCTAssertEqual(store.notes.first?.title, "Shopping list")
        try store.flush()

        let reopened = NoteStore(directories: directories)
        XCTAssertEqual(reopened.notes, store.notes)
        reopened.delete(id: second.id)
        XCTAssertEqual(reopened.notes.map(\.id), [first.id])
    }

    func testNoteAppendSeparatesDictations() throws {
        let store = NoteStore(directories: directories)
        let empty = store.create(text: "")
        store.append("  первая фраза ", toNote: empty.id)
        XCTAssertEqual(store.notes.first { $0.id == empty.id }?.text, "первая фраза")
        store.append("вторая", toNote: empty.id)
        XCTAssertEqual(store.notes.first { $0.id == empty.id }?.text, "первая фраза вторая")

        let lines = store.create(text: "Line one\n")
        store.append("line two", toNote: lines.id)
        XCTAssertEqual(store.notes.first { $0.id == lines.id }?.text, "Line one\nline two")

        let older = try XCTUnwrap(store.notes.last)
        store.append("   ", toNote: older.id)
        XCTAssertEqual(store.notes.last, older, "blank dictations change nothing")
        store.append("text", toNote: UUID())

        store.append("bumped", toNote: older.id)
        XCTAssertEqual(store.notes.first?.id, older.id)
        XCTAssertEqual(NoteStore.joined("a", "b"), "a b")
        XCTAssertEqual(NoteStore.joined("a ", "b"), "a b")
    }

    // MARK: - AppCategoryStore

    func testDefaultCategoriesCoverTheSpec() {
        let expected: [String: AppCategory] = [
            "ru.keepcoder.Telegram": .personal, "org.telegram.desktop": .personal, "com.apple.MobileSMS": .personal,
            "net.whatsapp.WhatsApp": .personal, "com.hnc.Discord": .personal,
            "com.tinyspeck.slackmacgap": .work, "com.microsoft.teams2": .work, "us.zoom.xos": .work,
            "com.apple.mail": .email, "com.microsoft.Outlook": .email
        ]
        for (bundleID, category) in expected {
            XCTAssertEqual(AppCategoryStore.defaultCategories[bundleID], category, bundleID)
        }
        XCTAssertEqual(AppCategoryStore.defaultCategory(bundleID: "com.example.Bitrix", name: "Bitrix24"), .work)
        XCTAssertEqual(AppCategoryStore.defaultCategory(bundleID: nil, name: "Yandex.Telemost"), .work)
        XCTAssertEqual(AppCategoryStore.defaultCategory(bundleID: nil, name: "Яндекс Телемост"), .work)
        XCTAssertEqual(AppCategoryStore.defaultCategory(bundleID: "com.readdle.SparkDesktop", name: "Spark Desktop.app"), .email)
        XCTAssertEqual(AppCategoryStore.defaultCategory(bundleID: "com.example.sparkle", name: "Sparkle"), .other)
    }

    func testCategoryLookupOrder() {
        let store = AppCategoryStore(directories: directories) { bundleID in
            bundleID == "com.readdle.SparkDesktop" ? "Spark Desktop" : nil
        }
        XCTAssertEqual(store.category(forBundleID: nil), .other)
        XCTAssertEqual(store.category(forBundleID: ""), .other)
        XCTAssertEqual(store.category(forBundleID: "ru.keepcoder.Telegram"), .personal)
        XCTAssertEqual(store.category(forBundleID: "com.tinyspeck.slackmacgap"), .work)
        XCTAssertEqual(store.category(forBundleID: "com.apple.mail"), .email)
        XCTAssertEqual(store.category(forBundleID: "com.readdle.SparkDesktop"), .email)
        XCTAssertEqual(store.category(forBundleID: "com.apple.dt.Xcode"), .other)
        XCTAssertEqual(store.category(for: AppIdentity(bundleID: "com.bitrix.other", name: "Битрикс24")), .work)
    }

    func testUserAssignmentsOverrideDefaultsAndPersist() throws {
        let store = AppCategoryStore(directories: directories) { _ in nil }
        let telegram = AppIdentity(bundleID: "ru.keepcoder.Telegram", name: "Telegram")
        let xcode = AppIdentity(bundleID: "com.apple.dt.Xcode", name: "Xcode")
        store.setCategory(.work, for: telegram)
        store.setCategory(.work, for: xcode)
        store.setCategory(.email, for: xcode)
        XCTAssertEqual(store.assignments.count, 2)
        XCTAssertEqual(store.category(forBundleID: telegram.bundleID), .work)
        XCTAssertEqual(store.category(for: xcode), .email)
        try store.flush()

        let reopened = AppCategoryStore(directories: directories) { _ in nil }
        XCTAssertEqual(reopened.assignments, store.assignments)
        XCTAssertEqual(reopened.category(forBundleID: "com.apple.dt.Xcode"), .email)
    }

    func testAppsInCategoryComeFromHistoryAndAssignments() {
        let store = AppCategoryStore(directories: directories) { _ in nil }
        let history = [
            entry("a", daysAgo: 0, app: ("com.tinyspeck.slackmacgap", "Slack")),
            entry("b", daysAgo: 1, app: ("com.tinyspeck.slackmacgap", "Slack (old name)")),
            entry("c", daysAgo: 0, app: ("us.zoom.xos", "zoom.us")),
            entry("d", daysAgo: 0, app: ("com.apple.dt.Xcode", "Xcode")),
            entry("e", daysAgo: 0, app: ("ru.bitrix.bitrix24desktop", "Bitrix24")),
            entry("f", daysAgo: 0, app: nil)
        ]
        store.setCategory(.work, for: AppIdentity(bundleID: "com.linear", name: "Linear"))
        store.setCategory(.personal, for: AppIdentity(bundleID: "us.zoom.xos", name: "zoom.us"))

        let work = store.apps(in: .work, seenIn: history)
        XCTAssertEqual(work.map(\.name), ["Bitrix24", "Linear", "Slack"])
        XCTAssertEqual(store.apps(in: .personal, seenIn: history).map(\.bundleID), ["us.zoom.xos"])
        XCTAssertEqual(store.apps(in: .other, seenIn: history).map(\.name), ["Xcode"])
        XCTAssertTrue(store.apps(in: .email, seenIn: history).isEmpty)
    }

    // MARK: - Helpers

    private func entry(_ text: String, daysAgo: Double, app: (String, String)? = ("com.apple.Notes", "Notes")) -> HistoryEntry {
        entry(text, date: Date().addingTimeInterval(-daysAgo * 86_400), app: app)
    }

    private func entry(_ text: String, date: Date, app: (String, String)? = ("com.apple.Notes", "Notes")) -> HistoryEntry {
        HistoryEntry(date: date, text: text, rawText: text, engineID: .gigaamRNNT, languageMode: .mixed,
                     appBundleID: app?.0, appName: app?.1, category: .other, duration: 3,
                     wordCount: HistoryEntry.countWords(in: text), fixesCount: 0, audioFileName: nil)
    }

    private func makeRecording(_ relativePath: String) throws -> URL {
        let url = directories.recordingURL(relativePath: relativePath)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data([0x52, 0x49, 0x46, 0x46]).write(to: url)
        return url
    }
}
