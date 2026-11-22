using System.Text;
using Slovo.Core.Model;

namespace Slovo.Core.Text;

/// <summary>
/// Applies the frontmost app category's style: capitalization and final punctuation.
/// <list type="bullet">
/// <item>Formal: sentence starts capitalized, punctuation kept, a final period added.</item>
/// <item>Casual: sentence starts capitalized, the final period of the last sentence dropped.</item>
/// <item>Very casual: sentence starts lowercased except dictionary terms and words that look
/// like names, sentence-ending periods dropped.</item>
/// </list>
/// Only sentence starts change case; the rest of the text keeps the engine's casing. A period
/// after an abbreviation («т. е.», «г.», «руб.», "etc.") does not start a sentence unless the
/// engine capitalized the next word, and it is never dropped.
/// </summary>
public sealed class StyleFormatter : ITextStage
{
    /// <param name="lexicon">
    /// Tells English words from names for lowercase terms, and proper nouns from ordinary
    /// words for Very casual. The app asks the system spell checker; tests and the shared
    /// fixtures pass a fixed word list.
    /// </param>
    public StyleFormatter(IWordLexicon lexicon)
    {
        Lexicon = lexicon;
    }

    public IWordLexicon Lexicon { get; }

    /// <param name="protectedTerms">Dictionary terms whose casing Very casual keeps.</param>
    public string Format(string text, StylePreset style, IReadOnlyList<string> protectedTerms) =>
        Format(TextElements.Characters(SwiftString.Canonical(text)), style, protectedTerms, []);

    /// <summary><paramref name="lockedRanges"/> (snippet expansions) keep their characters exactly.</summary>
    internal string Format(string[] input, StylePreset style, IReadOnlyList<string> protectedTerms, IReadOnlyList<CharacterRange> lockedRanges)
    {
        if (!input.Any(TextScanning.IsWordCharacter))
        {
            return string.Concat(input);
        }
        var characters = (string[])input.Clone();
        var locked = new bool[characters.Length];
        foreach (var range in lockedRanges)
        {
            for (var index = range.LowerBound; index < range.UpperBound && index < locked.Length; index++)
            {
                locked[index] = true;
            }
        }
        var words = TextScanning.Words(characters, TextScanning.WordJoiners);
        var terms = new TermCasing(protectedTerms, Lexicon);
        var starts = SentenceStarts(characters, words);

        switch (style)
        {
            case StylePreset.Formal:
            case StylePreset.Casual:
                foreach (var wordIndex in starts)
                {
                    var position = words[wordIndex].LowerBound;
                    if (locked[position] || !SwiftCharacter.IsLowercase(characters[position])
                        || IsCaseSensitive(characters, words[wordIndex])
                        || terms.KeepsLowercaseStart(characters, words, wordIndex))
                    {
                        continue;
                    }
                    TextScanning.SetCase(characters, position, uppercase: true);
                }
                break;
            case StylePreset.VeryCasual:
                var context = new NameContext(characters, words, [.. starts], Lexicon);
                foreach (var wordIndex in starts)
                {
                    var range = words[wordIndex];
                    if (locked[range.LowerBound])
                    {
                        continue;
                    }
                    if (terms.Match(characters, words, wordIndex) is { } termWord)
                    {
                        // A term keeps its dictionary spelling: «Docker», «user id».
                        if (termWord.Length == range.Count)
                        {
                            for (var offset = 0; offset < termWord.Length; offset++)
                            {
                                if (!locked[range.LowerBound + offset])
                                {
                                    characters[range.LowerBound + offset] = termWord[offset];
                                }
                            }
                        }
                        continue;
                    }
                    if (!SwiftCharacter.IsUppercase(characters[range.LowerBound]) || context.LooksLikeName(wordIndex))
                    {
                        continue;
                    }
                    TextScanning.SetCase(characters, range.LowerBound, uppercase: false);
                }
                break;
        }

        var removals = new HashSet<int>();
        int? appendPeriodAt = null;
        var lastVisible = Array.FindLastIndex(characters, character => !SwiftCharacter.IsWhitespace(character));
        if (lastVisible >= 0)
        {
            // An emoticon ends the text on its own: "спасибо :)" gets no period, and its colon
            // is not punctuation to replace.
            var endsWithEmoticon = IsEmoticonEnd(characters, lastVisible);
            var inner = lastVisible;
            while (inner > 0 && Closers.Contains(characters[inner]))
            {
                inner -= 1;
            }
            switch (style)
            {
                case StylePreset.Formal:
                    if (!locked[inner] && !endsWithEmoticon)
                    {
                        if (characters[inner] is "," or ";" or ":")
                        {
                            characters[inner] = ".";
                        }
                        else if (TextScanning.IsWordCharacter(characters[inner]) || characters[inner] is "%" or "+")
                        {
                            appendPeriodAt = lastVisible + 1;
                        }
                    }
                    break;
                case StylePreset.Casual:
                    if (!endsWithEmoticon && IsDroppablePeriod(characters, inner, locked))
                    {
                        removals.Add(inner);
                    }
                    break;
                case StylePreset.VeryCasual:
                    for (var index = 0; index < characters.Length; index++)
                    {
                        if (characters[index] == "." && EndsSentence(characters, index) && IsDroppablePeriod(characters, index, locked))
                        {
                            removals.Add(index);
                        }
                    }
                    break;
            }
        }

        var output = new StringBuilder(characters.Length + 1);
        for (var index = 0; index < characters.Length; index++)
        {
            if (index == appendPeriodAt)
            {
                output.Append('.');
            }
            if (!removals.Contains(index))
            {
                output.Append(characters[index]);
            }
        }
        if (appendPeriodAt == characters.Length)
        {
            output.Append('.');
        }
        return output.ToString();
    }

