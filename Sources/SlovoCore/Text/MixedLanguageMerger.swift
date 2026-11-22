import Foundation

/// Mixed Russian and English dictation: GigaAM writes the text, a multilingual helper
/// (Parakeet or Whisper) heard the same audio, and this puts the helper's English words in
/// only where GigaAM was unsure.
///
/// GigaAM knows only Russian. When it hears English it either falls back to Latin letters
/// with a wrong spelling (Makos, Hithub, Pod Vidos) or writes a transliteration that is not
/// a Russian word (мейн, ребейс). Those words are *suspect*; everything else GigaAM wrote —
/// its Russian, its punctuation, its capitals — is kept word for word, since GigaAM makes
/// three to ten times fewer Russian errors than any multilingual model.
///
/// Both texts are aligned on a sound key shared by both scripts. Where they differ, a small
/// dynamic programme pairs GigaAM's words with the helper's (one to one, or merging up to
/// three words: Baka Swift → Package.swift), and a suspect run is replaced by its partner
/// when the partner sounds alike and is English (or, for Latin garble, one short Russian
/// function word: Pod → под). The helper's own mistakes in Russian, its dropped sentences
/// and anything it invents in a pause never reach the text.
///
/// `Shared/fixtures/mixed-merge.json` covers behavior shared with the Windows app.
public struct MixedLanguageMerger: Sendable {
    /// Below this, two runs do not sound alike enough to be the same words.
    static let minimumSimilarity = 0.55
    /// A capitalised Cyrillic word this short is an abbreviation (БД, ООО), not a transliteration.
    static let abbreviationLength = 5

    /// Short Russian words GigaAM sometimes writes in Latin next to an English word (Pod Vidos).
    static let functionWords: Set<String> = [
        "в", "во", "на", "под", "над", "с", "со", "к", "ко", "по", "о", "об", "от", "до", "за", "из",
        "у", "и", "а", "но", "не", "для", "про", "без", "при",
    ]

    let lexicon: WordLexicon

    /// Uses the system's Russian spelling dictionary to tell real words from transliterations.
    public init() {
        self.init(lexicon: .system)
    }

    init(lexicon: WordLexicon) {
        self.lexicon = lexicon
    }

    /// The merged text and how many runs of GigaAM's words were replaced.
    public struct Result: Equatable, Sendable {
        public let text: String
        public let replacements: Int
    }

    /// Whether GigaAM's text has a word the helper could improve; without one there is no
    /// point in running the helper at all.
    public func needsHelper(_ russian: String) -> Bool {
        Self.tokens(russian).contains(where: isSuspect)
    }

    public func merge(russian: String, helper: String) -> Result {
        let g = Self.tokens(russian)
        let o = Self.tokens(helper)
        guard !g.isEmpty, !o.isEmpty else {
            return Result(text: g.joined(separator: " "), replacements: 0)
        }
        let gk = g.map { Self.sound(Self.core($0)) }
        let ok = o.map { Self.sound(Self.core($0)) }
        var out: [String] = []
        var replacements = 0
        for opcode in SequenceMatcher(gk, ok).opcodes() {
            guard !Task.isCancelled else { return Result(text: russian, replacements: 0) }
            let gblock = Array(g[opcode.aStart..<opcode.aEnd])
            let oblock = Array(o[opcode.bStart..<opcode.bEnd])
            if opcode.tag == .equal {
                // Sounding the same is not spelling the same: Makos/macOS, Гит/Git, мейн/main.
                for (offset, (gt, ot)) in zip(gblock, oblock).enumerated() {
                    if isSuspect(gt), Self.core(gt).lowercased() != Self.core(ot).lowercased(),
                       acceptable(run: [gt], partner: [ot], primary: g, at: opcode.aStart + offset) {
                        out += Self.replaceRun([gt], with: [ot], sentenceStart: Self.isSentenceStart(out))
                        replacements += 1
                    } else {
                        out.append(gt)
                    }
                }
                continue
            }
            guard opcode.tag == .replace, gblock.contains(where: isSuspect) else {
                out += gblock
                continue
            }
            var k = 0
            for piece in pair(gblock, oblock) {
                out += gblock[k..<piece.gStart]
                let run = Array(gblock[piece.gStart..<piece.gEnd])
                let partner = Array(oblock[piece.oStart..<piece.oEnd])
                if run.allSatisfy(isSuspect),
                   acceptable(run: run, partner: partner, primary: g, at: opcode.aStart + piece.gStart),
                   run.map({ Self.core($0).lowercased() }) != partner.map({ Self.core($0).lowercased() }) {
                    out += Self.replaceRun(run, with: partner, sentenceStart: Self.isSentenceStart(out))
                    replacements += 1
                } else {
                    out += run
                }
                k = piece.gEnd
            }
            out += gblock[k...]
        }
        return Result(text: out.joined(separator: " "), replacements: replacements)
    }

