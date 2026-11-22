using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Screens;
using Slovo.App.Views;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

public class ScreenSnapshotTests
{
    /// <summary>A main window showing one screen, built the way the app builds it.</summary>
    internal static (MainWindow Window, MainWindowViewModel Model) Shell(Screen screen, Func<ISheetHost, ViewModelBase> make)
    {
        var shell = new MainWindowViewModel((s, host) => s == screen ? make(host) : new EmptyScreen(), new FakeShellActions(), null);
        shell.Select(screen);
        return (new MainWindow { DataContext = shell }, shell);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Dictionary(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            var settings = new AppSettings(SettingsStore.InMemory());
            var store = FakeDictionary.Seeded();
            var (window, _) = Shell(Screen.Dictionary, host => new DictionaryViewModel(store, settings, host));
            Snapshot.RenderWindow(window, "screen-dictionary-" + language, 1100, 720);

            var (dark, shell) = Shell(Screen.Dictionary, host => new DictionaryViewModel(store, settings, host));
            ((DictionaryViewModel)shell.CurrentScreen!).AddNewCommand.Execute(null);
            Assert.True(shell.HasSheet);
            Snapshot.RenderWindow(dark, "screen-dictionary-editor-" + language, 1100, 720, ThemeVariant.Dark);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [AvaloniaFact]
    public void DictionaryEditorAddsAndRejectsDuplicates()
    {
        var store = FakeDictionary.Seeded();
        var editor = new DictionaryEditorViewModel(store, null) { Text = "kubernetes" };
        Assert.True(editor.IsDuplicate);
        Assert.False(editor.CanSave);
        editor.Text = "Redis";
        Assert.True(editor.CanSave);
        editor.SaveCommand.Execute(null);
        Assert.Contains(store.Items, i => i.Text == "Redis" && i.Kind == DictionaryItemKind.Term);
    }

    [AvaloniaFact]
    public void DictionaryFiltersByTabAndSearch()
    {
        var model = new DictionaryViewModel(FakeDictionary.Seeded(), new AppSettings(SettingsStore.InMemory()), new MainWindowViewModel((_, _) => new EmptyScreen(), new FakeShellActions(), null));
        Assert.Equal(8, model.Rows.Count);
        model.Tab = DictionaryTab.Replacements;
        Assert.Equal(2, model.Rows.Count);
        model.Tab = DictionaryTab.All;
        model.Query = "ГИТХАБ";
        Assert.Single(model.Rows);
        model.Query = "nothing like this";
        Assert.True(model.NothingFound);
    }
}

sealed class EmptyScreen : ViewModelBase;
