using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>Port of TextPipelineTests.</summary>
public class TextPipelineTests
{
    readonly TextPipeline pipeline = TestLexicons.Pipeline();

    static readonly IReadOnlyList<DictionaryItem> Dictionary =
    [
        .. new[] { "Kubernetes", "GitHub Actions", "Docker", "Postgres", "macOS" }.Select(term => DictionaryItem.Term(term)),
        DictionaryItem.ReplacementRule("слово", "Slovo"),
    ];

    [Fact]
    public void StandardStagesRunInTheSpecOrder()
    {
        var names = TextPipeline.StandardStages(WordLexicon.Missing).Select(stage => stage.GetType().Name);
        Assert.Equal(["SnippetExpander", "DictionaryCorrector", "FillerCleaner", "StyleFormatter"], names);
    }

    [Fact]
    public void RussianDictationGoesThroughEveryStage()
    {
        var result = pipeline.Process("э, пример на кибернетес готов, открой отчёт в гитхаб экшенс",
                                      new TextPipelineContext { Dictionary = Dictionary, Style = StylePreset.Formal });
        Assert.Equal("Пример на Kubernetes готов, открой отчёт в GitHub Actions.", result.Text);
        Assert.Equal(2, result.TermFixesApplied);
        Assert.Equal(0, result.ReplacementsApplied);
        Assert.Equal(2, result.FixesCount);
    }

    [Fact]
    public void EnglishDictation()
    {
        var result = pipeline.Process("um, so the the doker image is ready.",
                                      new TextPipelineContext { Dictionary = Dictionary, Style = StylePreset.Casual, LanguageMode = LanguageMode.English });
        Assert.Equal("So the Docker image is ready", result.Text);
        Assert.Equal(1, result.FixesCount);
    }

    [Fact]
    public void MixedDictationInVeryCasual()
    {
        var result = pipeline.Process("Запусти докер контейнер с постгресс. Потом обнови макос.",
                                      new TextPipelineContext { Dictionary = Dictionary, Style = StylePreset.VeryCasual });
        Assert.Equal("запусти Docker контейнер с Postgres потом обнови macOS", result.Text);
        Assert.Equal(3, result.TermFixesApplied);
    }

    [Fact]
    public void ReplacementsCountAsFixes()
    {
        var result = pipeline.Process("Открой слово и проверь", new TextPipelineContext { Dictionary = Dictionary });
        Assert.Equal("Открой Slovo и проверь", result.Text);
        Assert.Equal(1, result.ReplacementsApplied);
        Assert.Equal(1, result.FixesCount);
    }

    [Fact]
    public void SnippetExpansionIsLeftAloneByLaterStages()
    {
        var snippet = new Snippet { Trigger = "подпись", Expansion = "с уважением, докер-команда\nmacos.dev" };
        var result = pipeline.Process("спасибо, э, подпись", new TextPipelineContext { Dictionary = Dictionary, Snippets = [snippet], Style = StylePreset.Formal });
        Assert.Equal("Спасибо, с уважением, докер-команда\nmacos.dev", result.Text);
        Assert.Equal(1, result.SnippetsApplied);
        Assert.Equal(0, result.FixesCount);
    }

    [Fact]
    public void TextAroundAnExpansionIsStillProcessed()
    {
        var snippet = new Snippet { Trigger = "мой адрес", Expansion = "ул. Примерная, 1" };
        var result = pipeline.Process("отправь докер мой адрес и и кибернетес.",
                                      new TextPipelineContext { Dictionary = Dictionary, Snippets = [snippet], Style = StylePreset.Casual });
        Assert.Equal("Отправь Docker ул. Примерная, 1 и Kubernetes", result.Text);
        Assert.Equal(1, result.SnippetsApplied);
        Assert.Equal(2, result.TermFixesApplied);
    }

    [Fact]
    public void WithoutStyleOrCleanupTheEngineTextStays()
    {
        var result = pipeline.Process("ну э кибернетес ,да", new TextPipelineContext { Dictionary = Dictionary, Cleanup = AutoCleanupOptions.Off });
        Assert.Equal("ну э Kubernetes ,да", result.Text);
        Assert.Equal(1, result.FixesCount);
    }

    [Fact]
    public void EmptyDictationStaysEmpty()
    {
        var context = new TextPipelineContext { Dictionary = Dictionary, Style = StylePreset.Formal };
        Assert.Equal(new TextPipelineResult(""), pipeline.Process("", context));
        Assert.Equal("", pipeline.Process("э, ммм", context).Text);
    }

    [Fact]
    public void CustomStagesRunInOrder()
    {
        var custom = new TextPipeline([new SnippetExpander(), new UppercaseStage()]);
        var result = custom.Process("мой адрес", new TextPipelineContext { Snippets = [new Snippet { Trigger = "мой адрес", Expansion = "ул. Примерная" }] });
        Assert.Equal("УЛ. ПРИМЕРНАЯ", result.Text);
        Assert.Equal(1, result.SnippetsApplied);
    }

    [Fact]
    public void InputIsReadInCanonicalForm()
    {
        // A lone surrogate cannot occur in a Swift string; it reads as U+FFFD.
        var result = pipeline.Process("привет \uD800", new TextPipelineContext { Style = StylePreset.Formal });
        Assert.Equal("Привет \uFFFD", result.Text);
        // Decomposed input comes out composed.
        Assert.Equal("Ёжик.", pipeline.Process("е\u0308жик", new TextPipelineContext { Style = StylePreset.Formal }).Text);
    }

    sealed class UppercaseStage : ITextStage
    {
        public TextPipelineResult Apply(TextPipelineResult result, TextPipelineContext context) =>
            result with { Text = result.Text.ToUpperInvariant() };
    }
}
