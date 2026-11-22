import Foundation

/// A JSON value that keeps integers apart from other numbers, so request ids are echoed
/// exactly as they arrived (integer ids stay integers, string ids stay strings).
///
/// Parsing and serialization are hand-written: `JSONSerialization` turns every number
/// into `NSNumber`, which cannot tell `1` from `1.0` or `true`, and its output order
/// and escaping are not specified.
public enum JSONValue: Equatable, Sendable {
    case null
    case bool(Bool)
    /// A number written without a fraction or exponent that fits in `Int`.
    case integer(Int)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    /// Parses one JSON document; throws on anything that is not valid JSON. An escaped
    /// surrogate without its other half, which the JSON grammar allows, becomes U+FFFD.
    public init(parsing data: Data) throws {
        var parser = JSONParser(bytes: [UInt8](data))
        self = try parser.parseDocument()
    }

    /// Parses a JSON-RPC message and also returns the source text of its top-level `id`
    /// when that is a number, so the reply can repeat it exactly: an id too large for `Int`,
    /// or written as `1.50`, would otherwise come back as a different text.
    static func parsingMessage(_ data: Data) throws -> (message: JSONValue, numericID: String?) {
        var parser = JSONParser(bytes: [UInt8](data))
        parser.capturedKey = "id"
        let message = try parser.parseDocument()
        return (message, parser.capturedNumber)
    }

    /// Compact UTF-8 JSON without a line break (newlines inside strings are escaped),
    /// object keys sorted so output is deterministic.
    public func serialized() -> Data {
        var output: [UInt8] = []
        output.reserveCapacity(256)
        JSONWriter.write(self, into: &output)
        return Data(output)
    }

    /// `serialized()` as a string.
    public var serializedString: String {
        var output: [UInt8] = []
        JSONWriter.write(self, into: &output)
        return String(decoding: output, as: UTF8.self)
    }

    /// A member of an object; nil for other values and missing keys.
    public subscript(key: String) -> JSONValue? {
        guard case let .object(members) = self else { return nil }
        return members[key]
    }

    public var stringValue: String? {
        guard case let .string(value) = self else { return nil }
        return value
    }

    public var boolValue: Bool? {
        guard case let .bool(value) = self else { return nil }
        return value
    }

    /// Integers, and other numbers that are whole and fit in `Int` (JSON Schema's
    /// "integer" accepts 20.0).
    public var intValue: Int? {
        switch self {
        case let .integer(value): return value
        case let .number(value): return Int(exactly: value)
        default: return nil
        }
    }

    public var doubleValue: Double? {
        switch self {
        case let .integer(value): return Double(value)
        case let .number(value): return value
        default: return nil
        }
    }

    public var arrayValue: [JSONValue]? {
        guard case let .array(value) = self else { return nil }
        return value
    }

    public var objectValue: [String: JSONValue]? {
        guard case let .object(value) = self else { return nil }
        return value
    }

    public var isNull: Bool {
        self == .null
    }
}

extension JSONValue: ExpressibleByBooleanLiteral, ExpressibleByIntegerLiteral, ExpressibleByFloatLiteral,
    ExpressibleByStringLiteral, ExpressibleByArrayLiteral, ExpressibleByDictionaryLiteral {
    public init(booleanLiteral value: Bool) {
        self = .bool(value)
    }

    public init(integerLiteral value: Int) {
        self = .integer(value)
    }

    public init(floatLiteral value: Double) {
        self = .number(value)
    }

    public init(stringLiteral value: String) {
        self = .string(value)
    }

    public init(arrayLiteral elements: JSONValue...) {
        self = .array(elements)
    }

    /// A repeated key keeps its last value.
    public init(dictionaryLiteral elements: (String, JSONValue)...) {
        self = .object(Dictionary(elements, uniquingKeysWith: { _, last in last }))
    }
}

extension JSONValue {
    /// A string, or null when there is none; keeps result shapes stable for optional fields.
    static func optional(_ value: String?) -> JSONValue {
        value.map(JSONValue.string) ?? .null
    }

    /// Seconds rounded to one decimal, the precision results report timeline positions in.
    static func seconds(_ value: TimeInterval) -> JSONValue {
        guard value.isFinite else { return .number(0) }
        return .number((value * 10).rounded() / 10)
    }
}

