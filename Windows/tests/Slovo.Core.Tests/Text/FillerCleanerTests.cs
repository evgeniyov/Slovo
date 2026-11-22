using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>Ports TextFillerCleanerTests, FixTextCleanupTests and the cleanup parts of FixTextAuditTests.</summary>
public class FillerCleanerTests
{
    readonly FillerCleaner cleaner = new();

    string Clean(string text) => cleaner.Clean(text, AutoCleanupOptions.Default);

    [Theory]
    [InlineData("Я э думаю, что это ммм работает", "Я думаю, что это работает")]
    [InlineData("Спасибо, э, до встречи", "Спасибо, до встречи")]
    [InlineData("Э, давай начнём. Ээ, итак", "Давай начнём. Итак")]
    [InlineData("Ээээ, эмм, ну ладно", "Ну ладно")]
    [InlineData("Ну, э-э... давай", "Ну... давай")]
    [InlineData("Давай, э.", "Давай.")]
    public void RussianFillersGoWithTheirCommas(string text, string expected) => Assert.Equal(expected, Clean(text));

    [Theory]
    [InlineData("Um, I think uhh we should ship it, hmm.", "I think we should ship it.")]
    [InlineData("Sure, uh, sounds good", "Sure, sounds good")]
    [InlineData("Erm (um) okay", "Okay")]
    public void EnglishFillersIncludingDrawnOutOnes(string text, string expected) => Assert.Equal(expected, Clean(text));

    [Fact]
    public void WordsThatOnlyLookLikeFillersStay()
    {
        Assert.Equal("5 м от дома, мм, hm, umbrella, ум", Clean("5 м от дома, мм, hm, umbrella, ум"));
    }

    [Fact]
    public void ImmediateRepetitionsCollapse()
    {
        Assert.Equal("Я думаю что в общем да", Clean("Я я думаю думаю что в общем в общем да"));
        // Repeats set off by punctuation are emphasis, not a stutter.
        Assert.Equal("Я думаю, думаю", Clean("Я думаю, думаю"));
        Assert.Equal("the plan", Clean("the the the plan"));
    }

    [Theory]
    [InlineData("Нет. Нет, так нельзя")]
    [InlineData("Build 2 2 times")]
    [InlineData("да\nда")]
    public void RepetitionsAcrossSentencesAndNumbersStay(string text) => Assert.Equal(text, Clean(text));

    [Fact]
    public void OptionsAreIndependent()
    {
        const string text = "Ну э ну ну ладно";
        Assert.Equal("Ну ну ну ладно", cleaner.Clean(text, new AutoCleanupOptions(RemoveFillers: true, RemoveRepetitions: false)));
        Assert.Equal("Ну э ну ладно", cleaner.Clean(text, new AutoCleanupOptions(RemoveFillers: false, RemoveRepetitions: true)));
        Assert.Equal("Ну ладно", Clean(text));
        Assert.Equal("  так ,  э  ", cleaner.Clean("  так ,  э  ", AutoCleanupOptions.Off));
    }

    [Theory]
    [InlineData("  Привет ,мир !  Как дела ?Файл Package.swift ( черновик )  готов", "Привет, мир! Как дела? Файл Package.swift (черновик) готов")]
    [InlineData("Цена 1,5 , время 10:30 ,,  ок ,.", "Цена 1,5, время 10:30, ок.")]
    [InlineData("строка  \n  вторая", "строка\nвторая")]
    public void SpacingAroundPunctuation(string text, string expected) => Assert.Equal(expected, Clean(text));

    [Fact]
    public void FillerListsMatchTheSpec()
    {
        Assert.True(FillerCleaner.RussianFillers.SetEquals(["э", "ээ", "эм", "ммм"]));
        Assert.True(FillerCleaner.EnglishFillers.SetEquals(["um", "uh", "erm", "hmm"]));
    }

    // MARK: - FixTextCleanupTests

    [Theory]
    [InlineData("вызови функцию foo() и всё")]
    [InlineData("верни пустой массив [] из функции")]
    [InlineData("Добавь в .gitignore папку build")]
    [InlineData("создай файл .env и положи туда ключ")]
    [InlineData("вызови init() и map {} потом")]
    public void CodePunctuationAndDotfilesStay(string text) => Assert.Equal(text, Clean(text));

