using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>Ports TextSnippetExpanderTests, FixTextSnippetTests and the snippet parts of FixTextAuditTests.</summary>
public class SnippetExpanderTests
{
    readonly SnippetExpander expander = new();
    static readonly Snippet Address = Make("мой адрес", "ул. Примерная, 1");

    static Snippet Make(string trigger, string expansion) => new() { Trigger = trigger, Expansion = expansion };

    [Fact]
    public void WholeDictationIsReplacedWithoutEnginePunctuation()
    {
        var result = expander.Expand("Мой адрес.", [Address]);
        Assert.Equal("ул. Примерная, 1", result.Text);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void TriggerInsideDictationKeepsSurroundingText()
    {
        var result = expander.Expand("Отправь мой адрес, пожалуйста.", [Address]);
        Assert.Equal("Отправь ул. Примерная, 1, пожалуйста.", result.Text);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void MatchingIgnoresCasePunctuationAndYo()
    {
        var snippet = Make("Всё, готово!", "Готово ✅");
        Assert.Equal("Готово ✅", expander.Expand("все готово", [snippet]).Text);
        Assert.Equal("Короче, Готово ✅, иду", expander.Expand("Короче, ВСЕ — готово, иду", [snippet]).Text);
    }

    [Fact]
    public void EveryOccurrenceIsCounted()
    {
        var result = expander.Expand("My email, and again my email", [Make("my email", "me@example.com")]);
        Assert.Equal("me@example.com, and again me@example.com", result.Text);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void LongerTriggerWins()
    {
        Assert.Equal("мой B и A", expander.Expand("мой рабочий адрес и адрес", [Make("адрес", "A"), Make("рабочий адрес", "B")]).Text);
    }

    [Fact]
    public void WholeWordsOnly()
    {
        Assert.Equal("котик и 🐈", expander.Expand("котик и кот", [Make("кот", "🐈")]).Text);
    }

    [Fact]
    public void NothingToExpand()
    {
        Assert.Equal(0, expander.Expand("текст", [Make(" , ", "x")]).Count);
        Assert.Equal("текст", expander.Expand("текст", []).Text);
        Assert.Equal("", expander.Expand("", [Address]).Text);
    }

    [Fact]
    public void StageAddsToTheCounter()
    {
        var result = expander.Apply(new TextPipelineResult("мой адрес и мой адрес", SnippetsApplied: 1), new TextPipelineContext { Snippets = [Address] });
        Assert.Equal("ул. Примерная, 1 и ул. Примерная, 1", result.Text);
        Assert.Equal(3, result.SnippetsApplied);
        Assert.Equal(0, result.FixesCount);
    }

    // MARK: - FixTextSnippetTests

    [Fact]
    public void TriggerDoesNotMatchInsideHyphenatedWords()
    {
        var address = Make("адрес", "Примерск, Примерная 1");
        Assert.Equal("IP-адрес сервера", expander.Expand("IP-адрес сервера", [address]).Text);
        Assert.Equal(0, expander.Expand("IP-адрес сервера", [address]).Count);
        Assert.Equal("Запусти TEST-EXP и бета-тест", expander.Expand("Запусти тест и бета-тест", [Make("тест", "TEST-EXP")]).Text);
    }

    [Fact]
    public void HyphenatedTriggerStillMatches()
    {
        Assert.Equal("скажи !! сейчас", expander.Expand("скажи кое-что важное сейчас", [Make("кое-что важное", "!!")]).Text);
    }

    [Fact]
    public void FinalMarkIsNotDoubled()
    {
        var signature = Make("подпись", "с уважением, иван.");
        Assert.Equal("с уважением, иван. спасибо", expander.Expand("подпись. спасибо", [signature]).Text);
        Assert.Equal("Привет, Как у тебя дела? Жду", expander.Expand("Привет, как дела? Жду", [Make("как дела", "Как у тебя дела?")]).Text);

        var result = TestLexicons.Pipeline().Process("подпись. спасибо", new TextPipelineContext { Snippets = [signature], Style = StylePreset.Formal });
        Assert.DoesNotContain("..", result.Text);
        Assert.Equal("с уважением, иван. Спасибо.", result.Text);
    }

    [Fact]
    public void DifferentMarkAfterTheExpansionStays()
    {
        Assert.Equal("иван., спасибо", expander.Expand("подпись, спасибо", [Make("подпись", "иван.")]).Text);
    }

    // MARK: - FixTextAuditTests

    [Fact]
    public void SnippetTriggersFoldApostrophesAndTreatHyphensAsBreaks()
    {
        Assert.Equal("OK LG now", expander.Expand("OK let's go now", [Make("let’s go", "LG")]).Text);
        Assert.Equal("OK LG now", expander.Expand("OK let’s go now", [Make("let's go", "LG")]).Text);
        Assert.Equal("Мой 192.0.2.1 сломался", expander.Expand("Мой IP-адрес сломался", [Make("IP адрес", "192.0.2.1")]).Text);
        Assert.Equal("Пиши на me@example.com", expander.Expand("Пиши на e-mail", [Make("e mail", "me@example.com")]).Text);
        // Part of a hyphenated compound is still not a trigger.
        Assert.Equal("IP-адрес и адрес-строка и A", expander.Expand("IP-адрес и адрес-строка и адрес", [Make("адрес", "A")]).Text);
    }

    [Fact]
    public void CanonicallyEquivalentSpellingsMatch()
    {
        // «й» typed as и + combining breve is the same letter.
        Assert.Equal("X", expander.Expand("мои\u0306 адрес", [Make("мой адрес", "X")]).Text);
    }
}
