import Foundation

/// Auto cleanup: removes filler words and immediate repetitions, then fixes spacing
/// around punctuation.
public struct FillerCleaner: TextStage {
    public static let russianFillers: Set<String> = ["э", "ээ", "эм", "ммм"]
    public static let englishFillers: Set<String> = ["um", "uh", "erm", "hmm"]

    public init() {}

    /// The cleaned text; with both options off the text comes back untouched.
    public func clean(_ text: String, options: AutoCleanupOptions) -> String {
        clean(text, options: options, atStart: true, atEnd: true)
    }

    /// `atStart` and `atEnd` say whether the piece touches the ends of the whole text,
    /// so a piece next to a protected snippet keeps its edge spacing.
    func clean(_ text: String, options: AutoCleanupOptions, atStart: Bool, atEnd: Bool) -> String {
        guard options.removeFillers || options.removeRepetitions, !text.isEmpty else { return text }
        var split = SplitText(text)
        if options.removeFillers {
            split.removeFillers(atStart: atStart, atEnd: atEnd)
        }
        if options.removeRepetitions {
            split.removeRepetitions()
        }
        return Self.fixSpacing(split.joined, atStart: atStart, atEnd: atEnd)
    }

    /// A filler or a drawn-out one: «эээ», «эмм», "ummm". Shorter forms than the listed
    /// ones stay, so «м» and «мм» are not treated as «ммм».
    static func isFiller(_ word: String) -> Bool {
        let letters = String(TextScanning.fold(word).filter(\.isLetter))
        guard !letters.isEmpty, let minimum = fillerShapes[collapseRuns(letters)] else { return false }
        return letters.count >= minimum
    }

    private static let fillerShapes: [String: Int] = {
        var shapes: [String: Int] = [:]
        for filler in russianFillers.union(englishFillers) {
            let shape = collapseRuns(filler)
            shapes[shape] = min(shapes[shape] ?? Int.max, filler.count)
        }
        return shapes
    }()

    private static func collapseRuns(_ text: String) -> String {
        var output = ""
        for character in text where output.last != character {
            output.append(character)
        }
        return output
    }

    // MARK: - Spacing

    private static let closing: Set<Character> = [",", ".", ";", ":", "!", "?", "…", ")", "]", "}", "»", "”"]
    private static let opening: Set<Character> = ["(", "[", "{", "«", "„", "“"]
    private static let sentenceEnds: Set<Character> = [".", "!", "?", "…"]
    private static let dashes: Set<Character> = ["—", "–"]

