using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Meetings;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Tests.Rendering;

public class MeetingsTests
{
    static readonly string[] AllFixtures = ["format2-cowork", "format1-call", "format2-microphone", "damaged"];

    static (Views.MainWindow Window, MainWindowViewModel Shell, MeetingsViewModel Model) Screen(MeetingFixture fixture, MeetingsContext? context = null)
    {
        var (window, shell) = ScreenSnapshotTests.Shell(ViewModels.Screen.Meetings, host => new MeetingsViewModel(context ?? fixture.Context, host));
        return (window, shell, (MeetingsViewModel)shell.CurrentScreen!);
    }

    static MeetingRecord Cowork(MeetingFixture fixture) => fixture.Store.Meeting(fixture.Ids["format2-cowork"])!;

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void RendersEveryTab(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            using var fixture = new MeetingFixture(AllFixtures);
            var (window, _, model) = Screen(fixture);
            model.Select(Cowork(fixture).Id);
            Snapshot.RenderWindow(window, "meetings-transcript-" + language, 1180, 760);

            foreach (var tab in new[] { MeetingDetailTab.Highlights, MeetingDetailTab.Statistics, MeetingDetailTab.ClaudeNotes })
            {
                var (dark, _, darkModel) = Screen(fixture);
                darkModel.Select(Cowork(fixture).Id);
                darkModel.Detail!.Tab = tab;
                Snapshot.RenderWindow(dark, $"meetings-{tab.ToString().ToLowerInvariant()}-{language}", 1180, 760, ThemeVariant.Dark);
            }
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [AvaloniaFact]
    public void RendersTheRecordingPanelAndTheSheets()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (window, shell, model) = Screen(fixture);
        model.NewRecordingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.Recording.IsVisible);
        Assert.False(model.CanStart);
        Assert.Equal("Созвон с командой", model.Recording.Title);
        Assert.Equal("Созвон с командой", model.Detail?.Title);
        Snapshot.RenderWindow(window, "meetings-recording", 1180, 760);

