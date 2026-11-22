using System.Text;
using Slovo.Core.Model;

namespace Slovo.Core.Text;

/// <summary>
/// Applies Dictionary rules: explicit replacements first, then fuzzy correction of terms
/// written the wrong way (Cyrillic transliterations, near misses).
/// </summary>
public sealed class DictionaryCorrector : ITextStage
{
    /// <summary>
    /// Share of a term's consonants that may differ, voicing pairs counting half. Russian
    /// word forms tend to sit within the edit distance of a term but lose or swap its
    /// consonants (версию against Vercel, скрыть against SQLite).
    /// </summary>
    public const double ConsonantTolerance = 0.2;

    /// <summary>
    /// Without a dictionary for the window's language, keys this short may differ from the
    /// term by one edit at most: «кладу» against Claude, "cloud" against Claude.
    /// </summary>
    public const int FallbackShortKeyLetters = 6;

    /// <summary>
    /// Russian dictionary words that in dictation almost always mean the product they were
    /// borrowed from: «докер» is Docker far more often than a dock worker. Any other real
    /// Russian word stays itself even when it sounds exactly like a term («мастер» against
    /// master), unless it is written as a name in mid-sentence («в Редис»). Lowercase, ё
    /// written as е.
    /// </summary>
    public static readonly IReadOnlySet<string> BorrowedNames =
        new HashSet<string>(["докер", "телеграм", "джава", "андроид", "линукс", "твиттер", "инстаграм"], StringComparer.Ordinal);

    /// <param name="lexicon">Tells ordinary words from terms written the wrong way.</param>
    /// <param name="maximumDistance">Largest normalized edit distance that still counts as the term.</param>
    /// <param name="minimumTermLetters">Terms shorter than this many letters are never fuzzy-matched.</param>
    public DictionaryCorrector(IWordLexicon lexicon, double maximumDistance = 0.34, int minimumTermLetters = 4)
    {
        Lexicon = lexicon;
        MaximumDistance = maximumDistance;
        MinimumTermLetters = minimumTermLetters;
    }

    public double MaximumDistance { get; }
    public int MinimumTermLetters { get; }
    public IWordLexicon Lexicon { get; }

    // MARK: - Replacements

    /// <summary>
    /// Whole-word, case-insensitive "from → to" replacements; returns the count applied.
    /// Whitespace inside a "from" phrase matches any run of whitespace, and ё matches е.
    /// </summary>
    public (string Text, int Count) ApplyReplacements(string text, IReadOnlyList<DictionaryItem> items)
    {
        var result = Replacing(text, items);
        return (string.Concat(result.Characters), result.Count);
    }

    /// <summary>
    /// <see cref="ApplyReplacements"/>, also returning where replacements were written as
    /// ranges of the output characters, so term correction can leave the user's own
    /// spellings alone.
    /// </summary>
    internal (List<string> Characters, int Count, List<CharacterRange> Written) Replacing(string text, IReadOnlyList<DictionaryItem> items)
    {
        text = SwiftString.Canonical(text);
        var rules = new List<(ReplacementRule Rule, int Offset)>();
        foreach (var item in items)
        {
            if (item.Kind != DictionaryItemKind.Replacement)
            {
                continue;
            }
            var pattern = ReplacementRule.PatternFor(item.Text);
            if (pattern.Count > 0)
            {
                rules.Add((new ReplacementRule(pattern, SwiftString.Canonical(item.Replacement)), rules.Count));
            }
        }
        rules.Sort((lhs, rhs) => lhs.Rule.Pattern.Count != rhs.Rule.Pattern.Count
            ? rhs.Rule.Pattern.Count.CompareTo(lhs.Rule.Pattern.Count)
            : lhs.Offset.CompareTo(rhs.Offset));
        var characters = TextElements.Characters(text);
        if (rules.Count == 0 || text.Length == 0)
        {
            return ([.. characters], 0, []);
        }

        var folded = characters.Select(TextScanning.FoldCharacter).ToArray();
        var output = new List<string>(characters.Length);
        var written = new List<CharacterRange>();
        var count = 0;
        var index = 0;
        var copiedUpTo = 0;
        while (index < characters.Length)
        {
            if (!(index == 0 || !TextScanning.IsWordCharacter(characters[index - 1]) || !TextScanning.IsWordCharacter(characters[index]))
                || FirstMatch(rules, characters, folded, index) is not { } match)
            {
                index += 1;
                continue;
            }
            var (rule, end) = match;
            var original = SwiftString.Join(characters, index, end);
            AddRange(output, characters, copiedUpTo, index);
            var start = output.Count;
            output.AddRange(TextElements.Characters(rule.Replacement));
            written.Add(new CharacterRange(start, output.Count));
            if (!string.Equals(original, rule.Replacement, StringComparison.Ordinal))
            {
                count += 1;
            }
            copiedUpTo = end;
            index = Math.Max(end, index + 1);
        }
        AddRange(output, characters, copiedUpTo, characters.Length);
        return (output, count, written);
    }