    static readonly HashSet<string> Closers = new(StringComparer.Ordinal) { "\"", "'", "»", "”", "’", ")", "]", "}" };

    /// <summary>":)", ";-)", ":(", ":D" or ":P" ending at <paramref name="end"/>.</summary>
    static bool IsEmoticonEnd(string[] characters, int end)
    {
        if (end < 1 || characters[end] is not (")" or "(" or "D" or "P"))
        {
            return false;
        }
        var eyes = end - 1;
        if (characters[eyes] == "-" && eyes >= 1)
        {
            eyes -= 1;
        }
        return characters[eyes] is ":" or ";";
    }

    /// <summary>
    /// A sentence-start word whose case carries meaning: part of a link, an address, a path
    /// or a file or domain name, or a name with an inner capital (iPhone, eBay).
    /// </summary>
    static bool IsCaseSensitive(string[] characters, CharacterRange word)
    {
        for (var index = word.LowerBound + 1; index < word.UpperBound; index++)
        {
            if (SwiftCharacter.IsUppercase(characters[index]))
            {
                return true;
            }
        }
        var start = word.LowerBound;
        while (start > 0 && !SwiftCharacter.IsWhitespace(characters[start - 1]))
        {
            start -= 1;
        }
        var end = word.UpperBound;
        while (end < characters.Length && !SwiftCharacter.IsWhitespace(characters[end]))
        {
            end += 1;
        }
        var token = characters[start..end];
        var text = string.Concat(token);
        return SwiftString.Contains(text, "://") || SwiftString.Contains(text, "@") || SwiftString.Contains(text, "/") || HasNameExtension(token);
    }

