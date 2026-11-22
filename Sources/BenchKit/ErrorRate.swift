import Foundation

public struct ErrorCounts: Equatable, Sendable {
    public var errors: Int
    public var referenceLength: Int

    public init(errors: Int, referenceLength: Int) {
        self.errors = errors
        self.referenceLength = referenceLength
    }

    public var rate: Double {
        referenceLength == 0 ? 0 : Double(errors) / Double(referenceLength)
    }

    public static func + (lhs: ErrorCounts, rhs: ErrorCounts) -> ErrorCounts {
        ErrorCounts(errors: lhs.errors + rhs.errors, referenceLength: lhs.referenceLength + rhs.referenceLength)
    }
}

public enum ErrorRate {
    /// Word errors: substitutions, deletions and insertions against the reference.
    public static func words(reference: String, hypothesis: String) -> ErrorCounts {
        let expected = TextNormalizer.words(reference)
        let actual = TextNormalizer.words(hypothesis)
        return ErrorCounts(errors: editDistance(expected, actual), referenceLength: expected.count)
    }

    /// Character errors over the normalized text, spaces included.
    public static func characters(reference: String, hypothesis: String) -> ErrorCounts {
        let expected = Array(TextNormalizer.words(reference).joined(separator: " "))
        let actual = Array(TextNormalizer.words(hypothesis).joined(separator: " "))
        return ErrorCounts(errors: editDistance(expected, actual), referenceLength: expected.count)
    }

    /// How many Latin-script words of the reference appear in the hypothesis
    /// still written in Latin, counting repeated words separately.
    public static func latinTerms(reference: String, hypothesis: String) -> (kept: Int, total: Int) {
        let terms = TextNormalizer.words(reference).filter(TextNormalizer.isLatin)
        var available: [String: Int] = [:]
        for word in TextNormalizer.words(hypothesis) {
            available[word, default: 0] += 1
        }
        var kept = 0
        for term in terms where available[term, default: 0] > 0 {
            available[term, default: 0] -= 1
            kept += 1
        }
        return (kept, terms.count)
    }

    static func editDistance<T: Equatable>(_ a: [T], _ b: [T]) -> Int {
        if a.isEmpty { return b.count }
        if b.isEmpty { return a.count }
        var previous = Array(0...b.count)
        var current = [Int](repeating: 0, count: b.count + 1)
        for i in 1...a.count {
            current[0] = i
            for j in 1...b.count {
                let substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)
                current[j] = min(previous[j] + 1, current[j - 1] + 1, substitution)
            }
            swap(&previous, &current)
        }
        return previous[b.count]
    }
}
