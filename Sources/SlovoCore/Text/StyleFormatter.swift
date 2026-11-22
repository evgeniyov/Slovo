import Foundation

/// Applies the frontmost app category's style: capitalization and final punctuation.
///
/// - Formal: sentence starts capitalized, punctuation kept, a final period added.
/// - Casual: sentence starts capitalized, the final period of the last sentence dropped.
/// - Very casual: sentence starts lowercased except dictionary terms and words that
///   look like names, sentence-ending periods dropped.
///
/// Only sentence starts change case; the rest of the text keeps the engine's casing.
/// A period after an abbreviation («т. е.», «г.», «руб.», "etc.") does not start a
/// sentence unless the engine capitalized the next word, and it is never dropped.
public struct StyleFormatter: TextStage {
    /// Tells English words from names for lowercase terms, and proper nouns from ordinary
    /// words for Very casual. The app asks the system spell checker; tests and the shared
    /// fixtures pass a fixed word list.
    var lexicon: WordLexicon

    public init() {
        self.init(lexicon: .system)
    }

    init(lexicon: WordLexicon) {
        self.lexicon = lexicon
    }

    /// - Parameter protectedTerms: dictionary terms whose casing Very casual keeps.
    public func format(_ text: String, style: StylePreset, protectedTerms: [String]) -> String {
        format(Array(text), style: style, protectedTerms: protectedTerms, locked: [])
    }

    /// `locked` ranges (snippet expansions) keep their characters exactly.
    func format(_ input: [Character], style: StylePreset, protectedTerms: [String], locked lockedRanges: [Range<Int>]) -> String {
        guard input.contains(where: TextScanning.isWordCharacter) else { return String(input) }
        var characters = input
        var locked = [Bool](repeating: false, count: characters.count)
        for range in lockedRanges {
            for index in range where index < locked.count {
                locked[index] = true
            }
        }
        let words = TextScanning.words(in: characters, joiners: TextScanning.wordJoiners)
        let terms = TermCasing(protectedTerms, lexicon: lexicon)
        let starts = sentenceStarts(characters, words: words)

        switch style {
        case .formal, .casual:
            for wordIndex in starts {
                let position = words[wordIndex].lowerBound
                guard !locked[position], characters[position].isLowercase,
                      !Self.isCaseSensitive(characters, word: words[wordIndex]),
                      !terms.keepsLowercaseStart(characters, words: words, at: wordIndex) else { continue }
                TextScanning.setCase(of: &characters, at: position, uppercase: true)
            }
        case .veryCasual:
            let context = NameContext(characters: characters, words: words, starts: Set(starts), lexicon: lexicon)
            for wordIndex in starts {
                let range = words[wordIndex]
                guard !locked[range.lowerBound] else { continue }
                if let termWord = terms.match(characters, words: words, at: wordIndex) {
                    // A term keeps its dictionary spelling: «Docker», «user id».
                    if termWord.count == range.count {
                        for (offset, character) in termWord.enumerated() where !locked[range.lowerBound + offset] {
                            characters[range.lowerBound + offset] = character
                        }
                    }
                    continue
                }
                guard characters[range.lowerBound].isUppercase, !context.looksLikeName(wordIndex) else { continue }
                TextScanning.setCase(of: &characters, at: range.lowerBound, uppercase: false)
            }
        }

        var removals = Set<Int>()
        var appendPeriodAt: Int?
        if let lastVisible = characters.lastIndex(where: { !$0.isWhitespace }) {
            // An emoticon ends the text on its own: "спасибо :)" gets no period, and its
            // colon is not punctuation to replace.
            let endsWithEmoticon = Self.isEmoticonEnd(characters, at: lastVisible)
            var inner = lastVisible
            while inner > 0, Self.closers.contains(characters[inner]) {
                inner -= 1
            }
            switch style {
            case .formal:
                if !locked[inner], !endsWithEmoticon {
                    if ",;:".contains(characters[inner]) {
                        characters[inner] = "."
                    } else if TextScanning.isWordCharacter(characters[inner]) || "%+".contains(characters[inner]) {
                        appendPeriodAt = lastVisible + 1
                    }
                }
            case .casual:
                if !endsWithEmoticon, isDroppablePeriod(characters, at: inner, locked: locked) {
                    removals.insert(inner)
                }
            case .veryCasual:
                for index in characters.indices where characters[index] == "." && endsSentence(characters, at: index)
                    && isDroppablePeriod(characters, at: index, locked: locked) {
                    removals.insert(index)
                }
            }
        }

        var output = ""
        output.reserveCapacity(characters.count + 1)
        for (index, character) in characters.enumerated() {
            if index == appendPeriodAt {
                output.append(".")
            }
            if !removals.contains(index) {
                output.append(character)
            }
        }
        if appendPeriodAt == characters.count {
            output.append(".")
        }
        return output
    }

