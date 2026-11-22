using Slovo.Core.Storage;
using Slovo.Core.Text;

namespace Slovo.Core.Tests.Text;

/// <summary>Lexicons for the ported macOS tests that ask the system spell checker.</summary>
static class TestLexicons
{
    /// <summary>
    /// What NSSpellChecker on the macOS development machine answered for every word of the
    /// ported tests (the word, or its lowercase form when that is what it accepts). The macOS
    /// tests run against the live checker; the port runs against its recorded answers, so it
    /// checks the same behaviour on any system.
    /// </summary>
    public static readonly IWordLexicon RecordedMacSystem = WordLexicon.Fixed(new Dictionary<string, IReadOnlyList<string>>
    {
        ["ru"] =
        [
            "Версале", "Винер", "Маргарита", "Норберт", "Смит", "а", "адрес", "бабушке", "билд", "был", "были", "было",
            "в", "вернул", "версией", "версии", "версию", "версия", "виски", "возвращает", "все", "всё", "выше", "где",
            "гигам", "гит", "готов", "готово", "грузилась", "да", "давай", "девелопера", "дел", "джин", "добавь",
            "докер", "докер-команда", "докладе", "домен", "друг", "е", "е-мое", "его", "еж", "если", "есть", "завтра",
            "запись", "запусти", "и", "иван", "используем", "используй", "кибернетик", "кибернетика", "клад", "кладу",
            "клады", "кладёт", "ключи", "код", "команда", "контейнер", "корне", "кот", "котик", "кран", "круга",
            "кубинец", "кубинцы", "купи", "ладно", "лежит", "лениво", "логи", "май", "макс", "мастер", "мастерская",
            "маша", "машин", "мая", "места", "месте", "мин", "минимальную", "мистер", "ммм", "модель", "мое", "мой",
            "молчит", "мы", "на", "найди", "настрой", "находится", "нашли", "не", "нет", "новый", "новых", "ну", "нужно",
            "обнови", "обновил", "обновилась", "обсудим", "одинаковым", "ок", "он", "опция", "опыт", "ответы",
            "отвечает", "открой", "открыт", "отправь", "ошибкой", "перенесли", "плот", "повесь", "подключи", "подними",
            "подпись", "полей", "полем", "положи", "получил", "посмотри", "посмотрит", "потом", "починил", "правый",
            "при", "привет", "приложения", "пришёл", "проверишь", "проверь", "проект", "проекте", "прочитал", "пул",
            "пустой", "пустым", "пяти", "работает", "рабочий", "равен", "радиус", "радиусы", "размер", "ребус", "ребусы",
            "редис", "редиса", "редиской", "релизу", "роман", "рост", "роутер", "рынке", "с", "саду", "сайт", "свитер",
            "сделай", "серия", "сертификат", "серьёзно", "сигнал", "скажи", "сказал", "сказала", "скинь", "скрыто",
            "скрытое", "скрыть", "слово", "сломался", "смайлик", "созвонимся", "спасибо", "список", "спорта", "сравнить",
            "ссылка", "ссылку", "старте", "стол", "т", "т.е", "так", "телега", "телеграм", "телеграмм", "телеграмма",
            "телеграмму", "телеграммы", "телеграф", "тестовый", "то", "уважением", "увидимся", "удобно", "упал", "фоном",
            "форматирование", "фронт", "хаб", "через", "что", "чтобы", "э", "энд", "это", "этот", "юзер", "я",
        ],
        ["en"] =
        [
            "AVAudioEngine", "AirPods", "CI", "CSV", "Claude", "ESLint", "GigaAM", "GitHub", "Jason", "Kubernetes",
            "OAuth", "OSLog", "Postgres", "Sonoma", "SwiftUI", "TTL", "TestFlight", "UserDefaults", "ViewModel",
            "WebSocket", "Xcode", "a", "actions", "an", "api", "arrived", "before", "branch", "c", "caption", "case",
            "changes", "choose", "cloud", "com", "connect", "day", "dev", "developed", "developer", "docker", "down",
            "eBay", "end", "endpoint", "flit", "foo", "good", "here", "html", "https", "i", "iPad", "iPhone", "id",
            "image", "in", "index", "index.html", "install", "is", "it", "json", "loc", "local", "macOS", "mail",
            "mail.ru", "main", "main.swift", "make", "makes", "master", "me", "measure", "my", "of", "option", "os",
            "package", "package.swift", "please", "point", "post", "pr", "prettier", "pull", "radius", "read", "readme",
            "ready", "rebase", "reduce", "remain", "request", "restart", "review", "roadmap", "ru", "ruby", "run",
            "scanning", "science", "see", "send", "signing", "so", "soon", "sounds", "speak", "sqlite", "src", "story",
            "swift", "telegram", "test", "the", "think", "tomorrow", "um", "update", "user", "wants", "whisper", "you",
        ],
    });

    /// <summary>The Dictionary seed, the same terms as Bench/vocabulary.txt.</summary>
    public static IReadOnlyList<string> Vocabulary { get; } = DictionarySeed.BundledTerms();

    public static IWordLexicon Fixed(IReadOnlyList<string>? ru = null, IReadOnlyList<string>? en = null)
    {
        var lists = new Dictionary<string, IReadOnlyList<string>>();
        if (ru is not null)
        {
            lists["ru"] = ru;
        }
        if (en is not null)
        {
            lists["en"] = en;
        }
        return WordLexicon.Fixed(lists);
    }

    public static TextPipeline Pipeline(IWordLexicon? lexicon = null) => new(lexicon ?? RecordedMacSystem);
}