    static (ReplacementRule Rule, int End)? FirstMatch(List<(ReplacementRule Rule, int Offset)> rules, string[] characters, string[] folded, int start)
    {
        foreach (var (rule, _) in rules)
        {
            if (rule.Match(characters, folded, start) is { } end)
            {
                return (rule, end);
            }
        }
        return null;
    }

    static void AddRange(List<string> output, string[] characters, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            output.Add(characters[index]);
        }
    }

    // MARK: - Terms

    /// <summary>
    /// Replaces windows of 1–3 words that match a term containing Latin letters; returns the
    /// count applied.
    ///
    /// Words are compared through a spelling key: Cyrillic is transliterated, English
    /// spellings are reduced to how they sound (actions → akshns, docker → doker) and doubled
    /// letters collapse. A window matches when the normalized edit distance of the keys is at
    /// most <see cref="MaximumDistance"/> and the guards in <c>Accepts</c> pass. Among
    /// overlapping matches the longer term wins, then the closer match.
    /// </summary>
    /// <summary>
    /// Names whose spelling does not sound the way they are said, in the form a Russian-only
    /// engine writes them: «Клод» is Claude, «апи» is API. Fuzzy correction cannot find them
    /// (Claude is spelled like «клауде»; API is too short), so these are matched as words, in any
    /// case form, and only for terms in the Dictionary. Keys and aliases are folded.
    /// </summary>
    internal static readonly Dictionary<string, string[]> SpokenAliases = new(StringComparer.Ordinal)
    {
        ["claude"] = ["клод"],
        ["api"] = ["апи", "эйпиай"],
        ["ios"] = ["айос"],
        ["xcode"] = ["икскод"],
    };

    /// <summary>Endings a borrowed name takes in Russian: «у Клода», «Клоду», «с Клодом», «о Клоде».</summary>
    internal static readonly string[] CaseEndings = ["ом", "ем", "а", "я", "у", "ю", "е"];

    /// <summary>Writes the Dictionary's term for every spoken alias of it; returns the count applied.</summary>
    public (string Text, int Count) ApplySpokenAliases(string text, IReadOnlyList<string> terms)
    {
        text = SwiftString.Canonical(text);
        var termByAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            var trimmed = term.Trim();
            if (SpokenAliases.TryGetValue(TextScanning.Fold(trimmed), out var aliases))
            {
                foreach (var alias in aliases)
                {
                    termByAlias.TryAdd(alias, trimmed);
                }
            }
        }
        if (termByAlias.Count == 0 || text.Length == 0)
        {
            return (text, 0);
        }
        var characters = TextElements.Characters(text);
        var output = new StringBuilder(text.Length);
        var copiedUpTo = 0;
        var count = 0;
        foreach (var range in TextScanning.Words(characters))
        {
            if (AliasedTerm(TextScanning.Fold(SwiftString.Join(characters, range.LowerBound, range.UpperBound)), termByAlias) is not { } term)
            {
                continue;
            }
            output.Append(SwiftString.Join(characters, copiedUpTo, range.LowerBound));
            output.Append(term);
            copiedUpTo = range.UpperBound;
            count++;
        }
        output.Append(SwiftString.Join(characters, copiedUpTo, characters.Length));
        return (output.ToString(), count);
    }

    internal static string? AliasedTerm(string folded, Dictionary<string, string> termByAlias)
    {
        if (termByAlias.TryGetValue(folded, out var term))
        {
            return term;
        }
        foreach (var ending in CaseEndings)
        {
            if (folded.EndsWith(ending, StringComparison.Ordinal) && termByAlias.TryGetValue(folded[..^ending.Length], out var stemmed))
            {
                return stemmed;
            }
        }
        return null;
    }

    public (string Text, int Count) CorrectTerms(string text, IReadOnlyList<string> terms)
    {
        text = SwiftString.Canonical(text);
        var prepared = PrepareTerms(terms);
        if (prepared.Count == 0 || text.Length == 0)
        {
            return (text, 0);
        }

        var characters = TextElements.Characters(text);
        var words = TextScanning.Words(characters, TextScanning.NameJoiners);
        var context = new WordContext(characters, words);
        // Each word is transliterated once; windows share them.
        var wordTexts = words.Select(range => SwiftString.Join(characters, range.LowerBound, range.UpperBound)).ToArray();
        var compacts = wordTexts.Select(Transliterator.CompactLatin).ToArray();
        var candidates = new List<TermCandidate>();
        for (var first = 0; first < words.Count; first++)
        {
            for (var size = 1; size <= 3; size++)
            {
                var last = first + size - 1;
                if (last >= words.Count)
                {
                    break;
                }
                // Punctuation ends a phrase: "упал, посмотри" is never one window.
                if (size > 1 && !AllWhitespace(characters, words[last - 1].UpperBound, words[last].LowerBound))
                {
                    break;
                }
                var window = new TermWindow(characters, words, wordTexts, compacts, first, last, context);
                if (window.Key.Length == 0)
                {
                    continue;
                }
                if (size > 1 && window.HasShortCyrillicEdge)
                {
                    continue;
                }
                foreach (var term in prepared)
                {
                    if (Accepts(window, term) is { } match)
                    {
                        candidates.Add(new TermCandidate(term, match, first, last));
                    }
                }
            }
        }
        if (candidates.Count == 0)
        {
            return (text, 0);
        }

        candidates.Sort((lhs, rhs) =>
        {
            if (lhs.Term.Key.Length != rhs.Term.Key.Length) return rhs.Term.Key.Length.CompareTo(lhs.Term.Key.Length);
            if (lhs.Match.Distance != rhs.Match.Distance) return lhs.Match.Distance.CompareTo(rhs.Match.Distance);
            if (lhs.WordCount != rhs.WordCount) return lhs.WordCount.CompareTo(rhs.WordCount);
            if (lhs.FirstWord != rhs.FirstWord) return lhs.FirstWord.CompareTo(rhs.FirstWord);
            return SwiftString.CompareScalars(lhs.Term.Text, rhs.Term.Text);
        });
        var taken = new bool[words.Count];
        var chosen = new List<TermCandidate>();
        foreach (var candidate in candidates)
        {
            var free = true;
            for (var index = candidate.FirstWord; index <= candidate.LastWord; index++)
            {
                free &= !taken[index];
            }
            if (!free)
            {
                continue;
            }
            for (var index = candidate.FirstWord; index <= candidate.LastWord; index++)
            {
                taken[index] = true;
            }
            chosen.Add(candidate);
        }
        chosen.Sort((lhs, rhs) => lhs.FirstWord.CompareTo(rhs.FirstWord));

        var output = new StringBuilder(text.Length);
        var copiedUpTo = 0;
        var count = 0;
        foreach (var candidate in chosen)
        {
            var start = words[candidate.FirstWord].LowerBound;
            var end = words[candidate.LastWord].UpperBound;
            var original = SwiftString.Join(characters, start, end);
            // A match that keeps the text still claims its words, so no other term rewrites them.
            if (!candidate.Match.Rewrites || string.Equals(original, candidate.Term.Text, StringComparison.Ordinal))
            {
                continue;
            }
            output.Append(SwiftString.Join(characters, copiedUpTo, start));
            output.Append(candidate.Term.Text);
            copiedUpTo = end;
            count += 1;
        }
        output.Append(SwiftString.Join(characters, copiedUpTo, characters.Length));
        return (output.ToString(), count);
    }

    /// <summary>How the window matches the term, or null when it does not count as the term.</summary>
    internal TermMatch? Accepts(TermWindow window, PreparedTerm term)
    {
        var windowKey = window.Key;
        var termKey = term.Key;
        // The spelling key ignores numbers. Fuzzy correction must preserve them and their separators.
        if (windowKey.Length < MinimumTermLetters || !window.NumberTokens.SequenceEqual(term.NumberTokens, StringComparer.Ordinal))
        {
            return null;
        }
        var longest = Math.Max(windowKey.Length, termKey.Length);
        if ((double)Math.Abs(windowKey.Length - termKey.Length) > MaximumDistance * longest)
        {
            return null;
        }
        var edits = SpellingKey.EditDistance(windowKey, termKey);
        var distance = (double)edits / longest;
        if (distance > MaximumDistance)
        {
            return null;
        }

        var consonantDistance = SpellingKey.ConsonantDistance(window.Consonants, term.Consonants);
        if (consonantDistance > ConsonantTolerance * term.Consonants.Length + 1e-9)
        {
            return null;
        }

        // A word that is only the start of a longer term is a different word: Swift, not SwiftUI.
        if (windowKey.Length < termKey.Length && termKey.StartsWith(windowKey, StringComparison.Ordinal))
        {
            return null;
        }

        // With no dictionary to ask, a short near word is too likely to be an ordinary one.
        var language = window.ContainsCyrillic ? "ru" : "en";
        if (!Lexicon.HasLanguage(language) && longest <= FallbackShortKeyLetters && edits > 1)
        {
            return null;
        }
        return window.ContainsCyrillic
            ? AcceptsCyrillic(window, term, distance)
            : AcceptsLatin(window, term, distance);
    }

    TermMatch? AcceptsCyrillic(TermWindow window, PreparedTerm term, double distance)
    {
        // Real Russian words and names are not terms written the wrong way: «кладу» is not
        // Claude, «Версале» is not Vercel. Only a word that sounds exactly like the term can
        // still be it, and only when it is a borrowed product name (докер, телеграм) or the
        // engine wrote it as a name in mid-sentence («в Редис»): a lowercase «редис» is the
        // vegetable and «мастер» is a craftsman, whatever terms sound the same.
        if (Lexicon.AreRealWords(window.Words))
        {
            // The word must not double a letter the term has once: «телеграмм» (of
            // телеграмма) is not Telegram, while «Гигам» may still be GigaAM.
            if (window.ExactKey != term.ExactKey && window.ExactKey != term.Key)
            {
                return null;
            }
            var isBorrowedName = BorrowedNames.Contains(window.FoldedText);
            // A capital in mid-sentence marks a name, but not when the term is an ordinary
            // English word: «Мастер и Маргарита» is a title, not master.
            var isWrittenAsName = window.IsCapitalizedName && !Lexicon.AreEnglishWords(term.Words);
            if (!isBorrowedName && !isWrittenAsName)
            {
                return null;
            }
        }
        return new TermMatch(distance, Rewrites: true);
    }

    TermMatch? AcceptsLatin(TermWindow window, PreparedTerm term, double distance)
    {
        // Engines that already wrote Latin letters got the spelling mostly right.
        var plainLongest = Math.Max(window.PlainLetters.Length, term.PlainLetters.Length);
        if (plainLongest <= 0
            || (double)SpellingKey.EditDistance(window.PlainLetters, term.PlainLetters) / plainLongest > MaximumDistance
            || window.Words.Count < term.WordCount)
        {
            return null;
        }
        var caseOnly = window.FoldedText == term.FoldedText;

        if (!Lexicon.AreRealWords(window.Words))
        {
            // Without an English dictionary every word looks misspelled, so plain casing
            // stays: "option" is not rewritten as Option.
            var keepsCase = caseOnly && !term.HasNameCasing && !Lexicon.HasLanguage("en");
            return new TermMatch(distance, Rewrites: !keepsCase);
        }
        if (window.IsInsideRussian)
        {
            // Inside Russian speech the engine writes English names in Latin letters, with
            // sentence casing or split into dictionary words: "User Id", "Test Flit". A
            // single word is still never swapped for a different one: "cloud" stays.
            if (window.PlainLetters != term.PlainLetters && window.Words.Count <= term.WordCount)
            {
                return null;
            }
            return new TermMatch(distance, Rewrites: true);
        }
        // English text keeps its words ("Jason", "Read me") and takes a term's casing only
        // when the casing is part of the name: "json" becomes JSON, "option" stays.
        if (!caseOnly)
        {
            return null;
        }
        return new TermMatch(distance, Rewrites: term.HasNameCasing);
    }

    internal List<PreparedTerm> PrepareTerms(IReadOnlyList<string> terms)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new List<PreparedTerm>();
        foreach (var raw in terms)
        {
            var text = SwiftString.Canonical(raw).Trim();
            if (text.Length == 0 || !seen.Add(text))
            {
                continue;
            }
            var characters = TextElements.Characters(text);
            if (!characters.Any(TextScanning.IsLatinLetter) || characters.Count(SwiftCharacter.IsLetter) < MinimumTermLetters)
            {
                continue;
            }
            var ranges = TextScanning.Words(characters, TextScanning.NameJoiners);
            var words = ranges.Select(range => SwiftString.Join(characters, range.LowerBound, range.UpperBound)).ToArray();
            var key = SpellingKey.Make(words);
            if (key.Length == 0)
            {
                continue;
            }
            var exactKey = SpellingKey.Make(words, collapsingRepeats: false);
            // Inner capitals are part of a name: macOS, JSON, GitHub. Redis and user id are plain.
            var hasNameCasing = ranges.Any(range =>
            {
                for (var index = range.LowerBound + 1; index < range.UpperBound; index++)
                {
                    if (SwiftCharacter.IsUppercase(characters[index]))
                    {
                        return true;
                    }
                }
                return false;
            });
            prepared.Add(new PreparedTerm(text, words, key, exactKey, SpellingKey.PlainLetters(text), TextScanning.Fold(text),
                NumberTokens(characters, 0, characters.Length), hasNameCasing));
        }
        return prepared;
    }

    internal static IReadOnlyList<string> NumberTokens(string[] characters, int start, int end)
    {
        List<string>? tokens = null;
        for (var index = start; index < end; index++)
        {
            if (!SwiftCharacter.IsNumber(characters[index]))
            {
                continue;
            }
            var first = index;
            while (index + 1 < end)
            {
                var next = characters[index + 1];
                if (SwiftCharacter.IsNumber(next))
                {
                    index++;
                }
                else if (index + 2 < end && next is ("." or "," or ":" or "/" or "–" or "−" or "-")
                    && SwiftCharacter.IsNumber(characters[index + 2]))
                {
                    index += 2;
                }
                else
                {
                    break;
                }
            }
            (tokens ??= []).Add(SwiftString.Join(characters, first, index + 1));
        }
        return tokens is null ? [] : tokens;
    }

    static bool AllWhitespace(string[] characters, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!SwiftCharacter.IsWhitespace(characters[index]))
            {
                return false;
            }
        }
        return true;
    }

    // MARK: - Stage

    public TextPipelineResult Apply(TextPipelineResult result, TextPipelineContext context)
    {
        if (context.Dictionary.Count == 0)
        {
            return result;
        }
        var terms = context.Dictionary.Where(item => item.Kind == DictionaryItemKind.Term).Select(item => item.Text).ToList();
        var characters = TextElements.Characters(SwiftString.Canonical(result.Text));
        var protectedRanges = SnippetProtection.Ranges(characters, result, context.Snippets);
        var text = new StringBuilder(result.Text.Length);
        var replacements = 0;
        var fixes = 0;
        foreach (var (segment, isProtected) in SnippetProtection.Segments(characters, protectedRanges))
        {
            if (isProtected)
            {
                text.Append(segment);
                continue;
            }
            var replaced = Replacing(segment, context.Dictionary);
            replacements += replaced.Count;
            // A replacement's output is the user's explicit spelling; terms never rewrite it.
            foreach (var (piece, pieceIsProtected) in SnippetProtection.Segments(replaced.Characters, replaced.Written))
            {
                if (pieceIsProtected)
                {
                    text.Append(piece);
                    continue;
                }
                var aliased = ApplySpokenAliases(piece, terms);
                var corrected = CorrectTerms(aliased.Text, terms);
                text.Append(corrected.Text);
                fixes += aliased.Count + corrected.Count;
            }
        }
        return result with
        {
            Text = text.ToString(),
            ReplacementsApplied = result.ReplacementsApplied + replacements,
            TermFixesApplied = result.TermFixesApplied + fixes,
        };
    }
}

