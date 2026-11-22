import AppKit
import os

/// Tells real words from misspellings, so term correction leaves ordinary Russian and
/// English words alone. The app asks the system spell checker; tests pass a fixed word
/// list or a missing dictionary.
struct WordLexicon: Sendable {
    /// Whether a dictionary for the language code ("ru", "en") is available.
    let hasLanguage: @Sendable (_ language: String) -> Bool
    /// Whether the word is spelled correctly in the language; false without a dictionary.
    let isCorrect: @Sendable (_ word: String, _ language: String) -> Bool

    static let system = WordLexicon(hasLanguage: { SystemSpelling.hasLanguage($0) },
                                    isCorrect: { SystemSpelling.isCorrect($0, language: $1) })

    /// A fixed word list per language code ("ru", "en"), so results do not depend on the
    /// system's dictionaries. A language without a list has no dictionary. A word is
    /// correct when its list holds it exactly or holds its lowercase form, the way a spell
    /// checker accepts "Дом" and "ДОМ" for «дом» but not «москва» for «Москва».
    static func fixed(_ lists: [String: [String]]) -> WordLexicon {
        let sets = lists.mapValues(Set.init)
        return WordLexicon(hasLanguage: { sets[$0] != nil }, isCorrect: { word, language in
            guard let words = sets[language], !word.isEmpty else { return false }
            return words.contains(word) || words.contains(word.lowercased())
        })
    }

    /// True when every word is spelled correctly in the language of its script:
    /// Cyrillic as Russian, Latin as English. A misspelled word, a word mixing scripts
    /// or a missing dictionary makes it false.
    func areRealWords(_ words: [String]) -> Bool {
        guard !words.isEmpty else { return false }
        return words.allSatisfy { word in
            let hasCyrillic = word.contains(where: TextScanning.isCyrillic)
            let hasLatin = word.contains(where: TextScanning.isLatinLetter)
            switch (hasCyrillic, hasLatin) {
            case (true, false): return isCorrect(word, "ru")
            case (false, true): return isCorrect(word, "en")
            case (false, false): return true
            case (true, true): return false
            }
        }
    }

    /// True when every word, lowercased, is an ordinary English word: Docker and user id
    /// are, Redis and GigaAM are not.
    func areEnglishWords(_ words: [String]) -> Bool {
        !words.isEmpty && words.allSatisfy { isCorrect($0.lowercased(), "en") }
    }
}

/// The system spell checker behind `WordLexicon.system` and sentence case.
enum SystemSpelling {
    /// Cached answers are dropped once this many words have been checked, so a long
    /// session does not grow the cache without end.
    static let cacheLimit = 20_000

    private static let cache = OSAllocatedUnfairLock(initialState: [String: Bool]())
    /// NSSpellChecker is not safe to call from several threads at once, and the pipeline
    /// runs on the main actor in the app but on other threads in tests and the bench,
    /// so every call into it goes through this lock.
    private static let checkerLock = NSLock()
    private static let languages: Set<String> = checkerLock.withLock {
        Set(NSSpellChecker.shared.availableLanguages)
    }

    /// Whether the system has a spelling dictionary for the language code.
    static func hasLanguage(_ language: String) -> Bool {
        languages.contains(language)
    }

    /// False when the word is misspelled or the system has no dictionary for the language.
    static func isCorrect(_ word: String, language: String) -> Bool {
        guard !word.isEmpty, hasLanguage(language) else { return false }
        let key = language + ":" + word
        if let known = cache.withLock({ $0[key] }) {
            return known
        }
        let misspelled = checkerLock.withLock {
            NSSpellChecker.shared.checkSpelling(of: word, startingAt: 0, language: language, wrap: false,
                                                inSpellDocumentWithTag: 0, wordCount: nil)
        }
        let correct = misspelled.location == NSNotFound
        cache.withLock { cache in
            if cache.count >= cacheLimit {
                cache.removeAll(keepingCapacity: true)
            }
            cache[key] = correct
        }
        return correct
    }
}
