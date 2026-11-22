using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Slovo.App.Controls;
using Slovo.App.Tests.Fakes;
using Slovo.App.Views.Screens;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Screens;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Tests.Rendering;

public class InsightsTests
{
    static Insights Sample()
    {
        var start = DateTimeOffset.Now.Date.AddDays(-139);
        var random = new Random(7);
        var days = Enumerable.Range(0, 140).Select(i => new DailyWordCount(new DateTimeOffset(start.AddDays(i)), random.Next(0, 4) == 0 ? 0 : random.Next(10, 900))).ToList();
        return new Insights
        {
            TotalWords = 48210, DictationCount = 812, WordsPerMinute = 118, FixesMade = 364, TimeSaved = 11 * 3600 + 42 * 60, CurrentStreak = 6, LongestStreak = 21,
            DailyWords = days,
            CategoryShares = [new(AppCategory.Personal, 20000, 0.41), new(AppCategory.Work, 18000, 0.37), new(AppCategory.Email, 4000, 0.08), new(AppCategory.Other, 6210, 0.14)],
            TopApps = [new(new AppIdentity("telegram.exe", "Telegram"), 15000, 300), new(new AppIdentity("slack.exe", "Slack"), 12000, 250), new(new AppIdentity("code.exe", "Visual Studio Code"), 6000, 90)],
        };
    }

    [AvaloniaFact]
    public void ADayOnTheCalendarSaysItsWordsAndDateOnHover()
    {
        CoreStrings.SetLanguage(AppLanguage.En);
        // Tuesday 15 September 2026 opens the range, so it sits in the second row of the first column.
        var start = new DateTimeOffset(new DateTime(2026, 9, 15));
        var days = Enumerable.Range(0, 14).Select(i => new DailyWordCount(start.AddDays(i), i == 0 ? 412 : i)).ToList();
        var calendar = new ActivityCalendar { Days = days };
        var step = ActivityCalendar.Cell + ActivityCalendar.Gap;

        Assert.Equal(days[0], calendar.DayAt(new Point(ActivityCalendar.Cell / 2, step + ActivityCalendar.Cell / 2)));
        // Monday 21 September starts the second column.
        Assert.Equal(days[6], calendar.DayAt(new Point(step + 1, 1)));
        // Monday 14 September is before the range; gaps between cells belong to no day.
        Assert.Null(calendar.DayAt(new Point(1, 1)));
        Assert.Null(calendar.DayAt(new Point(ActivityCalendar.Cell + 1, step + 1)));

        var tip = ActivityCalendar.Describe(days[0]);
        Assert.StartsWith("412 words on ", tip);
        Assert.Contains("September", tip);
        Assert.Contains("2026", tip);
        Assert.DoesNotContain("Tuesday", tip);

        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            // The culture may keep "г." on the line with a no-break space.
            Assert.Equal("412 слов · 15 сентября 2026 г.", ActivityCalendar.Describe(days[0]).Replace(' ', ' ').Replace(' ', ' '));
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [AvaloniaFact]
    public void HoveringTheCalendarSetsTheTooltipAndTheGaugeNamesItsUnit()
    {
        CoreStrings.SetLanguage(AppLanguage.En);
        var (window, _) = ScreenSnapshotTests.Shell(Screen.Insights, _ => new InsightsViewModel(new FakeHistory(), Sample));
        window.Width = 1100;
        window.Height = 1000;
        window.Show();
        try
        {
            var view = window.GetVisualDescendants().OfType<InsightsView>().Single();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "words per minute" && text.IsEffectivelyVisible);

            var calendar = view.GetVisualDescendants().OfType<ActivityCalendar>().Single();
            var days = calendar.Days!;
            var last = days[^1];
            var cell = Enumerable.Range(0, 60 * 7).Select(i => new Point((i / 7) * (ActivityCalendar.Cell + ActivityCalendar.Gap) + 2, (i % 7) * (ActivityCalendar.Cell + ActivityCalendar.Gap) + 2))
                .First(point => calendar.DayAt(point) == last);
            var onWindow = calendar.TranslatePoint(cell, window)!.Value;
            window.MouseMove(onWindow);
            Assert.Equal(ActivityCalendar.Describe(last), ToolTip.GetTip(calendar));

            window.MouseMove(new Point(2, 2));
            Assert.Null(ToolTip.GetTip(calendar));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Render()
    {
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            var (window, _) = ScreenSnapshotTests.Shell(Screen.Insights, _ => new InsightsViewModel(new FakeHistory(), Sample));
            Snapshot.RenderWindow(window, "screen-insights-ru", 1100, 820);
            var (dark, _) = ScreenSnapshotTests.Shell(Screen.Insights, _ => new InsightsViewModel(new FakeHistory(), Sample));
            Snapshot.RenderWindow(dark, "screen-insights-ru", 1100, 820, Avalonia.Styling.ThemeVariant.Dark);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