    // MARK: - Suspect words

    /// Latin garble, or a Cyrillic word that is not Russian. Mixed scripts (Telegram-бот)
    /// and capitalised abbreviations (БД) are GigaAM's choice and stay.
    func isSuspect(_ token: String) -> Bool {
        let word = Self.core(token)
        guard !word.isEmpty, word.contains(where: Self.isWordCharacter) else { return false }
        let latin = word.contains(where: Self.isLatin)
        let cyrillic = word.contains(where: Self.isCyrillic)
        if latin && cyrillic {
            return false
        }
        if latin {
            return true
        }
        if Self.isAllUppercase(word), word.count <= Self.abbreviationLength {
            return false
        }
        return cyrillic && !isKnown(word)
    }

    /// A real Russian word; a hyphenated one when every part is. Without a Russian
    /// dictionary every word counts as known, so only Latin garble is replaced.
    func isKnown(_ word: String) -> Bool {
        guard lexicon.hasLanguage("ru") else { return true }
        let parts = word.lowercased().split(separator: "-").map(String.init).filter { !$0.isEmpty }
        return !parts.isEmpty && parts.allSatisfy { part in
            lexicon.isCorrect(part, "ru") || lexicon.isCorrect(part.replacingOccurrences(of: "ё", with: "е"), "ru")
        }
    }

    /// The helper supplies English: its Latin words may replace GigaAM's unsure ones. A
    /// Latin run may also become one short Russian function word (Pod → под), never a
    /// longer Russian word (Max stays Max).
    private func acceptable(run: [String], partner: [String], primary: [String], at start: Int) -> Bool {
        let words = partner.map(Self.core)
        guard words.allSatisfy({ !$0.isEmpty }) else { return false }
        let original = run.map(Self.core)
        let latinRun = original.contains { $0.contains(where: Self.isLatin) }
        if words.allSatisfy({ $0.contains(where: Self.isLatin) }) {
            // Collapsing repeated letters can make BB look close to BP. Keep the original
            // repeated-letter abbreviation when the helper only offers another pair of letters.
            if original.count == 1, words.count == 1, original[0].count == 2, words[0].count == 2,
               original[0].first == original[0].last, Self.isAllUppercase(original[0]),
               original[0].allSatisfy(Self.isLatin), words[0].allSatisfy(Self.isLatin) {
                return original[0].lowercased() == words[0].lowercased()
            }
            if latinRun && words.contains(where: { $0.contains(where: Self.isCyrillic) }) {
                return false
            }
            return true
        }
        guard latinRun, words.count == 1, Self.functionWords.contains(words[0].lowercased()) else { return false }
        guard original.count == 1, original[0].count == 1, words[0].count == 1 else { return true }

        // A single Latin letter next to English is a label or article: "Worker A", "A beta".
        // The word before it in the same sentence decides, else the word after: «был V
        // офисе», «Это V GitHub» and «Finder. V офисе» can still become «в».
        let previousInSentence = primary.indices.contains(start - 1)
            && !Self.endsSentence(primary[start - 1])
        let neighborIndex = previousInSentence ? start - 1 : start + 1
        guard primary.indices.contains(neighborIndex) else { return true }
        let neighbor = Self.core(primary[neighborIndex])
        return !(neighbor.contains(where: Self.isLatin) && !neighbor.contains(where: Self.isCyrillic))
    }