/// Why a document is not valid JSON; only the fact matters to callers, which answer −32700.
struct JSONParseError: Error, Equatable {
    let offset: Int
}

/// A strict RFC 8259 parser over UTF-8 bytes.
private struct JSONParser {
    /// Deeper nesting is rejected, so a line of a million `[` cannot exhaust the stack.
    static let maximumDepth = 128

    let bytes: [UInt8]
    var position = 0
    var depth = 0
    /// A member of the top-level object whose number, if it is one, is kept as written.
    var capturedKey: String?
    /// The source text of that member when its value is a number.
    var capturedNumber: String?

    init(bytes: [UInt8]) {
        self.bytes = bytes
    }

    mutating func parseDocument() throws -> JSONValue {
        // A UTF-8 byte order mark is not JSON text, but some writers add one.
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) {
            position = 3
        }
        skipWhitespace()
        let value = try parseValue()
        skipWhitespace()
        guard position == bytes.count else { throw failure() }
        return value
    }

    /// U+FFFD, what an unpaired surrogate escape reads as.
    static let replacement: Unicode.Scalar = "\u{FFFD}"

    private func failure() -> JSONParseError {
        JSONParseError(offset: position)
    }

    private mutating func skipWhitespace() {
        while position < bytes.count {
            switch bytes[position] {
            case 0x20, 0x09, 0x0A, 0x0D: position += 1
            default: return
            }
        }
    }

    private mutating func parseValue() throws -> JSONValue {
        guard position < bytes.count else { throw failure() }
        switch bytes[position] {
        case UInt8(ascii: "{"): return try parseObject()
        case UInt8(ascii: "["): return try parseArray()
        case UInt8(ascii: "\""): return .string(try parseString())
        case UInt8(ascii: "t"): try expect("true"); return .bool(true)
        case UInt8(ascii: "f"): try expect("false"); return .bool(false)
        case UInt8(ascii: "n"): try expect("null"); return .null
        case UInt8(ascii: "-"), UInt8(ascii: "0")...UInt8(ascii: "9"): return try parseNumber()
        default: throw failure()
        }
    }

    private mutating func expect(_ literal: String) throws {
        let utf8 = Array(literal.utf8)
        guard position + utf8.count <= bytes.count, Array(bytes[position..<position + utf8.count]) == utf8 else {
            throw failure()
        }
        position += utf8.count
    }

    private mutating func enter() throws {
        depth += 1
        guard depth <= Self.maximumDepth else { throw failure() }
    }

    private mutating func parseObject() throws -> JSONValue {
        try enter()
        defer { depth -= 1 }
        position += 1
        var members: [String: JSONValue] = [:]
        skipWhitespace()
        if position < bytes.count, bytes[position] == UInt8(ascii: "}") {
            position += 1
            return .object(members)
        }
        while true {
            skipWhitespace()
            guard position < bytes.count, bytes[position] == UInt8(ascii: "\"") else { throw failure() }
            let key = try parseString()
            skipWhitespace()
            guard position < bytes.count, bytes[position] == UInt8(ascii: ":") else { throw failure() }
            position += 1
            skipWhitespace()
            // A repeated key keeps its last value, as most parsers do.
            let valueStart = position
            let value = try parseValue()
            members[key] = value
            if depth == 1, key == capturedKey {
                switch value {
                case .integer, .number:
                    capturedNumber = String(decoding: bytes[valueStart..<position], as: UTF8.self)
                default:
                    capturedNumber = nil
                }
            }
            skipWhitespace()
            guard position < bytes.count else { throw failure() }
            if bytes[position] == UInt8(ascii: ",") {
                position += 1
            } else if bytes[position] == UInt8(ascii: "}") {
                position += 1
                return .object(members)
            } else {
                throw failure()
            }
        }
    }

    private mutating func parseArray() throws -> JSONValue {
        try enter()
        defer { depth -= 1 }
        position += 1
        var elements: [JSONValue] = []
        skipWhitespace()
        if position < bytes.count, bytes[position] == UInt8(ascii: "]") {
            position += 1
            return .array(elements)
        }
        while true {
            skipWhitespace()
            elements.append(try parseValue())
            skipWhitespace()
            guard position < bytes.count else { throw failure() }
            if bytes[position] == UInt8(ascii: ",") {
                position += 1
            } else if bytes[position] == UInt8(ascii: "]") {
                position += 1
                return .array(elements)
            } else {
                throw failure()
            }
        }
    }

    private mutating func parseString() throws -> String {
        position += 1
        var scalars = String.UnicodeScalarView()
        var runStart = position
        func flushRun(_ parser: JSONParser, upTo end: Int, into scalars: inout String.UnicodeScalarView) throws {
            guard end > runStart else { return }
            // Validates UTF-8: an invalid sequence makes the whole document invalid.
            guard let run = String(validatingUTF8Bytes: parser.bytes[runStart..<end]) else {
                throw JSONParseError(offset: runStart)
            }
            scalars.append(contentsOf: run.unicodeScalars)
        }
        while position < bytes.count {
            let byte = bytes[position]
            switch byte {
            case UInt8(ascii: "\""):
                try flushRun(self, upTo: position, into: &scalars)
                position += 1
                return String(scalars)
            case UInt8(ascii: "\\"):
                try flushRun(self, upTo: position, into: &scalars)
                position += 1
                scalars.append(try parseEscape())
                runStart = position
            case 0x00..<0x20:
                // Raw control characters must be escaped inside JSON strings.
                throw failure()
            default:
                position += 1
            }
        }
        throw failure()
    }

    private mutating func parseEscape() throws -> Unicode.Scalar {
        guard position < bytes.count else { throw failure() }
        let byte = bytes[position]
        position += 1
        switch byte {
        case UInt8(ascii: "\""): return "\""
        case UInt8(ascii: "\\"): return "\\"
        case UInt8(ascii: "/"): return "/"
        case UInt8(ascii: "b"): return "\u{08}"
        case UInt8(ascii: "f"): return "\u{0C}"
        case UInt8(ascii: "n"): return "\n"
        case UInt8(ascii: "r"): return "\r"
        case UInt8(ascii: "t"): return "\t"
        case UInt8(ascii: "u"):
            let first = try parseHex4()
            if (0xD800..<0xDC00).contains(first) {
                // A high surrogate pairs with an escaped low surrogate right after it. Without
                // one it stands alone, which the grammar allows and a string cannot hold: it
                // becomes U+FFFD, as other parsers do, rather than costing the whole message
                // and with it the id the client waits on. A following escape that is not a
                // low surrogate is left to be read on its own.
                guard position + 5 < bytes.count, bytes[position] == UInt8(ascii: "\\"),
                      bytes[position + 1] == UInt8(ascii: "u") else { return Self.replacement }
                let resume = position
                position += 2
                let second = try parseHex4()
                guard (0xDC00..<0xE000).contains(second) else {
                    position = resume
                    return Self.replacement
                }
                let combined = 0x10000 + ((first - 0xD800) << 10) + (second - 0xDC00)
                return Unicode.Scalar(combined) ?? Self.replacement
            }
            // A low surrogate on its own, the other half of the same case.
            return Unicode.Scalar(first) ?? Self.replacement
        default:
            throw failure()
        }
    }

    private mutating func parseHex4() throws -> UInt32 {
        guard position + 4 <= bytes.count else { throw failure() }
        var value: UInt32 = 0
        for _ in 0..<4 {
            let byte = bytes[position]
            let digit: UInt32
            switch byte {
            case UInt8(ascii: "0")...UInt8(ascii: "9"): digit = UInt32(byte - UInt8(ascii: "0"))
            case UInt8(ascii: "a")...UInt8(ascii: "f"): digit = UInt32(byte - UInt8(ascii: "a") + 10)
            case UInt8(ascii: "A")...UInt8(ascii: "F"): digit = UInt32(byte - UInt8(ascii: "A") + 10)
            default: throw failure()
            }
            value = value << 4 | digit
            position += 1
        }
        return value
    }

    private mutating func parseNumber() throws -> JSONValue {
        let start = position
        var isInteger = true
        if bytes[position] == UInt8(ascii: "-") {
            position += 1
        }
        guard position < bytes.count else { throw failure() }
        if bytes[position] == UInt8(ascii: "0") {
            position += 1
        } else if isDigit(bytes[position]) {
            while position < bytes.count, isDigit(bytes[position]) { position += 1 }
        } else {
            throw failure()
        }
        if position < bytes.count, bytes[position] == UInt8(ascii: ".") {
            isInteger = false
            position += 1
            guard position < bytes.count, isDigit(bytes[position]) else { throw failure() }
            while position < bytes.count, isDigit(bytes[position]) { position += 1 }
        }
        if position < bytes.count, bytes[position] == UInt8(ascii: "e") || bytes[position] == UInt8(ascii: "E") {
            isInteger = false
            position += 1
            if position < bytes.count, bytes[position] == UInt8(ascii: "+") || bytes[position] == UInt8(ascii: "-") {
                position += 1
            }
            guard position < bytes.count, isDigit(bytes[position]) else { throw failure() }
            while position < bytes.count, isDigit(bytes[position]) { position += 1 }
        }
        let text = String(decoding: bytes[start..<position], as: UTF8.self)
        if isInteger, let value = Int(text) {
            return .integer(value)
        }
        guard let value = Double(text), value.isFinite else { throw JSONParseError(offset: start) }
        return .number(value)
    }

    private func isDigit(_ byte: UInt8) -> Bool {
        byte >= UInt8(ascii: "0") && byte <= UInt8(ascii: "9")
    }
}

