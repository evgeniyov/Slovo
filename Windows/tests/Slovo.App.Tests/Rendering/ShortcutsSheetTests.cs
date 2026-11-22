using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.Views;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

/// <summary>Help → Keyboard shortcuts (the macOS ShortcutsSheet) with the Windows keys.</summary>
public class ShortcutsSheetTests
{
    public ShortcutsSheetTests() => CoreStrings.SetLanguage(AppLanguage.En);

    [AvaloniaFact]
    public void ListsTheWindowsKeys()
    {
        var sheet = new ShortcutsSheetViewModel(new AppSettings(SettingsStore.InMemory()));
        Assert.Equal(
            [
                ["Ctrl + Win"], ["Ctrl + Win", "Ctrl + Win"], ["Ctrl + Win"], ["Esc"],
                ["Ctrl+Alt+V"], ["Ctrl+Alt+M"], ["Ctrl+Alt+U"], ["Ctrl+,"],
            ],
            sheet.Rows.Select(r => r.Keys.ToArray()).ToArray());
        Assert.Equal(["Dictate", "Hands-free dictation", "Finish hands-free", "Cancel", "Paste last transcript",
                      "Mark a moment", "Switch the microphone", "Settings"], sheet.Rows.Select(r => r.Title));
        Assert.True(sheet.Rows[0].IsFirst);
        Assert.All(sheet.Rows.Skip(1), row => Assert.False(row.IsFirst));
        // No Mac key symbols anywhere.
        Assert.DoesNotContain(sheet.Rows.SelectMany(r => r.Keys), key => key.IndexOfAny(['⌘', '⌥', '⌃', '⇧']) >= 0 || key == "fn");
    }

    [AvaloniaFact]
    public void FollowsTheChosenKeys()
    {
        var settings = new AppSettings(SettingsStore.InMemory())
        {
            DictationKey = DictationKey.RightAlt,
            PasteLastShortcut = HotkeyShortcut.PasteLastChoices[1],
            HighlightShortcut = HotkeyShortcut.MarkMomentChoices[2],
            MicrophoneShortcut = HotkeyShortcut.MicrophoneToggleChoices[1],
        };
        var sheet = new ShortcutsSheetViewModel(settings);
        Assert.Equal(["Right Alt", "Right Alt"], sheet.Rows[1].Keys);
        Assert.Equal(["Ctrl+Shift+V"], sheet.Rows[4].Keys);
        Assert.Equal(["Ctrl+Win+M"], sheet.Rows[5].Keys);
        Assert.Equal(["Ctrl+Shift+U"], sheet.Rows[6].Keys);
    }

    [AvaloniaFact]
    public void OpensOverTheMainWindowAndClosesWithItsButton()
    {
        var shell = new MainWindowViewModel((_, _) => new EmptyScreen(), new FakeShellActions(), null);
        var sheet = new ShortcutsSheetViewModel(new AppSettings(SettingsStore.InMemory()));
        shell.ShowSheet(sheet);
        Assert.Same(sheet, shell.Sheet);
        sheet.DismissCommand.Execute(null);
        Assert.False(shell.HasSheet);
    }

    [AvaloniaFact]
    public void CtrlCommaOpensSettingsAsTheSheetSays()
    {
        var actions = new FakeShellActions();
        var window = new MainWindow { DataContext = new MainWindowViewModel((_, _) => new EmptyScreen(), actions, null) };
        window.Show();
        try
        {
            window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, ",");
            window.KeyRelease(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, ",");
            Assert.Equal(["OpenSettings"], actions.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Render(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            var settings = new AppSettings(SettingsStore.InMemory());
            var (window, shell) = ScreenSnapshotTests.Shell(Screen.Home, _ => new EmptyScreen());
            shell.ShowSheet(new ShortcutsSheetViewModel(settings));
            Snapshot.RenderWindow(window, "sheet-shortcuts-" + language, 1100, 720);
            if (language == "en")
            {
                var (dark, darkShell) = ScreenSnapshotTests.Shell(Screen.Home, _ => new EmptyScreen());
                darkShell.ShowSheet(new ShortcutsSheetViewModel(settings));
                Snapshot.RenderWindow(dark, "sheet-shortcuts-" + language, 1100, 720, ThemeVariant.Dark);
            }
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
