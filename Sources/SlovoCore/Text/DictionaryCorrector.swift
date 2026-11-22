import Foundation

/// Applies Dictionary rules: explicit replacements first, then fuzzy correction of
/// terms written the wrong way (Cyrillic transliterations, near misses).
public struct DictionaryCorrector: TextStage {
    /// Largest normalized edit distance that still counts as the term.
    public var maximumDistance: Double
    /// Terms shorter than this many letters are never fuzzy-matched.
    public var minimumTermLetters: Int
    /// Tells ordinary words from terms written the wrong way.
    var lexicon: WordLexicon

    /// Share of a term's consonants that may differ, voicing pairs counting half.
    /// Russian word forms tend to sit within the edit distance of a term but lose or
    /// swap its consonants (версию against Vercel, скрыть against SQLite).
    static let consonantTolerance = 0.2

    /// Without a dictionary for the window's language, keys this short may differ from
    /// the term by one edit at most: «кладу» against Claude, "cloud" against Claude.
    static let fallbackShortKeyLetters = 6

    /// Russian dictionary words that in dictation almost always mean the product they
    /// were borrowed from: «докер» is Docker far more often than a dock worker. Any other
    /// real Russian word stays itself even when it sounds exactly like a term («мастер»
    /// against master), unless it is written as a name in mid-sentence («в Редис»).
    /// Lowercase, ё written as е.
    static let borrowedNames: Set<String> = ["докер", "телеграм", "джава", "андроид", "линукс", "твиттер", "инстаграм"]

    public init(maximumDistance: Double = 0.34, minimumTermLetters: Int = 4) {
        self.init(maximumDistance: maximumDistance, minimumTermLetters: minimumTermLetters, lexicon: .system)
    }

    init(maximumDistance: Double = 0.34, minimumTermLetters: Int = 4, lexicon: WordLexicon) {
        self.maximumDistance = maximumDistance
        self.minimumTermLetters = minimumTermLetters
        self.lexicon = lexicon
    }

    // MARK: - Replacements

    /// Whole-word, case-insensitive "from → to" replacements; returns the count applied.
    /// Whitespace inside a "from" phrase matches any run of whitespace, and ё matches е.
    public func applyReplacements(_ text: String, items: [DictionaryItem]) -> (text: String, count: Int) {
        let result = replacing(text, items: items)
        return (String(result.characters), result.count)
    }

    /// `applyReplacements`, also returning where replacements were written as ranges of
    /// `characters`, so term correction can leave the user's own spellings alone.
    func replacing(_ text: String, items: [DictionaryItem]) -> (characters: [Character], count: Int, written: [Range<Int>]) {
        let rules = items
            .filter { $0.kind == .replacement }
            .compactMap { item -> ReplacementRule? in
                let pattern = ReplacementRule.pattern(for: item.text)
                let carriesCase = item.text.trimmingCharacters(in: .whitespaces).first?.isLowercase ?? false
                return pattern.isEmpty ? nil : ReplacementRule(pattern: pattern, replacement: item.replacement, carriesCase: carriesCase)
            }
            .enumerated()
            .sorted { lhs, rhs in
                lhs.element.pattern.count != rhs.element.pattern.count
                    ? lhs.element.pattern.count > rhs.element.pattern.count
                    : lhs.offset < rhs.offset
            }
            .map(\.element)
        let characters = Array(text)
        guard !rules.isEmpty, !text.isEmpty else { return (characters, 0, []) }

        let folded = characters.map(TextScanning.fold)
        var output: [Character] = []
        var written: [Range<Int>] = []
        var count = 0
        var index = 0
        var copiedUpTo = 0
        while index < characters.count {
            guard index == 0 || !TextScanning.isWordCharacter(characters[index - 1]) || !TextScanning.isWordCharacter(characters[index]),
                  let (rule, end) = firstMatch(of: rules, in: characters, folded: folded, at: index) else {
                index += 1
                continue
            }
            let original = characters[index..<end]
            var replacement = Array(rule.replacement)
            // A lowercase trigger («комит») carries a capital at a sentence start; a trigger
            // the user wrote with capitals (Bb → bb) asks for the replacement's case as is.
            if rule.carriesCase, let initial = original.first, initial.isUppercase,
               let target = replacement.first, target.isLowercase, TextScanning.fold(initial) == TextScanning.fold(target),
               !replacement.prefix(while: TextScanning.isWordCharacter).contains(where: \.isUppercase) {
                TextScanning.setCase(of: &replacement, at: 0, uppercase: true)
            }
            output += characters[copiedUpTo..<index]
            let start = output.count
            output += replacement
            written.append(start..<output.count)
            if !original.elementsEqual(replacement) {
                count += 1
            }
            copiedUpTo = end
            index = max(end, index + 1)
        }
        output += characters[copiedUpTo...]
        return (output, count, written)
    }

