using Avalonia.Headless.XUnit;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Screens;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

public class HomeTests
{
    internal static FakeHistory SampleHistory()
    {
        var history = new FakeHistory();
        var now = DateTimeOffset.Now;
        (string Text, string App, int MinutesAgo, EngineId Engine)[] rows =
        [
            ("Да, скачай модель, проверь как работает, и напиши мне в Telegram.", "Claude", 5, EngineId.GigaamRnnt),
            ("Can we move the standup to 11? I still need to finish the release notes", "Slack", 40, EngineId.Parakeet),
            ("Купить молоко и хлеб, потом позвонить в банк насчёт новой карты.", "Блокнот", 60 * 26, EngineId.GigaamRnnt),
        ];
        foreach (var row in rows)
        {
            history.Items.Add(new HistoryEntry
            {
                Date = now.AddMinutes(-row.MinutesAgo), Text = row.Text, RawText = row.Text, EngineId = row.Engine,
                AppBundleId = row.App.ToLowerInvariant() + ".exe", AppName = row.App, Category = AppCategory.Other,
                Duration = 26, WordCount = HistoryEntry.CountWords(row.Text),
            });
        }
        return history;
    }

    static Insights SampleInsights() => new() { TotalWords = 12480, WordsPerMinute = 111, CurrentStreak = 2, LongestStreak = 9, DictationCount = 3 };

    [AvaloniaFact]
    public void GroupsByDaySearchesAndCopies()
    {
        var actions = new FakeHistoryActions();
        var model = new HomeViewModel(SampleHistory(), new AppSettings(SettingsStore.InMemory()), SampleInsights, actions,
            new MainWindowViewModel((_, _) => new EmptyScreen(), new FakeShellActions(), null), () => { }, "Михаил");
        Assert.Equal(2, model.Groups.Count);
        Assert.Equal(2, model.Groups[0].Rows.Count);
        model.Query = "БАНК";
        Assert.Single(model.Groups);
        model.CopyCommand.Execute(model.Groups[0].Rows[0]);
        Assert.StartsWith("Купить", actions.Copied);
        model.Query = "nothing";
        Assert.True(model.NothingFound);
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Render(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            var settings = new AppSettings(SettingsStore.InMemory());
            var (window, _) = ScreenSnapshotTests.Shell(Screen.Home, host => new HomeViewModel(SampleHistory(), settings, SampleInsights,
                new FakeHistoryActions(), host, () => { }, language == "ru" ? "Михаил" : "Michael"));
            Snapshot.RenderWindow(window, "screen-home-" + language, 1100, 720);
            var (empty, _) = ScreenSnapshotTests.Shell(Screen.Home, host => new HomeViewModel(new FakeHistory(), settings, () => Insights.Empty,
                new FakeHistoryActions(), host, () => { }, "Michael"));
            Snapshot.RenderWindow(empty, "screen-home-empty-" + language, 1100, 720, Avalonia.Styling.ThemeVariant.Dark);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