        foreach (var open in new Action<MeetingDetailViewModel>[]
                 {
                     d => d.EditSpeakerCommand.Execute(d.Speakers[0]),
                     d => d.ReprocessCommand.Execute(null),
                     d => d.EditNoteCommand.Execute(d.Lines[0]),
                 })
        {
            var (sheetWindow, sheetShell, sheetModel) = Screen(fixture);
            sheetModel.Select(Cowork(fixture).Id);
            open(sheetModel.Detail!);
            Assert.True(sheetShell.HasSheet);
            Snapshot.RenderWindow(sheetWindow, "meetings-sheet-" + sheetShell.Sheet!.GetType().Name.Replace("ViewModel", "").ToLowerInvariant(), 1180, 760);
        }
        Assert.False(shell.HasSheet);
    }

    [AvaloniaFact]
    public void StarringALineAddsAHighlightToTheStore()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, _, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        var detail = model.Detail!;
        var before = Cowork(fixture).Highlights.Count;
        var line = detail.Lines.First(l => !l.IsStarred);

        detail.ToggleStarCommand.Execute(line);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 1, Cowork(fixture).Highlights.Count);
        Assert.True(detail.Lines.Single(l => l.Line.Start == line.Line.Start && l.Text == line.Text).IsStarred);
        Assert.Contains((before + 1).ToString(), detail.HighlightsTabTitle);
    }

    [AvaloniaFact]
    public void RenamingASpeakerSavesTheNameAndRelabelsTheLines()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, shell, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        var chip = model.Detail!.Speakers[0];

        model.Detail.EditSpeakerCommand.Execute(chip);
        var editor = Assert.IsType<SpeakerEditorViewModel>(shell.Sheet);
        editor.Name = "  Аня  ";
        editor.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.HasSheet);
        Assert.Equal("Аня", Cowork(fixture).EffectiveSpeakers.Single(s => s.Id == chip.Speaker.Id).DisplayName);
        Assert.Contains(model.Detail.Lines, l => l.SpeakerLabel == "Аня");
    }

    [AvaloniaFact]
    public void TheSpeakerFilterHidesOtherVoices()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, _, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        var detail = model.Detail!;
        Assert.True(detail.Speakers.Count > 1);
        var all = detail.Lines.Count;
        var chip = detail.Speakers[0];

        detail.ToggleSpeakerCommand.Execute(chip);

        Assert.True(detail.IsFiltered);
        Assert.InRange(detail.Lines.Count, 1, all - 1);
        Assert.All(detail.Lines, l => Assert.Equal(chip.Label, l.SpeakerLabel));
        detail.CopyTranscriptCommand.Execute(null);
        Assert.DoesNotContain(detail.Speakers[1].Label + ":", fixture.Copied.Single());
    }

    [AvaloniaFact]
    public async Task DeletingAMeetingAsksFirstAndSelectsTheNextOne()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, shell, model) = Screen(fixture);
        var victim = Cowork(fixture);
        model.Select(victim.Id);
        fixture.Playback.Play(victim);

        var deleting = model.Detail!.DeleteCommand.ExecuteAsync(null);
        var confirm = Assert.IsType<ConfirmSheetViewModel>(shell.Sheet);
        Assert.Contains(victim.Title, confirm.Message);
        confirm.ConfirmCommand.Execute(null);
        await deleting;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(fixture.Store.Meeting(victim.Id));
        Assert.False(Directory.Exists(fixture.Paths.MeetingFolder(victim.Id)));
        Assert.Null(fixture.Playback.MeetingId);
        Assert.Contains(victim.Id, fixture.Processing.Cancelled);
        Assert.Equal(AllFixtures.Length - 1, model.Rows.Count);
        Assert.NotNull(model.Detail);
        Assert.NotEqual(victim.Id, model.Detail!.Id);
    }

    [AvaloniaFact]
    public void CancellingTheDeleteKeepsTheMeeting()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, shell, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        _ = model.Detail!.DeleteCommand.ExecuteAsync(null);
        Assert.IsType<ConfirmSheetViewModel>(shell.Sheet).CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AllFixtures.Length, fixture.Store.Meetings.Count);
    }

    [AvaloniaFact]
    public void ReprocessPassesTheChosenOptions()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, shell, model) = Screen(fixture);
        var meeting = fixture.Store.Meetings.First(m => m.HasAudio && m.Status == MeetingStatus.Transcribed);
        model.Select(meeting.Id);

        model.Detail!.ReprocessCommand.Execute(null);
        var sheet = Assert.IsType<ReprocessViewModel>(shell.Sheet);
        Assert.True(sheet.SpeakersOnly);
        sheet.IsFull = true;
        sheet.SeparateSpeakers = true;
        sheet.SelectedPeople = sheet.PeopleChoices.Single(c => c.Value == 3);
        sheet.StartCommand.Execute(null);

        var (id, options) = Assert.Single(fixture.Processing.Reprocessed);
        Assert.Equal(meeting.Id, id);
        Assert.Equal(new ReprocessOptions(false, true, 3, sheet.SelectedEngine.Value), options);
        Assert.False(shell.HasSheet);
    }

    [AvaloniaFact]
    public void AFailedStartShowsTheReason()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        fixture.Recorder.FailStart = "No microphone";
        var (_, shell, model) = Screen(fixture);
        model.NewRecordingCommand.Execute(null);
        var alert = Assert.IsType<AlertSheetViewModel>(shell.Sheet);
        Assert.Equal("No microphone", alert.Message);
        Assert.Equal(AllFixtures.Length, fixture.Store.Meetings.Count);
    }

    /// <summary>
    /// The card as the Mac shows it: «All» and the pencils in the speakers bar, a filtered
    /// transcript, the separation-failed notice with its Try again, the moments with their
    /// speakers, and the statistics down to the "Speech and pauses" section.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void RendersTheSpeakerBarTheNoticeAndTheWholeStatistics(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            using var fixture = new MeetingFixture(AllFixtures);
            var cowork = Cowork(fixture);
            fixture.Store.Update(cowork with
            {
                Diarization = cowork.Diarization! with { State = DiarizationState.Failed, Message = null },
            });

            var (window, _, model) = Screen(fixture);
            model.Select(cowork.Id);
            var detail = model.Detail!;
            detail.ToggleSpeakerCommand.Execute(detail.Speakers.Single(s => s.Label == "Аня"));
            Assert.True(detail.HasDiarizationFailure);
            Assert.True(detail.CanRetrySpeakers);
            Snapshot.RenderWindow(window, "meetings-filtered-" + language, 1180, 760);

            var (tall, _, tallModel) = Screen(fixture);
            tallModel.Select(cowork.Id);
            tallModel.Detail!.Tab = MeetingDetailTab.Statistics;
            Snapshot.RenderWindow(tall, "meetings-statistics-full-" + language, 1180, 1000);

            var (moments, _, momentsModel) = Screen(fixture);
            momentsModel.Select(cowork.Id);
            momentsModel.Detail!.Tab = MeetingDetailTab.Highlights;
            Snapshot.RenderWindow(moments, "meetings-highlights-" + language, 1180, 760);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    /// <summary>Clicks on the card itself: the chips, «All», the pencil, a line's name, the line menu and Try again.</summary>
    [AvaloniaFact]
    public void TheCardsNewControlsReachTheirCommands()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var cowork = Cowork(fixture);
        fixture.Store.Update(cowork with { Diarization = cowork.Diarization! with { State = DiarizationState.Failed } });
        var (window, shell, model) = Screen(fixture);
        model.Select(cowork.Id);
        var detail = model.Detail!;
        window.Width = 1180;
        window.Height = 760;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button Find(Func<Button, bool> match) => window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && match(b));

        Click(window, Find(b => b.Classes.Contains("ChipPart") && b.DataContext is SpeakerChipViewModel { Label: "Аня" }));
        Assert.True(detail.IsFiltered);
        Assert.All(detail.Lines, l => Assert.Equal("Аня", l.SpeakerLabel));
        Click(window, Find(b => b.Classes.Contains("AllChip")));
        Assert.False(detail.IsFiltered);

        Click(window, Find(b => b.Classes.Contains("ChipPart") && b.DataContext is SpeakerChipViewModel { Label: "Speaker 3" } && ToolTip.GetTip(b) is not null));
        Assert.Equal("Speaker 3", Assert.IsType<SpeakerEditorViewModel>(shell.Sheet).Label);
        ((SpeakerEditorViewModel)shell.Sheet!).CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Click(window, Find(b => b.Classes.Contains("SpeakerName") && b.DataContext is TranscriptLineViewModel { SpeakerLabel: "Аня" }));
        Assert.Equal("Аня", Assert.IsType<SpeakerEditorViewModel>(shell.Sheet).Label);
        ((SpeakerEditorViewModel)shell.Sheet!).CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The line menu: "Rename speaker…" and "This is me", which «Я» cannot use again.
        var row = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is TranscriptLineViewModel { IsMe: true });
        var flyout = (MenuFlyout)row.GetVisualDescendants().OfType<Grid>().First(g => g.ContextFlyout is not null).ContextFlyout!;
        flyout.ShowAt(row);
        Dispatcher.UIThread.RunJobs();
        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.False(items.Single(i => i.Command == detail.MarkLineAsMeCommand).IsEffectivelyEnabled);
        Assert.True(items.Single(i => i.Command == detail.RenameLineSpeakerCommand).IsEffectivelyEnabled);
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Click(window, Find(b => b.Command == detail.RetrySpeakersCommand));
        Assert.Single(fixture.Processing.Reprocessed);
        window.Close();
    }

    static void Click(Window window, Control target)
    {
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void AllClearsTheFilterAndCopyNamesTheSelection()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, _, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        var detail = model.Detail!;
        Assert.True(detail.ShowsAllSpeakers);
        Assert.Equal(new AllSpeakersChip(true), detail.SpeakerBar[0]);
        Assert.Equal(detail.Speakers, detail.SpeakerBar.Skip(1));
        Assert.Equal("Copy", detail.CopyTranscriptTitle);

        detail.ToggleSpeakerCommand.Execute(detail.Speakers[1]);
        Assert.False(detail.ShowsAllSpeakers);
        Assert.Equal(new AllSpeakersChip(false), detail.SpeakerBar[0]);
        Assert.Equal("Copy selected", detail.CopyTranscriptTitle);
        Assert.True(detail.Speakers[1].IsActive);
        Assert.False(detail.Speakers[0].IsActive);

        detail.ShowAllSpeakersCommand.Execute(null);
        Assert.False(detail.IsFiltered);
        Assert.Equal(new AllSpeakersChip(true), detail.SpeakerBar[0]);
        Assert.All(detail.Speakers, chip => Assert.True(chip.IsActive));
    }

    [AvaloniaFact]
    public async Task ExportWritesTheWholeMeetingWhateverTheFilter()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var path = Path.Combine(fixture.Root, "export.md");
        var (_, _, model) = Screen(fixture, fixture.Context with { PickSaveFile = _ => Task.FromResult<string?>(path) });
        var meeting = Cowork(fixture);
        model.Select(meeting.Id);
        var detail = model.Detail!;
        detail.ToggleSpeakerCommand.Execute(detail.Speakers.Single(s => s.Label == "Аня"));
        Assert.True(detail.IsFiltered);

        await detail.ExportCommand.ExecuteAsync(null);

        var written = await File.ReadAllTextAsync(path);
        Assert.Equal(Slovo.Core.Meetings.TranscriptMerger.Markdown(meeting), written);
        Assert.Contains("Команда дизайна: Со стороны дизайна всё готово.", written);
        // Copy keeps following the filter.
        detail.CopyTranscriptCommand.Execute(null);
        Assert.DoesNotContain("Команда дизайна", fixture.Copied.Single());
    }

    [AvaloniaFact]
    public void MomentsShowTheirSpeakerAndCopyInTheDocumentFormat()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, _, model) = Screen(fixture);
        var meeting = Cowork(fixture);
        model.Select(meeting.Id);
        var detail = model.Detail!;

        Assert.Equal(["Аня", "Speaker 3", "Команда дизайна", null], detail.Highlights.Select(h => h.SpeakerLabel));
        Assert.Equal("“Со стороны дизайна всё готово.”", detail.Highlights[2].Quote);
        Assert.Equal("4 moments", detail.HighlightsCountText);

        detail.CopyHighlightsCommand.Execute(null);
        var lines = fixture.Copied.Single().Split('\n');
        Assert.StartsWith("- [12:40] Аня: «Давайте к пятнице соберём прототип", lines[0]);
        Assert.EndsWith("…» — дедлайн", lines[0]);
        Assert.Equal("- [13:25] Speaker 3: «Согласен, ёлка на ресепшене тоже нужна.»", lines[1]);
        // The note keeps its own line break, as the Mac copies it.
        Assert.Equal("- [15:15] Команда дизайна: «Со стороны дизайна всё готово.» — важно", lines[2]);
        Assert.Equal("очень", lines[3]);
        Assert.Equal("- [50:00] — без строки", lines[4]);

        // With a filter only the chosen speakers' moments are listed and copied; a moment
        // without a line belongs to no one.
        detail.ToggleSpeakerCommand.Execute(detail.Speakers.Single(s => s.Label == "Аня"));
        Assert.Equal(["Аня"], detail.Highlights.Select(h => h.SpeakerLabel));
        Assert.Equal("1 moment", detail.HighlightsCountText);
        detail.CopyHighlightsCommand.Execute(null);
        Assert.StartsWith("- [12:40] Аня: «", fixture.Copied[1]);
        Assert.DoesNotContain("\n", fixture.Copied[1]);
    }

    [AvaloniaFact]
    public void TheLineMenuRenamesTheSpeakerAndMarksThem()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, shell, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        var detail = model.Detail!;
        var anya = detail.Lines.First(l => l.SpeakerLabel == "Аня");
        var me = detail.Lines.First(l => l.Line.SpeakerId == "s1");

        detail.RenameLineSpeakerCommand.Execute(anya);
        var editor = Assert.IsType<SpeakerEditorViewModel>(shell.Sheet);
        Assert.Equal("Аня", editor.Label);
        editor.CancelCommand.Execute(null);

        Assert.True(me.IsMe);
        Assert.False(detail.MarkLineAsMeCommand.CanExecute(me));
        Assert.True(detail.MarkLineAsMeCommand.CanExecute(anya));
        detail.MarkLineAsMeCommand.Execute(anya);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Cowork(fixture).Speakers.Single(s => s.Id == "s2").IsMe);
        Assert.False(Cowork(fixture).Speakers.Single(s => s.Id == "s1").IsMe);
        Assert.True(detail.Lines.First(l => l.Line.SpeakerId == "s2").IsMe);
        Assert.False(detail.MarkLineAsMeCommand.CanExecute(detail.Lines.First(l => l.Line.SpeakerId == "s2")));
    }

    [AvaloniaFact]
    public void TryAgainSeparatesSpeakersAgainOrProcessesTheWholeMeeting()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var cowork = Cowork(fixture);
        fixture.Store.Update(cowork with { Diarization = cowork.Diarization! with { State = DiarizationState.Failed, Message = "Out of memory" } });
        var (_, _, model) = Screen(fixture);
        model.Select(cowork.Id);
        var detail = model.Detail!;
        Assert.Equal("Out of memory", detail.DiarizationFailure);
        Assert.True(detail.CanRetrySpeakers);

        detail.RetrySpeakersCommand.Execute(null);
        var (id, options) = Assert.Single(fixture.Processing.Reprocessed);
        Assert.Equal(cowork.Id, id);
        Assert.Equal(new ReprocessOptions(SpeakersOnly: true, SeparateSpeakers: true, 3, EngineId.GigaamCtc), options);

        // Without a speaker model only a whole run can separate them again.
        fixture.Processing.IsSpeakerModelReady = false;
        detail.RetrySpeakersCommand.Execute(null);
        Assert.Equal([cowork.Id], fixture.Processing.Transcribed);
        Assert.Single(fixture.Processing.Reprocessed);

        // Without audio there is nothing to run again.
        fixture.Store.Update(Cowork(fixture) with { HasAudio = false });
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.Detail!.CanRetrySpeakers);
    }

    [AvaloniaFact]
    public void AMeetingThatRecordsCannotBeDeletedAndItsRowCountsUp()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, _, model) = Screen(fixture);
        model.NewRecordingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var detail = model.Detail!;
        Assert.True(detail.IsRecordingNow);
        Assert.False(detail.CanDelete);
        Assert.False(detail.DeleteCommand.CanExecute(null));

        var row = model.Rows.Single(r => r.Id == detail.Id);
        Assert.Equal("12:34", row.DurationText);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        fixture.Recorder.Elapsed = 800;
        row.Tick();
        Assert.Equal([nameof(MeetingRowViewModel.DurationText)], changed);
        Assert.Equal("13:20", row.DurationText);
        // Other rows keep their stored duration and do not tick.
        var other = model.Rows.First(r => r.Id == Cowork(fixture).Id);
        Assert.Equal("1:04:12", other.DurationText);

        // Once it stops recording, it can go.
        fixture.Recorder.Stop();
        fixture.Store.Update(fixture.Store.Meeting(detail.Id)! with { Status = MeetingStatus.Recorded, Duration = 812 });
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.Detail!.DeleteCommand.CanExecute(null));
        Assert.Equal("13:32", model.Rows.Single(r => r.Id == detail.Id).DurationText);
    }

    [AvaloniaFact]
    public void AMeetingLeftRecordingWithoutTheRecorderShowsNoDuration()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var stale = fixture.Store.Create("Оборванная запись");
        Assert.Equal(MeetingStatus.Recording, stale.Status);
        var (_, _, model) = Screen(fixture);
        var row = model.Rows.Single(r => r.Id == stale.Id);
        Assert.Null(row.DurationText);
        Assert.False(row.HasDuration);
    }

    [AvaloniaFact]
    public void StatisticsReadLikeTheMac()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, _, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        var detail = model.Detail!;

        Assert.True(detail.HasStats);
        Assert.Equal("Аня talks the most", detail.StatsHeadline);
        Assert.Equal("Your share — 25 % · Across the whole recording", detail.StatsCaption);
        Assert.Equal("0:17 · 2 turns · 0:08 on average · 102 words/min", detail.StatRows[0].Detail);
        Assert.Equal("0:12 · 1 turn · 30 words/min", detail.StatRows[^1].Detail);
        Assert.Equal(["Speech", "Silence", "Longest pause", "Pauses over 5 s"], detail.SpeechFigures.Select(f => f.Title));
        var speech = detail.SpeechFigures[0];
        Assert.Equal("0:54", speech.Value);
        Assert.Equal("1 %", speech.Caption);
        Assert.Equal("4", detail.SpeechFigures[3].Value);
    }

    [AvaloniaFact]
    public void TitleEditsAreSavedWhenCommitted()
    {
        using var fixture = new MeetingFixture(AllFixtures);
        var (_, _, model) = Screen(fixture);
        model.Select(Cowork(fixture).Id);
        model.Detail!.Title = "Планёрка по релизу";
        model.Detail.CommitTitleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Планёрка по релизу", Cowork(fixture).Title);
        Assert.Equal("Планёрка по релизу", model.Rows.Single(r => r.Id == Cowork(fixture).Id).Title);
    }
}