private extension String {
    /// Decodes UTF-8 and fails on invalid sequences instead of repairing them.
    init?(validatingUTF8Bytes bytes: ArraySlice<UInt8>) {
        var scalars = String.UnicodeScalarView()
        var decoder = UTF8()
        var iterator = bytes.makeIterator()
        while true {
            switch decoder.decode(&iterator) {
            case let .scalarValue(scalar): scalars.append(scalar)
            case .emptyInput: self = String(scalars); return
            case .error: return nil
            }
        }
    }
}

/// Compact serialization with sorted keys.
private enum JSONWriter {
    static func write(_ value: JSONValue, into output: inout [UInt8]) {
        switch value {
        case .null:
            output.append(contentsOf: Array("null".utf8))
        case let .bool(flag):
            output.append(contentsOf: Array((flag ? "true" : "false").utf8))
        case let .integer(number):
            output.append(contentsOf: Array(String(number).utf8))
        case let .number(number):
            output.append(contentsOf: Array(format(number).utf8))
        case let .string(text):
            writeString(text, into: &output)
        case let .array(elements):
            output.append(UInt8(ascii: "["))
            for (index, element) in elements.enumerated() {
                if index > 0 { output.append(UInt8(ascii: ",")) }
                write(element, into: &output)
            }
            output.append(UInt8(ascii: "]"))
        case let .object(members):
            output.append(UInt8(ascii: "{"))
            for (index, key) in members.keys.sorted().enumerated() {
                if index > 0 { output.append(UInt8(ascii: ",")) }
                writeString(key, into: &output)
                output.append(UInt8(ascii: ":"))
                if let member = members[key] {
                    write(member, into: &output)
                }
            }
            output.append(UInt8(ascii: "}"))
        }
    }

