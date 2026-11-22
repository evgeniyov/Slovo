import Foundation

public struct DictionarySuggestion: Sendable, Equatable, Identifiable {
    public let from: String
    public let to: String
    /// Number of dictations containing the form, regardless of repeats within one dictation.
    public let occurrences: Int
    public let examples: [String]

    /// Dismissal follows the trigger, including when a later scan finds a different target.
    public var id: String { DictionarySuggestions.triggerKey(from) }
}

/// Finds exact replacements for distortions that recur in the inserted dictation text.
public enum DictionarySuggestions {
    /// Both sources determine the suggestions and when a displayed scan is out of date.
    public struct Input: Equatable, Sendable {
        public let history: [String]
        private let dictionary: [DictionaryItem]

        public init(history: [HistoryEntry], dictionary: [DictionaryItem]) {
            self.history = history.map(\.text)
            self.dictionary = dictionary
        }

        public static func == (lhs: Self, rhs: Self) -> Bool {
            lhs.history == rhs.history && lhs.dictionary.elementsEqual(rhs.dictionary) { first, second in
                first.kind == second.kind && first.text == second.text && first.replacement == second.replacement
            }
        }

        public func suggestions() -> [DictionarySuggestion] {
            DictionarySuggestions.find(in: history, dictionary: dictionary)
        }
    }

    public static func find(in history: [String], dictionary: [DictionaryItem],
                            bundledTerms: [String] = DictionarySeed.bundledTerms()) -> [DictionarySuggestion] {
        find(in: history, dictionary: dictionary, bundledTerms: bundledTerms, lexicon: .system)
    }

    static func triggerKey(_ text: String) -> String {
        TextScanning.fold(text.split(whereSeparator: \.isWhitespace).joined(separator: " "))
    }

    static func find(in history: [String], dictionary: [DictionaryItem], bundledTerms: [String],
                     lexicon: WordLexicon) -> [DictionarySuggestion] {
        let terms = dictionary.filter { $0.kind == .term }.map(\.text)
        let knownSpellings = Set((terms + bundledTerms + dictionary.filter { $0.kind == .replacement }.map(\.replacement))
            .map(triggerKey))
        let replacementRules = dictionary.filter { $0.kind == .replacement }.compactMap { item -> ReplacementRule? in
            let pattern = ReplacementRule.pattern(for: item.text)
            return pattern.isEmpty ? nil : ReplacementRule(pattern: pattern, replacement: item.replacement)
        }
        let rulesByFirstCharacter = Dictionary(grouping: replacementRules, by: { $0.pattern[0] })
        let forms = collectForms(in: history, rules: rulesByFirstCharacter)
        let targets = targetIndex(terms: terms + bundledTerms, forms: forms, lexicon: lexicon)
        let merger = MixedLanguageMerger(lexicon: lexicon)
        let corrector = DictionaryCorrector(lexicon: lexicon)
        var suspects: [String: Bool] = [:]
        var suggestions: [DictionarySuggestion] = []

        for (key, form) in forms where form.count >= 3 && !knownSpellings.contains(key) {
            guard !Task.isCancelled else { return [] }
            let words = form.text.components(separatedBy: " ")
            let isLatin = isLatinForm(form.text)
            guard words.allSatisfy({ word in
                if let cached = suspects[word] { return cached }
                let suspect = merger.isSuspect(word)
                suspects[word] = suspect
                return suspect
            }) else { continue }

            let source = Signature(form.text)
            let stems = inflectionStems(of: form.text)
            let isOrdinaryEnglish = isLatin && lexicon.areEnglishWords(words)
            guard let target = bestMatch(for: source, key: key, occurrences: form.count, isLatin: isLatin,
                                         isOrdinaryEnglish: isOrdinaryEnglish, stems: stems, targets: targets),
                  corrector.applySpokenAliases(form.text, terms: terms).count == 0,
                  corrector.correctTerms(form.text, terms: terms).count == 0 else { continue }
            let examples = [form.first, form.second].compactMap { sample in
                sample.map { excerpt(in: history[$0.entry], at: $0.range) }
            }
            // Terms can need the surrounding sentence: "Посмотри read me перед…" already
            // becomes README there, so the dictionary needs no new rule for it.
            let dictionaryStage = TextPipeline(stages: [DictionaryCorrector(lexicon: lexicon)])
            let context = TextPipelineContext(dictionary: dictionary, cleanup: .off)
            if examples.contains(where: { example in
                example.range(of: target.text, options: .caseInsensitive) == nil
                    && dictionaryStage.process(example, context: context).text
                        .range(of: target.text, options: .caseInsensitive) != nil
            }) { continue }
            suggestions.append(DictionarySuggestion(from: form.text, to: target.text,
                                                    occurrences: form.count, examples: examples))
        }

        return suggestions.sorted {
            if $0.occurrences != $1.occurrences { return $0.occurrences > $1.occurrences }
            return $0.id < $1.id
        }
    }

