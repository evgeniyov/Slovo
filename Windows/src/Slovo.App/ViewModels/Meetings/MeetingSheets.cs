using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Meetings;

public sealed record MergeChoice(string SpeakerId, string Label);

/// <summary>Rename a speaker, mark "This is me", bring back the default name or merge with another voice.</summary>
public sealed partial class SpeakerEditorViewModel : SheetViewModel
{
    readonly MeetingSpeaker speaker;
    readonly MeetingRecord meeting;
    readonly Action<MeetingRecord> save;

    public SpeakerEditorViewModel(MeetingSpeaker speaker, MeetingRecord meeting, Action<MeetingRecord> save)
    {
        this.speaker = speaker;
        this.meeting = meeting;
        this.save = save;
        Name = speaker.DisplayName ?? "";
        Label = TranscriptMerger.SpeakerLabel(speaker, meeting);
        MergeChoices = meeting.EffectiveSpeakers.Where(s => s.Track == speaker.Track && s.Id != speaker.Id)
            .Select(s => new MergeChoice(s.Id, TranscriptMerger.SpeakerLabel(s, meeting))).ToList();
        var loc = Loc.Instance;
        SourceText = loc.Format(speaker.Track == MeetingTrack.Microphone ? "meetings.speaker.source.microphone" : "meetings.speaker.source.system",
            Formatting.Clock(meeting.TalkTime(speaker.Id)));
    }

    public string Label { get; }
    public string SourceText { get; }
    public IReadOnlyList<MergeChoice> MergeChoices { get; }
    public bool CanMerge => MergeChoices.Count > 0;
    public bool IsMe => TranscriptMerger.IsMe(speaker, meeting);

    [ObservableProperty] public partial string Name { get; set; } = "";

    [RelayCommand]
    void Save()
    {
        save(MeetingEdits.RenameSpeaker(speaker.Id, Name.Trim().Length == 0 ? null : Name, meeting));
        Close();
    }

    [RelayCommand]
    void MarkAsMe()
    {
        save(MeetingEdits.MarkAsMe(speaker.Id, meeting));
        Close();
    }

    [RelayCommand]
    void ResetName()
    {
        save(MeetingEdits.RenameSpeaker(speaker.Id, null, meeting));
        Close();
    }

    [RelayCommand]
    void Merge(MergeChoice choice)
    {
        save(MeetingEdits.MergeSpeaker(speaker.Id, choice.SpeakerId, meeting));
        Close();
    }

    [RelayCommand]
    void Cancel() => Close();
}

/// <summary>A note on an important moment.</summary>
public sealed partial class HighlightNoteViewModel(string? note, Action<string?> save) : SheetViewModel
{
    [ObservableProperty] public partial string Note { get; set; } = note ?? "";

    [RelayCommand]
    void Save()
    {
        save(Note.Trim().Length == 0 ? null : Note);
        Close();
    }

    [RelayCommand]
    void Cancel() => Close();
}

/// <summary>"Process again…": speakers only or speakers and text, the number of people and the engine.</summary>
public sealed partial class ReprocessViewModel : SheetViewModel
{
    readonly MeetingRecord meeting;
    readonly MeetingsContext context;

    public ReprocessViewModel(MeetingRecord meeting, MeetingsContext context)
    {
        this.meeting = meeting;
        this.context = context;
        var loc = Loc.Instance;
        CanSpeakersOnly = context.Processing.CanReprocessSpeakersOnly(meeting);
        SpeakersOnly = CanSpeakersOnly;
        SeparateSpeakers = context.Settings.SpeakerSeparation && context.Processing.IsSpeakerModelReady;
        PeopleChoices = AppSettings.ExpectedSpeakerChoices
            .Select(n => new ChoiceItem<int>(n, n == 0 ? loc.Get("settings.meetings.people.automatic") : n.ToString(loc.Culture))).ToList();
        SelectedPeople = PeopleChoices.FirstOrDefault(c => c.Value == (meeting.Diarization?.RequestedSpeakers ?? context.Settings.ExpectedSpeakers)) ?? PeopleChoices[0];
        EngineChoices = EngineIds.MeetingChoices.Select(id => new ChoiceItem<EngineId>(id, context.Models.Status(id).IsUsable
            ? id.DisplayName() : loc.Format("settings.recognition.choice.notReady", id.DisplayName()))).ToList();
        SelectedEngine = EngineChoices.FirstOrDefault(c => c.Value == (meeting.EngineId ?? context.Settings.MeetingEngine)) ?? EngineChoices[0];
    }

    public bool CanSpeakersOnly { get; }
    public bool HasAudio => meeting.HasAudio;
    public bool IsBusy => context.Processing.IsProcessing(meeting.Id);
    public bool SpeakerModelReady => context.Processing.IsSpeakerModelReady;
    public IReadOnlyList<ChoiceItem<int>> PeopleChoices { get; }
    public IReadOnlyList<ChoiceItem<EngineId>> EngineChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFull))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool SpeakersOnly { get; set; }

    public bool IsFull
    {
        get => !SpeakersOnly;
        set => SpeakersOnly = !value;
    }

    [ObservableProperty] public partial bool SeparateSpeakers { get; set; }
    [ObservableProperty] public partial ChoiceItem<int> SelectedPeople { get; set; }
    [ObservableProperty] public partial ChoiceItem<EngineId> SelectedEngine { get; set; }

    public bool CanStart => HasAudio && !IsBusy && (!SpeakersOnly || CanSpeakersOnly);

    [RelayCommand]
    void Start()
    {
        if (!CanStart)
        {
            return;
        }
        context.Processing.Reprocess(meeting.Id, new ReprocessOptions(SpeakersOnly, SpeakersOnly || SeparateSpeakers, SelectedPeople.Value, SelectedEngine.Value));
        Close();
    }

    [RelayCommand]
    void Cancel() => Close();
}
