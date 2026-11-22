import Foundation

/// Word scanning shared by the text stages. Positions are offsets into
/// `Array(text)`, so stages can edit characters without juggling string indices.
enum TextScanning {
    /// Joiners that keep hyphenated words and contractions whole: «кое-что», "Let's".
    static let wordJoiners: Set<Character> = ["-", "'", "’"]
    /// Also keeps file and package names whole: "Package.swift".
    static let nameJoiners: Set<Character> = ["-", "'", "’", "."]
    /// Only contractions: a hyphen splits «IP-адрес» into two words.
    static let apostropheJoiners: Set<Character> = ["'", "’"]

    static func isWordCharacter(_ character: Character) -> Bool {
        character.isLetter || character.isNumber
    }

    /// Word ranges: runs of letters and digits; a joiner between two word characters
    /// stays inside the word.
    static func words(in characters: [Character], joiners: Set<Character> = []) -> [Range<Int>] {
        var ranges: [Range<Int>] = []
        var index = 0
        while index < characters.count {
            guard isWordCharacter(characters[index]) else {
                index += 1
                continue
            }
            let start = index
            index += 1
            while index < characters.count {
                if isWordCharacter(characters[index]) {
                    index += 1
                } else if joiners.contains(characters[index]), index + 1 < characters.count,
                          isWordCharacter(characters[index + 1]) {
                    index += 2
                } else {
                    break
                }
            }
            ranges.append(start..<index)
        }
        return ranges
    }

    /// The comparison form of a character: lowercased, with ё read as е and the
    /// typographic apostrophe ’ read as '.
    static func fold(_ character: Character) -> String {
        let lowered = String(character).lowercased()
        switch lowered {
        case "ё": return "е"
        case "’": return "'"
        default: return lowered
        }
    }

    /// The comparison form of a string: lowercased, with ё read as е and ’ as '.
    static func fold<S: StringProtocol>(_ text: S) -> String {
        text.lowercased()
            .replacingOccurrences(of: "ё", with: "е")
            .replacingOccurrences(of: "’", with: "'")
    }

    static func isCyrillic(_ character: Character) -> Bool {
        character.unicodeScalars.contains { (0x0400...0x04FF).contains($0.value) }
    }

    static func isLatinLetter(_ character: Character) -> Bool {
        guard character.isLetter, let scalar = character.unicodeScalars.first else { return false }
        return scalar.isASCII || (0x00C0...0x024F).contains(scalar.value)
    }

    /// Replaces the character at `index` when the case mapping stays one character
    /// ("ß" uppercases to "SS" and is left alone).
    static func setCase(of characters: inout [Character], at index: Int, uppercase: Bool) {
        let source = String(characters[index])
        let mapped = uppercase ? source.uppercased() : source.lowercased()
        guard mapped.count == 1, let character = mapped.first else { return }
        characters[index] = character
    }
}

/// Snippet expansions are inserted verbatim, so the stages after `SnippetExpander`
/// find them in the text and leave them alone.
enum SnippetProtection {
    /// Merged, sorted ranges of expansions present in the text; empty when the
    /// pipeline applied no snippet.
    static func ranges(in characters: [Character], result: TextPipelineResult, snippets: [Snippet]) -> [Range<Int>] {
        guard result.snippetsApplied > 0 else { return [] }
        var found: [Range<Int>] = []
        for snippet in snippets {
            let expansion = Array(snippet.expansion)
            guard !expansion.isEmpty, expansion.count <= characters.count else { continue }
            var start = 0
            while start + expansion.count <= characters.count {
                if characters[start] == expansion[0],
                   characters[start..<(start + expansion.count)].elementsEqual(expansion) {
                    found.append(start..<(start + expansion.count))
                    start += expansion.count
                } else {
                    start += 1
                }
            }
        }
        found.sort { $0.lowerBound < $1.lowerBound }
        var merged: [Range<Int>] = []
        for range in found {
            if let last = merged.last, range.lowerBound <= last.upperBound {
                merged[merged.count - 1] = last.lowerBound..<max(last.upperBound, range.upperBound)
            } else {
                merged.append(range)
            }
        }
        return merged
    }

    /// The text cut into consecutive pieces, marking the protected ones.
    static func segments(_ characters: [Character], protected: [Range<Int>]) -> [(text: String, isProtected: Bool)] {
        var pieces: [(text: String, isProtected: Bool)] = []
        var cursor = 0
        for range in protected {
            if range.lowerBound > cursor {
                pieces.append((String(characters[cursor..<range.lowerBound]), false))
            }
            pieces.append((String(characters[range]), true))
            cursor = range.upperBound
        }
        if cursor < characters.count || pieces.isEmpty {
            pieces.append((String(characters[cursor...]), false))
        }
        return pieces
    }
}
