import Foundation
import XCTest
@testable import SlovoCore

/// Checks that the shared model and a few stubs behave as their contracts say. These
/// tests stay valid once the modules are implemented.
@MainActor
final class FoundationSmokeTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suiteName = ""
    private var temporary: URL!

    override func setUp() async throws {
        suiteName = "slovo.tests.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoFoundationTests-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: temporary)
    }

    func testSettingsDefaultsAndRoundTrip() {
        let settings = AppSettings(defaults: defaults)
        XCTAssertEqual(settings.dictationKey, .fn)
        XCTAssertEqual(settings.languageMode, .mixed)
        XCTAssertEqual(settings.engine(for: .dictation(.mixed)), .gigaamRNNT)
        XCTAssertEqual(settings.engine(for: .dictation(.english)), .parakeet)
        XCTAssertEqual(settings.engine(for: .meetings), .gigaamCTC)
        XCTAssertEqual(settings.idleUnload, .minutes15)
        XCTAssertEqual(settings.keepDictationAudio, .days7)
        XCTAssertEqual(settings.style(for: .personal), .casual)
        XCTAssertEqual(settings.pasteLastShortcut, .pasteLastDefault)
        XCTAssertNil(settings.microphoneUID)
        XCTAssertTrue(settings.showMenuBarIcon)

        settings.dictationKey = .rightCommand
        settings.showMenuBarIcon = false
        settings.pasteLastShortcut = HotkeyShortcut(keyCode: 1, modifiers: [.option])
        settings.microphoneUID = "BuiltInMicrophoneDevice"
        settings.setStyle(.veryCasual, for: .work)
        settings.idleUnload = .never

        let reread = AppSettings(defaults: defaults)
        XCTAssertEqual(reread.dictationKey, .rightCommand)
        XCTAssertEqual(reread.pasteLastShortcut, HotkeyShortcut(keyCode: 1, modifiers: [.option]))
        XCTAssertEqual(reread.microphoneUID, "BuiltInMicrophoneDevice")
        XCTAssertEqual(reread.style(for: .work), .veryCasual)
        XCTAssertEqual(reread.idleUnload, .never)
        XCTAssertFalse(reread.showMenuBarIcon)
    }

    /// The menu bar icon is on unless turned off, also for defaults registered at launch.
    func testMenuBarIconIsShownByDefault() {
        let settings = AppSettings(defaults: defaults)
        settings.registerDefaults()
        XCTAssertEqual(defaults.object(forKey: SettingsKey.showMenuBarIcon) as? Bool, true)
        XCTAssertTrue(settings.showMenuBarIcon)
    }

    func testLanguageResolution() {
        XCTAssertEqual(AppLanguage.system.resolvedCode(preferredLanguages: ["ru-RU", "en-US"]), "ru")
        XCTAssertEqual(AppLanguage.system.resolvedCode(preferredLanguages: ["de-DE", "en-GB"]), "en")
        XCTAssertEqual(AppLanguage.system.resolvedCode(preferredLanguages: ["fr-FR"]), "en")
        XCTAssertEqual(AppLanguage.ru.resolvedCode(preferredLanguages: ["en-US"]), "ru")
    }

    func testModelTypesEncodeStableValues() throws {
        let item = DictionaryItem.replacement(from: "гитхаб", to: "GitHub", createdAt: Date(timeIntervalSince1970: 0))
        let decoded = try JSONDecoder().decode(DictionaryItem.self, from: JSONEncoder().encode(item))
        XCTAssertEqual(decoded, item)
        XCTAssertEqual(EngineID(rawValue: "gigaam-v3-rnnt"), .gigaamRNNT)
        XCTAssertEqual(Note(text: "\n  Shopping list \nmilk").title, "Shopping list")
        XCTAssertEqual(TextPipelineResult(text: "", replacementsApplied: 2, termFixesApplied: 3).fixesCount, 5)
    }

    func testPipelineWithoutStagesReturnsRawText() {
        let pipeline = TextPipeline(stages: [])
        let result = pipeline.process("кибернетес", context: TextPipelineContext())
        XCTAssertEqual(result, TextPipelineResult(text: "кибернетес"))
    }

    func testMeetingStoreFileLayout() {
        let directories = AppDirectories(base: temporary)
        let store = MeetingStore(directories: directories)
        let id = UUID()
        let folder = directories.meetingsFolder.appendingPathComponent(id.uuidString, isDirectory: true)
        XCTAssertEqual(store.folder(for: id).standardizedFileURL, folder.standardizedFileURL)
        XCTAssertEqual(store.microphoneAudioURL(for: id).lastPathComponent, "mic.caf")
        XCTAssertEqual(store.systemAudioURL(for: id).lastPathComponent, "system.caf")
        XCTAssertEqual(store.transcriptURL(for: id).lastPathComponent, "transcript.md")
    }

    func testRecordingPathUsesDayFolder() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(identifier: "UTC"))
        let id = UUID()
        let date = Date(timeIntervalSince1970: 1_789_000_000) // 2026-09-10 UTC
        XCTAssertEqual(WAVFile.relativePath(for: id, date: date, calendar: calendar), "2026-09-10/\(id.uuidString).wav")
    }

    func testCoreResourceBundleHasSeedVocabulary() {
        XCTAssertNotNil(CoreStrings.resourceBundle.url(forResource: "vocabulary", withExtension: "txt"))
    }
}