    private func firstMatch(of rules: [ReplacementRule], in characters: [Character], folded: [String],
                            at start: Int) -> (ReplacementRule, Int)? {
        for rule in rules {
            if let end = rule.match(in: characters, folded: folded, at: start) {
                return (rule, end)
            }
        }
        return nil
    }

    // MARK: - Terms

    /// Replaces windows of 1–3 words that match a term containing Latin letters;
    /// returns the count applied.
    ///
    /// Words are compared through a spelling key: Cyrillic is transliterated, English
    /// spellings are reduced to how they sound (actions → akshns, docker → doker) and
    /// doubled letters collapse. A window matches when the normalized edit distance of
    /// the keys is at most `maximumDistance` and the guards in `accepts` pass. Among
    /// overlapping matches the longer term wins, then the closer match.
    public func correctTerms(_ text: String, terms: [String]) -> (text: String, count: Int) {
        let prepared = prepareTerms(terms)
        guard !prepared.isEmpty, !text.isEmpty else { return (text, 0) }

        let characters = Array(text)
        let words = TextScanning.words(in: characters, joiners: TextScanning.nameJoiners)
        let context = WordContext(characters: characters, words: words)
        var candidates: [TermCandidate] = []
        for first in words.indices {
            for size in 1...3 {
                let last = first + size - 1
                guard last < words.count else { break }
                // Punctuation ends a phrase: "упал, посмотри" is never one window.
                if size > 1, !characters[words[last - 1].upperBound..<words[last].lowerBound].allSatisfy(\.isWhitespace) {
                    break
                }
                let window = TermWindow(characters: characters, words: words, first: first, last: last, context: context)
                guard !window.key.isEmpty else { continue }
                if size > 1, window.hasShortCyrillicEdge {
                    continue
                }
                for term in prepared {
                    guard let match = accepts(window, term) else { continue }
                    candidates.append(TermCandidate(term: term, match: match, firstWord: first, lastWord: last))
                }
            }
        }
        guard !candidates.isEmpty else { return (text, 0) }

        candidates.sort { lhs, rhs in
            if lhs.term.key.count != rhs.term.key.count { return lhs.term.key.count > rhs.term.key.count }
            if lhs.match.distance != rhs.match.distance { return lhs.match.distance < rhs.match.distance }
            if lhs.wordCount != rhs.wordCount { return lhs.wordCount < rhs.wordCount }
            if lhs.firstWord != rhs.firstWord { return lhs.firstWord < rhs.firstWord }
            return lhs.term.text < rhs.term.text
        }
        var taken = [Bool](repeating: false, count: words.count)
        var chosen: [TermCandidate] = []
        for candidate in candidates where !taken[candidate.firstWord...candidate.lastWord].contains(true) {
            for index in candidate.firstWord...candidate.lastWord {
                taken[index] = true
            }
            chosen.append(candidate)
        }
        chosen.sort { $0.firstWord < $1.firstWord }

        var output = ""
        var copiedUpTo = 0
        var count = 0
        for candidate in chosen {
            let start = words[candidate.firstWord].lowerBound
            let end = words[candidate.lastWord].upperBound
            let original = String(characters[start..<end])
            // A match that keeps the text still claims its words, so no other term rewrites them.
            guard candidate.match.rewrites, original != candidate.term.text else { continue }
            output += String(characters[copiedUpTo..<start])
            output += candidate.term.text
            copiedUpTo = end
            count += 1
        }
        output += String(characters[copiedUpTo...])
        return (output, count)
    }

