import Foundation
import XCTest
@testable import SlovoCore

/// The hand-written JSON parser and writer: exact ids, strictness and one-line output.
final class MCPJSONValueTests: XCTestCase {
    private func parse(_ text: String) throws -> JSONValue {
        try JSONValue(parsing: Data(text.utf8))
    }

    func testParsesEveryKindOfValue() throws {
        let value = try parse(#" { "a" : [1, -2, 3.5, 1e3, true, false, null], "b" : "x\"\\\/\b\f\n\r\t\u0041\u00e9\ud83d\ude00" } "#)
        XCTAssertEqual(value["a"], [1, -2, 3.5, 1000.0, true, false, .null])
        XCTAssertEqual(value["b"], .string("x\"\\/\u{08}\u{0C}\n\r\tAé😀"))
    }

    func testIntegersStayIntegers() throws {
        XCTAssertEqual(try parse("42"), .integer(42))
        XCTAssertEqual(try parse("-0"), .integer(0))
        XCTAssertEqual(try parse("9007199254740993"), .integer(9_007_199_254_740_993))
        XCTAssertEqual(try parse("1.0"), .number(1.0))
        XCTAssertEqual(try parse("1E2"), .number(100))
        // Larger than Int: a number, not a failure.
        XCTAssertEqual(try parse("18446744073709551616"), .number(1.8446744073709551616e19))
        XCTAssertEqual(JSONValue.number(20.0).intValue, 20)
        XCTAssertNil(JSONValue.number(20.5).intValue)
    }

    func testRejectsInvalidDocuments() {
        let invalid = [
            "", " ", "[1,]", "{\"a\":1,}", "01", "1.", ".5", "-", "+1", "1e", "NaN", "Infinity", "tru", "nul",
            "\"unterminated", "\"raw\ncontrol\"", "\"bad \\x escape\"", "\"\\ud800\\uzzzz\"",
            "{\"a\" 1}", "{1:2}", "[1 2]", "{} {}", "[1]x"
        ]
        for text in invalid {
            XCTAssertThrowsError(try parse(text), "accepted \(text.debugDescription)")
        }
        XCTAssertThrowsError(try JSONValue(parsing: Data([0x22, 0xFF, 0xFE, 0x22])), "invalid UTF-8")
        XCTAssertThrowsError(try parse(String(repeating: "[", count: 200) + String(repeating: "]", count: 200)))
        XCTAssertNoThrow(try parse(String(repeating: "[", count: 100) + String(repeating: "]", count: 100)))
    }

    /// The grammar allows an escaped surrogate without its other half; it reads as U+FFFD
    /// instead of failing the whole message and the id the client is waiting on.
    func testUnpairedSurrogatesBecomeReplacementCharacters() throws {
        XCTAssertEqual(try parse(#""\ud800""#), .string("\u{FFFD}"))
        XCTAssertEqual(try parse(#""ab\ud83d""#), .string("ab\u{FFFD}"))
        XCTAssertEqual(try parse(#""\ude00x""#), .string("\u{FFFD}x"))
        XCTAssertEqual(try parse(#""\ud800\u0041""#), .string("\u{FFFD}A"), "the next escape is read on its own")
        XCTAssertEqual(try parse(#""\ud800\ud800\udc00""#), .string("\u{FFFD}\u{10000}"))
        XCTAssertEqual(try parse(#""\ud83d\ude00""#), .string("😀"))
    }

    func testTheSourceTextOfANumericIDIsKept() throws {
        let big = try JSONValue.parsingMessage(Data(#"{"id": 12345678901234567890, "x": {"id": 1}}"#.utf8))
        XCTAssertEqual(big.numericID, "12345678901234567890")
        XCTAssertEqual(try JSONValue.parsingMessage(Data(#"{"id":1.50}"#.utf8)).numericID, "1.50")
        XCTAssertNil(try JSONValue.parsingMessage(Data(#"{"id":"7"}"#.utf8)).numericID)
        XCTAssertNil(try JSONValue.parsingMessage(Data(#"{"x":{"id":7}}"#.utf8)).numericID, "only the top level counts")
    }

    func testRepeatedKeysKeepTheLastValue() throws {
        XCTAssertEqual(try parse(#"{"a":1,"a":2}"#), ["a": 2])
    }

    func testSerializationIsCompactSortedAndOneLine() throws {
        let value: JSONValue = [
            "z": 1, "a": [true, .null, 2.5, 3.0], "m": "line\nbreak\r\t\u{0001}\u{2028}\"\\/ é😀"
        ]
        let text = value.serializedString
        XCTAssertEqual(text, #"{"a":[true,null,2.5,3.0],"m":"line\nbreak\r\t\u0001\u2028\"\\/ é😀","z":1}"#)
        XCTAssertFalse(text.contains("\n"))
        XCTAssertEqual(try JSONValue(parsing: value.serialized()), value)
        XCTAssertEqual(JSONValue.number(.nan).serializedString, "null")
        XCTAssertEqual(JSONValue.integer(9_007_199_254_740_993).serializedString, "9007199254740993")
    }

    func testRoundTripsRandomDocuments() throws {
        var generator = SystemRandomNumberGenerator()
        for _ in 0..<200 {
            let value = randomValue(depth: 0, generator: &generator)
            XCTAssertEqual(try JSONValue(parsing: value.serialized()), value)
        }
    }

    private func randomValue(depth: Int, generator: inout SystemRandomNumberGenerator) -> JSONValue {
        let kind = Int.random(in: 0..<(depth > 3 ? 5 : 7), using: &generator)
        switch kind {
        case 0: return .null
        case 1: return .bool(Bool.random(using: &generator))
        case 2: return .integer(Int.random(in: Int.min...Int.max, using: &generator))
        case 3: return .number(Double.random(in: -1e12...1e12, using: &generator))
        case 4:
            let scalars = (0..<Int.random(in: 0..<12, using: &generator)).compactMap { _ in
                Unicode.Scalar(UInt32.random(in: 0...0x2FFF, using: &generator)).flatMap { (0xD800...0xDFFF).contains($0.value) ? nil : $0 }
            }
            return .string(String(String.UnicodeScalarView(scalars)))
        case 5:
            return .array((0..<Int.random(in: 0..<4, using: &generator)).map { _ in
                randomValue(depth: depth + 1, generator: &generator)
            })
        default:
            var members: [String: JSONValue] = [:]
            for index in 0..<Int.random(in: 0..<4, using: &generator) {
                members["k\(index)"] = randomValue(depth: depth + 1, generator: &generator)
            }
            return .object(members)
        }
    }
}