    // MARK: - Pairing inside a differing stretch

    struct Piece: Equatable {
        let gStart: Int, gEnd: Int, oStart: Int, oEnd: Int
    }

    /// How many words of each side one pairing may join.
    static let shapes: [(Int, Int)] = [(1, 1), (2, 1), (1, 2), (3, 1), (1, 3)]

    private struct SoundPair: Hashable {
        let left: Int
        let right: Int
    }

    /// The best monotone pairing of GigaAM's words with the helper's, scored by how alike
    /// the paired words sound; words may also stay unpaired.
    func pair(_ g: [String], _ o: [String]) -> [Piece] {
        let n = g.count, m = o.count
        let width = m + 1
        let cellCount = (n + 1) * width
        var scores = [Double](repeating: -.infinity, count: cellCount)
        var steps = [UInt8](repeating: 0, count: cellCount)
        let moves = [(1, 0), (0, 1)] + Self.shapes
        var sounds: [[Character]] = []
        var soundIDs: [String: Int] = [:]
        let left = Self.soundIDs(for: g, sounds: &sounds, ids: &soundIDs)
        let right = Self.soundIDs(for: o, sounds: &sounds, ids: &soundIDs)
        var similarities: [SoundPair: Double] = [:]
        scores[0] = 0
        for i in 0...n {
            guard !Task.isCancelled else { return [] }
            for j in 0...m {
                let current = scores[i * width + j]
                for (step, (di, dj)) in moves.enumerated() where i + di <= n && j + dj <= m {
                    var score = current
                    if di > 0, dj > 0 {
                        let key = SoundPair(left: left[i][di - 1], right: right[j][dj - 1])
                        let similarity: Double
                        if let cached = similarities[key] {
                            similarity = cached
                        } else {
                            similarity = Self.similarity(sounds[key.left], sounds[key.right])
                            if similarities.count < 4_096 { similarities[key] = similarity }
                        }
                        guard similarity >= Self.minimumSimilarity else { continue }
                        score += similarity * Double(di + dj)
                    }
                    let destination = (i + di) * width + j + dj
                    if score > scores[destination] {
                        scores[destination] = score
                        steps[destination] = UInt8(step)
                    }
                }
            }
        }
        // Store one predecessor per cell and reconstruct only the winning path.
        var path: [Piece] = []
        var i = n, j = m
        while i > 0 || j > 0 {
            let (di, dj) = moves[Int(steps[i * width + j])]
            if di > 0, dj > 0 {
                path.append(Piece(gStart: i - di, gEnd: i, oStart: j - dj, oEnd: j))
            }
            i -= di
            j -= dj
        }
        return path.reversed()
    }

    private static func soundIDs(for tokens: [String], sounds: inout [[Character]],
                                 ids: inout [String: Int]) -> [[Int]] {
        let words = tokens.map(core)
        return words.indices.map { start in
            (1...min(3, words.count - start)).map { length in
                let key = sound(words[start..<(start + length)].joined(separator: " "))
                if let id = ids[key] { return id }
                let id = sounds.count
                ids[key] = id
                sounds.append(Array(key))
                return id
            }
        }
    }

    // MARK: - Writing a replacement

    /// The helper's words, with GigaAM's punctuation around the run and GigaAM's idea of
    /// whether a Russian word starts a sentence.
    static func replaceRun(_ run: [String], with partner: [String], sentenceStart: Bool) -> [String] {
        var words = partner.map { core($0).isEmpty ? $0 : core($0) }
        words[0] = leadingPunctuation(run[0]) + words[0]
        words[words.count - 1] += trailingPunctuation(run[run.count - 1])
        let first = core(words[0])
        if let initial = first.first, isCyrillic(initial), !isAllUppercase(first) {
            // GigaAM capitalises its Latin guesses, so only a sentence start or GigaAM's own
            // capital on a Russian word decides.
            let gigaamWord = core(run[0])
            let gigaamCapital = (gigaamWord.first?.isUppercase ?? false) && !gigaamWord.contains(where: isLatin)
            let wanted = sentenceStart || gigaamCapital ? String(initial).uppercased() : String(initial).lowercased()
            if let range = words[0].range(of: first) {
                words[0].replaceSubrange(range, with: wanted + first.dropFirst())
            }
        }
        return words
    }