// MARK: - Replacement matching

internal sealed class ReplacementRule(IReadOnlyList<string?> pattern, string replacement)
{
    /// <summary>Folded characters; null stands for a run of whitespace.</summary>
    public IReadOnlyList<string?> Pattern { get; } = pattern;
    public string Replacement { get; } = replacement;

    public static List<string?> PatternFor(string from)
    {
        var elements = new List<string?>();
        foreach (var character in TextElements.Characters(SwiftString.Canonical(from).Trim()))
        {
            if (SwiftCharacter.IsWhitespace(character))
            {
                if (elements.Count == 0 || elements[^1] is not null)
                {
                    elements.Add(null);
                }
            }
            else
            {
                elements.Add(TextScanning.FoldCharacter(character));
            }
        }
        return elements;
    }

    /// <summary>The end offset of a match starting at <paramref name="start"/>, respecting word boundaries.</summary>
    public int? Match(string[] characters, string[] folded, int start)
    {
        if (Pattern.Count == 0 || Pattern[0] is null)
        {
            return null;
        }
        var position = start;
        foreach (var element in Pattern)
        {
            if (element is not null)
            {
                if (position >= characters.Length || !string.Equals(folded[position], element, StringComparison.Ordinal))
                {
                    return null;
                }
                position += 1;
            }
            else
            {
                if (position >= characters.Length || !SwiftCharacter.IsWhitespace(characters[position]))
                {
                    return null;
                }
                while (position < characters.Length && SwiftCharacter.IsWhitespace(characters[position]))
                {
                    position += 1;
                }
            }
        }
        // Whole words only: "кот" must not match inside «котик».
        if (TextScanning.IsWordCharacter(characters[start]) && start > 0 && TextScanning.IsWordCharacter(characters[start - 1]))
        {
            return null;
        }
        if (TextScanning.IsWordCharacter(characters[position - 1]) && position < characters.Length
            && TextScanning.IsWordCharacter(characters[position]))
        {
            return null;
        }
        return position;
    }
}

