import Foundation
import XCTest
@testable import SlovoCore

/// Store files with some unreadable entries keep the readable ones, and a file that can
/// be neither read nor set aside is never overwritten.
@MainActor
final class InputStoreRecoveryTests: XCTestCase {
    private var temporary = FileManager.default.temporaryDirectory
    private var directories = AppDirectories(base: FileManager.default.temporaryDirectory)

    override func setUp() async throws {
        temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlovoInputStoreRecoveryTests-\(UUID().uuidString)", isDirectory: true)
        directories = AppDirectories(base: temporary)
        try directories.createFolders()
    }

    override func tearDown() async throws {
        try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: temporary.path)
        try? FileManager.default.removeItem(at: temporary)
    }

    // MARK: - Dropping unreadable entries

    func testHistoryKeepsReadableEntriesAndTheOriginalFile() throws {
        let good = entry("хорошая запись")
        let newer = entry("запись из новой версии")
        // An engine a newer build knows: this one entry cannot be decoded.
        let original = try writeArray([good, newer], to: directories.historyFile) { index, object in
            if index == 1 { object["engineID"] = "whisper-v9" }
        }

        let store = HistoryStore(directories: directories)
        XCTAssertEqual(store.entries, [good])
        let warning = try XCTUnwrap(store.loadWarning)
        XCTAssertTrue(warning.contains("history.json"), warning)
        let backups = try backupNames(prefix: "history.corrupt-")
        XCTAssertEqual(backups.count, 1)
        let backup = try XCTUnwrap(backups.first)
        XCTAssertTrue(warning.contains(backup), warning)
        XCTAssertEqual(try Data(contentsOf: temporary.appendingPathComponent(backup)), original)
        XCTAssertEqual(try Data(contentsOf: directories.historyFile), original, "nothing is written until something changes")

        store.add(entry("новая"))
        try store.flush()
        let reopened = HistoryStore(directories: directories)
        XCTAssertEqual(reopened.entries.count, 2)
        XCTAssertNil(reopened.loadWarning)
    }

    func testEveryStoreDropsOnlyItsBadEntries() throws {
        let term = DictionaryItem.term("Docker")
        try writeArray([term, .term("broken")], to: directories.dictionaryFile) { index, object in
            if index == 1 { object["kind"] = "synonym" }
        }
        XCTAssertEqual(DictionaryStore(directories: directories).items, [term])

        let snippet = Snippet(trigger: "адрес", expansion: "Москва")
        try writeArray([snippet, Snippet(trigger: "x", expansion: "y")], to: directories.snippetsFile) { index, object in
            if index == 1 { object.removeValue(forKey: "expansion") }
        }
        XCTAssertEqual(SnippetStore(directories: directories).snippets, [snippet])

        let note = Note(text: "список")
        try writeArray([Note(text: "сломано"), note], to: directories.notesFile) { index, object in
            if index == 0 { object["text"] = 5 }
        }
        XCTAssertEqual(NoteStore(directories: directories).notes, [note])

        let assignment = AppCategoryAssignment(bundleID: "com.tinyspeck.slackmacgap", appName: "Slack", category: .work)
        try writeArray([assignment, AppCategoryAssignment(bundleID: "a.b", appName: "B", category: .other)],
                       to: directories.appCategoriesFile) { index, object in
            if index == 1 { object["category"] = "gaming" }
        }
        let categories = AppCategoryStore(directories: directories, installedNameLookup: { _ in nil })
        XCTAssertEqual(categories.assignments, [assignment])
        XCTAssertNotNil(categories.loadWarning)
    }

    func testJSONFileReportsWhatItDropped() throws {
        let url = temporary.appendingPathComponent("notes.json")
        let note = Note(text: "ok")
        try writeArray([note, note, note], to: url) { index, object in
            if index > 0 { object["id"] = "not a uuid" }
        }
        guard case let .partial(value, dropped, copiedTo) = JSONFile<[Note]>(url: url).load() else {
            return XCTFail("Expected .partial")
        }
        XCTAssertEqual(value, [note])
        XCTAssertEqual(dropped, 2)
        XCTAssertNotNil(copiedTo)

        // A file that is not an array at all is still damaged as a whole.
        try Data(#"{"entries": 5}"#.utf8).write(to: url)
        guard case .corrupt = JSONFile<[Note]>(url: url).load() else { return XCTFail("Expected .corrupt") }
    }

    // MARK: - A file that cannot be set aside

    func testAnUnreadableFileThatCannotBeMovedIsNeverOverwritten() async throws {
        let damaged = Data("{ not json".utf8)
        try damaged.write(to: directories.snippetsFile)
        // A read-only folder: the damaged file cannot be renamed out of the way.
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: temporary.path)
        let store = SnippetStore(directories: directories)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: temporary.path)

        XCTAssertTrue(store.snippets.isEmpty)
        let warning = try XCTUnwrap(store.loadWarning)
        XCTAssertTrue(warning.contains("snippets.json"), warning)
        XCTAssertTrue(try backupNames(prefix: "snippets.corrupt-").isEmpty)

        // The folder is writable again, but the store must still not replace the original.
        store.add(Snippet(trigger: "адрес", expansion: "Москва"))
        try await Task.sleep(for: .milliseconds(800))
        XCTAssertEqual(try Data(contentsOf: directories.snippetsFile), damaged)
        XCTAssertThrowsError(try store.flush()) { error in
            XCTAssertTrue(error is SlovoError)
            XCTAssertTrue(error.localizedDescription.contains("snippets.json"))
        }
        XCTAssertEqual(try Data(contentsOf: directories.snippetsFile), damaged)
    }

    // MARK: - Helpers

    private func entry(_ text: String) -> HistoryEntry {
        HistoryEntry(date: Date(timeIntervalSince1970: 1_789_000_000), text: text, rawText: text, engineID: .gigaamRNNT,
                     languageMode: .mixed, appBundleID: nil, appName: nil, category: .other, duration: 1,
                     wordCount: 1, fixesCount: 0, audioFileName: nil)
    }

    /// Writes `values` as a JSON array after letting `edit` change each encoded object.
    @discardableResult
    private func writeArray<T: Encodable>(_ values: [T], to url: URL,
                                          edit: (Int, inout [String: Any]) -> Void) throws -> Data {
        let encoded = try JSONEncoder().encode(values)
        guard var objects = try JSONSerialization.jsonObject(with: encoded) as? [[String: Any]] else {
            throw XCTSkip("Could not re-read the encoded array")
        }
        for index in objects.indices {
            edit(index, &objects[index])
        }
        let data = try JSONSerialization.data(withJSONObject: objects)
        try data.write(to: url)
        return data
    }

    private func backupNames(prefix: String) throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: temporary.path).filter { $0.hasPrefix(prefix) }
    }
}
