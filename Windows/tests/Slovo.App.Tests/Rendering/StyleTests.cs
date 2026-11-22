using Avalonia.Headless.XUnit;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Screens;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

public class StyleTests
{
    static FakeHistory History()
    {
        var history = new FakeHistory();
        foreach (var (id, name) in new[] { ("telegram.exe", "Telegram"), ("slack.exe", "Slack"), ("code.exe", "Visual Studio Code"), ("chrome.exe", "Google Chrome") })
        {
            history.Items.Add(new HistoryEntry { Date = DateTimeOffset.UtcNow, Text = "x", RawText = "x", AppBundleId = id, AppName = name, Category = AppCategory.Other });
        }
        return history;
    }

    [AvaloniaFact]
    public void ChoosingAPresetAndMovingAnAppChangeSettings()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var categories = new FakeCategories();
        var model = new StyleViewModel(settings, categories, History());
        Assert.Equal(StylePreset.Casual, settings.Style(AppCategory.Personal));
        Assert.Equal(["Telegram"], model.Apps.Select(a => a.App.Name));
        model.ChooseCommand.Execute(model.Cards.Single(c => c.Preset == StylePreset.VeryCasual));
        Assert.Equal(StylePreset.VeryCasual, settings.Style(AppCategory.Personal));
        model.MoveCommand.Execute(model.Apps[0].Moves.Single(m => m.Target == AppCategory.Work));
        Assert.Empty(model.Apps);
        model.SelectedTab = model.Tabs[1];
        Assert.Equal(["Slack", "Telegram"], model.Apps.Select(a => a.App.Name));
    }

    [AvaloniaFact]
    public void Render()
    {
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            var settings = new AppSettings(SettingsStore.InMemory());
            var (window, shell) = ScreenSnapshotTests.Shell(Screen.Style, _ => new StyleViewModel(settings, new FakeCategories(), History()));
            Snapshot.RenderWindow(window, "screen-style-ru", 1100, 720);
            var model = (StyleViewModel)shell.CurrentScreen!;
            model.SelectedTab = model.Tabs[^1];
            Snapshot.RenderWindow(new Views.MainWindow { DataContext = shell }, "screen-style-cleanup-ru", 1100, 720);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
