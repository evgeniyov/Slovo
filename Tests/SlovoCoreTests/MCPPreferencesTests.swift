import CoreFoundation
import Foundation
import XCTest
@testable import SlovoCore

/// `SlovoPreferences` against a throwaway preferences suite; the app's own domain is never read or written.
final class MCPPreferencesTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!

    override func setUpWithError() throws {
        suite = "local.slovo.tests.mcp-\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    }

    override func tearDown() {
        defaults.removePersistentDomain(forName: suite)
        CFPreferencesAppSynchronize(suite as CFString)
    }

    func testTheDefaultDomainIsTheApp() {
        XCTAssertEqual(SlovoPreferences.appDomain, "local.slovo.Slovo")
        XCTAssertEqual(SlovoPreferences().domain, "local.slovo.Slovo")
    }

    func testMissingKeysMeanAccessOffAndTheSystemLanguage() {
        let preferences = SlovoPreferences(domain: suite)
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: false, maxAgeDays: 0))
        XCTAssertEqual(preferences.language(), .system)
    }

    func testReadsWhatTheAppWritesOnEveryCall() {
        let preferences = SlovoPreferences(domain: suite)
        let settings = AppSettings(defaults: defaults)
        settings.mcpEnabled = true
        settings.mcpMaxAgeDays = 30
        settings.appLanguage = .ru
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: true, maxAgeDays: 30))
        XCTAssertEqual(preferences.language(), .ru)
        settings.mcpEnabled = false
        settings.appLanguage = .en
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: false, maxAgeDays: 30))
        XCTAssertEqual(preferences.language(), .en)
    }

    /// Writing is its own switch, and reading it does not turn reading on.
    func testWritingIsASwitchOfItsOwn() {
        let preferences = SlovoPreferences(domain: suite)
        let settings = AppSettings(defaults: defaults)
        XCTAssertFalse(preferences.access().canWrite)

        settings.mcpWriteEnabled = true
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: false, canWrite: true, maxAgeDays: 0))
        settings.mcpEnabled = true
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: true, canWrite: true, maxAgeDays: 0))
        settings.mcpWriteEnabled = false
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: true, canWrite: false, maxAgeDays: 0))

        defaults.set("YES", forKey: SettingsKey.mcpWriteEnabled)
        XCTAssertTrue(preferences.access().canWrite)
        defaults.set(["yes"], forKey: SettingsKey.mcpWriteEnabled)
        XCTAssertFalse(preferences.access().canWrite)
    }

    func testToleratesOddStoredValues() {
        let preferences = SlovoPreferences(domain: suite)
        defaults.set("YES", forKey: SettingsKey.mcpEnabled)
        defaults.set("7", forKey: SettingsKey.mcpMaxAgeDays)
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: true, maxAgeDays: 7))
        defaults.set(1, forKey: SettingsKey.mcpEnabled)
        defaults.set(45, forKey: SettingsKey.mcpMaxAgeDays)
        XCTAssertEqual(preferences.access(), MCPAccess(enabled: true, maxAgeDays: 0))
        defaults.set(["yes"], forKey: SettingsKey.mcpEnabled)
        XCTAssertFalse(preferences.access().enabled)
        defaults.set("klingon", forKey: SettingsKey.appLanguage)
        XCTAssertEqual(preferences.language(), .system)
    }
}
