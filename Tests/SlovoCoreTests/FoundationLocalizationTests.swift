import Foundation
import XCTest
@testable import SlovoCore

/// Checks every string table under Sources: English and Russian have the same
/// tables and keys, and keys used through the lookup helpers exist.
final class FoundationLocalizationTests: XCTestCase {
    private var sourcesRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Sources", isDirectory: true)
    }

    func testEnglishAndRussianTablesHaveIdenticalKeys() throws {
        let tables = try stringTables()
        XCTAssertFalse(tables.isEmpty, "No string tables found under \(sourcesRoot.path)")

        // Tables keyed by resources folder + file name, so SlovoCore's and the app's
        // tables of the same name are compared separately.
        var byName: [String: [String: Set<String>]] = [:]
        for table in tables {
            byName[table.identity, default: [:]][table.language] = table.keys
        }
        for (identity, languages) in byName.sorted(by: { $0.key < $1.key }) {
            guard let english = languages["en"] else {
                XCTFail("\(identity) has no English version")
                continue
            }
            guard let russian = languages["ru"] else {
                XCTFail("\(identity) has no Russian version")
                continue
            }
            let onlyEnglish = english.subtracting(russian).sorted()
            let onlyRussian = russian.subtracting(english).sorted()
            XCTAssertTrue(onlyEnglish.isEmpty, "\(identity): keys missing in ru: \(onlyEnglish)")
            XCTAssertTrue(onlyRussian.isEmpty, "\(identity): keys missing in en: \(onlyRussian)")
        }
    }

    func testCoreStringKeysUsedInCodeExist() throws {
        let coreRoot = sourcesRoot.appendingPathComponent("SlovoCore", isDirectory: true)
        let tables = try stringTables().filter { $0.resourcesFolder.path.hasPrefix(coreRoot.path) && $0.language == "en" }
        var keysByTable: [String: Set<String>] = [:]
        for table in tables {
            keysByTable[table.name, default: []].formUnion(table.keys)
        }
        let pattern = try NSRegularExpression(
            pattern: #"CoreStrings\.(?:string|format)\(\s*"([^"\\]+)"\s*,\s*table:\s*\.(\w+)"#)
        for file in try swiftFiles(in: coreRoot) {
            let source = try String(contentsOf: file, encoding: .utf8)
            let range = NSRange(source.startIndex..., in: source)
            for match in pattern.matches(in: source, range: range) {
                guard let keyRange = Range(match.range(at: 1), in: source),
                      let caseRange = Range(match.range(at: 2), in: source) else { continue }
                let key = String(source[keyRange])
                let caseName = String(source[caseRange])
                guard let table = CoreTable.allCases.first(where: { "\($0)" == caseName }) else {
                    XCTFail("\(file.lastPathComponent): unknown table .\(caseName)")
                    continue
                }
                XCTAssertTrue(keysByTable[table.rawValue, default: []].contains(key),
                              "\(file.lastPathComponent): \"\(key)\" is not in \(table.rawValue).strings")
            }
        }
    }

    func testAppKeysUsedInCodeExist() throws {
        let appRoot = sourcesRoot.appendingPathComponent("Slovo", isDirectory: true)
        let english = try stringTables().filter { $0.resourcesFolder.path.hasPrefix(appRoot.path) && $0.language == "en" }
        let strings = english.filter { $0.name == "Localizable" && $0.kind == "strings" }
            .reduce(into: Set<String>()) { $0.formUnion($1.keys) }
        let plurals = english.filter { $0.name == "Localizable" && $0.kind == "stringsdict" }
            .reduce(into: Set<String>()) { $0.formUnion($1.keys) }
        // The app's lookups are `loc.t("key")`, `loc.t("key", args)` and `loc.plural("key", n)`.
        let stringPattern = try NSRegularExpression(pattern: #"\.t\(\s*"([^"\\]+)""#)
        let pluralPattern = try NSRegularExpression(pattern: #"\.plural\(\s*"([^"\\]+)""#)
        for file in try swiftFiles(in: appRoot) {
            let source = try String(contentsOf: file, encoding: .utf8)
            for key in matches(stringPattern, in: source) {
                XCTAssertTrue(strings.contains(key), "\(file.lastPathComponent): \"\(key)\" is not in Localizable.strings")
            }
            for key in matches(pluralPattern, in: source) {
                XCTAssertTrue(plurals.contains(key), "\(file.lastPathComponent): \"\(key)\" is not in Localizable.stringsdict")
            }
        }
    }

    func testAppKeysAreUsedInCode() throws {
        let appRoot = sourcesRoot.appendingPathComponent("Slovo", isDirectory: true)
        let tables = try stringTables().filter {
            $0.resourcesFolder.path.hasPrefix(appRoot.path) && $0.language == "en" && $0.name == "Localizable"
        }
        XCTAssertFalse(tables.isEmpty, "No Localizable tables under \(appRoot.path)")
        var source = ""
        for file in try swiftFiles(in: appRoot) {
            source += try String(contentsOf: file, encoding: .utf8)
        }
        // Keys built from a raw value, such as `"settings.section.\(rawValue)"`, count by prefix.
        let dynamicPrefixes = matches(try NSRegularExpression(pattern: #""([A-Za-z0-9_.]+\.)\\\("#), in: source)
        XCTAssertFalse(dynamicPrefixes.isEmpty, "The pattern for keys built from raw values found nothing")
        for table in tables {
            for key in table.keys.sorted()
            where !source.contains("\"\(key)\"") && !dynamicPrefixes.contains(where: { key.hasPrefix($0) }) {
                XCTFail("\(table.name).\(table.kind): \"\(key)\" is not used in code")
            }
        }
    }

    func testCoreStringsFollowsRequestedLanguage() {
        let english = CoreStrings.string("engines.gigaam.noResult", table: .engines, language: .en)
        let russian = CoreStrings.string("engines.gigaam.noResult", table: .engines, language: .ru)
        XCTAssertEqual(english, "sherpa-onnx returned no recognition result")
        XCTAssertEqual(russian, "sherpa-onnx не вернул результат распознавания")
        XCTAssertEqual(CoreStrings.format("engines.parakeet.downloadFailed", table: .engines, language: .en, "offline"),
                       "Could not download the Parakeet model: offline")
    }

    // MARK: - Helpers

    private struct Table {
        let resourcesFolder: URL
        let language: String
        let name: String
        let kind: String
        let keys: Set<String>

        var identity: String {
            "\(resourcesFolder.path)/\(name).\(kind)"
        }
    }

    private func stringTables() throws -> [Table] {
        var tables: [Table] = []
        guard let enumerator = FileManager.default.enumerator(at: sourcesRoot, includingPropertiesForKeys: nil) else {
            return tables
        }
        for case let file as URL in enumerator {
            let kind = file.pathExtension
            guard kind == "strings" || kind == "stringsdict" else { continue }
            let lproj = file.deletingLastPathComponent()
            guard lproj.pathExtension == "lproj" else { continue }
            let data = try Data(contentsOf: file)
            let plist = try PropertyListSerialization.propertyList(from: data, format: nil)
            guard let dictionary = plist as? [String: Any] else {
                XCTFail("\(file.path) is not a dictionary")
                continue
            }
            tables.append(Table(resourcesFolder: lproj.deletingLastPathComponent(),
                                language: lproj.deletingPathExtension().lastPathComponent,
                                name: file.deletingPathExtension().lastPathComponent,
                                kind: kind,
                                keys: Set(dictionary.keys)))
        }
        return tables
    }

    private func swiftFiles(in folder: URL) throws -> [URL] {
        guard let enumerator = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: nil) else {
            return []
        }
        return enumerator.compactMap { $0 as? URL }.filter { $0.pathExtension == "swift" }
    }

    private func matches(_ pattern: NSRegularExpression, in source: String) -> [String] {
        let range = NSRange(source.startIndex..., in: source)
        return pattern.matches(in: source, range: range).compactMap { match in
            Range(match.range(at: 1), in: source).map { String(source[$0]) }
        }
    }
}
