namespace Slovo.Core.Text;

/// <summary>
/// Tells real words from misspellings, so term correction leaves ordinary Russian and
/// English words alone (port of the macOS <c>WordLexicon</c>). The app asks the system
/// spell checker (<c>WindowsSpellingLexicon</c>); tests and the shared fixtures pass a fixed
/// word list or a missing dictionary. Implementations must be safe to call from any thread.
/// </summary>
public interface IWordLexicon
{
    /// <summary>Whether a dictionary for the language code ("ru", "en") is available.</summary>
    bool HasLanguage(string language);

    /// <summary>Whether the word is spelled correctly in the language; false without a dictionary.</summary>
    bool IsCorrect(string word, string language);
}

/// <summary>Word lexicons that depend on nothing outside the process, and the checks built on any lexicon.</summary>
public static class WordLexicon
{
    /// <summary>A system with no spelling dictionaries at all.</summary>
    public static IWordLexicon Missing { get; } = new FunctionLexicon(_ => false, (_, _) => false);

    /// <summary>
    /// A fixed word list per language code ("ru", "en"), so results do not depend on the
    /// system's dictionaries. A language without a list has no dictionary. A word is correct
    /// when its list holds it exactly or holds its lowercase form, the way a spell checker
    /// accepts "Дом" and "ДОМ" for «дом» but not «москва» for «Москва».
    /// </summary>
    public static IWordLexicon Fixed(IReadOnlyDictionary<string, IReadOnlyList<string>> lists) => new FixedLexicon(lists);

    /// <summary>A lexicon answering through two functions (tests).</summary>
    public static IWordLexicon From(Func<string, bool> hasLanguage, Func<string, string, bool> isCorrect) =>
        new FunctionLexicon(hasLanguage, isCorrect);

    /// <summary>
    /// True when every word is spelled correctly in the language of its script: Cyrillic as
    /// Russian, Latin as English. A misspelled word, a word mixing scripts or a missing
    /// dictionary makes it false.
    /// </summary>
    public static bool AreRealWords(this IWordLexicon lexicon, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return false;
        }
        foreach (var word in words)
        {
            var hasCyrillic = TextScanning.Any(word, TextScanning.IsCyrillic);
            var hasLatin = TextScanning.Any(word, TextScanning.IsLatinLetter);
            var correct = (hasCyrillic, hasLatin) switch
            {
                (true, false) => lexicon.IsCorrect(word, "ru"),
                (false, true) => lexicon.IsCorrect(word, "en"),
                (false, false) => true,
                (true, true) => false,
            };
            if (!correct)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when every word, lowercased, is an ordinary English word: Docker and user id are, Redis and GigaAM are not.</summary>
    public static bool AreEnglishWords(this IWordLexicon lexicon, IReadOnlyList<string> words) =>
        words.Count > 0 && words.All(word => lexicon.IsCorrect(SwiftUnicode.Lowercased(word), "en"));

    /// <summary>
    /// A system spell checker behind the lexicon rules (port of <c>SystemSpelling</c>): a
    /// language without a dictionary has no correct words, answers are cached, and an answer
    /// the checker could not give (<paramref name="check"/> returns null) counts as a
    /// misspelling without being remembered.
    /// </summary>
    /// <param name="hasLanguage">
    /// Whether the checker has a dictionary for a language code; asked until it answers
    /// (null means it could not tell yet), then the answer is kept for the life of the lexicon.
    /// </param>
    /// <param name="check">Whether a non-empty word is spelled correctly in a language the checker has; may run on any thread.</param>
    public static IWordLexicon Cached(Func<string, bool?> hasLanguage, Func<string, string, bool?> check, int cacheLimit = WordLexiconCache.DefaultLimit) =>
        new CachedLexicon(hasLanguage, check, cacheLimit);

    sealed class CachedLexicon(Func<string, bool?> hasLanguage, Func<string, string, bool?> check, int cacheLimit) : IWordLexicon
    {
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> languages = new(StringComparer.Ordinal);
        readonly WordLexiconCache cache = new(cacheLimit);

        public bool HasLanguage(string language)
        {
            if (languages.TryGetValue(language, out var known))
            {
                return known;
            }
            if (hasLanguage(language) is not { } available)
            {
                return false;
            }
            return languages.GetOrAdd(language, available);
        }

        public bool IsCorrect(string word, string language)
        {
            if (word.Length == 0 || !HasLanguage(language))
            {
                return false;
            }
            if (cache.TryGet(word, language, out var known))
            {
                return known;
            }
            if (check(word, language) is not { } correct)
            {
                return false;
            }
            cache.Store(word, language, correct);
            return correct;
        }
    }

    sealed class FunctionLexicon(Func<string, bool> hasLanguage, Func<string, string, bool> isCorrect) : IWordLexicon
    {
        public bool HasLanguage(string language) => hasLanguage(language);
        public bool IsCorrect(string word, string language) => isCorrect(word, language);
    }

    sealed class FixedLexicon : IWordLexicon
    {
        readonly Dictionary<string, HashSet<string>> sets;

        public FixedLexicon(IReadOnlyDictionary<string, IReadOnlyList<string>> lists)
        {
            sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (language, words) in lists)
            {
                sets[language] = new HashSet<string>(words.Select(SwiftString.Canonical), StringComparer.Ordinal);
            }
        }

        public bool HasLanguage(string language) => sets.ContainsKey(language);

        public bool IsCorrect(string word, string language)
        {
            if (!sets.TryGetValue(language, out var words) || word.Length == 0)
            {
                return false;
            }
            var canonical = SwiftString.Canonical(word);
            return words.Contains(canonical) || words.Contains(SwiftUnicode.Lowercased(canonical));
        }
    }
}

/// <summary>
/// Spell checker answers by language and word, safe to use from many threads. Once
/// <see cref="Limit"/> words are cached the cache starts over, so a long session does not
/// grow it without end.
/// </summary>
public sealed class WordLexiconCache(int limit = WordLexiconCache.DefaultLimit)
{
    public const int DefaultLimit = 20_000;

    readonly Lock gate = new();
    readonly Dictionary<(string Language, string Word), bool> answers = [];

    public int Limit { get; } = Math.Max(1, limit);

    public int Count
    {
        get
        {
            lock (gate)
            {
                return answers.Count;
            }
        }
    }

    public bool TryGet(string word, string language, out bool correct)
    {
        lock (gate)
        {
            return answers.TryGetValue((language, word), out correct);
        }
    }

    public void Store(string word, string language, bool correct)
    {
        lock (gate)
        {
            if (answers.Count >= Limit && !answers.ContainsKey((language, word)))
            {
                answers.Clear();
            }
            answers[(language, word)] = correct;
        }
    }
}