    /// How the window matches the term, or nil when it does not count as the term.
    func accepts(_ window: TermWindow, _ term: PreparedTerm) -> TermMatch? {
        let windowKey = window.key
        let termKey = term.key
        // A close spelling may still be a different version or identifier.
        guard windowKey.count >= minimumTermLetters, window.numberTokens == term.numberTokens else { return nil }
        let longest = max(windowKey.count, termKey.count)
        guard Double(abs(windowKey.count - termKey.count)) <= maximumDistance * Double(longest) else { return nil }
        let edits = SpellingKey.editDistance(windowKey, termKey)
        let distance = Double(edits) / Double(longest)
        guard distance <= maximumDistance else { return nil }

        let termConsonants = SpellingKey.consonants(termKey)
        let consonantDistance = SpellingKey.consonantDistance(SpellingKey.consonants(windowKey), termConsonants)
        guard consonantDistance <= Self.consonantTolerance * Double(termConsonants.count) + 1e-9 else { return nil }

        // A word that is only the start of a longer term is a different word: Swift, not SwiftUI.
        if windowKey.count < termKey.count, termKey.starts(with: windowKey) {
            return nil
        }

        // With no dictionary to ask, a short near word is too likely to be an ordinary one.
        let language = window.containsCyrillic ? "ru" : "en"
        if !lexicon.hasLanguage(language), longest <= Self.fallbackShortKeyLetters, edits > 1 {
            return nil
        }
        return window.containsCyrillic
            ? acceptsCyrillic(window, term, distance: distance)
            : acceptsLatin(window, term, distance: distance)
    }

    private func acceptsCyrillic(_ window: TermWindow, _ term: PreparedTerm, distance: Double) -> TermMatch? {
        // Real Russian words and names are not terms written the wrong way: «кладу» is not
        // Claude, «Версале» is not Vercel. Only a word that sounds exactly like the term can
        // still be it, and only when it is a borrowed product name (докер, телеграм) or the
        // engine wrote it as a name in mid-sentence («в Редис»): a lowercase «редис» is the
        // vegetable and «мастер» is a craftsman, whatever terms sound the same.
        if lexicon.areRealWords(window.words) {
            // The word must not double a letter the term has once: «телеграмм» (of
            // телеграмма) is not Telegram, while «Гигам» may still be GigaAM.
            guard window.exactKey == term.exactKey || window.exactKey == term.key else { return nil }
            let isBorrowedName = Self.borrowedNames.contains(window.foldedText)
            // A capital in mid-sentence marks a name, but not when the term is an ordinary
            // English word: «Мастер и Маргарита» is a title, not master.
            let isWrittenAsName = window.isCapitalizedName && !lexicon.areEnglishWords(term.words)
            guard isBorrowedName || isWrittenAsName else { return nil }
        }
        return TermMatch(distance: distance, rewrites: true)
    }

    private func acceptsLatin(_ window: TermWindow, _ term: PreparedTerm, distance: Double) -> TermMatch? {
        // Engines that already wrote Latin letters got the spelling mostly right.
        let plainLongest = max(window.plainLetters.count, term.plainLetters.count)
        guard plainLongest > 0,
              Double(SpellingKey.editDistance(window.plainLetters, term.plainLetters)) / Double(plainLongest) <= maximumDistance,
              window.words.count >= term.wordCount else { return nil }
        let caseOnly = window.foldedText == term.foldedText

        guard lexicon.areRealWords(window.words) else {
            // Without an English dictionary every word looks misspelled, so plain casing
            // stays: "option" is not rewritten as Option.
            let keepsCase = caseOnly && !term.hasNameCasing && !lexicon.hasLanguage("en")
            return TermMatch(distance: distance, rewrites: !keepsCase)
        }
        if window.isInsideRussian {
            // Inside Russian speech the engine writes English names in Latin letters, with
            // sentence casing or split into dictionary words: "User Id", "Test Flit". A
            // single word is still never swapped for a different one: "cloud" stays.
            guard window.plainLetters == term.plainLetters || window.words.count > term.wordCount else { return nil }
            return TermMatch(distance: distance, rewrites: true)
        }
        // English text keeps its words ("Jason", "Read me") and takes a term's casing only
        // when the casing is part of the name: "json" becomes JSON, "option" stays.
        guard caseOnly else { return nil }
        return TermMatch(distance: distance, rewrites: term.hasNameCasing)
    }