    private struct Sample {
        let entry: Int
        let range: Range<Int>
    }

    private struct Form {
        let text: String
        let first: Sample
        var second: Sample?
        var count = 1
        var lastEntry: Int

        mutating func record(_ sample: Sample) {
            guard sample.entry != lastEntry else { return }
            lastEntry = sample.entry
            count += 1
            if second == nil { second = sample }
        }
    }

    private static func collectForms(in history: [String], rules: [ReplacementRule.Element: [ReplacementRule]]) -> [String: Form] {
        var forms: [String: Form] = [:]
        for (entry, text) in history.enumerated() {
            guard !Task.isCancelled else { return [:] }
            let characters = Array(text)
            let covered = coveredCharacters(characters, rules: rules)
            let ranges = TextScanning.words(in: characters, joiners: TextScanning.nameJoiners)
            for index in ranges.indices {
                let first = ranges[index]
                guard !covered[first].contains(true) else { continue }
                let word = String(characters[first])
                if (4...40).contains(first.count) {
                    record(word, sample: Sample(entry: entry, range: first), in: &forms)
                }
                guard index + 1 < ranges.count else { continue }
                let second = ranges[index + 1]
                let gap = characters[first.upperBound..<second.lowerBound]
                let run = first.lowerBound..<second.upperBound
                if !covered[second].contains(true), first.count >= 2, second.count >= 2, run.count <= 40,
                   gap.allSatisfy({ $0.isWhitespace && !$0.isNewline }) {
                    record(word + " " + String(characters[second]), sample: Sample(entry: entry, range: run), in: &forms)
                }
            }
        }
        return forms
    }

    private static func record(_ text: String, sample: Sample, in forms: inout [String: Form]) {
        let key = TextScanning.fold(text)
        if forms[key] == nil {
            forms[key] = Form(text: text, first: sample, lastEntry: sample.entry)
        } else {
            forms[key]?.record(sample)
        }
    }

    private static func coveredCharacters(_ characters: [Character],
                                          rules: [ReplacementRule.Element: [ReplacementRule]]) -> [Bool] {
        var covered = [Bool](repeating: false, count: characters.count)
        guard !rules.isEmpty else { return covered }
        let folded = characters.map(TextScanning.fold)
        for start in characters.indices {
            guard let matches = rules[.character(folded[start])] else { continue }
            for rule in matches {
                guard let end = rule.match(in: characters, folded: folded, at: start) else { continue }
                for index in start..<end { covered[index] = true }
            }
        }
        return covered
    }

    private struct Signature {
        let spelling: [UInt8]
        let sound: [UInt8]
        let numbers: [Substring]

        init(_ text: String) {
            spelling = SpellingKey.make(text.components(separatedBy: " "))
            sound = Array(MixedLanguageMerger.sound(text).utf8)
            numbers = SpellingKey.numberTokens(text)
        }
    }

    private struct Target {
        let text: String
        let key: String
        let signature: Signature
        let spokenAliases: [Signature]
        /// Nil for an explicitly spelled dictionary or vocabulary term.
        let attestations: Int?
    }

    private static func targetIndex(terms: [String], forms: [String: Form],
                                    lexicon: WordLexicon) -> [Int: [Target]] {
        var seen = Set<String>()
        var targets: [Target] = []
        for term in terms {
            let key = triggerKey(term)
            guard isLatinForm(term), seen.insert(key).inserted else { continue }
            let aliases = (DictionaryCorrector.spokenAliases[key] ?? []).map(Signature.init)
            targets.append(Target(text: term, key: key, signature: Signature(term), spokenAliases: aliases, attestations: nil))
        }
        for (key, form) in forms where form.count >= 2 && isLatinForm(form.text) && !seen.contains(key) {
            let words = form.text.components(separatedBy: " ")
            // A repeated typo is not good evidence for a spelling. Prefer dictionary words
            // and names with distinctive casing such as GitHub and OpenAI.
            guard lexicon.areEnglishWords(words) || words.contains(where: { $0.dropFirst().contains(where: \.isUppercase) })
            else { continue }
            targets.append(Target(text: form.text, key: key, signature: Signature(form.text), spokenAliases: [], attestations: form.count))
        }
        return Dictionary(grouping: targets, by: { $0.signature.spelling.count })
    }