    private static let codeSpans = try! NSRegularExpression(pattern: #"(`+)(?s:.*?)\1"#)
    // The end of a prose piece can be followed by a protected code span.
    private static let splitEllipsis = try! NSRegularExpression(pattern: #"(?<!\.)\.\h+(?:\.{3}(?!\.)|…)(\h*(?=[\p{L}\p{N}\p{Ps}\p{Pi}\p{Pd}"'\x60]|\z))?"#)

    static func fixSpacing(_ text: String, atStart: Bool, atEnd: Bool) -> String {
        guard text.contains("`") else { return fixProseSpacing(text, atStart: atStart, atEnd: atEnd) }
        let matches = codeSpans.matches(in: text, range: NSRange(text.startIndex..., in: text))
        var cursor = text.startIndex
        var output = ""
        for match in matches {
            guard let range = Range(match.range, in: text) else { continue }
            output += fixProseSpacing(String(text[cursor..<range.lowerBound]),
                                      atStart: atStart && cursor == text.startIndex, atEnd: false)
            output += text[range]
            cursor = range.upperBound
        }
        return output + fixProseSpacing(String(text[cursor...]),
                                        atStart: atStart && cursor == text.startIndex, atEnd: atEnd)
    }

    private static func fixProseSpacing(_ text: String, atStart: Bool, atEnd: Bool) -> String {
        let normalized = NSMutableString(string: text)
        let matches = splitEllipsis.matches(in: text, range: NSRange(text.startIndex..., in: text))
        for match in matches.reversed() {
            let ellipsis = match.range(at: 1).location == NSNotFound ? "..." : "... "
            normalized.replaceCharacters(in: match.range, with: ellipsis)
        }
        var characters = collapseWhitespace(Array(normalized as String))
        var output: [Character] = []
        output.reserveCapacity(characters.count)
        var emoticonEnd = 0
        for (index, character) in characters.enumerated() {
            let followsEmoticon = emoticonEnd > 0 && index == emoticonEnd
            if index >= emoticonEnd {
                emoticonEnd = index + emoticonLength(characters, at: index)
            }
            // An emoticon is not punctuation: "Смайлик :) и всё" keeps both spaces.
            if index < emoticonEnd {
                output.append(character)
                continue
            }
            // A dot directly before a letter starts a name: ".gitignore", ".env".
            let startsName = character == "." && index + 1 < characters.count
                && TextScanning.isWordCharacter(characters[index + 1])
            if closing.contains(character), !startsName {
                while output.last == " " {
                    output.removeLast()
                }
            }
            if character == " ", !followsEmoticon, let last = output.last, opening.contains(last) {
                continue
            }
            if character == ",", lastVisible(output) == "," {
                continue
            }
            if dashes.contains(character), let last = lastVisible(output), dashes.contains(last) {
                continue
            }
            if sentenceEnds.contains(character), lastVisible(output) == "," {
                while output.last == " " {
                    output.removeLast()
                }
                output.removeLast()
            }
            // «да,нет» and «Готово!Проверишь»; after ! and ? only before a capital, so a
            // "?q=" query string stays whole. Links, addresses and paths keep every mark.
            if character.isLetter, let last = output.last,
               last == "," || last == ";" || ((last == "!" || last == "?") && character.isUppercase),
               !endsInLink(output) {
                output.append(" ")
            }
            output.append(character)
        }
        characters = collapseWhitespace(output)
        if atStart {
            while let first = characters.first, first.isWhitespace || first == "," || first == ";" || first == ":" {
                characters.removeFirst()
            }
        }
        if atEnd {
            while characters.last?.isWhitespace == true {
                characters.removeLast()
            }
        }
        return String(characters)
    }

    /// The length of an emoticon starting at `index` (":)", ";-)", ":(", ":D"), or 0.
    /// It must end its word, so "10:30" and ":Data" are not emoticons.
    static func emoticonLength(_ characters: [Character], at index: Int) -> Int {
        guard characters[index] == ":" || characters[index] == ";" else { return 0 }
        var mouth = index + 1
        if mouth < characters.count, characters[mouth] == "-" {
            mouth += 1
        }
        guard mouth < characters.count, ")(DP".contains(characters[mouth]) else { return 0 }
        let end = mouth + 1
        guard end == characters.count || !TextScanning.isWordCharacter(characters[end]) else { return 0 }
        return end - index
    }

    /// Runs of spaces and tabs become one space; spaces next to a line break go.
    private static func collapseWhitespace(_ characters: [Character]) -> [Character] {
        var output: [Character] = []
        output.reserveCapacity(characters.count)
        for character in characters {
            if character.isNewline {
                while output.last == " " {
                    output.removeLast()
                }
                output.append(character)
            } else if character.isWhitespace {
                if let last = output.last, last == " " || last.isNewline {
                    continue
                }
                output.append(" ")
            } else {
                output.append(character)
            }
        }
        return output
    }

    private static func lastVisible(_ characters: [Character]) -> Character? {
        characters.last { $0 != " " }
    }

    /// Whether the text ends inside a link, an e-mail address or a path: the run of
    /// characters since the last whitespace has "://", "@", "/" or starts with "www.".
    static func endsInLink(_ characters: [Character]) -> Bool {
        let start = characters.lastIndex(where: \.isWhitespace).map { $0 + 1 } ?? 0
        let token = String(characters[start...])
        return token.contains("://") || token.contains("@") || token.contains("/") || token.lowercased().hasPrefix("www.")
    }

    public func apply(to result: inout TextPipelineResult, context: TextPipelineContext) {
        let characters = Array(result.text)
        let protected = SnippetProtection.ranges(in: characters, result: result, snippets: context.snippets)
        guard !protected.isEmpty else {
            result.text = clean(result.text, options: context.cleanup)
            return
        }
        let segments = SnippetProtection.segments(characters, protected: protected)
        var text = ""
        for (index, segment) in segments.enumerated() {
            guard !segment.isProtected else {
                text += segment.text
                continue
            }
            var cleaned = clean(segment.text, options: context.cleanup, atStart: index == 0,
                                atEnd: index == segments.count - 1)
            // Keep a space between the cleaned words and a neighbouring expansion.
            if index > 0, segment.text.first?.isWhitespace == true, cleaned.first?.isWhitespace != true {
                cleaned = " " + cleaned
            }
            if index < segments.count - 1, segment.text.last?.isWhitespace == true, cleaned.last?.isWhitespace != true {
                cleaned += " "
            }
            text += cleaned
        }
        result.text = text
    }
}

/// A text as words and the separators around them: `separators[i]` precedes
/// `words[i]`, and the last separator trails the text.
struct SplitText {
    var separators: [String]
    var words: [String]

    init(_ text: String) {
        let characters = Array(text)
        var separators: [String] = []
        var words: [String] = []
        var cursor = 0
        for range in TextScanning.words(in: characters, joiners: TextScanning.wordJoiners) {
            separators.append(String(characters[cursor..<range.lowerBound]))
            words.append(String(characters[range]))
            cursor = range.upperBound
        }
        separators.append(String(characters[cursor...]))
        self.separators = separators
        self.words = words
    }

    var joined: String {
        var text = ""
        for (index, word) in words.enumerated() {
            text += separators[index]
            text += word
        }
        return text + (separators.last ?? "")
    }

    mutating func removeFillers(atStart: Bool, atEnd: Bool) {
        var index = 0
        while index < words.count {
            guard isFiller(at: index, atStart: atStart, atEnd: atEnd) else {
                index += 1
                continue
            }
            let startsSentence = index == 0 || separators[index].contains { ".!?…".contains($0) || $0.isNewline }
            let wasCapitalized = words[index].first?.isUppercase == true
            separators[index] = Self.merge(separators[index], separators[index + 1], isTextEnd: index == words.count - 1,
                                           startsSentence: startsSentence)
            words.remove(at: index)
            separators.remove(at: index + 1)
            // «Э, давай» becomes «Давай»: the sentence keeps its capital letter.
            if startsSentence, wasCapitalized, index < words.count, words[index].first?.isLowercase == true {
                var characters = Array(words[index])
                TextScanning.setCase(of: &characters, at: 0, uppercase: true)
                words[index] = String(characters)
            }
        }
    }

    private static let hesitations: Set<String> = ["а-а", "а-а-а", "м-м", "м-м-м"]

    private func isFiller(at index: Int, atStart: Bool, atEnd: Bool) -> Bool {
        let word = words[index]
        guard Self.hesitations.contains(TextScanning.fold(word)) else { return FillerCleaner.isFiller(word) }

        let left = separators[index].last { !$0.isWhitespace || $0.isNewline }
        let right = separators[index + 1].first { !$0.isWhitespace || $0.isNewline }
        if left == ",", right == "," { return true }
        // «А-а. Дальше…» is a pause. A following comma, question or exclamation can
        // make it an interjection, as in «А-а, понял» or «А-а!».
        let startsPause = left.map { Self.sentenceMarks.contains($0) } ?? (index == 0 && atStart)
        let endsPause = right.map { $0 == "." || $0 == "…" } ?? (index == words.count - 1 && atEnd)
        return startsPause && endsPause
    }

    /// Joins the separators on both sides of a removed word. A comma that only sets the
    /// word off goes with it: «Э, давай» → «Давай», «Давай, э.» → «Давай.». With commas
    /// on both sides one stays, because engines put commas there mostly at clause
    /// boundaries: «Спасибо, э, до встречи» → «Спасибо, до встречи». A filler the engine
    /// wrote as a sentence of its own takes its closing mark along: «Э. Давай» → «Давай»,
    /// «Так. Эм. Что дальше?» → «Так. Что дальше?», "Hmm? What?" → "What?".
    static func merge(_ left: String, _ right: String, isTextEnd: Bool, startsSentence: Bool = false) -> String {
        var leading = Array(left)
        var trailing = Array(right)
        let hadSpace = leading.contains(where: \.isWhitespace) || trailing.contains(where: \.isWhitespace)
        trimTrailingWhitespace(&leading)
        trimLeadingWhitespace(&trailing)
        // Brackets that held only the filler go with it: «(э)». Empty brackets the
        // speaker dictated, as in foo(), stay.
        if let open = leading.last, let close = trailing.first, (open == "(" && close == ")") || (open == "[" && close == "]") {
            leading.removeLast()
            trailing.removeFirst()
            trimTrailingWhitespace(&leading)
            trimLeadingWhitespace(&trailing)
        }
        if startsSentence, let first = trailing.first, sentenceMarks.contains(first) {
            trailing.removeFirst(trailing.prefix(while: { sentenceMarks.contains($0) }).count)
            trimLeadingWhitespace(&trailing)
            return String(leading) + (hadSpace ? " " : "") + String(trailing)
        }
        let leftComma = leading.last == ","
        if trailing.first == "," {
            trailing.removeFirst()
        } else if leftComma {
            let endsHere = trailing.isEmpty && isTextEnd
            let beforeStop = trailing.first.map { ".!?…;:".contains($0) } ?? false
            if endsHere || beforeStop {
                leading.removeLast()
            }
        }
        trimTrailingWhitespace(&leading)
        trimLeadingWhitespace(&trailing)
        return String(leading) + (hadSpace ? " " : "") + String(trailing)
    }

    /// Drops an immediately repeated group of one to three words: «я я» → «я», «в общем
    /// в общем» → «в общем». Only spaces may separate the repeats: «Нет, нет, нет!» is
    /// emphasis, not a stutter. Numbers alone never count, nor do grammatical doubles
    /// such as "had had".
    mutating func removeRepetitions() {
        var index = 0
        while index < words.count {
            var removed = false
            for size in stride(from: 3, through: 1, by: -1) where index + 2 * size <= words.count {
                guard isRepetition(at: index, size: size) else { continue }
                words.removeSubrange((index + size)..<(index + 2 * size))
                separators.removeSubrange((index + size)..<(index + 2 * size))
                removed = true
                break
            }
            if !removed {
                index += 1
            }
        }
    }

    private func isRepetition(at index: Int, size: Int) -> Bool {
        var hasLetters = false
        for offset in 0..<size {
            let first = words[index + offset]
            guard TextScanning.fold(first) == TextScanning.fold(words[index + size + offset]) else { return false }
            hasLetters = hasLetters || first.contains(where: \.isLetter)
        }
        guard hasLetters else { return false }
        if size == 1, Self.grammaticalDoubles.contains(TextScanning.fold(words[index])) {
            return false
        }
        for separator in separators[(index + 1)...(index + 2 * size - 1)] {
            guard separator.allSatisfy({ $0.isWhitespace && !$0.isNewline }) else { return false }
        }
        return true
    }

    /// English words that correct sentences repeat: "I had had enough", "I know that that is true".
    static let grammaticalDoubles: Set<String> = ["had", "that"]

    /// Marks that close a sentence.
    private static let sentenceMarks: Set<Character> = [".", "!", "?", "…"]

    private static func trimTrailingWhitespace(_ characters: inout [Character]) {
        while characters.last?.isWhitespace == true {
            characters.removeLast()
        }
    }

    private static func trimLeadingWhitespace(_ characters: inout [Character]) {
        while characters.first?.isWhitespace == true {
            characters.removeFirst()
        }
    }
}