    func prepareTerms(_ terms: [String]) -> [PreparedTerm] {
        var seen = Set<String>()
        var prepared: [PreparedTerm] = []
        for raw in terms {
            let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, seen.insert(text).inserted,
                  text.contains(where: TextScanning.isLatinLetter),
                  text.filter(\.isLetter).count >= minimumTermLetters else { continue }
            let characters = Array(text)
            let ranges = TextScanning.words(in: characters, joiners: TextScanning.nameJoiners)
            let words = ranges.map { String(characters[$0]) }
            let key = SpellingKey.make(words)
            guard !key.isEmpty else { continue }
            let exactKey = SpellingKey.make(words, collapsingRepeats: false)
            // Inner capitals are part of a name: macOS, JSON, GitHub. Redis and user id are plain.
            let hasNameCasing = ranges.contains { characters[$0].dropFirst().contains(where: \.isUppercase) }
            prepared.append(PreparedTerm(text: text, words: words, key: key, exactKey: exactKey,
                                         plainLetters: SpellingKey.plainLetters(text),
                                         numberTokens: SpellingKey.numberTokens(text),
                                         foldedText: TextScanning.fold(text), hasNameCasing: hasNameCasing))
        }
        return prepared
    }

    // MARK: - Spoken names

    /// Names whose spelling does not sound the way they are said, in the form a Russian-only
    /// engine writes them: «Клод» is Claude, «апи» is API. Fuzzy correction cannot find them
    /// (Claude is spelled like «клауде»; API is too short), so these are matched as words, in
    /// any case form, and only for terms in the Dictionary. Keys and aliases are folded.
    static let spokenAliases: [String: [String]] = [
        "claude": ["клод"],
        "api": ["апи", "эйпиай"],
        "ios": ["айос"],
        "xcode": ["икскод"],
    ]

    /// Endings a borrowed name takes in Russian: «у Клода», «Клоду», «с Клодом», «о Клоде».
    static let caseEndings = ["ом", "ем", "а", "я", "у", "ю", "е"]

    /// Writes the Dictionary's term for every spoken alias of it; returns the count applied.
    func applySpokenAliases(_ text: String, terms: [String]) -> (text: String, count: Int) {
        var termByAlias: [String: String] = [:]
        for term in terms {
            let trimmed = term.trimmingCharacters(in: .whitespacesAndNewlines)
            for alias in Self.spokenAliases[TextScanning.fold(trimmed)] ?? [] where termByAlias[alias] == nil {
                termByAlias[alias] = trimmed
            }
        }
        guard !termByAlias.isEmpty, !text.isEmpty else { return (text, 0) }
        let characters = Array(text)
        var output = ""
        var copiedUpTo = 0
        var count = 0
        for range in TextScanning.words(in: characters) {
            guard let term = Self.aliasedTerm(TextScanning.fold(String(characters[range])), termByAlias) else { continue }
            output += String(characters[copiedUpTo..<range.lowerBound])
            output += term
            copiedUpTo = range.upperBound
            count += 1
        }
        output += String(characters[copiedUpTo...])
        return (output, count)
    }

    static func aliasedTerm(_ folded: String, _ termByAlias: [String: String]) -> String? {
        if let term = termByAlias[folded] {
            return term
        }
        for ending in caseEndings where folded.hasSuffix(ending) {
            if let term = termByAlias[String(folded.dropLast(ending.count))] {
                return term
            }
        }
        return nil
    }

    // MARK: - Stage

    public func apply(to result: inout TextPipelineResult, context: TextPipelineContext) {
        guard !context.dictionary.isEmpty else { return }
        let terms = context.dictionary.filter { $0.kind == .term }.map(\.text)
        let characters = Array(result.text)
        let protected = SnippetProtection.ranges(in: characters, result: result, snippets: context.snippets)
        var text = ""
        var replacements = 0
        var fixes = 0
        for segment in SnippetProtection.segments(characters, protected: protected) {
            guard !segment.isProtected else {
                text += segment.text
                continue
            }
            let replaced = replacing(segment.text, items: context.dictionary)
            replacements += replaced.count
            // A replacement's output is the user's explicit spelling; terms never rewrite it.
            for piece in SnippetProtection.segments(replaced.characters, protected: replaced.written) {
                guard !piece.isProtected else {
                    text += piece.text
                    continue
                }
                let aliased = applySpokenAliases(piece.text, terms: terms)
                let corrected = correctTerms(aliased.text, terms: terms)
                text += corrected.text
                fixes += aliased.count + corrected.count
            }
        }
        result.text = text
        result.replacementsApplied += replacements
        result.termFixesApplied += fixes
    }
}