    /// <summary>
    /// "github.com", "index.html": a dot after a word character and before at least two
    /// letters. «т.е.» and "e.g." have one letter after each dot and still count as words.
    /// </summary>
    static bool HasNameExtension(string[] token)
    {
        for (var index = 1; index < token.Length; index++)
        {
            if (token[index] != "." || !TextScanning.IsWordCharacter(token[index - 1]))
            {
                continue;
            }
            var letters = 0;
            for (var next = index + 1; next < token.Length && SwiftCharacter.IsLetter(token[next]); next++)
            {
                letters += 1;
            }
            if (letters >= 2)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Word indices that begin a sentence: the first word, and words after ., !, ? or …
    /// followed by a space, or after a line break.
    /// </summary>
    static List<int> SentenceStarts(string[] characters, List<CharacterRange> words)
    {
        if (words.Count == 0)
        {
            return [];
        }
        var starts = new List<int> { 0 };
        for (var index = 1; index < words.Count; index++)
        {
            var gapStart = words[index - 1].UpperBound;
            var gapEnd = words[index].LowerBound;
            if (WordContext.AnyIn(characters, new CharacterRange(gapStart, gapEnd), SwiftCharacter.IsNewline))
            {
                starts.Add(index);
                continue;
            }
            string? terminator = null;
            var spaced = false;
            for (var position = gapStart; position < gapEnd; position++)
            {
                var character = characters[position];
                if (character is "." or "!" or "?" or "…")
                {
                    terminator = character;
                }
                else if (SwiftCharacter.IsWhitespace(character) && terminator is not null)
                {
                    spaced = true;
                    break;
                }
            }
            if (terminator is null || !spaced)
            {
                continue;
            }
            if (terminator == "." && IsAbbreviationPeriod(characters, words[index - 1].UpperBound))
            {
                continue;
            }
            // «т. е. в среду», «в 2025 г. мы»: an abbreviation's period, unless the engine itself
            // started a new sentence with a capital.
            if (terminator == "." && gapStart < gapEnd && characters[gapStart] == "." && IsAbbreviation(characters, words[index - 1])
                && !SwiftCharacter.IsUppercase(characters[words[index].LowerBound]))
            {
                continue;
            }
            starts.Add(index);
        }
        return starts;
    }

    /// <summary>A period that closes a sentence: only closing quotes or brackets, then space or the end, follow it.</summary>
    static bool EndsSentence(string[] characters, int index)
    {
        var next = index + 1;
        while (next < characters.Length && Closers.Contains(characters[next]))
        {
            next += 1;
        }
        return next == characters.Length || SwiftCharacter.IsWhitespace(characters[next]);
    }

    /// <summary>A single period that is not part of an ellipsis, an abbreviation or a snippet.</summary>
    static bool IsDroppablePeriod(string[] characters, int index, bool[] locked)
    {
        if (characters[index] != "." || locked[index])
        {
            return false;
        }
        if (index > 0 && characters[index - 1] == ".")
        {
            return false;
        }
        if (index + 1 < characters.Length && characters[index + 1] == ".")
        {
            return false;
        }
        return !IsAbbreviationPeriod(characters, index) && !EndsAbbreviation(characters, index);
    }

    /// <summary>
    /// Abbreviations written with a period and usually followed by more of the sentence,
    /// lowercased and without ё. Single letters are handled separately.
    /// </summary>
    public static readonly IReadOnlySet<string> Abbreviations = new HashSet<string>(
    [
        // Russian
        "гг", "вв", "мин", "сек", "руб", "коп", "тыс", "млн", "млрд", "трлн", "см", "стр", "ул", "пр", "пл",
        "др", "им", "напр", "обл", "кв", "корп", "рис", "табл", "гл", "ст", "св", "проф", "доц", "акад", "тел",
        "зам", "нач", "англ", "рус", "лат", "пос", "дер", "наб", "пер",
        // English
        "etc", "vs", "approx", "incl", "excl", "dept", "fig", "vol", "mr", "mrs", "ms", "dr", "prof", "jr", "sr",
        "inc", "ltd", "corp", "cf", "al", "eg", "ie",
    ], StringComparer.Ordinal);

    /// <summary>Single letters that stand for a word on their own: «в 2025 г.», «в 5 ч.», «XX в.».</summary>
    public static readonly IReadOnlySet<string> LetterAbbreviations = new HashSet<string>(["г", "в", "ч", "с"], StringComparer.Ordinal);

    /// <summary>
    /// Whether the word right before a period is an abbreviation: a listed one or any single
    /// letter («т. е.», «А. С. Пушкин», "e. g.").
    /// </summary>
    static bool IsAbbreviation(string[] characters, CharacterRange word)
    {
        var text = TextScanning.Fold(SwiftString.Join(characters, word.LowerBound, word.UpperBound));
        return Abbreviations.Contains(text) || (TextElements.Count(text) == 1 && SwiftCharacter.IsLetter(characters[word.LowerBound]));
    }

    /// <summary>
    /// Whether the period at <paramref name="index"/> closes an abbreviation that keeps its
    /// period at the end of a text: a listed abbreviation, a letter such as «г.», or the
    /// second letter of a pair such as «т. д.», «т. п.», «н. э.».
    /// </summary>
    static bool EndsAbbreviation(string[] characters, int index)
    {
        var start = index;
        while (start > 0 && SwiftCharacter.IsLetter(characters[start - 1]))
        {
            start -= 1;
        }
        if (!(start < index && (start == 0 || !TextScanning.IsWordCharacter(characters[start - 1]) || SwiftCharacter.IsNumber(characters[start - 1]))))
        {
            return false;
        }
        var word = TextScanning.Fold(SwiftString.Join(characters, start, index));
        if (Abbreviations.Contains(word) || LetterAbbreviations.Contains(word))
        {
            return true;
        }
        if (TextElements.Count(word) != 1)
        {
            return false;
        }
        // «т. д.»: the first letter of the pair, followed by a single letter and its period.
        if (index + 3 < characters.Length && characters[index + 1] == " " && SwiftCharacter.IsLetter(characters[index + 2])
            && characters[index + 3] == ".")
        {
            return true;
        }
        // The second letter, after a single letter and its period.
        if (!(start >= 3 && characters[start - 1] == " " && characters[start - 2] == "." && SwiftCharacter.IsLetter(characters[start - 3])))
        {
            return false;
        }
        return start == 3 || !SwiftCharacter.IsLetter(characters[start - 4]);
    }

    /// <summary>«т.е.», "e.g.": a single letter between periods.</summary>
    static bool IsAbbreviationPeriod(string[] characters, int index) =>
        index < characters.Length && characters[index] == "." && index >= 2
        && SwiftCharacter.IsLetter(characters[index - 1]) && characters[index - 2] == ".";

    public TextPipelineResult Apply(TextPipelineResult result, TextPipelineContext context)
    {
        if (context.Style is not { } style)
        {
            return result;
        }
        // Replacement targets are the user's spellings too, so they count as terms.
        var terms = context.Dictionary
            .Select(item => item.Kind == DictionaryItemKind.Term ? item.Text : item.Replacement)
            .Where(term => term.Length > 0)
            .ToList();
        var characters = TextElements.Characters(SwiftString.Canonical(result.Text));
        var locked = SnippetProtection.Ranges(characters, result, context.Snippets);
        return result with { Text = Format(characters, style, terms, locked) };
    }
}

/// <summary>Dictionary terms as words, for recognizing a term at a sentence start.</summary>
internal sealed class TermCasing
{
    readonly List<(string[] Words, string[] Folded)> terms = [];
    readonly IWordLexicon lexicon;

    public TermCasing(IReadOnlyList<string> protectedTerms, IWordLexicon lexicon)
    {
        this.lexicon = lexicon;
        foreach (var term in protectedTerms)
        {
            var characters = TextElements.Characters(SwiftString.Canonical(term));
            var words = TextScanning.Words(characters, TextScanning.WordJoiners)
                .Select(range => SwiftString.Join(characters, range.LowerBound, range.UpperBound))
                .ToArray();
            if (words.Length > 0)
            {
                terms.Add((words, words.Select(TextScanning.Fold).ToArray()));
            }
        }
    }

    /// <summary>The term's own spelling of the first word, as characters, when the text at <paramref name="index"/> is a term.</summary>
    public string[]? Match(string[] characters, List<CharacterRange> words, int index)
    {
        foreach (var term in terms)
        {
            if (index + term.Words.Length > words.Count)
            {
                continue;
            }
            var matches = true;
            for (var offset = 0; offset < term.Folded.Length && matches; offset++)
            {
                var word = words[index + offset];
                matches = TextScanning.Fold(SwiftString.Join(characters, word.LowerBound, word.UpperBound)) == term.Folded[offset];
            }
            if (matches)
            {
                return TextElements.Characters(term.Words[0]);
            }
        }
        return null;
    }

    /// <summary>
    /// Terms that start lowercase on purpose keep it at a sentence start: macOS and iOS, and
    /// names such as npm that are not English words (rebase still becomes Rebase).
    /// </summary>
    public bool KeepsLowercaseStart(string[] characters, List<CharacterRange> words, int index)
    {
        if (Match(characters, words, index) is not { } first || first.Length == 0 || !SwiftCharacter.IsLowercase(first[0]))
        {
            return false;
        }
        var range = words[index];
        if (!characters.AsSpan(range.LowerBound, range.Count).SequenceEqual(first))
        {
            return false;
        }
        if (first.Any(SwiftCharacter.IsUppercase))
        {
            return true;
        }
        var word = string.Concat(first);
        return first.All(character => !SwiftCharacter.IsLetter(character) || TextScanning.IsLatinLetter(character))
            && !lexicon.IsCorrect(word, "en");
    }
}

/// <summary>Heuristics for Very casual: which capitalized sentence starts are names.</summary>
internal sealed class NameContext
{
    readonly string[] wordTexts;
    readonly HashSet<int> starts;
    readonly bool mostlyCyrillic;
    readonly IWordLexicon lexicon;

    public NameContext(string[] characters, List<CharacterRange> words, HashSet<int> starts, IWordLexicon lexicon)
    {
        wordTexts = words.Select(range => SwiftString.Join(characters, range.LowerBound, range.UpperBound)).ToArray();
        this.starts = starts;
        this.lexicon = lexicon;
        var cyrillic = characters.Count(character => SwiftCharacter.IsLetter(character) && TextScanning.IsCyrillic(character));
        var latin = characters.Count(TextScanning.IsLatinLetter);
        mostlyCyrillic = cyrillic > latin;
    }

    public bool LooksLikeName(int index)
    {
        var word = wordTexts[index];
        var wordCharacters = TextElements.Characters(word);
        var letters = wordCharacters.Where(SwiftCharacter.IsLetter).ToArray();
        // Acronyms and inner capitals: API, GitHub, iPhone.
        if (letters.Length >= 2 && letters.All(SwiftCharacter.IsUppercase))
        {
            return true;
        }
        if (wordCharacters.Skip(1).Any(SwiftCharacter.IsUppercase))
        {
            return true;
        }
        // English "I" is always a capital.
        if (word == "I" || SwiftString.HasPrefix(word, "I'") || SwiftString.HasPrefix(word, "I’"))
        {
            return true;
        }
        // A Latin word opening a Russian sentence is almost always a product or a name.
        if (mostlyCyrillic && wordCharacters.Any(TextScanning.IsLatinLetter))
        {
            return true;
        }
        // A proper noun the dictionary knows only capitalized: «Москва», "London".
        if (IsKnownProperNoun(word, wordCharacters))
        {
            return true;
        }
        // Capitalized again in the middle of a sentence: «Маша сказала, что Маша придёт».
        for (var other = 0; other < wordTexts.Length; other++)
        {
            if (other != index && !starts.Contains(other) && wordTexts[other] == word)
            {
                return true;
            }
        }
        return false;
    }

    bool IsKnownProperNoun(string word, string[] wordCharacters)
    {
        var hasCyrillic = wordCharacters.Any(TextScanning.IsCyrillic);
        var hasLatin = wordCharacters.Any(TextScanning.IsLatinLetter);
        if (hasCyrillic == hasLatin)
        {
            return false;
        }
        var language = hasCyrillic ? "ru" : "en";
        if (!lexicon.HasLanguage(language))
        {
            return false;
        }
        return lexicon.IsCorrect(word, language) && !lexicon.IsCorrect(SwiftUnicode.Lowercased(word), language);
    }
}