    private static let closers: Set<Character> = ["\"", "'", "»", "”", "’", ")", "]", "}"]

    /// ":)", ";-)", ":(", ":D" or ":P" ending at `end`.
    private static func isEmoticonEnd(_ characters: [Character], at end: Int) -> Bool {
        guard end >= 1, ")(DP".contains(characters[end]) else { return false }
        var eyes = end - 1
        if characters[eyes] == "-", eyes >= 1 {
            eyes -= 1
        }
        return characters[eyes] == ":" || characters[eyes] == ";"
    }

    /// A sentence-start word whose case carries meaning: part of a link, an address, a
    /// path or a file or domain name, or a name with an inner capital (iPhone, eBay).
    private static func isCaseSensitive(_ characters: [Character], word: Range<Int>) -> Bool {
        if characters[word].dropFirst().contains(where: \.isUppercase) {
            return true
        }
        var start = word.lowerBound
        while start > 0, !characters[start - 1].isWhitespace {
            start -= 1
        }
        var end = word.upperBound
        while end < characters.count, !characters[end].isWhitespace {
            end += 1
        }
        let token = Array(characters[start..<end])
        let text = String(token)
        return text.contains("://") || text.contains("@") || text.contains("/") || hasNameExtension(token)
    }

    /// "github.com", "index.html": a dot after a word character and before at least two
    /// letters. «т.е.» and "e.g." have one letter after each dot and still count as words.
    private static func hasNameExtension(_ token: [Character]) -> Bool {
        token.indices.contains { index in
            guard token[index] == ".", index > 0, TextScanning.isWordCharacter(token[index - 1]) else { return false }
            return token[(index + 1)...].prefix(while: \.isLetter).count >= 2
        }
    }

    /// Word indices that begin a sentence: the first word, and words after ., !, ?
    /// or … followed by a space, or after a line break.
    private func sentenceStarts(_ characters: [Character], words: [Range<Int>]) -> [Int] {
        guard !words.isEmpty else { return [] }
        var starts = [0]
        for index in words.indices.dropFirst() {
            let gap = characters[words[index - 1].upperBound..<words[index].lowerBound]
            if gap.contains(where: \.isNewline) {
                starts.append(index)
                continue
            }
            var terminator: Character?
            var spaced = false
            for character in gap {
                if ".!?…".contains(character) {
                    terminator = character
                } else if character.isWhitespace, terminator != nil {
                    spaced = true
                    break
                }
            }
            guard let found = terminator, spaced else { continue }
            if found == ".", isAbbreviationPeriod(characters, at: words[index - 1].upperBound) {
                continue
            }
            // «т. е. в среду», «в 2025 г. мы»: an abbreviation's period, unless the engine
            // itself started a new sentence with a capital.
            if found == ".", gap.first == ".", Self.isAbbreviation(characters, word: words[index - 1]),
               !characters[words[index].lowerBound].isUppercase {
                continue
            }
            starts.append(index)
        }
        return starts
    }

    /// A period that closes a sentence: only closing quotes or brackets, then space or
    /// the end, follow it.
    private func endsSentence(_ characters: [Character], at index: Int) -> Bool {
        var next = index + 1
        while next < characters.count, Self.closers.contains(characters[next]) {
            next += 1
        }
        return next == characters.count || characters[next].isWhitespace
    }