// MARK: - Term matching

internal sealed record PreparedTerm(
    string Text,
    IReadOnlyList<string> Words,
    string Key,
    /* The key with doubled letters kept. */ string ExactKey,
    string PlainLetters,
    string FoldedText,
    IReadOnlyList<string> NumberTokens,
    /* Casing that belongs to the name rather than to a sentence: macOS, JSON. */ bool HasNameCasing)
{
    public int WordCount => Words.Count;

    public string Consonants { get; } = SpellingKey.Consonants(Key);
}

/// <summary>How a window matches a term.</summary>
/// <param name="Rewrites">
/// False when the window counts as the term but keeps its own spelling, as a real English
/// word in plain casing does ("option" for Option).
/// </param>
internal readonly record struct TermMatch(double Distance, bool Rewrites);

/// <summary>Where each word of a text stands: its script and whether it starts a sentence.</summary>
internal sealed class WordContext
{
    readonly bool[] hasCyrillic;
    readonly bool[] hasLatin;
    /// <summary>The nearest word with letters before each word (numbers skipped), if any.</summary>
    readonly int?[] previousLettered;
    /// <summary>The nearest word with letters after each word, if any.</summary>
    readonly int?[] nextLettered;

    public WordContext(string[] characters, List<CharacterRange> words)
    {
        hasCyrillic = words.Select(range => AnyIn(characters, range, TextScanning.IsCyrillic)).ToArray();
        hasLatin = words.Select(range => AnyIn(characters, range, TextScanning.IsLatinLetter)).ToArray();
        StartsSentence = new bool[words.Count];
        for (var index = 0; index < words.Count; index++)
        {
            StartsSentence[index] = index == 0 || AnyIn(characters, new CharacterRange(words[index - 1].UpperBound, words[index].LowerBound),
                character => character is "." or "!" or "?" or "…" || SwiftCharacter.IsNewline(character));
        }
        previousLettered = new int?[words.Count];
        nextLettered = new int?[words.Count];
        int? lastLettered = null;
        for (var index = 0; index < words.Count; index++)
        {
            previousLettered[index] = lastLettered;
            if (hasCyrillic[index] || hasLatin[index])
            {
                lastLettered = index;
            }
        }
        lastLettered = null;
        for (var index = words.Count - 1; index >= 0; index--)
        {
            nextLettered[index] = lastLettered;
            if (hasCyrillic[index] || hasLatin[index])
            {
                lastLettered = index;
            }
        }
    }

