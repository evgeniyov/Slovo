using System.Text.Json;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>
/// Runs every case of <c>Shared/fixtures/mixed-merge.json</c>, which the macOS tests run too
/// (Tests/SlovoCoreTests/MixedLanguageMergerTests.swift); both must agree on every case.
/// </summary>
public class MixedLanguageMergerTests
{
    sealed record Case(string Name, string Russian, string Helper, string Expected, int Replacements, bool NeedsHelper);

    sealed record Fixture(Dictionary<string, List<string>> Lexicon, List<Case> Cases);

    static Fixture Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "mixed-merge.json");
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void EveryFixtureCaseMatchesTheMac()
    {
        var fixture = Load();
        Assert.True(fixture.Cases.Count >= 20);
        var lexicon = WordLexicon.Fixed(fixture.Lexicon.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
        var merger = new MixedLanguageMerger(lexicon);
        foreach (var item in fixture.Cases)
        {
            var result = merger.Merge(item.Russian, item.Helper);
            Assert.True(item.Expected == result.Text, $"{item.Name}: expected «{item.Expected}», got «{result.Text}»");
            Assert.True(item.Replacements == result.Replacements, $"{item.Name}: {result.Replacements} replacements");
            Assert.True(item.NeedsHelper == merger.NeedsHelper(item.Russian), $"{item.Name}: needsHelper");
        }
    }

    [Fact]
    public void SequenceMatcherMatchesDifflib()
    {
        // difflib.SequenceMatcher(None, "qabxcd", "abycdf").get_opcodes()
        var opcodes = new SequenceMatcher<char>([.. "qabxcd"], [.. "abycdf"]).Opcodes();
        Assert.Equal(
            [(SequenceMatcher<char>.OpTag.Delete, 0, 1, 0, 0), (SequenceMatcher<char>.OpTag.Equal, 1, 3, 0, 2), (SequenceMatcher<char>.OpTag.Replace, 3, 4, 2, 3),
             (SequenceMatcher<char>.OpTag.Equal, 4, 6, 3, 5), (SequenceMatcher<char>.OpTag.Insert, 6, 6, 5, 6)],
            opcodes.Select(o => (o.Tag, o.AStart, o.AEnd, o.BStart, o.BEnd)));
        Assert.Equal(2.0 * 4 / 9, new SequenceMatcher<char>([.. "abxcd"], [.. "abcd"]).Ratio(), 12);
    }

    [Fact]
    public void SoundKeysMeetAcrossScripts()
    {
        Assert.Equal(MixedLanguageMerger.Sound("Makos"), MixedLanguageMerger.Sound("macOS"));
        Assert.Equal(MixedLanguageMerger.Sound("мейн"), MixedLanguageMerger.Sound("main"));
        Assert.Equal(MixedLanguageMerger.Sound("Pod"), MixedLanguageMerger.Sound("под"));
    }

    [Fact]
    public void WithoutARussianDictionaryOnlyLatinGarbleIsReplaced()
    {
        var result = new MixedLanguageMerger(WordLexicon.Missing).Merge("Сделай ребейс в версии Makos.", "Сделай rebase в версии macOS.");
        Assert.Equal("Сделай ребейс в версии macOS.", result.Text);
        Assert.Equal(1, result.Replacements);
    }
}
