import Foundation

/// Splits a transcript into comparable words, so that case, punctuation and
/// the ё/е spelling do not count as recognition errors.
public enum TextNormalizer {
    public static func words(_ text: String) -> [String] {
        let lowered = text.lowercased().replacingOccurrences(of: "ё", with: "е")
        var cleaned = String.UnicodeScalarView()
        for scalar in lowered.unicodeScalars {
            if CharacterSet.letters.contains(scalar) || CharacterSet.decimalDigits.contains(scalar) {
                cleaned.append(scalar)
            } else {
                cleaned.append(" ")
            }
        }
        return String(cleaned).split(separator: " ").map(String.init)
    }

    /// True for words made of ASCII letters and digits with at least one letter,
    /// which is how English terms look after normalization.
    public static func isLatin(_ word: String) -> Bool {
        var hasLetter = false
        for scalar in word.unicodeScalars {
            switch scalar.value {
            case 0x61...0x7A: hasLetter = true
            case 0x30...0x39: continue
            default: return false
            }
        }
        return hasLetter
    }
}