    public bool[] StartsSentence { get; }

    /// <summary>
    /// Whether the nearest words with letters around first…last are all Russian, so the
    /// words in between are English written inside Russian speech.
    /// </summary>
    public bool IsInsideRussian(int first, int last)
    {
        var neighbours = new[] { previousLettered[first], nextLettered[last] }.OfType<int>().ToArray();
        return neighbours.Length > 0 && neighbours.All(index => !hasLatin[index]) && neighbours.Any(index => hasCyrillic[index]);
    }

    internal static bool AnyIn(string[] characters, CharacterRange range, Func<string, bool> predicate)
    {
        for (var index = range.LowerBound; index < range.UpperBound; index++)
        {
            if (predicate(characters[index]))
            {
                return true;
            }
        }
        return false;
    }
}

internal sealed class TermWindow
{
    string? consonants;

    /// <param name="wordTexts">The text of every word of <paramref name="ranges"/>.</param>
    /// <param name="compacts">Their <see cref="Transliterator.CompactLatin"/> forms.</param>
    public TermWindow(string[] characters, List<CharacterRange> ranges, string[] wordTexts, string[] compacts, int first, int last, WordContext context)
    {
        Words = wordTexts[first..(last + 1)];
        var windowCompacts = compacts[first..(last + 1)];
        Key = SpellingKey.FromCompact(windowCompacts);
        ExactKey = SpellingKey.FromCompact(windowCompacts, collapsingRepeats: false);
        var span = SwiftString.Join(characters, ranges[first].LowerBound, ranges[last].UpperBound);
        PlainLetters = SpellingKey.PlainLetters(span);
        FoldedText = TextScanning.Fold(span);
        NumberTokens = DictionaryCorrector.NumberTokens(characters, ranges[first].LowerBound, ranges[last].UpperBound);
        ContainsCyrillic = WordContext.AnyIn(characters, new CharacterRange(ranges[first].LowerBound, ranges[last].UpperBound), TextScanning.IsCyrillic);
        // Prepositions and conjunctions (в, и, на) never belong to a term.
        bool IsShortCyrillic(CharacterRange range)
        {
            if (range.Count > 2)
            {
                return false;
            }
            for (var index = range.LowerBound; index < range.UpperBound; index++)
            {
                if (!TextScanning.IsCyrillic(characters[index]))
                {
                    return false;
                }
            }
            return true;
        }
        HasShortCyrillicEdge = IsShortCyrillic(ranges[first]) || IsShortCyrillic(ranges[last]);
        IsInsideRussian = context.IsInsideRussian(first, last);
        var allCapitalized = true;
        for (var index = first; index <= last; index++)
        {
            allCapitalized &= SwiftCharacter.IsUppercase(characters[ranges[index].LowerBound]);
        }
        IsCapitalizedName = !context.StartsSentence[first] && allCapitalized;
    }

