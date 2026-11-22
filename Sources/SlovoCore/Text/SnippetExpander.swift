import Foundation

/// Replaces spoken trigger phrases with their expansions, whether the trigger is the
/// whole dictation or a phrase inside it. Case-insensitive, ignores punctuation,
/// treats ё as е and ’ as '.
///
/// A hyphen counts as a word break, so the trigger «IP адрес» matches «IP-адрес» and
/// "e mail" matches "e-mail"; a match never starts or ends inside a hyphenated
/// compound, so «адрес» alone does not match inside «IP-адрес».
public struct SnippetExpander: TextStage {
    public init() {}

    /// The expanded text and how many triggers were replaced.
    public func expand(_ text: String, snippets: [Snippet]) -> (text: String, count: Int) {
        let triggers = prepare(snippets)
        guard !triggers.isEmpty, !text.isEmpty else { return (text, 0) }

        let characters = Array(text)
        let ranges = TextScanning.words(in: characters, joiners: TextScanning.apostropheJoiners)
        let words = ranges.map { TextScanning.fold(String(characters[$0])) }
        guard !words.isEmpty else { return (text, 0) }
        // `hyphenated[i]`: word i is glued to word i - 1 by a hyphen, as in «IP-адрес».
        let hyphenated = ranges.indices.map { index in
            index > 0 && characters[ranges[index - 1].upperBound..<ranges[index].lowerBound].elementsEqual(["-"])
        }

        // The whole dictation is the trigger: the engine's punctuation around it goes too.
        if let whole = triggers.first(where: { $0.words == words }) {
            return (whole.expansion, 1)
        }

        var output = ""
        var copiedUpTo = 0
        var count = 0
        var index = 0
        while index < words.count {
            guard let trigger = triggers.first(where: { matches($0.words, words, hyphenated: hyphenated, at: index) }) else {
                index += 1
                continue
            }
            let start = ranges[index].lowerBound
            let end = ranges[index + trigger.words.count - 1].upperBound
            output += String(characters[copiedUpTo..<start])
            output += trigger.expansion
            copiedUpTo = end
            // The engine's own period after the trigger would double the expansion's: «иван..».
            if let mark = trigger.expansion.last, ".!?".contains(mark), end < characters.count, characters[end] == mark,
               end + 1 == characters.count || characters[end + 1] != mark {
                copiedUpTo = end + 1
            }
            count += 1
            index += trigger.words.count
        }
        output += String(characters[copiedUpTo...])
        return (output, count)
    }

    private struct Trigger {
        let words: [String]
        let expansion: String
    }

    /// Triggers as folded words, longest first so «мой рабочий адрес» wins over «мой адрес».
    private func prepare(_ snippets: [Snippet]) -> [Trigger] {
        snippets
            .compactMap { snippet -> Trigger? in
                let characters = Array(snippet.trigger)
                let words = TextScanning.words(in: characters, joiners: TextScanning.apostropheJoiners)
                    .map { TextScanning.fold(String(characters[$0])) }
                return words.isEmpty ? nil : Trigger(words: words, expansion: snippet.expansion)
            }
            .enumerated()
            .sorted { lhs, rhs in
                if lhs.element.words.count != rhs.element.words.count {
                    return lhs.element.words.count > rhs.element.words.count
                }
                let lhsLength = lhs.element.words.reduce(0) { $0 + $1.count }
                let rhsLength = rhs.element.words.reduce(0) { $0 + $1.count }
                return lhsLength != rhsLength ? lhsLength > rhsLength : lhs.offset < rhs.offset
            }
            .map(\.element)
    }

    /// The trigger's words at `index`, not starting or ending inside a hyphenated compound.
    private func matches(_ trigger: [String], _ words: [String], hyphenated: [Bool], at index: Int) -> Bool {
        let end = index + trigger.count
        guard end <= words.count, !hyphenated[index], end == words.count || !hyphenated[end] else { return false }
        return words[index..<end].elementsEqual(trigger)
    }

    public func apply(to result: inout TextPipelineResult, context: TextPipelineContext) {
        let expanded = expand(result.text, snippets: context.snippets)
        result.text = expanded.text
        result.snippetsApplied += expanded.count
    }
}
