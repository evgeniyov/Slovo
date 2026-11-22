using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Slovo.App.Controls;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Meetings;
using Slovo.App.Views.Meetings;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Tests.Rendering;

/// <summary>The meeting card's playback bar, and the transcript and moments that play through it.</summary>
public class PlaybackBarTests
{
    static readonly string[] Fixtures = ["format2-cowork", "format1-call", "format2-microphone", "damaged"];

    static (Views.MainWindow Window, MainWindowViewModel Shell, MeetingDetailViewModel Detail) Card(MeetingFixture fixture, string name = "format2-cowork")
    {
        var (window, shell) = ScreenSnapshotTests.Shell(Screen.Meetings, host => new MeetingsViewModel(fixture.Context, host));
        var meetings = (MeetingsViewModel)shell.CurrentScreen!;
        meetings.Select(fixture.Ids[name]);
        return (window, shell, meetings.Detail!);
    }

    static MeetingRecord Meeting(MeetingFixture fixture, string name = "format2-cowork") => fixture.Store.Meeting(fixture.Ids[name])!;

    /// <summary>Shows the window at a size where the whole card is laid out, focus settled.</summary>
    static void Show(Window window)
    {
        window.Width = 1180;
        window.Height = 760;
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    static void Click(Window window, Visual target, Point at)
    {
        var point = target.TranslatePoint(at, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ShowsTheMeetingLengthAndPlaysAndPauses()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (_, _, detail) = Card(fixture);
        var bar = detail.Playback;
        Assert.True(bar.IsVisible);
        Assert.False(bar.IsPlaying);
        Assert.Equal("0:00", bar.CurrentTimeText);
        Assert.Equal("1:04:12", bar.DurationText);
        Assert.Equal(0, bar.Fraction);
        Assert.Equal(Slovo.App.Localization.Loc.Instance.Get("meetings.playback.play"), bar.PlayPauseTitle);

        bar.TogglePlayCommand.Execute(null);
        Assert.Equal((Meeting(fixture).Id, (double?)null), Assert.Single(fixture.Playback.Plays));
        Assert.True(bar.IsPlaying);
        Assert.Equal(Slovo.App.Localization.Loc.Instance.Get("meetings.playback.pause"), bar.PlayPauseTitle);

        fixture.Playback.Advance(90);
        Assert.Equal("1:30", bar.CurrentTimeText);
        Assert.Equal(90 / 3852.4, bar.Fraction, 6);

        bar.TogglePlayCommand.Execute(null);
        Assert.False(bar.IsPlaying);
        Assert.Equal("1:30", bar.CurrentTimeText);
    }

    [AvaloniaFact]
    public void AnotherMeetingPlayingLeavesThisBarAtTheStart()
    {
        using var fixture = new MeetingFixture(Fixtures);
        fixture.Playback.Play(Meeting(fixture, "format1-call"), 600);
        var (_, _, detail) = Card(fixture);
        Assert.False(detail.Playback.IsPlaying);
        Assert.Equal("0:00", detail.Playback.CurrentTimeText);
        Assert.Equal(0, detail.Playback.Fraction);
    }

    [AvaloniaFact]
    public void TheScrubberJumpsThereAndPlays()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (_, _, detail) = Card(fixture);
        detail.Playback.SeekCommand.Execute(0.5);
        Assert.Equal((Meeting(fixture).Id, (double?)(3852.4 / 2)), fixture.Playback.Plays[^1]);
        Assert.True(detail.Playback.IsPlaying);
        Assert.Equal(0.5, detail.Playback.Fraction, 6);
    }

    [AvaloniaFact]
    public void OffersTheMacSpeedsAndKeepsTheChoiceAcrossMeetings()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (_, _, detail) = Card(fixture);
        var bar = detail.Playback;
        Assert.Equal(["0.75×", "1×", "1.25×", "1.5×", "1.75×", "2×"], bar.RateChoices.Select(c => c.Title));
        Assert.Equal(1, bar.SelectedRate.Value);

        bar.SelectedRate = bar.RateChoices.Single(c => c.Value == 1.5);
        Assert.Equal(1.5, fixture.Playback.Rate);

        var (_, _, other) = Card(fixture, "format1-call");
        Assert.Equal("1.5×", other.Playback.SelectedRate.Title);
    }

    [AvaloniaFact]
    public void ArrowsNudgeOnlyAMeetingAlreadyPlaying()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (_, _, detail) = Card(fixture);
        var bar = detail.Playback;

        // Not loaded: the key is taken, nothing starts.
        Assert.True(bar.Nudge(PlaybackBarViewModel.SeekStep));
        Assert.Empty(fixture.Playback.Plays);

        bar.SeekAndPlay(60);
        bar.TogglePlayCommand.Execute(null);
        Assert.True(bar.Nudge(-PlaybackBarViewModel.SeekStep));
        Assert.Equal(55, fixture.Playback.Position);
        Assert.False(bar.IsPlaying);
        bar.Nudge(-100);
        Assert.Equal(0, fixture.Playback.Position);