    public IReadOnlyList<string> Words { get; }
    public string Key { get; }
    /// <summary>The key with doubled letters kept.</summary>
    public string ExactKey { get; }
    /// <summary>The consonants of the key, computed when a term comes close enough to need them.</summary>
    public string Consonants => consonants ??= SpellingKey.Consonants(Key);
    public string PlainLetters { get; }
    public string FoldedText { get; }
    public IReadOnlyList<string> NumberTokens { get; }
    public bool ContainsCyrillic { get; }
    public bool HasShortCyrillicEdge { get; }
    /// <summary>Latin words with Russian words around them.</summary>
    public bool IsInsideRussian { get; }
    /// <summary>Every word capitalized, and not because a sentence starts with it: «в Редис».</summary>
    public bool IsCapitalizedName { get; }
}

internal sealed record TermCandidate(PreparedTerm Term, TermMatch Match, int FirstWord, int LastWord)
{
    public int WordCount => LastWord - FirstWord + 1;
}

/// <summary>
/// A rough pronunciation key in ASCII, so a Russian spelling of an English term and the term
/// itself come out close.
/// </summary>
internal static class SpellingKey
{
    /// <param name="collapsingRepeats">Doubled letters become one (the default), so «постгресс» sounds like Postgres.</param>
    public static string Make(IReadOnlyList<string> words, bool collapsingRepeats = true) =>
        FromCompact(words.Select(Transliterator.CompactLatin).ToArray(), collapsingRepeats);

