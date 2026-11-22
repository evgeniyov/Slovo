using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels;

/// <summary>One line of the cheat sheet: what it does, when, and the keys, each drawn as a keycap.</summary>
public sealed record ShortcutRow(string Title, string Detail, IReadOnlyList<string> Keys, bool IsFirst);

/// <summary>Help → Keyboard shortcuts: a cheat sheet that follows the chosen keys (the macOS ShortcutsSheet).</summary>
public sealed partial class ShortcutsSheetViewModel : SheetViewModel
{
    /// <summary>Esc cancels a dictation while it records.</summary>
    public const string CancelKey = "Esc";

    /// <summary>Opens Settings while the main window is in front (⌘, on the Mac).</summary>
    public const string OpenSettingsKey = "Ctrl+,";

    public ShortcutsSheetViewModel(AppSettings settings)
    {
        var loc = Loc.Instance;
        var key = settings.DictationKey.Symbol();
        (string Title, string Detail, string[] Keys)[] rows =
        [
            (loc.Get("shortcuts.hold"), loc.Get("shortcuts.hold.detail"), [key]),
            (loc.Get("shortcuts.handsFree"), loc.Get("shortcuts.handsFree.detail"), [key, key]),
            (loc.Get("shortcuts.finish"), loc.Get("shortcuts.finish.detail"), [key]),
            (loc.Get("shortcuts.cancel"), loc.Get("shortcuts.cancel.detail"), [CancelKey]),
            (loc.Get("shortcuts.pasteLast"), loc.Get("shortcuts.pasteLast.detail"), [settings.PasteLastShortcut.DisplayText]),
            (loc.Get("shortcuts.markMoment"), loc.Get("shortcuts.markMoment.detail"), [settings.HighlightShortcut.DisplayText]),
            (loc.Get("shortcuts.meetingMicrophone"), loc.Get("shortcuts.meetingMicrophone.detail"), [settings.MicrophoneShortcut.DisplayText]),
            (loc.Get("shortcuts.settings"), loc.Get("shortcuts.settings.detail"), [OpenSettingsKey]),
        ];
        Rows = rows.Select((row, index) => new ShortcutRow(row.Title, row.Detail, row.Keys, index == 0)).ToList();
    }

    public IReadOnlyList<ShortcutRow> Rows { get; }

    [RelayCommand]
    void Dismiss() => Close();
}