    /// A single period that is not part of an ellipsis, an abbreviation or a snippet.
    private func isDroppablePeriod(_ characters: [Character], at index: Int, locked: [Bool]) -> Bool {
        guard characters[index] == ".", !locked[index] else { return false }
        if index > 0, characters[index - 1] == "." { return false }
        if index + 1 < characters.count, characters[index + 1] == "." { return false }
        return !isAbbreviationPeriod(characters, at: index) && !Self.endsAbbreviation(characters, periodAt: index)
    }

    /// Abbreviations written with a period and usually followed by more of the sentence,
    /// lowercased and without ё. Single letters are handled separately.
    static let abbreviations: Set<String> = [
        // Russian
        "гг", "вв", "мин", "сек", "руб", "коп", "тыс", "млн", "млрд", "трлн", "см", "стр", "ул", "пр", "пл",
        "др", "им", "напр", "обл", "кв", "корп", "рис", "табл", "гл", "ст", "св", "проф", "доц", "акад", "тел",
        "зам", "нач", "англ", "рус", "лат", "пос", "дер", "наб", "пер",
        // English
        "etc", "vs", "approx", "incl", "excl", "dept", "fig", "vol", "mr", "mrs", "ms", "dr", "prof", "jr", "sr",
        "inc", "ltd", "corp", "cf", "al", "eg", "ie"
    ]

    /// Single letters that stand for a word on their own: «в 2025 г.», «в 5 ч.», «XX в.».
    static let letterAbbreviations: Set<String> = ["г", "в", "ч", "с"]

    /// Whether the word right before a period is an abbreviation: a listed one or any
    /// single letter («т. е.», «А. С. Пушкин», "e. g.").
    static func isAbbreviation(_ characters: [Character], word: Range<Int>) -> Bool {
        let text = TextScanning.fold(String(characters[word]))
        return abbreviations.contains(text) || (text.count == 1 && characters[word.lowerBound].isLetter)
    }

    /// Whether the period at `index` closes an abbreviation that keeps its period at the
    /// end of a text: a listed abbreviation, a letter such as «г.», or the second letter
    /// of a pair such as «т. д.», «т. п.», «н. э.».
    static func endsAbbreviation(_ characters: [Character], periodAt index: Int) -> Bool {
        var start = index
        while start > 0, characters[start - 1].isLetter {
            start -= 1
        }
        guard start < index, start == 0 || !TextScanning.isWordCharacter(characters[start - 1]) || characters[start - 1].isNumber
        else { return false }
        let word = TextScanning.fold(String(characters[start..<index]))
        if abbreviations.contains(word) || letterAbbreviations.contains(word) {
            return true
        }
        guard word.count == 1 else { return false }
        // «т. д.»: the first letter of the pair, followed by a single letter and its period.
        if index + 3 < characters.count, characters[index + 1] == " ", characters[index + 2].isLetter,
           characters[index + 3] == "." {
            return true
        }
        // The second letter, after a single letter and its period.
        guard start >= 3, characters[start - 1] == " ", characters[start - 2] == ".",
              characters[start - 3].isLetter else { return false }
        return start == 3 || !characters[start - 4].isLetter
    }

    /// «т.е.», "e.g.": a single letter between periods.
    private func isAbbreviationPeriod(_ characters: [Character], at index: Int) -> Bool {
        index < characters.count && characters[index] == "." && index >= 2
            && characters[index - 1].isLetter && characters[index - 2] == "."
    }

    public func apply(to result: inout TextPipelineResult, context: TextPipelineContext) {
        guard let style = context.style else { return }
        // Replacement targets are the user's spellings too, so they count as terms.
        let terms = context.dictionary.map { $0.kind == .term ? $0.text : $0.replacement }.filter { !$0.isEmpty }
        let characters = Array(result.text)
        let locked = SnippetProtection.ranges(in: characters, result: result, snippets: context.snippets)
        result.text = format(characters, style: style, protectedTerms: terms, locked: locked)
    }
}

