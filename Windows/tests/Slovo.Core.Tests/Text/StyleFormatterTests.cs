using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>Ports TextStyleFormatterTests, FixTextStyleTests and the style parts of FixTextAuditTests.</summary>
public class StyleFormatterTests
{
    readonly StyleFormatter formatter = new(TestLexicons.RecordedMacSystem);

    string Format(string text, StylePreset style, params string[] terms) => formatter.Format(text, style, terms);

    [Theory]
    [InlineData("давай созвонимся завтра. в 10 удобно", "Давай созвонимся завтра. В 10 удобно.")]
    [InlineData("готово! проверишь?", "Готово! Проверишь?")]
    [InlineData("список дел:", "Список дел.")]
    [InlineData("он сказал «привет»", "Он сказал «привет».")]
    [InlineData("see you soon.", "See you soon.")]
    public void FormalCapitalizesSentencesAndAddsFinalPeriod(string text, string expected) =>
        Assert.Equal(expected, Format(text, StylePreset.Formal));

    [Fact]
    public void FormalKeepsInnerCasingAndLowercaseTerms()
    {
        Assert.Equal("macOS обновилась, GitHub Actions упал.", Format("macOS обновилась, GitHub Actions упал", StylePreset.Formal, "macOS", "GitHub Actions"));
        Assert.Equal("Rebase готов.", Format("rebase готов", StylePreset.Formal, "rebase"));
        Assert.Equal("То есть т.е. завтра.", Format("то есть т.е. завтра", StylePreset.Formal));
    }

    [Theory]
    [InlineData("ок. увидимся завтра.", "Ок. Увидимся завтра")]
    [InlineData("Серьёзно?", "Серьёзно?")]
    [InlineData("ну ладно...", "Ну ладно...")]
    [InlineData("то есть т.е.", "То есть т.е.")]
    [InlineData("открой Package.swift.", "Открой Package.swift")]
    public void CasualDropsOnlyTheLastPeriod(string text, string expected) => Assert.Equal(expected, Format(text, StylePreset.Casual));

    [Theory]
    [InlineData("Созвон перенесли. Давай обсудим завтра.", "созвон перенесли давай обсудим завтра")]
    [InlineData("Готово! Проверишь?", "готово! проверишь?")]
    [InlineData("Sounds good. See you tomorrow.", "sounds good see you tomorrow")]
    [InlineData("Скажи «Привет.» Ладно", "скажи «Привет» ладно")]
    public void VeryCasualLowercasesSentenceStartsAndDropsPeriods(string text, string expected) =>
        Assert.Equal(expected, Format(text, StylePreset.VeryCasual));

    [Fact]
    public void VeryCasualKeepsTermsAndNames()
    {
        const string text = "Docker упал. API не отвечает. Маша сказала, что Маша посмотрит. User id пустой. Telegram молчит. I think so.";
        Assert.Equal("Docker упал API не отвечает Маша сказала, что Маша посмотрит user id пустой Telegram молчит I think so",
                     Format(text, StylePreset.VeryCasual, "Docker", "user id"));
        Assert.Equal("GitHub упал", Format("GitHub упал.", StylePreset.VeryCasual));
    }

    [Fact]
    public void TextWithoutWordsIsUntouched()
    {
        foreach (var style in new[] { StylePreset.Formal, StylePreset.Casual, StylePreset.VeryCasual })
        {
            Assert.Equal("", Format("", style));
            Assert.Equal(" … ", Format(" … ", style));
        }
    }

    [Fact]
    public void StageUsesTheContextStyle()
    {
        var result = formatter.Apply(new TextPipelineResult("привет"), new TextPipelineContext());
        Assert.Equal("привет", result.Text);
        result = formatter.Apply(result, new TextPipelineContext { Style = StylePreset.Formal });
        Assert.Equal("Привет.", result.Text);
    }

    [Fact]
    public void ReplacementTargetsKeepTheirCasingInVeryCasual()
    {
        var context = new TextPipelineContext { Dictionary = [DictionaryItem.ReplacementRule("слово", "Slovo")], Style = StylePreset.VeryCasual };
        Assert.Equal("Slovo готово", formatter.Apply(new TextPipelineResult("Slovo готово."), context).Text);
    }

    // MARK: - FixTextStyleTests

    [Fact]
    public void LinksAddressesAndInnerCapitalsKeepTheirCase()
    {
        Assert.Equal("https://github.com/foo это ссылка", Format("https://github.com/foo это ссылка", StylePreset.Casual));
        Assert.Equal("ivan@example.com мой адрес", Format("ivan@example.com мой адрес", StylePreset.Casual));
        Assert.Equal("iPhone сломался", Format("iPhone сломался.", StylePreset.Casual));
        Assert.Equal("Готово. eBay и iPad на месте.", Format("готово. eBay и iPad на месте", StylePreset.Formal));
        Assert.Equal("src/main.swift открыт.", Format("src/main.swift открыт", StylePreset.Formal));
    }

    [Fact]
    public void FileAndDomainNamesKeepTheirCase()
    {
        Assert.Equal("github.com это сайт", Format("github.com это сайт", StylePreset.Casual));
        Assert.Equal("Готово. index.html лежит в корне.", Format("готово. index.html лежит в корне", StylePreset.Formal));
        Assert.Equal("Т.е. завтра.", Format("т.е. завтра", StylePreset.Formal));
    }