    private static func isLatinForm(_ text: String) -> Bool {
        text.contains(where: MixedLanguageMerger.isLatin)
            && text.allSatisfy { MixedLanguageMerger.isLatin($0) || $0.isNumber || $0.isWhitespace || "-.'’".contains($0) }
    }

    private static func bestMatch(for source: Signature, key: String, occurrences: Int, isLatin: Bool,
                                  isOrdinaryEnglish: Bool, stems: [Signature], targets: [Int: [Target]]) -> Target? {
        guard source.spelling.count >= 4, source.sound.count >= 4 else { return nil }
        var best: Target?
        var bestRank = Int.max
        var ambiguous = false
        for length in max(4, source.spelling.count - 2)...(source.spelling.count + 2) {
            for target in targets[length] ?? [] {
                guard target.key != key, source.numbers == target.signature.numbers else { continue }
                if isOrdinaryEnglish {
                    // Real English words only qualify for an exact change of word boundaries.
                    guard target.attestations == nil,
                          SpellingKey.plainLetters(key) == SpellingKey.plainLetters(target.key) else { continue }
                }
                let edits = distance(from: source, to: target.signature)
                    ?? (stems.contains { matchesStem($0, target.signature) } ? 1 : nil)
                    ?? aliasDistance(from: source, to: target.spokenAliases)
                guard let edits else { continue }
                if let attestations = target.attestations, isLatin || edits > 0 {
                    guard attestations > occurrences else { continue }
                }
                let rank = 2 * edits + (target.attestations == nil ? 0 : 1)
                if rank < bestRank {
                    best = target
                    bestRank = rank
                    ambiguous = false
                } else if rank == bestRank {
                    ambiguous = true
                }
            }
        }
        return ambiguous ? nil : best
    }

    private static func inflectionStems(of text: String) -> [Signature] {
        let folded = TextScanning.fold(text)
        guard folded.allSatisfy(MixedLanguageMerger.isCyrillic) else { return [] }
        return DictionaryCorrector.caseEndings.compactMap { ending in
            guard folded.hasSuffix(ending), folded.count - ending.count >= 5 else { return nil }
            return Signature(String(folded.dropLast(ending.count)))
        }
    }

    private static func matchesStem(_ stem: Signature, _ target: Signature) -> Bool {
        if stem.spelling == target.spelling || stem.sound == target.sound { return true }
        return stem.spelling.count == target.spelling.count
            && SpellingKey.consonants(stem.spelling) == SpellingKey.consonants(target.spelling)
            && SpellingKey.editDistance(stem.spelling, target.spelling) == 1
    }

    private static func aliasDistance(from source: Signature, to aliases: [Signature]) -> Int? {
        for alias in aliases where alias.spelling.count == source.spelling.count {
            let edits = SpellingKey.editDistance(source.spelling, alias.spelling)
            if edits == 0 { return 0 }
            if edits == 1, source.spelling.dropLast() == alias.spelling.dropLast(),
               SpellingKey.consonantDistance(Array(source.spelling.suffix(1)), Array(alias.spelling.suffix(1))) == 0.5 {
                return 1
            }
        }
        return nil
    }

    private static func distance(from source: Signature, to target: Signature) -> Int? {
        let spellingEdits = SpellingKey.editDistance(source.spelling, target.spelling)
        if spellingEdits == 0 || source.sound == target.sound { return 0 }
        let longest = max(source.spelling.count, target.spelling.count)
        if longest >= 5 && spellingEdits == 1 { return 1 }
        if longest >= 9 && spellingEdits == 2
            && SpellingKey.consonants(source.spelling) == SpellingKey.consonants(target.spelling) { return 2 }
        return nil
    }

    private static func excerpt(in text: String, at range: Range<Int>) -> String {
        let characters = Array(text)
        let start = max(0, range.lowerBound - 24)
        let end = min(characters.count, range.upperBound + 24)
        let content = String(characters[start..<end]).split(whereSeparator: \.isWhitespace).joined(separator: " ")
        return (start > 0 ? "…" : "") + content + (end < characters.count ? "…" : "")
    }
}