    [Theory]
    [InlineData("Скажи (э) привет", "Скажи привет")]
    [InlineData("Erm (um) okay", "Okay")]
    [InlineData("вызови foo() [эм] и всё", "вызови foo() и всё")]
    public void BracketsThatHeldOnlyAFillerGo(string text, string expected) => Assert.Equal(expected, Clean(text));

    [Fact]
    public void SentencePeriodsStillCloseUp() => Assert.Equal("Готово. Файл .env готов.", Clean("Готово . Файл .env готов ."));

    [Fact]
    public void EmoticonsKeepTheirSpace()
    {
        foreach (var text in new[] { "Смайлик :) и всё", "Ок ;) пока", "Грустно :( очень", "Супер :D да", "Смайлик :-)" })
        {
            Assert.Equal(text, Clean(text));
        }
        Assert.Equal("Список дел: купить хлеб", Clean("Список дел : купить хлеб"));
        Assert.Equal("Встреча в 10:30 (или позже)", Clean("Встреча в 10:30 ( или позже )"));
    }

    [Theory]
    [InlineData("Нет, нет, нет!", "Нет, нет, нет!")]
    [InlineData("Да — да, конечно", "Да — да, конечно")]
    [InlineData("я я думаю", "я думаю")]
    [InlineData("the the plan", "the plan")]
    [InlineData("I had had enough.", "I had had enough.")]
    [InlineData("I know that that is true.", "I know that that is true.")]
    public void OnlyWhitespaceSeparatedRepeatsCollapseAndGrammaticalDoublesStay(string text, string expected) => Assert.Equal(expected, Clean(text));

    // MARK: - FixTextAuditTests

    [Theory]
    [InlineData("Э. Давай начнём", "Давай начнём")]
    [InlineData("Хорошо. Э. Давай начнём", "Хорошо. Давай начнём")]
    [InlineData("Так. Эм. Что дальше?", "Так. Что дальше?")]
    [InlineData("Hmm? What?", "What?")]
    [InlineData("Uh! Wow", "Wow")]
    [InlineData("Um… okay", "Okay")]
    [InlineData("Ээ... давай", "Давай")]
    [InlineData("Давай. Э.", "Давай.")]
    [InlineData("Э. Э. Давай", "Давай")]
    [InlineData("Ммм. Вкусно", "Вкусно")]
    // Unchanged behaviour next to commas and mid-sentence.
    [InlineData("Давай, э.", "Давай.")]
    [InlineData("Ну, э-э... давай", "Ну... давай")]
    [InlineData("Э, давай", "Давай")]
    public void AFillerSentenceLeavesNoStrayPunctuation(string text, string expected) => Assert.Equal(expected, Clean(text));

    [Fact]
    public void AFillerSentenceInThePipeline()
    {
        var result = TestLexicons.Pipeline(TestLexicons.Fixed()).Process("Хорошо. Э. Давай начнём", new TextPipelineContext { Style = StylePreset.Formal });
        Assert.Equal("Хорошо. Давай начнём.", result.Text);
    }

    [Theory]
    [InlineData("Открой https://github.com/foo/bar?Page=2 пожалуйста")]
    [InlineData("Ссылка example.com/a,b готова")]
    [InlineData("Напиши на mailto:ivan@example.com?Subject=Hi сегодня")]
    [InlineData("Путь ~/Documents/a;b тут")]
    [InlineData("Сайт www.example.com?Ref=1 работает")]
    public void SpacingLeavesLinksAddressesAndPathsWhole(string text) => Assert.Equal(text, Clean(text));

    [Fact]
    public void ACommaBeforeAWordGetsASpace() => Assert.Equal("да, нет и Готово! Проверишь", Clean("да,нет и Готово!Проверишь"));

    // MARK: - Pieces around snippets

    [Fact]
    public void TextAroundAnExpansionKeepsItsEdgeSpaces()
    {
        var snippet = new Snippet { Trigger = "подпись", Expansion = "с уважением, иван." };
        var result = cleaner.Apply(new TextPipelineResult("спасибо ,  с уважением, иван.  э  пока", SnippetsApplied: 1),
                                   new TextPipelineContext { Snippets = [snippet] });
        Assert.Equal("спасибо, с уважением, иван. пока", result.Text);
    }
}