        // A meeting whose audio is gone has no bar, and leaves the arrows alone.
        var (_, _, silent) = Card(fixture, "format2-microphone");
        Assert.False(silent.Playback.IsVisible);
        Assert.False(silent.Playback.Nudge(5));
    }

    [AvaloniaFact]
    public void AFailureToPlaySaysWhy()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (_, shell, detail) = Card(fixture);
        fixture.Playback.FailPlay = "No output device";
        detail.Playback.TogglePlayCommand.Execute(null);
        var alert = Assert.IsType<AlertSheetViewModel>(shell.Sheet);
        Assert.Equal(Slovo.App.Localization.Loc.Instance.Get("meetings.error.playback"), alert.Title);
        Assert.Equal("No output device", alert.Message);
    }

    [AvaloniaFact]
    public void ALineOrAMomentPlaysFromItsTime()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (_, _, detail) = Card(fixture);
        var line = detail.Lines[3];
        detail.PlayFromCommand.Execute(line);
        Assert.Equal((detail.Id, (double?)line.Line.Start), fixture.Playback.Plays[^1]);

        detail.Tab = MeetingDetailTab.Highlights;
        var moment = detail.Highlights.First(h => h.HasLine);
        Assert.Equal(Slovo.App.Localization.Loc.Instance.Get("meetings.highlight.show"), moment.OpenTitle);
        detail.OpenHighlightCommand.Execute(moment);
        Assert.Equal((detail.Id, (double?)moment.Highlight.Time), fixture.Playback.Plays[^1]);
        Assert.Equal(MeetingDetailTab.Transcript, detail.Tab);
        Assert.NotNull(detail.FocusLine);
        Assert.True(detail.FocusLine!.IsFlashing);
    }

    [AvaloniaFact]
    public void TheCardTakesFocusAndTheArrowsMoveThePlayhead()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (window, _, detail) = Card(fixture);
        Show(window);
        var card = window.GetVisualDescendants().OfType<MeetingDetailView>().Single();
        Assert.True(card.IsKeyboardFocusWithin);

        detail.Playback.SeekAndPlay(100);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(105, fixture.Playback.Position);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(95, fixture.Playback.Position);

        // Renaming: the arrows move the caret, not the playhead.
        card.FindControl<TextBox>("TitleBox")!.Focus();
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(95, fixture.Playback.Position);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingAndDraggingTheScrubberSeeksWhereItIsLetGo()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (window, _, detail) = Card(fixture);
        Show(window);
        var scrubber = window.GetVisualDescendants().OfType<PlaybackScrubber>().Single();
        var size = scrubber.Bounds.Size;
        Assert.True(size.Width > 300);

        Click(window, scrubber, new Point(size.Width / 4, size.Height / 2));
        Assert.Equal(3852.4 / 4, fixture.Playback.Plays[^1].From!.Value, tolerance: 1.0);

        var from = scrubber.TranslatePoint(new Point(size.Width * 0.1, size.Height / 2), window)!.Value;
        var to = scrubber.TranslatePoint(new Point(size.Width * 0.6, size.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.6, scrubber.ShownFraction, 2);
        Assert.Single(fixture.Playback.Plays);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, fixture.Playback.Plays.Count);
        Assert.Equal(3852.4 * 0.6, fixture.Playback.Plays[^1].From!.Value, tolerance: 1.0);
        Assert.Equal(0.6, detail.Playback.Fraction, 2);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingATimestampOrAMomentPlaysFromThere()
    {
        using var fixture = new MeetingFixture(Fixtures);
        var (window, _, detail) = Card(fixture);
        Show(window);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TranscriptList");
        var firstRow = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        var timestamp = firstRow.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("Plain"));
        Click(window, timestamp, new Point(4, timestamp.Bounds.Height / 2));
        var line = (TranscriptLineViewModel)firstRow.DataContext!;
        Assert.Equal((detail.Id, (double?)line.Line.Start), fixture.Playback.Plays[^1]);

        detail.Tab = MeetingDetailTab.Highlights;
        Dispatcher.UIThread.RunJobs();
        var moment = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("Plain") && b.DataContext is HighlightRowViewModel { HasLine: true } && b.IsEffectivelyVisible);
        var row = (HighlightRowViewModel)moment.DataContext!;
        Click(window, moment, new Point(10, 8));
        Assert.Equal(row.Highlight.Time, fixture.Playback.Plays[^1].From);
        Assert.Equal(MeetingDetailTab.Transcript, detail.Tab);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void RendersTheBarWhilePlayingInBothThemes(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                using var fixture = new MeetingFixture(Fixtures);
                var (window, _, detail) = Card(fixture);
                fixture.Playback.SetRate(1.5);
                detail.Playback.SeekAndPlay(942);
                Snapshot.RenderWindow(window, "meetings-playback-" + language, 1180, 760, theme);

                var bar = new PlaybackBarView { DataContext = detail.Playback };
                Snapshot.Render(bar, "playback-bar-" + language, 640, 40, theme);
                Assert.Equal("15:42", detail.Playback.CurrentTimeText);
                Assert.Equal("1.5×", detail.Playback.SelectedRate.Title);
            }
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