    /// JSON has no NaN or infinity; whole doubles keep a ".0" so they read back as numbers.
    private static func format(_ number: Double) -> String {
        guard number.isFinite else { return "null" }
        return "\(number)"
    }

    private static let hexDigits = Array("0123456789abcdef".utf8)

    private static func writeString(_ text: String, into output: inout [UInt8]) {
        output.append(UInt8(ascii: "\""))
        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x22: output.append(contentsOf: [UInt8(ascii: "\\"), UInt8(ascii: "\"")])
            case 0x5C: output.append(contentsOf: [UInt8(ascii: "\\"), UInt8(ascii: "\\")])
            case 0x0A: output.append(contentsOf: [UInt8(ascii: "\\"), UInt8(ascii: "n")])
            case 0x0D: output.append(contentsOf: [UInt8(ascii: "\\"), UInt8(ascii: "r")])
            case 0x09: output.append(contentsOf: [UInt8(ascii: "\\"), UInt8(ascii: "t")])
            // Control characters, and the two separators some JavaScript line readers
            // split on, are escaped so a message always stays on one line.
            case 0x00..<0x20, 0x7F, 0x2028, 0x2029:
                let value = scalar.value
                output.append(contentsOf: [UInt8(ascii: "\\"), UInt8(ascii: "u"),
                                           hexDigits[Int((value >> 12) & 0xF)], hexDigits[Int((value >> 8) & 0xF)],
                                           hexDigits[Int((value >> 4) & 0xF)], hexDigits[Int(value & 0xF)]])
            default:
                UTF8.encode(scalar) { output.append($0) }
            }
        }
        output.append(UInt8(ascii: "\""))
    }
}