// MARK: - Replacement matching

struct ReplacementRule {
    enum Element: Hashable {
        case character(String)
        case whitespace
    }

    let pattern: [Element]
    let replacement: String
    /// Whether a capitalized match capitalizes a lowercase replacement.
    var carriesCase = false

    static func pattern(for from: String) -> [Element] {
        var elements: [Element] = []
        for character in from.trimmingCharacters(in: .whitespacesAndNewlines) {
            if character.isWhitespace {
                if elements.last != .whitespace {
                    elements.append(.whitespace)
                }
            } else {
                elements.append(.character(TextScanning.fold(character)))
            }
        }
        return elements
    }

    /// The end offset of a match starting at `start`, respecting word boundaries.
    func match(in characters: [Character], folded: [String], at start: Int) -> Int? {
        guard let firstElement = pattern.first, case .character = firstElement else { return nil }
        var position = start
        for element in pattern {
            switch element {
            case .character(let expected):
                guard position < characters.count, folded[position] == expected else { return nil }
                position += 1
            case .whitespace:
                guard position < characters.count, characters[position].isWhitespace else { return nil }
                while position < characters.count, characters[position].isWhitespace {
                    position += 1
                }
            }
        }
        // Whole words only: "кот" must not match inside «котик».
        if TextScanning.isWordCharacter(characters[start]), start > 0, TextScanning.isWordCharacter(characters[start - 1]) {
            return nil
        }
        if TextScanning.isWordCharacter(characters[position - 1]), position < characters.count,
           TextScanning.isWordCharacter(characters[position]) {
            return nil
        }
        return position
    }
}

// MARK: - Term matching

struct PreparedTerm {
    let text: String
    let words: [String]
    let key: [UInt8]
    /// The key with doubled letters kept.
    let exactKey: [UInt8]
    let plainLetters: [UInt8]
    let numberTokens: [Substring]
    let foldedText: String
    /// Casing that belongs to the name rather than to a sentence: macOS, JSON.
    let hasNameCasing: Bool

    var wordCount: Int { words.count }
}

/// How a window matches a term.
struct TermMatch {
    let distance: Double
    /// False when the window counts as the term but keeps its own spelling, as a real
    /// English word in plain casing does ("option" for Option).
    let rewrites: Bool
}

/// Where each word of a text stands: its script and whether it starts a sentence.
struct WordContext {
    let hasCyrillic: [Bool]
    let hasLatin: [Bool]
    let startsSentence: [Bool]
    /// The nearest word with letters before each word (numbers skipped), if any.
    private let previousLettered: [Int?]
    /// The nearest word with letters after each word, if any.
    private let nextLettered: [Int?]

    init(characters: [Character], words: [Range<Int>]) {
        let hasCyrillic = words.map { characters[$0].contains(where: TextScanning.isCyrillic) }
        let hasLatin = words.map { characters[$0].contains(where: TextScanning.isLatinLetter) }
        self.hasCyrillic = hasCyrillic
        self.hasLatin = hasLatin
        startsSentence = words.indices.map { index in
            guard index > 0 else { return true }
            return characters[words[index - 1].upperBound..<words[index].lowerBound]
                .contains { ".!?…".contains($0) || $0.isNewline }
        }
        var previous = [Int?](repeating: nil, count: words.count)
        var next = [Int?](repeating: nil, count: words.count)
        var lastLettered: Int?
        for index in words.indices {
            previous[index] = lastLettered
            if hasCyrillic[index] || hasLatin[index] {
                lastLettered = index
            }
        }
        lastLettered = nil
        for index in words.indices.reversed() {
            next[index] = lastLettered
            if hasCyrillic[index] || hasLatin[index] {
                lastLettered = index
            }
        }
        previousLettered = previous
        nextLettered = next
    }