    [Fact]
    public void LowercaseTermsKeepTheirCaseAtSentenceStart()
    {
        Assert.Equal("npm install и всё.", Format("npm install и всё", StylePreset.Formal, "npm"));
        Assert.Equal("npm install и всё", Format("npm install и всё", StylePreset.Casual, "npm"));
        var context = new TextPipelineContext { Dictionary = TestLexicons.Vocabulary.Select(term => DictionaryItem.Term(term)).ToList(), Style = StylePreset.Formal };
        Assert.Equal("npm install и всё.", TestLexicons.Pipeline().Process("npm install и всё", context).Text);
    }

    [Fact]
    public void FormalAddsAPeriodAfterPercentAndPlus()
    {
        Assert.Equal("Рост на 5%.", Format("рост на 5%", StylePreset.Formal));
        Assert.Equal("Используй C++.", Format("используй C++", StylePreset.Formal));
    }

    [Fact]
    public void EmoticonAtTheEndIsNotAColon()
    {
        Assert.Equal("Смайлик :)", Format("смайлик :)", StylePreset.Formal));
        Assert.Equal("Смайлик :)", Format("смайлик :)", StylePreset.Casual));
        Assert.Equal("Список дел.", Format("список дел:", StylePreset.Formal));
    }

    // MARK: - FixTextAuditTests

    [Theory]
    [InlineData("приходи завтра, т. е. в среду", "Приходи завтра, т. е. в среду.")]
    [InlineData("купи хлеб, молоко и т. д. потом приходи", "Купи хлеб, молоко и т. д. потом приходи.")]
    [InlineData("фрукты и т. п. бери всё", "Фрукты и т. п. бери всё.")]
    [InlineData("в 2025 г. мы запустили продукт", "В 2025 г. мы запустили продукт.")]
    [InlineData("встретимся в 5 ч. вечера", "Встретимся в 5 ч. вечера.")]
    [InlineData("цена 1 000,50 руб. за штуку", "Цена 1 000,50 руб. за штуку.")]
    [InlineData("Apple Inc. announced it", "Apple Inc. announced it.")]
    // The engine's own capital still starts a sentence.
    [InlineData("купи хлеб и т. д. Потом обсудим", "Купи хлеб и т. д. Потом обсудим.")]
    [InlineData("давай созвонимся завтра. в 10 удобно", "Давай созвонимся завтра. В 10 удобно.")]
    [InlineData("ок. увидимся", "Ок. Увидимся.")]
    public void SpacedAbbreviationsDoNotStartSentences(string text, string expected) =>
        Assert.Equal(expected, new StyleFormatter(TestLexicons.Fixed()).Format(text, StylePreset.Formal, []));

    [Fact]
    public void AbbreviationPeriodsAreNotDropped()
    {
        var withoutDictionaries = new StyleFormatter(TestLexicons.Fixed());
        Assert.Equal("Купи хлеб и т. д.", withoutDictionaries.Format("купи хлеб и т. д.", StylePreset.Casual, []));
        Assert.Equal("Вышел в 2025 г.", withoutDictionaries.Format("вышел в 2025 г.", StylePreset.Casual, []));
        Assert.Equal("Цена 100 руб.", withoutDictionaries.Format("цена 100 руб.", StylePreset.Casual, []));
        Assert.Equal("Это план Б", withoutDictionaries.Format("это план Б.", StylePreset.Casual, []));
        Assert.Equal("купи хлеб и т. д. потом приходи", withoutDictionaries.Format("Купи хлеб и т. д. Потом приходи.", StylePreset.VeryCasual, []));
        Assert.Equal("приходи в 5 ч. вечером жду", withoutDictionaries.Format("Приходи в 5 ч. вечером. Жду.", StylePreset.VeryCasual, []));
    }

    [Fact]
    public void StyleFormatterAsksTheInjectedLexicon()
    {
        Assert.Equal("Rebase готов.", new StyleFormatter(TestLexicons.Fixed(en: ["rebase"])).Format("rebase готов", StylePreset.Formal, ["rebase"]));
        Assert.Equal("rebase готов.", new StyleFormatter(TestLexicons.Fixed(en: [])).Format("rebase готов", StylePreset.Formal, ["rebase"]));
    }

    [Fact]
    public void VeryCasualKeepsProperNounsTheLexiconKnowsOnlyCapitalized()
    {
        var withNames = new StyleFormatter(TestLexicons.Fixed(ru: ["Москва", "большая", "маша", "пришла"], en: ["London", "is", "big"]));
        Assert.Equal("Москва большая маша пришла", withNames.Format("Москва большая. Маша пришла.", StylePreset.VeryCasual, []));
        Assert.Equal("London is big", withNames.Format("London is big.", StylePreset.VeryCasual, []));
        // Without a dictionary nothing counts as a known proper noun.
        Assert.Equal("москва большая", new StyleFormatter(TestLexicons.Fixed()).Format("Москва большая.", StylePreset.VeryCasual, []));
    }

    // MARK: - Swift Character semantics

    [Fact]
    public void ACaseMappingThatChangesTheLengthIsSkipped()
    {
        // "ß" uppercases to "SS", two characters: the sentence start stays as it is.
        Assert.Equal("ßtraße ist lang.", Format("ßtraße ist lang", StylePreset.Formal));
        // Titlecase and full mappings of one character still apply.
        Assert.Equal("Ǆemal.", Format("ǆemal", StylePreset.Formal));
    }
}
