using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>The lexicon abstraction, and the cache the system spell checker sits behind (FixTextSpellingTests).</summary>
public class WordLexiconTests
{
    [Fact]
    public void FixedListsAcceptTheWordOrItsLowercaseForm()
    {
        var lexicon = TestLexicons.Fixed(ru: ["дом", "Москва"], en: ["London", "docker"]);
        Assert.True(lexicon.IsCorrect("ДОМ", "ru"));
        Assert.True(lexicon.IsCorrect("Москва", "ru"));
        Assert.False(lexicon.IsCorrect("москва", "ru"));
        Assert.False(lexicon.IsCorrect("МОСКВА", "ru"));
        Assert.True(lexicon.IsCorrect("Docker", "en"));
        Assert.False(lexicon.IsCorrect("london", "en"));
        Assert.False(lexicon.IsCorrect("", "ru"));
        // Canonically equivalent spellings are the same word.
        Assert.True(TestLexicons.Fixed(ru: ["ёж"]).IsCorrect("е\u0308ж", "ru"));
    }

    [Fact]
    public void MissingHasNoLanguageAndNoWords()
    {
        Assert.False(WordLexicon.Missing.HasLanguage("ru"));
        Assert.False(WordLexicon.Missing.IsCorrect("дом", "ru"));
    }

    [Fact]
    public void RealWordsFollowTheirScript()
    {
        var lexicon = TestLexicons.Fixed(ru: ["дом"], en: ["house"]);
        Assert.True(lexicon.AreRealWords(["дом", "house", "42"]));
        Assert.False(lexicon.AreRealWords(["дом", "domik"]));
        Assert.False(lexicon.AreRealWords(["домhouse"]));
        Assert.False(lexicon.AreRealWords([]));
        Assert.True(lexicon.AreEnglishWords(["House"]));
        Assert.False(lexicon.AreEnglishWords(["дом"]));
        Assert.False(lexicon.AreEnglishWords([]));
    }

    [Fact]
    public void TheCachedLexiconAsksEachWordOnceAndNeverWithoutADictionary()
    {
        var checks = 0;
        var languageChecks = 0;
        var lexicon = WordLexicon.Cached(
            language => { Interlocked.Increment(ref languageChecks); return language == "ru"; },
            (word, _) => { Interlocked.Increment(ref checks); return word == "дом"; });
        Assert.True(lexicon.IsCorrect("дом", "ru"));
        Assert.True(lexicon.IsCorrect("дом", "ru"));
        Assert.False(lexicon.IsCorrect("кот", "ru"));
        Assert.False(lexicon.IsCorrect("кот", "ru"));
        Assert.False(lexicon.IsCorrect("house", "en"));
        Assert.False(lexicon.IsCorrect("", "ru"));
        Assert.Equal(2, checks);
        Assert.True(lexicon.HasLanguage("ru"));
        Assert.False(lexicon.HasLanguage("en"));
        Assert.Equal(2, languageChecks);
    }

    [Fact]
    public void AnAnswerTheCheckerCouldNotGiveIsNotRemembered()
    {
        var available = false;
        bool? answer = null;
        var lexicon = WordLexicon.Cached(_ => available ? true : null, (_, _) => answer);
        Assert.False(lexicon.HasLanguage("ru"));
        available = true;
        Assert.True(lexicon.HasLanguage("ru"));
        Assert.False(lexicon.IsCorrect("дом", "ru"));
        answer = true;
        Assert.True(lexicon.IsCorrect("дом", "ru"));
        answer = false;
        Assert.True(lexicon.IsCorrect("дом", "ru"));
    }

    [Fact]
    public void TheCacheStartsOverAtItsLimit()
    {
        var cache = new WordLexiconCache(limit: 3);
        cache.Store("a", "en", true);
        cache.Store("b", "en", false);
        cache.Store("c", "en", true);
        Assert.Equal(3, cache.Count);
        cache.Store("c", "en", false);
        Assert.Equal(3, cache.Count);
        cache.Store("d", "en", true);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("d", "en", out var d) && d);
        Assert.False(cache.TryGet("a", "en", out _));
        Assert.False(cache.TryGet("d", "ru", out _));
    }

    [Fact]
    public void TheCachedLexiconAnswersTheSameFromManyThreads()
    {
        (string Word, string Language, bool Correct)[] words =
        [
            ("дом", "ru", true), ("окно", "ru", true), ("кладу", "ru", true), ("редиской", "ru", true),
            ("кибернетис", "ru", false), ("постгресс", "ru", false), ("эсленд", "ru", false), ("опшен", "ru", false),
            ("whisper", "en", true), ("window", "en", true), ("kubernetes", "en", false), ("defolts", "en", false),
        ];
        var truth = words.ToDictionary(item => (item.Word, item.Language), item => item.Correct);
        var lexicon = WordLexicon.Cached(language => language is "ru" or "en", (word, language) =>
        {
            Thread.SpinWait(50);
            return truth[(word, language)];
        }, cacheLimit: 5);
        var wrong = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 960, iteration =>
        {
            var item = words[iteration % words.Length];
            if (lexicon.IsCorrect(item.Word, item.Language) != item.Correct)
            {
                wrong.Add(item.Word);
            }
        });
        Assert.Empty(wrong);
        Assert.False(lexicon.HasLanguage("xx"));
        Assert.False(lexicon.IsCorrect("дом", "xx"));
        Assert.False(lexicon.IsCorrect("", "ru"));
    }
}
