using Avalonia.Headless.XUnit;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Screens;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

public class NotesSnippetsTests
{
    [AvaloniaFact]
    public void ScratchpadEditsGoToTheStoreAndDictationFollowsTheNote()
    {
        var notes = new FakeNotes();
        var dictation = new FakeNoteDictation();
        var model = new ScratchpadViewModel(notes, dictation, new MainWindowViewModel((_, _) => new EmptyScreen(), new FakeShellActions(), null), _ => { });
        Assert.True(model.IsEmpty);
        model.NewNoteCommand.Execute(null);
        model.Text = "Купить хлеб\nи молоко";
        Assert.Equal("Купить хлеб\nи молоко", notes.List.Single().Text);
        Assert.Equal("Купить хлеб", model.SelectedRow!.Title);

        model.ToggleDictationCommand.Execute(null);
        Assert.True(model.IsDictating);
        notes.Append("и сыр", notes.List.Single().Id);
        Assert.EndsWith("и сыр", model.Text);
        model.ToggleDictationCommand.Execute(null);
        Assert.False(model.IsDictating);
    }

    [AvaloniaFact]
    public void CopyPutsTheNoteOnTheClipboardAndSaysSoForAMoment()
    {
        var notes = new FakeNotes();
        var note = notes.Create("План на неделю\n— выпустить Slovo для Windows");
        var copied = new List<string>();
        var model = new ScratchpadViewModel(notes, new FakeNoteDictation(), new MainWindowViewModel((_, _) => new EmptyScreen(), new FakeShellActions(), null), copied.Add);
        Assert.Equal(note.Id, model.SelectedId);
        Assert.False(model.Copied.IsOn);

        // What the editor shows is what is copied, as the Mac copies its draft.
        model.Text += "\n— проверить диктовку";
        model.CopyCommand.Execute(null);

        Assert.Equal(["План на неделю\n— выпустить Slovo для Windows\n— проверить диктовку"], copied);
        Assert.True(model.Copied.IsOn);
        Assert.True(Wait.Until(() => !model.Copied.IsOn, CopiedFlag.Duration + TimeSpan.FromSeconds(5)));
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    public void Render(string language)
    {
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            var settings = new AppSettings(SettingsStore.InMemory());
            var snippets = new FakeSnippets();
            snippets.List.Add(new Snippet { Trigger = "мой адрес", Expansion = "г. Примерск, ул. Примерная, д. 1" });
            snippets.List.Add(new Snippet { Trigger = "подпись", Expansion = "С уважением,\nКоманда проекта" });
            var (window, _) = ScreenSnapshotTests.Shell(Screen.Snippets, host => new SnippetsViewModel(snippets, settings, host));
            Snapshot.RenderWindow(window, "screen-snippets-" + language, 1100, 720);

            var notes = new FakeNotes();
            notes.Create("План на неделю\n— выпустить Slovo для Windows\n— проверить диктовку в Telegram");
            notes.Create("Идеи для встречи");
            var (notesWindow, _) = ScreenSnapshotTests.Shell(Screen.Scratchpad, host => new ScratchpadViewModel(notes, new FakeNoteDictation(), host, _ => { }));
            Snapshot.RenderWindow(notesWindow, "screen-scratchpad-" + language, 1100, 720);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