/// Dictionary terms as words, for recognizing a term at a sentence start.
private struct TermCasing {
    private let terms: [(words: [String], folded: [String])]
    private let lexicon: WordLexicon

    init(_ protectedTerms: [String], lexicon: WordLexicon) {
        self.lexicon = lexicon
        terms = protectedTerms.compactMap { term in
            let characters = Array(term)
            let words = TextScanning.words(in: characters, joiners: TextScanning.wordJoiners).map { String(characters[$0]) }
            return words.isEmpty ? nil : (words, words.map(TextScanning.fold))
        }
    }

    /// The term's own spelling of the first word when the text at `index` is a term.
    func match(_ characters: [Character], words: [Range<Int>], at index: Int) -> [Character]? {
        for term in terms where index + term.words.count <= words.count {
            let matches = term.folded.enumerated().allSatisfy { offset, folded in
                TextScanning.fold(String(characters[words[index + offset]])) == folded
            }
            if matches, let first = term.words.first {
                return Array(first)
            }
        }
        return nil
    }

    /// Terms that start lowercase on purpose keep it at a sentence start: macOS and iOS,
    /// and names such as npm that are not English words (rebase still becomes Rebase).
    func keepsLowercaseStart(_ characters: [Character], words: [Range<Int>], at index: Int) -> Bool {
        guard let first = match(characters, words: words, at: index), first.first?.isLowercase == true,
              Array(characters[words[index]]) == first else { return false }
        if first.contains(where: \.isUppercase) {
            return true
        }
        let word = String(first)
        return word.allSatisfy { !$0.isLetter || TextScanning.isLatinLetter($0) }
            && !lexicon.isCorrect(word, "en")
    }
}

/// Heuristics for Very casual: which capitalized sentence starts are names.
private struct NameContext {
    let characters: [Character]
    let words: [Range<Int>]
    let starts: Set<Int>
    let mostlyCyrillic: Bool
    let lexicon: WordLexicon

    init(characters: [Character], words: [Range<Int>], starts: Set<Int>, lexicon: WordLexicon) {
        self.characters = characters
        self.words = words
        self.starts = starts
        self.lexicon = lexicon
        let cyrillic = characters.filter { $0.isLetter && TextScanning.isCyrillic($0) }.count
        let latin = characters.filter(TextScanning.isLatinLetter).count
        mostlyCyrillic = cyrillic > latin
    }

    func looksLikeName(_ index: Int) -> Bool {
        let word = String(characters[words[index]])
        let letters = word.filter(\.isLetter)
        // Acronyms and inner capitals: API, GitHub, iPhone.
        if letters.count >= 2, letters.allSatisfy(\.isUppercase) { return true }
        if word.dropFirst().contains(where: \.isUppercase) { return true }
        // English "I" is always a capital.
        if word == "I" || word.hasPrefix("I'") || word.hasPrefix("I’") { return true }
        // A Latin word opening a Russian sentence is almost always a product or a name.
        if mostlyCyrillic, word.contains(where: TextScanning.isLatinLetter) { return true }
        // A proper noun the dictionary knows only capitalized: «Москва», "London".
        if isKnownProperNoun(word) { return true }
        // Capitalized again in the middle of a sentence: «Маша сказала, что Маша придёт».
        return words.indices.contains { other in
            other != index && !starts.contains(other) && String(characters[words[other]]) == word
        }
    }

    private func isKnownProperNoun(_ word: String) -> Bool {
        let hasCyrillic = word.contains(where: TextScanning.isCyrillic)
        let hasLatin = word.contains(where: TextScanning.isLatinLetter)
        guard hasCyrillic != hasLatin else { return false }
        let language = hasCyrillic ? "ru" : "en"
        guard lexicon.hasLanguage(language) else { return false }
        return lexicon.isCorrect(word, language) && !lexicon.isCorrect(word.lowercased(), language)
    }
}