    /// <summary><see cref="Make"/> from words already reduced by <see cref="Transliterator.CompactLatin"/>.</summary>
    public static string FromCompact(IReadOnlyList<string> compacts, bool collapsingRepeats = true)
    {
        var bytes = new StringBuilder();
        foreach (var compact in compacts)
        {
            // A leading x is read as "eks": Xcode.
            if (compact.Length > 0 && compact[0] == 'x')
            {
                bytes.Append('e');
            }
            bytes.Append(compact);
        }
        var key = bytes.ToString()
            .Replace("tion", "shn", StringComparison.Ordinal)
            .Replace("ph", "f", StringComparison.Ordinal)
            .Replace("wh", "v", StringComparison.Ordinal)
            .Replace("ck", "k", StringComparison.Ordinal)
            .Replace("qu", "kv", StringComparison.Ordinal)
            .Replace("q", "k", StringComparison.Ordinal)
            .Replace("x", "ks", StringComparison.Ordinal)
            .Replace("igh", "ai", StringComparison.Ordinal);
        key = SoftenC(key)
            .Replace("dzh", "j", StringComparison.Ordinal)
            .Replace("w", "v", StringComparison.Ordinal)
            .Replace("z", "s", StringComparison.Ordinal)
            .Replace("y", "i", StringComparison.Ordinal);
        return collapsingRepeats ? CollapseRepeats(key) : key;
    }

