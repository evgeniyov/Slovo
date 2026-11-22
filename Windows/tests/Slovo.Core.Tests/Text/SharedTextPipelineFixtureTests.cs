using System.Text;
using System.Text.Json;
using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>
/// Runs every case of <c>Shared/fixtures/text-pipeline.json</c> through the text pipeline with
/// the case's fixed word lexicon instead of the system spell checker, so the expected output
/// depends on nothing but the file. The macOS app runs the same file
/// (Tests/SlovoCoreTests/SharedTextPipelineFixtureTests.swift); both must agree on every case.
/// </summary>
public class SharedTextPipelineFixtureTests
{
    static readonly Lazy<Fixture> Loaded = new(Fixture.Load);

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var fixtureCase in Loaded.Value.Cases)
        {
            data.Add(fixtureCase.Name);
        }
        return data;
    }

    [Fact]
    public void TheFixtureDocumentsItsFormatAndHasUniqueCases()
    {
        var fixture = Loaded.Value;
        Assert.NotEmpty(fixture.Format);
        Assert.True(fixture.Cases.Count >= 150, $"only {fixture.Cases.Count} cases");
        var duplicates = fixture.Cases.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void CaseProducesTheExpectedResult(string name)
    {
        var fixtureCase = Loaded.Value.Cases.Single(c => c.Name == name);
        var actual = Run(fixtureCase);
        var expected = fixtureCase.Expected;
        // Swift compares strings by canonical equivalence.
        Assert.Equal(Nfc(expected.Text), Nfc(actual.Text));
        Assert.Equal(expected.SnippetsApplied, actual.SnippetsApplied);
        Assert.Equal(expected.ReplacementsApplied, actual.ReplacementsApplied);
        Assert.Equal(expected.TermFixesApplied, actual.TermFixesApplied);
    }

    [Fact]
    public void EveryCasePasses()
    {
        var failures = new List<string>();
        foreach (var fixtureCase in Loaded.Value.Cases)
        {
            var actual = Run(fixtureCase);
            var expected = fixtureCase.Expected;
            if (Nfc(actual.Text) != Nfc(expected.Text) || actual.SnippetsApplied != expected.SnippetsApplied
                || actual.ReplacementsApplied != expected.ReplacementsApplied || actual.TermFixesApplied != expected.TermFixesApplied)
            {
                failures.Add($"{fixtureCase.Name}: expected «{expected.Text}» ({expected.SnippetsApplied}/{expected.ReplacementsApplied}/{expected.TermFixesApplied}), " +
                             $"got «{actual.Text}» ({actual.SnippetsApplied}/{actual.ReplacementsApplied}/{actual.TermFixesApplied})");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {Loaded.Value.Cases.Count} cases differ:\n" + string.Join("\n", failures));
    }

    /// <summary>The fixed lexicon follows the rules the fixture's _format describes.</summary>
    [Fact]
    public void FixedLexiconRules()
    {
        var lexicon = WordLexicon.Fixed(new Dictionary<string, IReadOnlyList<string>> { ["ru"] = ["дом", "Москва"], ["en"] = [] });
        Assert.True(lexicon.HasLanguage("ru"));
        Assert.True(lexicon.HasLanguage("en"));
        Assert.False(lexicon.HasLanguage("de"));
        Assert.True(lexicon.IsCorrect("дом", "ru"));
        Assert.True(lexicon.IsCorrect("Дом", "ru"));
        Assert.True(lexicon.IsCorrect("ДОМ", "ru"));
        Assert.True(lexicon.IsCorrect("Москва", "ru"));
        Assert.False(lexicon.IsCorrect("москва", "ru"));
        Assert.False(lexicon.IsCorrect("дом", "en"));
        Assert.False(lexicon.IsCorrect("дом", "de"));
    }

    static string Nfc(string text) => text.Normalize(NormalizationForm.FormC);

    static TextPipelineResult Run(Case fixtureCase)
    {
        var epoch = DateTimeOffset.FromUnixTimeSeconds(0);
        var context = new TextPipelineContext
        {
            Dictionary = fixtureCase.Dictionary.Select(item => new DictionaryItem
            {
                Kind = item.Kind switch
                {
                    "term" => DictionaryItemKind.Term,
                    "replacement" => DictionaryItemKind.Replacement,
                    _ => throw new InvalidOperationException($"{fixtureCase.Name}: unknown dictionary kind {item.Kind}"),
                },
                Text = item.Text,
                Replacement = item.Replacement ?? "",
                CreatedAt = epoch,
            }).ToList(),
            Snippets = fixtureCase.Snippets.Select(s => new Snippet { Trigger = s.Trigger, Expansion = s.Expansion, CreatedAt = epoch, UpdatedAt = epoch }).ToList(),
            Cleanup = new AutoCleanupOptions(fixtureCase.RemoveFillers, fixtureCase.RemoveRepetitions),
            Style = fixtureCase.Style is { } style
                ? EnumText.Parse<StylePreset>(style) ?? throw new InvalidOperationException($"{fixtureCase.Name}: unknown style {style}")
                : null,
            LanguageMode = EnumText.Parse<LanguageMode>(fixtureCase.LanguageMode)
                ?? throw new InvalidOperationException($"{fixtureCase.Name}: unknown language mode {fixtureCase.LanguageMode}"),
        };
        var pipeline = new TextPipeline(WordLexicon.Fixed(fixtureCase.Lexicon));
        return pipeline.Process(fixtureCase.Input, context);
    }

    sealed record Item(string Kind, string Text, string? Replacement);
    sealed record SnippetItem(string Trigger, string Expansion);
    sealed record Expected(string Text, int SnippetsApplied, int ReplacementsApplied, int TermFixesApplied);

    sealed record Case(
        string Name,
        string Input,
        IReadOnlyList<Item> Dictionary,
        IReadOnlyList<SnippetItem> Snippets,
        bool RemoveFillers,
        bool RemoveRepetitions,
        string? Style,
        string LanguageMode,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Lexicon,
        Expected Expected);

    sealed record Fixture(IReadOnlyList<string> Format, IReadOnlyList<Case> Cases)
    {
        public static Fixture Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "text-pipeline.json");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var format = root.GetProperty("_format").EnumerateArray().Select(line => line.GetString()!).ToList();
            var cases = new List<Case>();
            foreach (var element in root.GetProperty("cases").EnumerateArray())
            {
                var context = element.GetProperty("context");
                var cleanup = context.GetProperty("cleanup");
                var styleElement = context.GetProperty("style");
                var lexicon = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                foreach (var language in element.GetProperty("lexicon").EnumerateObject())
                {
                    lexicon[language.Name] = language.Value.EnumerateArray().Select(word => word.GetString()!).ToList();
                }
                var expected = element.GetProperty("expected");
                cases.Add(new Case(
                    element.GetProperty("name").GetString()!,
                    element.GetProperty("input").GetString()!,
                    context.GetProperty("dictionary").EnumerateArray().Select(item => new Item(
                        item.GetProperty("kind").GetString()!,
                        item.GetProperty("text").GetString()!,
                        item.TryGetProperty("replacement", out var replacement) ? replacement.GetString() : null)).ToList(),
                    context.GetProperty("snippets").EnumerateArray().Select(item => new SnippetItem(
                        item.GetProperty("trigger").GetString()!,
                        item.GetProperty("expansion").GetString()!)).ToList(),
                    cleanup.GetProperty("removeFillers").GetBoolean(),
                    cleanup.GetProperty("removeRepetitions").GetBoolean(),
                    styleElement.ValueKind == JsonValueKind.Null ? null : styleElement.GetString(),
                    context.GetProperty("languageMode").GetString()!,
                    lexicon,
                    new Expected(
                        expected.GetProperty("text").GetString()!,
                        expected.GetProperty("snippetsApplied").GetInt32(),
                        expected.GetProperty("replacementsApplied").GetInt32(),
                        expected.GetProperty("termFixesApplied").GetInt32())));
            }
            return new Fixture(format, cases);
        }
    }
}