    /// Whether the nearest words with letters around `first...last` are all Russian, so
    /// the words in between are English written inside Russian speech.
    func isInsideRussian(first: Int, last: Int) -> Bool {
        let neighbours = [previousLettered[first], nextLettered[last]].compactMap { $0 }
        return !neighbours.isEmpty && neighbours.allSatisfy { !hasLatin[$0] } && neighbours.contains { hasCyrillic[$0] }
    }
}

struct TermWindow {
    let words: [String]
    let key: [UInt8]
    /// The key with doubled letters kept.
    let exactKey: [UInt8]
    let plainLetters: [UInt8]
    let numberTokens: [Substring]
    let foldedText: String
    let containsCyrillic: Bool
    let hasShortCyrillicEdge: Bool
    /// Latin words with Russian words around them.
    let isInsideRussian: Bool
    /// Every word capitalized, and not because a sentence starts with it: «в Редис».
    let isCapitalizedName: Bool

    init(characters: [Character], words ranges: [Range<Int>], first: Int, last: Int, context: WordContext) {
        let words = ranges[first...last].map { String(characters[$0]) }
        self.words = words
        key = SpellingKey.make(words)
        exactKey = SpellingKey.make(words, collapsingRepeats: false)
        let span = String(characters[ranges[first].lowerBound..<ranges[last].upperBound])
        plainLetters = SpellingKey.plainLetters(span)
        numberTokens = SpellingKey.numberTokens(span)
        foldedText = TextScanning.fold(span)
        containsCyrillic = span.contains(where: TextScanning.isCyrillic)
        // Prepositions and conjunctions (в, и, на) never belong to a term.
        let isShortCyrillic = { (word: String) in word.count <= 2 && word.allSatisfy(TextScanning.isCyrillic) }
        hasShortCyrillicEdge = words.first.map(isShortCyrillic) == true || words.last.map(isShortCyrillic) == true
        isInsideRussian = context.isInsideRussian(first: first, last: last)
        isCapitalizedName = !context.startsSentence[first] && words.allSatisfy { $0.first?.isUppercase == true }
    }
}

struct TermCandidate {
    let term: PreparedTerm
    let match: TermMatch
    let firstWord: Int
    let lastWord: Int

    var wordCount: Int { lastWord - firstWord + 1 }
}

/// A rough pronunciation key in ASCII, so a Russian spelling of an English term and
/// the term itself come out close.
enum SpellingKey {
    /// - Parameter collapsingRepeats: doubled letters become one (the default), so
    ///   «постгресс» sounds like Postgres.
    static func make(_ words: [String], collapsingRepeats: Bool = true) -> [UInt8] {
        var bytes: [UInt8] = []
        for word in words {
            let compact = Array(Transliterator.compactLatin(word).utf8)
            // A leading x is read as "eks": Xcode.
            if compact.first == ascii("x") {
                bytes.append(ascii("e"))
            }
            bytes += compact
        }
        bytes = replace(bytes, "tion", with: "shn")
        bytes = replace(bytes, "ph", with: "f")
        bytes = replace(bytes, "wh", with: "v")
        bytes = replace(bytes, "ck", with: "k")
        bytes = replace(bytes, "qu", with: "kv")
        bytes = replace(bytes, "q", with: "k")
        bytes = replace(bytes, "x", with: "ks")
        bytes = replace(bytes, "igh", with: "ai")
        bytes = softenC(bytes)
        bytes = replace(bytes, "dzh", with: "j")
        bytes = replace(bytes, "w", with: "v")
        bytes = replace(bytes, "z", with: "s")
        bytes = replace(bytes, "y", with: "i")
        return collapsingRepeats ? collapseRepeats(bytes) : bytes
    }

    /// Lowercased ASCII letters and digits of a text, without transliteration.
    static func plainLetters(_ text: String) -> [UInt8] {
        Array(text.lowercased().utf8).filter { (0x61...0x7A).contains($0) || (0x30...0x39).contains($0) }
    }

