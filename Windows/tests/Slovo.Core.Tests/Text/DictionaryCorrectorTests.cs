using Slovo.Core.Model;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>
/// Ports TextDictionaryCorrectorTests, FixTextTermCorrectionTests, the term parts of
/// FixTextAuditTests and FixTextSpellingTests. Tests that ask the system spell checker on
/// macOS use its recorded answers (<see cref="TestLexicons.RecordedMacSystem"/>).
/// </summary>
public class DictionaryCorrectorTests
{
    static readonly IReadOnlyList<string> Vocabulary = TestLexicons.Vocabulary;
    readonly DictionaryCorrector corrector = new(TestLexicons.RecordedMacSystem);

    [Fact]
    public void TheSeedVocabularyIsBundled()
    {
        Assert.NotEmpty(Vocabulary);
        Assert.DoesNotContain(Vocabulary, term => term.Equals("master", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Telegram", Vocabulary);
    }

    // MARK: - Terms

    [Theory]
    [InlineData("кибернетес", "Kubernetes")]
    [InlineData("Гитхаб экшенс", "GitHub Actions")]
    [InlineData("юзер Айди", "user id")]
    [InlineData("постгресс", "Postgres")]
    [InlineData("докер", "Docker")]
    [InlineData("User Defolts", "UserDefaults")]
    public void SpecExamplesBecomeTerms(string spoken, string term)
    {
        var result = corrector.CorrectTerms(spoken, Vocabulary);
        Assert.Equal(term, result.Text);
        Assert.Equal(1, result.Count);
    }

    [Theory]
    [InlineData("Учебный стенд на кибернетис готов. Открой отчёт в Гитхаб экшенс.", "Учебный стенд на Kubernetes готов. Открой отчёт в GitHub Actions.")]
    [InlineData("Демо выдаёт Джисон с полем юзер Айди.", "Демо выдаёт JSON с полем user id.")]
    [InlineData("Запусти Doker образ рядом с Post Gress Local.", "Запусти Docker образ рядом с Postgres Local.")]
    [InlineData("Для примера прочитай настройки из User Defolts.", "Для примера прочитай настройки из UserDefaults.")]
    [InlineData("Для каталога включи эсленд и Pretter, потом запусти проверку.", "Для каталога включи ESLint и Prettier, потом запусти проверку.")]
    [InlineData("У демо сломался Сигнинг. В списке нет сертификата девелопера Айди.", "У демо сломался signing. В списке нет сертификата Developer ID.")]
    [InlineData("Покажи интерактивный каталог на верселе после урока.", "Покажи интерактивный каталог на Vercel после урока.")]
    public void TermCorrectionsInSampleSentences(string transcript, string expected)
    {
        Assert.Equal(expected, corrector.CorrectTerms(transcript, Vocabulary).Text);
    }

    [Fact]
    public void OrdinaryRussianWordsStay()
    {
        // The spec's examples, then word forms that sit within the edit distance of a seed
        // term: версию and Vercel, скрыть and SQLite.
        string[] words = ["деплой", "логи", "модель", "проект", "версию", "версия", "версии", "версией", "вернул",
                          "скрыть", "скрыто", "скрытое", "месте", "мин", "май", "мая", "машин", "размер", "если",
                          "выше", "серия", "фоном", "релизу", "джин", "мастерская", "опция", "опыт", "рабочий",
                          "докладе", "полей", "телега", "свитер", "виски", "роутер", "сигнал", "тестовый"];
        foreach (var word in words)
        {
            var result = corrector.CorrectTerms(word, Vocabulary);
            Assert.Equal(word, result.Text);
            Assert.Equal(0, result.Count);
        }
        const string sentence = "Пусть модель стартует после открытия учебного примера.";
        Assert.Equal(sentence, corrector.CorrectTerms(sentence, Vocabulary).Text);
    }

    [Theory]
    [InlineData("makes")]
    [InlineData("reduce")]
    [InlineData("scanning")]
    [InlineData("developed")]
    [InlineData("remain")]
    [InlineData("mail")]
    [InlineData("caption")]
    [InlineData("Swift")]
    [InlineData("the user wants")]
    [InlineData("Please review the sample menu before class.")]
    public void CommonEnglishWordsStay(string text)
    {
        Assert.Equal(text, corrector.CorrectTerms(text, Vocabulary).Text);
    }

    [Fact]
    public void PrepositionsAreNotSwallowed()
    {
        Assert.Equal("Обнови Pach Swift и подними версию", corrector.CorrectTerms("Обнови Pach Swift и подними версию", Vocabulary).Text);
        Assert.Equal("логи в GitHub Actions", corrector.CorrectTerms("логи в гитхаб экшенс", ["GitHub Actions"]).Text);
    }

    [Fact]
    public void LongerTermWinsOverAShorterOne()
    {
        Assert.Equal("UserDefaults", corrector.CorrectTerms("юзер дефолтс", ["user id", "UserDefaults"]).Text);
    }

    [Fact]
    public void ExactTermsAreNotCountedAndCasingIsRestored()
    {
        Assert.Equal(0, corrector.CorrectTerms("Запусти Docker", ["Docker"]).Count);
        var result = corrector.CorrectTerms("обновил macos и redis", ["macOS", "Redis"]);
        Assert.Equal("обновил macOS и Redis", result.Text);
        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData("используем Kubernetes2", "Kubernetes", "используем Kubernetes2", 0)]
    [InlineData("Обнови Package.swift6", "Package.swift", "Обнови Package.swift6", 0)]
    [InlineData("Обнови macOS 26", "macOS 27", "Обнови macOS 26", 0)]
    [InlineData("Обнови макос 26", "macOS 27", "Обнови макос 26", 0)]
    [InlineData("Обнови макос 2.7", "macOS 27", "Обнови макос 2.7", 0)]
    [InlineData("Обнови макос 27-2", "macOS 27.2", "Обнови макос 27-2", 0)]
    [InlineData("Обнови макос 27", "macOS 27", "Обнови macOS 27", 1)]
    [InlineData("Обнови макос 27.2", "macOS 27.2", "Обнови macOS 27.2", 1)]
    [InlineData("Сравни Whisper4", "Whisper-4", "Сравни Whisper-4", 1)]
    public void FuzzyCorrectionPreservesNumberTokens(string text, string term, string expected, int count)
    {
        Assert.Equal((expected, count), corrector.CorrectTerms(text, [term]));
    }

    [Fact]
    public void PunctuationSplitsWindows()
    {
        Assert.Equal(0, corrector.CorrectTerms("гитхаб, экшенс", ["GitHub Actions"]).Count);
    }

    [Fact]
    public void ShortTermsAndThresholds()
    {
        Assert.Equal(0, corrector.CorrectTerms("апи и сиай", ["API", "CI"]).Count);
        var strict = new DictionaryCorrector(TestLexicons.RecordedMacSystem, maximumDistance: 0);
        Assert.Equal("кибернетес и Docker", strict.CorrectTerms("кибернетес и докер", ["Kubernetes", "Docker"]).Text);
        var longTermsOnly = new DictionaryCorrector(TestLexicons.RecordedMacSystem, minimumTermLetters: 7);
        Assert.Equal("Kubernetes и докер", longTermsOnly.CorrectTerms("кибернетес и докер", ["Kubernetes", "Docker"]).Text);
        Assert.Equal(0.34, corrector.MaximumDistance);
        Assert.Equal(4, corrector.MinimumTermLetters);
    }

    [Fact]
    public void TermsWithoutLatinLettersAreIgnored()
    {
        Assert.Equal(0, corrector.CorrectTerms("кибернетес", ["кубернетес"]).Count);
    }

    // MARK: - Real Russian words and English terms (FixTextTermCorrectionTests, FixTextAuditTests)

    [Theory]
    [InlineData("Я кладу ключи на стол.")]
    [InlineData("Мы нашли клад в саду.")]
    [InlineData("Радиус круга равен пяти.")]
    [InlineData("Купи редис на рынке.")]
    [InlineData("Отправь телеграмму бабушке.")]
    [InlineData("Телеграф работает.")]
    [InlineData("Его друг кубинец.")]
    [InlineData("Мистер Смит пришёл.")]
    [InlineData("Это ребус.")]
    [InlineData("Норберт Винер был кибернетик.")]
    [InlineData("Макс, привет!")]
    [InlineData("Мы были в Версале.")]
    public void OrdinaryRussianSentencesStay(string sentence)
    {
        var result = corrector.CorrectTerms(sentence, Vocabulary);
        Assert.Equal(sentence, result.Text);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void OrdinaryRussianWordFormsStay()
    {
        string[] words = ["кладу", "клад", "клады", "кладёт", "радиус", "радиусы", "редис", "редиса", "редиской",
                          "телеграмма", "телеграмму", "телеграммы", "телеграф", "кубинец", "кубинцы", "кибернетик",
                          "кибернетика", "мистер", "ребус", "ребусы", "макс"];
        foreach (var word in words)
        {
            Assert.Equal(word, corrector.CorrectTerms(word, Vocabulary).Text);
        }
    }

    [Theory]
    [InlineData("кибернетес", "Kubernetes")]
    [InlineData("Гитхаб экшенс", "GitHub Actions")]
    [InlineData("юзер Айди", "user id")]
    [InlineData("постгресс", "Postgres")]
    [InlineData("докер", "Docker")]
    [InlineData("User Defolts", "UserDefaults")]
    [InlineData("Запусти докер контейнер", "Запусти Docker контейнер")]
    [InlineData("Покажи каталог на верселе", "Покажи каталог на Vercel")]
    [InlineData("Настрой Эсленд и Преттер", "Настрой ESLint и Prettier")]
    [InlineData("Нужно сравнить Гигам и Виспер", "Нужно сравнить GigaAM и Whisper")]
    [InlineData("Повесь запись на правый опшен", "Повесь запись на правый Option")]
    [InlineData("Скинь телеграм ссылку", "Скинь Telegram ссылку")]
    [InlineData("Закашируй ответы в Редис", "Закашируй ответы в Redis")]
    [InlineData("Обнови минимальную версию макоз", "Обнови минимальную версию macOS")]
    public void TransliteratedTermsAreStillCorrected(string spoken, string expected)
    {
        Assert.Equal(expected, corrector.CorrectTerms(spoken, Vocabulary).Text);
    }

    [Theory]
    [InlineData("Choose an option.")]
    [InlineData("Speak in a whisper.")]
    [InlineData("Make it prettier.")]
    [InlineData("A telegram arrived.")]
    [InlineData("Master of Science.")]
    [InlineData("Jason is here.")]
    [InlineData("The cloud is down.")]
    [InlineData("Measure the radius.")]
    [InlineData("Rebase the branch.")]
    [InlineData("A case in point.")]
    [InlineData("Read me a story.")]
    public void CommonEnglishWordsKeepTheirSpelling(string sentence)
    {
        var result = corrector.CorrectTerms(sentence, Vocabulary);
        Assert.Equal(sentence, result.Text);
        Assert.Equal(0, result.Count);
    }

    [Theory]
    [InlineData("restart redis", "restart Redis", 1)]
    [InlineData("update macos", "update macOS", 1)]
    [InlineData("connect airpods", "connect AirPods", 1)]
    [InlineData("send json", "send JSON", 1)]
    [InlineData("run npm", "run npm", 0)]
    public void NamesThatAreNotEnglishWordsGetTheirCasing(string text, string expected, int count)
    {
        var result = corrector.CorrectTerms(text, Vocabulary);
        Assert.Equal(expected, result.Text);
        Assert.Equal(count, result.Count);
    }

    /// <summary>Engines write English names inside Russian speech in Latin letters, often split into dictionary words or with sentence casing.</summary>
    [Theory]
    [InlineData("Скинь telegram ссылку", "Скинь Telegram ссылку")]
    [InlineData("Скинь ссылку на новый билд в Test Flit", "Скинь ссылку на новый билд в TestFlight")]
    [InlineData("Этот in point отвечает ошибкой", "Этот endpoint отвечает ошибкой")]
    [InlineData("Добавь логирование через OS Loc.", "Добавь логирование через OSLog.")]
    [InlineData("В примере написано Джин, затем User Id.", "В примере написано Джин, затем user id.")]
    // A single real English word is still not swapped for a different term.
    [InlineData("Задеплой всё в cloud", "Задеплой всё в cloud")]
    [InlineData("Он получил Master of Science", "Он получил Master of Science")]
    public void EnglishWordsInsideRussianSentencesStillBecomeTerms(string spoken, string expected)
    {
        Assert.Equal(expected, corrector.CorrectTerms(spoken, Vocabulary).Text);
    }

    [Fact]
    public void OrdinaryWordsThatSoundLikeTermsStayWithTheSystemSpellChecker()
    {
        var terms = Vocabulary.Append("master").ToList();
        foreach (var sentence in new[] { "Пришёл мастер и починил кран", "Мастер сказал, что всё готово", "Роман «Мастер и Маргарита» прочитал",
                                         "Нет новых телеграмм", "Мастер спорта" })
        {
            var result = corrector.CorrectTerms(sentence, terms);
            Assert.Equal(sentence, result.Text);
            Assert.Equal(0, result.Count);
        }
        Assert.Equal("Скинь Telegram ссылку", corrector.CorrectTerms("Скинь телеграм ссылку", Vocabulary).Text);
        Assert.Equal("Запусти Docker", corrector.CorrectTerms("Запусти докер", Vocabulary).Text);
    }

    [Theory]
    [InlineData("пришёл мастер", "master", "пришёл мастер")]
    [InlineData("роман «Мастер и Маргарита»", "master", "роман «Мастер и Маргарита»")]
    [InlineData("нет новых телеграмм", "Telegram", "нет новых телеграмм")]
    [InlineData("в телеграм", "Telegram", "в Telegram")]
    [InlineData("запусти докер", "Docker", "запусти Docker")]
    // Ruby is an English word and «руби» is not on the borrowed list: it stays.
    [InlineData("руби дрова", "Ruby", "руби дрова")]
    [InlineData("положи в Редис", "Redis", "положи в Redis")]
    [InlineData("купи редис", "Redis", "купи редис")]
    // A doubled letter in the term does not block a name: «Гигам» is GigaAM.
    [InlineData("сравни Гигам и Whisper", "GigaAM", "сравни GigaAM и Whisper")]
    public void ARealWordNeedsABorrowedNameOrANameInMidSentence(string text, string term, string expected)
    {
        var lexicon = TestLexicons.Fixed(
            ru: ["мастер", "телеграмм", "телеграм", "докер", "редис", "руби", "гигам", "и", "в", "пришёл", "нет", "новых"],
            en: ["master", "telegram", "docker", "ruby"]);
        Assert.Equal(expected, new DictionaryCorrector(lexicon).CorrectTerms(text, [term]).Text);
    }

    // MARK: - Missing and injected dictionaries (FixTextSpellingTests)

    [Fact]
    public void WithoutDictionariesShortNearWordsStayAndSpecExamplesStillWork()
    {
        var withoutDictionaries = new DictionaryCorrector(WordLexicon.Missing);
        foreach (var text in new[] { "кладу", "клад", "радиус", "ребус", "The cloud is down.", "Measure the radius.", "Choose an option." })
        {
            Assert.Equal(text, withoutDictionaries.CorrectTerms(text, Vocabulary).Text);
        }
        foreach (var (spoken, expected) in new[] { ("кибернетес", "Kubernetes"), ("Гитхаб экшенс", "GitHub Actions"), ("юзер Айди", "user id"),
                                                   ("постгресс", "Postgres"), ("докер", "Docker"), ("User Defolts", "UserDefaults"),
                                                   ("send json", "send JSON") })
        {
            Assert.Equal(expected, withoutDictionaries.CorrectTerms(spoken, Vocabulary).Text);
        }
    }

    [Fact]
    public void TheLexiconDecidesWhatIsARussianWord()
    {
        var known = WordLexicon.From(_ => true, (word, language) =>
            language == "ru" ? word is "кибернетес" or "редис" or "Редис" or "докер" : word == "docker");
        var withLexicon = new DictionaryCorrector(known);
        Assert.Equal("кибернетес", withLexicon.CorrectTerms("кибернетес", ["Kubernetes"]).Text);
        // Sounds exactly like Redis, which is not an English word: only a name in mid-sentence is the term.
        Assert.Equal("купи редис", withLexicon.CorrectTerms("купи редис", ["Redis"]).Text);
        Assert.Equal("положи в Redis", withLexicon.CorrectTerms("положи в Редис", ["Redis"]).Text);
        Assert.Equal("Редис упал", withLexicon.CorrectTerms("Редис упал", ["Redis"]).Text);
        // Docker is an English word, so «докер» is that word borrowed.
        Assert.Equal("запусти Docker", withLexicon.CorrectTerms("запусти докер", ["Docker"]).Text);
    }

    // MARK: - Replacements

    [Fact]
    public void ReplacementsAreWholeWordCaseInsensitiveAndCounted()
    {
        DictionaryItem[] items =
        [
            DictionaryItem.ReplacementRule("гит хаб", "GitHub"),
            DictionaryItem.ReplacementRule("ё-моё", "ого"),
            DictionaryItem.Term("Docker"),
            DictionaryItem.ReplacementRule("кот", "кошка"),
        ];
        var result = corrector.ApplyReplacements("Открой Гит   Хаб, котик, кот и ЕЖ. Е-мое!", items);
        Assert.Equal("Открой GitHub, котик, кошка и ЕЖ. ого!", result.Text);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ReplacementOutputIsNotScannedAgain()
    {
        var result = corrector.ApplyReplacements("a b", [DictionaryItem.ReplacementRule("a", "a a")]);
        Assert.Equal("a a b", result.Text);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void LongerReplacementWinsAndUnchangedTextIsNotCounted()
    {
        DictionaryItem[] items =
        [
            DictionaryItem.ReplacementRule("пул", "pool"),
            DictionaryItem.ReplacementRule("пул реквест", "pull request"),
            DictionaryItem.ReplacementRule("Slovo", "Slovo"),
        ];
        var result = corrector.ApplyReplacements("Открой пул реквест в Slovo", items);
        Assert.Equal("Открой pull request в Slovo", result.Text);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void ReplacementsFoldTheTypographicApostrophe()
    {
        var result = new DictionaryCorrector(TestLexicons.Fixed()).ApplyReplacements("It’s done", [DictionaryItem.ReplacementRule("it's done", "Done")]);
        Assert.Equal("Done", result.Text);
    }

    [Fact]
    public void ExplicitReplacementOutputIsNotRewrittenByTerms()
    {
        var items = new List<DictionaryItem> { DictionaryItem.ReplacementRule("клауд", "cloud"), DictionaryItem.ReplacementRule("мистер", "Mister") };
        items.AddRange(Vocabulary.Select(term => DictionaryItem.Term(term)));
        var result = corrector.Apply(new TextPipelineResult("положи в клауд, мистер Смит"),
                                     new TextPipelineContext { Dictionary = items, Cleanup = AutoCleanupOptions.Off });
        Assert.Equal("положи в cloud, Mister Смит", result.Text);
        Assert.Equal(2, result.ReplacementsApplied);
        Assert.Equal(0, result.TermFixesApplied);
    }

    [Fact]
    public void TextAroundAReplacementIsStillCorrected()
    {
        DictionaryItem[] items = [DictionaryItem.ReplacementRule("клауд", "cloud"), DictionaryItem.Term("Docker")];
        var result = corrector.Apply(new TextPipelineResult("докер в клауд"), new TextPipelineContext { Dictionary = items, Cleanup = AutoCleanupOptions.Off });
        Assert.Equal("Docker в cloud", result.Text);
        Assert.Equal(2, result.FixesCount);
    }

    // MARK: - Stage

    [Fact]
    public void StageAddsToTheCounters()
    {
        var context = new TextPipelineContext
        {
            Dictionary = [DictionaryItem.Term("Docker"), DictionaryItem.Term("Kubernetes"), DictionaryItem.ReplacementRule("и", "and")],
        };
        var result = corrector.Apply(new TextPipelineResult("докер и кибернетес", ReplacementsApplied: 1, TermFixesApplied: 2), context);
        Assert.Equal("Docker and Kubernetes", result.Text);
        Assert.Equal(2, result.ReplacementsApplied);
        Assert.Equal(4, result.TermFixesApplied);
        Assert.Equal(6, result.FixesCount);
    }

    [Fact]
    public void AnEmptyDictionaryLeavesTheResultAlone()
    {
        var input = new TextPipelineResult("докер", 1, 2, 3);
        Assert.Same(input, corrector.Apply(input, new TextPipelineContext()));
    }

    [Theory]
    [InlineData("спроси у клод что делать", "спроси у Claude что делать")]
    [InlineData("поговорил с Клодом вчера", "поговорил с Claude вчера")]
    [InlineData("у клода есть доступ", "у Claude есть доступ")]
    [InlineData("отправь через апи.", "отправь через API.")]
    public void SpokenNamesBecomeTheirTermsInAnyCase(string spoken, string written)
    {
        var items = new[] { DictionaryItem.Term("Claude"), DictionaryItem.Term("API") };
        var result = corrector.Apply(new TextPipelineResult(spoken), new TextPipelineContext { Dictionary = items, Cleanup = AutoCleanupOptions.Off });
        Assert.Equal(written, result.Text);
        Assert.Equal(1, result.TermFixesApplied);
    }

    [Fact]
    public void SpokenNamesNeedTheirTermAndAWholeWord()
    {
        var items = new[] { DictionaryItem.Term("API") };
        var result = corrector.Apply(new TextPipelineResult("спроси у клод через апили"), new TextPipelineContext { Dictionary = items, Cleanup = AutoCleanupOptions.Off });
        Assert.Equal("спроси у клод через апили", result.Text);
    }
}