    /// <summary>Lowercased ASCII letters and digits of a text, without transliteration.</summary>
    public static string PlainLetters(string text)
    {
        var lowered = SwiftUnicode.Lowercased(text);
        var output = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                output.Append(c);
            }
        }
        return output.ToString();
    }

    public static string Consonants(string key)
    {
        var output = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (c is not ('a' or 'e' or 'i' or 'o' or 'u'))
            {
                output.Append(c);
            }
        }
        return output.ToString();
    }

    public static int EditDistance(string lhs, string rhs)
    {
        if (lhs.Length == 0) return rhs.Length;
        if (rhs.Length == 0) return lhs.Length;
        var previous = rhs.Length < 128 ? stackalloc int[rhs.Length + 1] : new int[rhs.Length + 1];
        var current = rhs.Length < 128 ? stackalloc int[rhs.Length + 1] : new int[rhs.Length + 1];
        for (var j = 0; j <= rhs.Length; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= lhs.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= rhs.Length; j++)
            {
                var substitution = previous[j - 1] + (lhs[i - 1] == rhs[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            var swap = previous;
            previous = current;
            current = swap;
        }
        return previous[rhs.Length];
    }

    /// <summary>
    /// Edit distance where a voiced consonant swapped for its voiceless pair (d/t, b/p, g/k,
    /// z/s, v/f) costs half.
    /// </summary>
    public static double ConsonantDistance(string lhs, string rhs)
    {
        if (lhs.Length == 0) return rhs.Length;
        if (rhs.Length == 0) return lhs.Length;
        var previous = rhs.Length < 128 ? stackalloc double[rhs.Length + 1] : new double[rhs.Length + 1];
        var current = rhs.Length < 128 ? stackalloc double[rhs.Length + 1] : new double[rhs.Length + 1];
        for (var j = 0; j <= rhs.Length; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= lhs.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= rhs.Length; j++)
            {
                var cost = lhs[i - 1] == rhs[j - 1] ? 0 : (ArePaired(lhs[i - 1], rhs[j - 1]) ? 0.5 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            var swap = previous;
            previous = current;
            current = swap;
        }
        return previous[rhs.Length];
    }

    static readonly string[] Pairs = ["bp", "dt", "gk", "sz", "vf"];

    static bool ArePaired(char lhs, char rhs)
    {
        foreach (var pair in Pairs)
        {
            if (pair.Contains(lhs) && pair.Contains(rhs))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>"c" sounds like "s" before e, i and y and like "k" elsewhere; "ch" stays.</summary>
    static string SoftenC(string key)
    {
        if (!key.Contains('c'))
        {
            return key;
        }
        var output = new StringBuilder(key.Length);
        var index = 0;
        while (index < key.Length)
        {
            var c = key[index];
            if (c != 'c')
            {
                output.Append(c);
                index += 1;
                continue;
            }
            var next = index + 1 < key.Length ? key[index + 1] : '\0';
            if (next == 'h')
            {
                output.Append("ch");
                index += 2;
                continue;
            }
            output.Append(next is 'e' or 'i' or 'y' ? 's' : 'k');
            index += 1;
        }
        return output.ToString();
    }

    static string CollapseRepeats(string key)
    {
        var output = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (output.Length == 0 || output[^1] != c)
            {
                output.Append(c);
            }
        }
        return output.ToString();
    }
}
