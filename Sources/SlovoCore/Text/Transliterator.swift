import Foundation

/// Cyrillic to Latin transliteration used to compare Russian spellings of English
/// terms with the terms themselves (кубернетес → kubernetes).
public enum Transliterator {
    /// Lowercased Latin transliteration; Latin letters, digits, spaces and
    /// punctuation pass through: «юзер дефолтс» → "yuzer defolts".
    public static func latin(_ text: String) -> String {
        var output = ""
        output.reserveCapacity(text.utf8.count)
        for character in text.lowercased() {
            if let mapped = table[character] {
                output += mapped
            } else {
                output.append(character)
            }
        }
        return output
    }

    /// The transliteration with everything except Latin letters and digits removed,
    /// the form term matching compares: «гитхаб экшенс» → "githabekshens".
    public static func compactLatin(_ text: String) -> String {
        let folded = latin(text).folding(options: .diacriticInsensitive, locale: nil)
        var scalars = String.UnicodeScalarView()
        for scalar in folded.unicodeScalars where isCompactScalar(scalar) {
            scalars.append(scalar)
        }
        return String(scalars)
    }

    static func isCompactScalar(_ scalar: Unicode.Scalar) -> Bool {
        (0x61...0x7A).contains(scalar.value) || (0x30...0x39).contains(scalar.value)
    }

    // Close to everyday Russian spelling of English words rather than a formal
    // standard: х is "h" (гитхаб → githab), й and ы are "y", soft and hard signs vanish.
    private static let table: [Character: String] = [
        "а": "a", "б": "b", "в": "v", "г": "g", "д": "d", "е": "e", "ё": "yo", "ж": "zh",
        "з": "z", "и": "i", "й": "y", "к": "k", "л": "l", "м": "m", "н": "n", "о": "o",
        "п": "p", "р": "r", "с": "s", "т": "t", "у": "u", "ф": "f", "х": "h", "ц": "ts",
        "ч": "ch", "ш": "sh", "щ": "shch", "ъ": "", "ы": "y", "ь": "", "э": "e", "ю": "yu",
        "я": "ya", "і": "i", "ї": "yi", "є": "ye", "ґ": "g"
    ]
}