    static func isSentenceStart(_ out: [String]) -> Bool {
        guard let last = out.last else { return true }
        return endsSentence(last)
    }

    private static func endsSentence(_ token: String) -> Bool {
        let last = token.reversed().first { !"\"'»”’)]}›".contains($0) }
        return last.map { ".!?…".contains($0) } ?? false
    }

    // MARK: - Text helpers shared with the Windows port

    static func tokens(_ text: String) -> [String] {
        text.split(whereSeparator: { $0.isWhitespace }).map(String.init)
    }

    static func isLatin(_ character: Character) -> Bool {
        guard let scalar = character.unicodeScalars.first, character.unicodeScalars.count == 1 else { return false }
        return ("A"..."Z").contains(scalar) || ("a"..."z").contains(scalar)
    }

    static func isCyrillic(_ character: Character) -> Bool {
        guard let scalar = character.unicodeScalars.first, character.unicodeScalars.count == 1 else { return false }
        return ("А"..."я").contains(scalar) || scalar == "Ё" || scalar == "ё"
    }

    static func isWordCharacter(_ character: Character) -> Bool {
        isLatin(character) || isCyrillic(character) || ("0"..."9").contains(character)
    }

    /// The word without the punctuation around it (inner dots and hyphens stay).
    static func core(_ token: String) -> String {
        let characters = Array(token)
        guard let start = characters.firstIndex(where: isWordCharacter),
              let end = characters.lastIndex(where: isWordCharacter) else { return "" }
        return String(characters[start...end])
    }

    static func leadingPunctuation(_ token: String) -> String {
        String(token.prefix { !isWordCharacter($0) })
    }

    static func trailingPunctuation(_ token: String) -> String {
        String(token.reversed().prefix { !isWordCharacter($0) }.reversed())
    }

    /// At least one cased letter and no lowercase one ("БД", "2X").
    static func isAllUppercase(_ word: String) -> Bool {
        word.contains { $0.isUppercase } && !word.contains { $0.isLowercase }
    }

    static let transliteration: [Character: String] = [
        "а": "a", "б": "b", "в": "v", "г": "g", "д": "d", "е": "e", "ё": "e", "ж": "zh", "з": "z", "и": "i",
        "й": "i", "к": "k", "л": "l", "м": "m", "н": "n", "о": "o", "п": "p", "р": "r", "с": "s", "т": "t",
        "у": "u", "ф": "f", "х": "h", "ц": "ts", "ч": "ch", "ш": "sh", "щ": "sh", "ъ": "", "ы": "i", "ь": "",
        "э": "e", "ю": "yu", "я": "ya",
    ]

    /// Spellings that sound alike in either script, applied in this order.
    static let soundRules: [(String, String)] = [
        ("ph", "f"), ("th", "t"), ("ck", "k"), ("c", "k"), ("q", "k"), ("w", "v"), ("x", "ks"),
        ("y", "i"), ("ou", "u"), ("ee", "i"), ("oo", "u"), ("kh", "h"), ("j", "dzh"), ("ei", "e"), ("ai", "e"),
    ]

    /// A rough sound key shared by both scripts: macOS and Makos, мейн and main meet.
    static func sound(_ text: String) -> String {
        var key = text.lowercased().map { transliteration[$0] ?? String($0) }.joined()
        for (from, to) in soundRules {
            key = key.replacingOccurrences(of: from, with: to)
        }
        let kept = key.filter { ("a"..."z").contains($0) || ("0"..."9").contains($0) }
        var collapsed = ""
        for character in kept where character != collapsed.last {
            collapsed.append(character)
        }
        return collapsed
    }

    static func similarity(_ a: [String], _ b: [String]) -> Double {
        similarity(Array(sound(a.joined(separator: " "))), Array(sound(b.joined(separator: " "))))
    }