    /// Keep separators inside numbers so 27-2, 27.2 and 272 stay distinct.
    static func numberTokens(_ text: String) -> [Substring] {
        var tokens: [Substring] = []
        var index = text.startIndex
        while index < text.endIndex {
            let start = index
            text.formIndex(after: &index)
            guard text[start].isNumber else { continue }
            while index < text.endIndex {
                let next = text.index(after: index)
                if text[index].isNumber {
                    index = next
                } else if ".,:/–−-".contains(text[index]), next < text.endIndex, text[next].isNumber {
                    index = text.index(after: next)
                } else {
                    break
                }
            }
            tokens.append(text[start..<index])
        }
        return tokens
    }

    static func consonants(_ key: [UInt8]) -> [UInt8] {
        key.filter { !vowels.contains($0) }
    }

    static func editDistance(_ lhs: [UInt8], _ rhs: [UInt8]) -> Int {
        if lhs.isEmpty { return rhs.count }
        if rhs.isEmpty { return lhs.count }
        var previous = Array(0...rhs.count)
        var current = [Int](repeating: 0, count: rhs.count + 1)
        for i in 1...lhs.count {
            current[0] = i
            for j in 1...rhs.count {
                let substitution = previous[j - 1] + (lhs[i - 1] == rhs[j - 1] ? 0 : 1)
                current[j] = min(previous[j] + 1, current[j - 1] + 1, substitution)
            }
            swap(&previous, &current)
        }
        return previous[rhs.count]
    }

    /// Edit distance where a voiced consonant swapped for its voiceless pair (d/t,
    /// b/p, g/k, z/s, v/f) costs half.
    static func consonantDistance(_ lhs: [UInt8], _ rhs: [UInt8]) -> Double {
        if lhs.isEmpty { return Double(rhs.count) }
        if rhs.isEmpty { return Double(lhs.count) }
        var previous = (0...rhs.count).map(Double.init)
        var current = [Double](repeating: 0, count: rhs.count + 1)
        for i in 1...lhs.count {
            current[0] = Double(i)
            for j in 1...rhs.count {
                let cost: Double = lhs[i - 1] == rhs[j - 1] ? 0 : (arePaired(lhs[i - 1], rhs[j - 1]) ? 0.5 : 1)
                current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost)
            }
            swap(&previous, &current)
        }
        return previous[rhs.count]
    }

    private static let vowels: Set<UInt8> = Set("aeiou".utf8)
    private static let pairs: [Set<UInt8>] = ["bp", "dt", "gk", "sz", "vf"].map { Set($0.utf8) }

    private static func arePaired(_ lhs: UInt8, _ rhs: UInt8) -> Bool {
        pairs.contains { $0.contains(lhs) && $0.contains(rhs) }
    }

    private static func ascii(_ character: Unicode.Scalar) -> UInt8 {
        UInt8(truncatingIfNeeded: character.value)
    }

    private static func replace(_ bytes: [UInt8], _ pattern: String, with replacement: String) -> [UInt8] {
        let needle = Array(pattern.utf8)
        let substitute = Array(replacement.utf8)
        guard bytes.count >= needle.count else { return bytes }
        var output: [UInt8] = []
        output.reserveCapacity(bytes.count + 4)
        var index = 0
        while index < bytes.count {
            if index + needle.count <= bytes.count, bytes[index..<(index + needle.count)].elementsEqual(needle) {
                output += substitute
                index += needle.count
            } else {
                output.append(bytes[index])
                index += 1
            }
        }
        return output
    }

    /// "c" sounds like "s" before e, i and y and like "k" elsewhere; "ch" stays.
    private static func softenC(_ bytes: [UInt8]) -> [UInt8] {
        var output: [UInt8] = []
        output.reserveCapacity(bytes.count)
        var index = 0
        while index < bytes.count {
            let byte = bytes[index]
            guard byte == ascii("c") else {
                output.append(byte)
                index += 1
                continue
            }
            let next = index + 1 < bytes.count ? bytes[index + 1] : 0
            if next == ascii("h") {
                output += [ascii("c"), ascii("h")]
                index += 2
                continue
            }
            output.append([ascii("e"), ascii("i"), ascii("y")].contains(next) ? ascii("s") : ascii("k"))
            index += 1
        }
        return output
    }

    private static func collapseRepeats(_ bytes: [UInt8]) -> [UInt8] {
        var output: [UInt8] = []
        output.reserveCapacity(bytes.count)
        for byte in bytes where output.last != byte {
            output.append(byte)
        }
        return output
    }
}
