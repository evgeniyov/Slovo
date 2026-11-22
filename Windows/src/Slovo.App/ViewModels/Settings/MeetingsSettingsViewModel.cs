using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Settings;

/// <summary>Settings → Meetings: tracks, engine, speakers, shortcuts, audio retention and the Inbox.</summary>
public sealed partial class MeetingsSettingsViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly IShell shell;
    readonly AppPaths paths;
    readonly Func<HotkeyShortcut, bool> registerMark;
    readonly Func<HotkeyShortcut, bool> registerMicrophone;

    public MeetingsSettingsViewModel(AppSettings settings, IModelManager models, IDiarizationModelManager speakerModel, IDiarizationProvider diarization,
                                     ISheetHost sheets, IShell shell, AppPaths paths,
                                     Func<HotkeyShortcut, bool> registerMark, Func<HotkeyShortcut, bool> registerMicrophone,
                                     Func<Task>? deleteSpeakerModel = null)
    {
        this.settings = settings;
        this.shell = shell;
        this.paths = paths;
        this.registerMark = registerMark;
        this.registerMicrophone = registerMicrophone;
        var loc = Loc.Instance;
        EngineChoices = EngineIds.MeetingChoices.Select(id => new ChoiceItem<EngineId>(id, models.Status(id).IsUsable
            ? id.DisplayName() : loc.Format("settings.recognition.choice.notReady", id.DisplayName()))).ToList();
        PeopleChoices = AppSettings.ExpectedSpeakerChoices
            .Select(n => new ChoiceItem<int>(n, n == 0 ? loc.Get("settings.meetings.people.automatic") : n.ToString(loc.Culture))).ToList();
        KeepAudioChoices = Retention.MeetingAudioChoices.Select(p => new ChoiceItem<RetentionPeriod>(p, loc.Get("retention." + EnumText.Raw(p)))).ToList();
        MarkChoices = HotkeyShortcut.MarkMomentChoices.Select(s => new ChoiceItem<HotkeyShortcut>(s, s.DisplayText)).ToList();
        MicrophoneChoices = HotkeyShortcut.MicrophoneToggleChoices.Select(s => new ChoiceItem<HotkeyShortcut>(s, s.DisplayText)).ToList();
        SpeakerModel = new ModelRowViewModel(loc.Get("settings.meetings.speakerModel"), loc.Get("settings.meetings.speakerModel.tradeoff"),
            new SpeakerModelSource(speakerModel, deleteSpeakerModel), sheets, loc.Get("settings.meetings.speakerModel.delete.title"),
            loc.Get("settings.meetings.speakerModel.delete.message"), beforeDelete: deleteSpeakerModel is null ? diarization.Release : null);
        speakerModel.Refresh();
    }

    public IReadOnlyList<ChoiceItem<EngineId>> EngineChoices { get; }
    public IReadOnlyList<ChoiceItem<int>> PeopleChoices { get; }
    public IReadOnlyList<ChoiceItem<RetentionPeriod>> KeepAudioChoices { get; }
    public IReadOnlyList<ChoiceItem<HotkeyShortcut>> MarkChoices { get; }
    public IReadOnlyList<ChoiceItem<HotkeyShortcut>> MicrophoneChoices { get; }
    public ModelRowViewModel SpeakerModel { get; }

    [ObservableProperty] public partial bool MarkRefused { get; private set; }
    [ObservableProperty] public partial bool MicrophoneRefused { get; private set; }

    public bool RecordSystemAudio
    {
        get => settings.RecordSystemAudio;
        set { settings.RecordSystemAudio = value; OnPropertyChanged(); }
    }

    public bool RecordMicrophone
    {
        get => settings.RecordMicrophone;
        set { settings.RecordMicrophone = value; OnPropertyChanged(); }
    }

    /// <summary>Whether a new meeting also records the screen; what it captures is chosen next to "New recording".</summary>
    public bool RecordScreen
    {
        get => settings.RecordScreen;
        set { settings.RecordScreen = value; OnPropertyChanged(); }
    }

    public ChoiceItem<EngineId> SelectedEngine
    {
        get => EngineChoices.FirstOrDefault(c => c.Value == settings.MeetingEngine) ?? EngineChoices[0];
        set { if (value is not null) { settings.MeetingEngine = value.Value; OnPropertyChanged(); } }
    }

    public bool SpeakerSeparation
    {
        get => settings.SpeakerSeparation;
        set { settings.SpeakerSeparation = value; OnPropertyChanged(); }
    }

    public ChoiceItem<int> SelectedPeople
    {
        get => PeopleChoices.FirstOrDefault(c => c.Value == settings.ExpectedSpeakers) ?? PeopleChoices[0];
        set { if (value is not null) { settings.ExpectedSpeakers = value.Value; OnPropertyChanged(); } }
    }

    public ChoiceItem<RetentionPeriod> SelectedKeepAudio
    {
        get => KeepAudioChoices.FirstOrDefault(c => c.Value == settings.KeepMeetingAudio) ?? KeepAudioChoices[0];
        set { if (value is not null) { settings.KeepMeetingAudio = value.Value; OnPropertyChanged(); } }
    }

    public ChoiceItem<HotkeyShortcut> SelectedMark
    {
        get => MarkChoices.FirstOrDefault(c => c.Value == settings.HighlightShortcut) ?? MarkChoices[0];
        set
        {
            if (value is null || value.Value == settings.HighlightShortcut) return;
            settings.HighlightShortcut = value.Value;
            MarkRefused = !registerMark(value.Value);
            OnPropertyChanged();
        }
    }

    public ChoiceItem<HotkeyShortcut> SelectedMicrophone
    {
        get => MicrophoneChoices.FirstOrDefault(c => c.Value == settings.MicrophoneShortcut) ?? MicrophoneChoices[0];
        set
        {
            if (value is null || value.Value == settings.MicrophoneShortcut) return;
            settings.MicrophoneShortcut = value.Value;
            MicrophoneRefused = !registerMicrophone(value.Value);
            OnPropertyChanged();
        }
    }

    public bool InboxEnabled
    {
        get => settings.InboxEnabled;
        set { settings.InboxEnabled = value; OnPropertyChanged(); }
    }

    public string InboxFolder => paths.InboxFolder;

    [RelayCommand]
    void OpenInbox()
    {
        Directory.CreateDirectory(paths.InboxFolder);
        shell.OpenFolder(paths.InboxFolder);
    }
}
