using System.ComponentModel;
using Slovo.App.Onboarding;
using Slovo.App.ViewModels.Screens;
using Slovo.Core.Dictation;
using Slovo.Core.Model;

namespace Slovo.App.Composition;

/// <summary>Scratchpad's dictation into a note, through the one dictation controller.</summary>
sealed class NoteDictation : INoteDictation
{
    readonly DictationController dictation;

    public NoteDictation(DictationController dictation)
    {
        this.dictation = dictation;
        dictation.PropertyChanged += OnChanged;
    }

    public Guid? DictatingInto => dictation.State is DictationState.Recording { Info.Target: DictationTarget.Note note } ? note.Id : null;
    public event Action? Changed;

    public void Start(Guid noteId) => dictation.StartHandsFree(new DictationTarget.Note(noteId));
    public void Finish() => dictation.Finish();

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DictationController.State) or null or "")
        {
            Changed?.Invoke();
        }
    }
}

/// <summary>Onboarding's practice card: dictations go to the card instead of the focused app while it is on screen.</summary>
sealed class PracticeDictation : IPracticeDictation
{
    readonly DictationController dictation;

    public PracticeDictation(DictationController dictation)
    {
        this.dictation = dictation;
        dictation.PropertyChanged += OnChanged;
    }

    public bool IsRecording => dictation.State is DictationState.Recording { Info.Target: DictationTarget.Practice };
    public event Action<string>? Transcribed;
    public event Action? StateChanged;

    public void SetActive(bool active) =>
        dictation.KeyTarget = active ? DictationTarget.PracticeTarget : DictationTarget.Foreground;

    public void StartHandsFree() => dictation.StartHandsFree(DictationTarget.PracticeTarget);
    public void Finish() => dictation.Finish();

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DictationController.PracticeResult) when dictation.PracticeResult is { } text:
                Transcribed?.Invoke(text);
                break;
            case nameof(DictationController.State) or null or "":
                StateChanged?.Invoke();
                break;
        }
    }
}