    private static func similarity(_ left: [Character], _ right: [Character]) -> Double {
        guard !left.isEmpty, !right.isEmpty else { return 0 }
        return SequenceMatcher(left, right).ratio()
    }
}

/// Python's `difflib.SequenceMatcher` without junk heuristics: the same matching blocks, the
/// same opcodes and the same ratio, so the Swift and C# mergers and the prototype that tuned
/// them agree to the word.
struct SequenceMatcher<Element: Hashable> {
    enum Tag: Equatable { case equal, replace, delete, insert }

    struct Opcode: Equatable {
        let tag: Tag
        let aStart: Int, aEnd: Int, bStart: Int, bEnd: Int
    }

    let a: [Element]
    let b: [Element]
    private let positions: [Element: [Int]]

    init(_ a: [Element], _ b: [Element]) {
        self.a = a
        self.b = b
        var positions: [Element: [Int]] = [:]
        for (index, element) in b.enumerated() {
            positions[element, default: []].append(index)
        }
        self.positions = positions
    }

    /// The longest block of `a[aLow..<aHigh]` equal to a block of `b[bLow..<bHigh]`; the
    /// earliest in `a`, then in `b`, among equally long ones.
    func longestMatch(_ aLow: Int, _ aHigh: Int, _ bLow: Int, _ bHigh: Int) -> (a: Int, b: Int, size: Int) {
        var bestI = aLow, bestJ = bLow, bestSize = 0
        var lengths: [Int: Int] = [:]
        for i in aLow..<aHigh {
            var next: [Int: Int] = [:]
            for j in positions[a[i]] ?? [] {
                if j < bLow { continue }
                if j >= bHigh { break }
                let k = (lengths[j - 1] ?? 0) + 1
                next[j] = k
                if k > bestSize {
                    bestI = i - k + 1
                    bestJ = j - k + 1
                    bestSize = k
                }
            }
            lengths = next
        }
        return (bestI, bestJ, bestSize)
    }

    func matchingBlocks() -> [(a: Int, b: Int, size: Int)] {
        var queue = [(0, a.count, 0, b.count)]
        var blocks: [(a: Int, b: Int, size: Int)] = []
        while let (aLow, aHigh, bLow, bHigh) = queue.popLast() {
            let match = longestMatch(aLow, aHigh, bLow, bHigh)
            guard match.size > 0 else { continue }
            blocks.append(match)
            if aLow < match.a && bLow < match.b {
                queue.append((aLow, match.a, bLow, match.b))
            }
            if match.a + match.size < aHigh && match.b + match.size < bHigh {
                queue.append((match.a + match.size, aHigh, match.b + match.size, bHigh))
            }
        }
        blocks.sort { $0.a != $1.a ? $0.a < $1.a : ($0.b != $1.b ? $0.b < $1.b : $0.size < $1.size) }
        var merged: [(a: Int, b: Int, size: Int)] = []
        for block in blocks {
            if let last = merged.last, last.a + last.size == block.a, last.b + last.size == block.b {
                merged[merged.count - 1].size += block.size
            } else {
                merged.append(block)
            }
        }
        merged.append((a.count, b.count, 0))
        return merged
    }

    func opcodes() -> [Opcode] {
        var i = 0, j = 0
        var result: [Opcode] = []
        for block in matchingBlocks() {
            let tag: Tag? = i < block.a && j < block.b ? .replace : i < block.a ? .delete : j < block.b ? .insert : nil
            if let tag {
                result.append(Opcode(tag: tag, aStart: i, aEnd: block.a, bStart: j, bEnd: block.b))
            }
            i = block.a + block.size
            j = block.b + block.size
            if block.size > 0 {
                result.append(Opcode(tag: .equal, aStart: block.a, aEnd: i, bStart: block.b, bEnd: j))
            }
        }
        return result
    }

    func ratio() -> Double {
        let matches = matchingBlocks().reduce(0) { $0 + $1.size }
        let total = a.count + b.count
        return total > 0 ? 2.0 * Double(matches) / Double(total) : 1.0
    }
}
