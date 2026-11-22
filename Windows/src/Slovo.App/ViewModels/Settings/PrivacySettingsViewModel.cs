using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Settings;

/// <summary>Settings → Data and privacy: permissions, retention, the data folder, deleting history.</summary>
public sealed partial class PrivacySettingsViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly IHistoryStore history;
    readonly IShell shell;
    readonly AppPaths paths;
    readonly Func<string, string, Task<bool>> confirm;
    readonly Action openIntegrations;

    public PrivacySettingsViewModel(AppSettings settings, IHistoryStore history, IShell shell, IPermissions permissions, AppPaths paths,
                                    Func<string, string, Task<bool>> confirm, Action openIntegrations)
    {
        this.settings = settings;
        this.history = history;
        this.shell = shell;
        this.paths = paths;
        this.confirm = confirm;
        this.openIntegrations = openIntegrations;
        var loc = Loc.Instance;
        HistoryChoices = Retention.HistoryChoices.Select(p => new ChoiceItem<RetentionPeriod>(p, loc.Get("retention." + EnumText.Raw(p)))).ToList();
        AudioChoices = Retention.DictationAudioChoices.Select(p => new ChoiceItem<RetentionPeriod>(p, loc.Get("retention." + EnumText.Raw(p)))).ToList();
        Microphone = new PermissionRowViewModel(permissions, PermissionKind.Microphone, "Icon.Mic",
            loc.Get("permission.microphone"), loc.Get("onboarding.permissions.microphone.detail"));
        history.PropertyChanged += (_, _) => DeleteAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Delete all waits until there is a dictation to delete.</summary>
    public bool HasHistory => history.Entries.Count > 0;

    /// <summary>
    /// The one permission a Windows desktop app must respect. The macOS rows for Accessibility,
    /// Input Monitoring, System Audio Recording and Screen Recording have no Windows switch.
    /// </summary>
    public PermissionRowViewModel Microphone { get; }

    /// <summary>Reads the permission again; the view calls it when the Settings window comes back.</summary>
    public void RefreshPermissions() => Microphone.Refresh();

    public IReadOnlyList<ChoiceItem<RetentionPeriod>> HistoryChoices { get; }
    public IReadOnlyList<ChoiceItem<RetentionPeriod>> AudioChoices { get; }

    public bool ClaudeAccessOn => settings.McpEnabled;

    public ChoiceItem<RetentionPeriod> SelectedHistory
    {
        get => HistoryChoices.FirstOrDefault(c => c.Value == settings.KeepHistory) ?? HistoryChoices[0];
        set
        {
            if (value is null) return;
            settings.KeepHistory = value.Value;
            OnPropertyChanged();
        }
    }

    public ChoiceItem<RetentionPeriod> SelectedAudio
    {
        get => AudioChoices.FirstOrDefault(c => c.Value == settings.KeepDictationAudio) ?? AudioChoices[0];
        set
        {
            if (value is null) return;
            settings.KeepDictationAudio = value.Value;
            OnPropertyChanged();
        }
    }

    public string DataFolder => paths.Base;

    [RelayCommand]
    void OpenDataFolder()
    {
        Directory.CreateDirectory(paths.Base);
        shell.OpenFolder(paths.Base);
    }

    [RelayCommand]
    void OpenIntegrations() => openIntegrations();

    [RelayCommand(CanExecute = nameof(HasHistory))]
    async Task DeleteAllAsync()
    {
        var loc = Loc.Instance;
        var count = loc.Plural("plural.dictations", history.Entries.Count);
        if (await confirm(loc.Get("settings.privacy.deleteAll.confirmTitle"), loc.Format("settings.privacy.deleteAll.confirmMessage", count)))
        {
            history.DeleteAll();
        }
    }
}
