using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.ViewModels;

public sealed record TroubleshootingItem(string IconKey, string Title, string Detail, string ActionTitle, Action Action, string? Status, bool StatusOk)
{
    public bool HasStatus => Status is not null;
}

/// <summary>Help → If something doesn't work: the usual causes, each with the action that fixes it.</summary>
public sealed partial class TroubleshootingViewModel : SheetViewModel
{
    public TroubleshootingViewModel(IPermissions permissions, IShellActions shell, IHotkeyMonitor hotkeys)
    {
        var loc = Loc.Instance;
        var microphone = permissions.Status(PermissionKind.Microphone) == PermissionStatus.Granted;
        Items =
        [
            new("Icon.Keyboard", loc.Get("troubleshooting.key.title"), loc.Get("troubleshooting.key.detail"),
                loc.Get("settings.section.general"), () => shell.OpenSettings("General"),
                loc.Get(hotkeys.IsRunning ? "keytest.running" : "keytest.needsPermission"), hotkeys.IsRunning),
            new("Icon.MicOff", loc.Get("troubleshooting.nothingHeard.title"), loc.Get("troubleshooting.nothingHeard.detail"),
                loc.Get("troubleshooting.nothingHeard.action"), () => shell.OpenSettings("General"),
                $"{loc.Get("permission.microphone")}: {loc.Get(microphone ? "permission.status.granted" : "permission.status.missing")}", microphone),
            new("Icon.Copy", loc.Get("troubleshooting.notPasted.title"), loc.Get("troubleshooting.notPasted.detail"),
                loc.Get("common.ok"), Close, null, true),
            new("Icon.Export", loc.Get("troubleshooting.model.title"), loc.Get("troubleshooting.model.detail"),
                loc.Get("troubleshooting.model.action"), () => shell.OpenSettings("Recognition"), null, true),
            new("Icon.People", loc.Get("troubleshooting.meetingOthers.title"), loc.Get("troubleshooting.meetingOthers.detail"),
                loc.Get("troubleshooting.meetingOthers.action"), () => shell.OpenSettings("Meetings"), null, true),
            new("Icon.Search", loc.Get("troubleshooting.other.title"), loc.Get("troubleshooting.other.detail"),
                loc.Get("help.openLogs"), shell.OpenLogs, null, true),
        ];
        if (!microphone)
        {
            Items[1] = Items[1] with { ActionTitle = loc.Get("onboarding.permissions.open"), Action = () => permissions.OpenSystemSettings(PermissionKind.Microphone) };
        }
    }

    public TroubleshootingItem[] Items { get; }

    [RelayCommand]
    void Run(TroubleshootingItem item)
    {
        Close();
        item.Action();
    }

    [RelayCommand]
    void Dismiss() => Close();
}
