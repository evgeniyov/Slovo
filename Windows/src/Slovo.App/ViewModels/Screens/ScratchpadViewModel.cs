using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.ViewModels.Screens;

/// <summary>Hands-free dictation into a note instead of the foreground app.</summary>
public interface INoteDictation
{
    /// <summary>The note a running dictation writes into, if any.</summary>
    Guid? DictatingInto { get; }
    event Action? Changed;
    void Start(Guid noteId);
    void Finish();
}

public sealed record NoteRowViewModel(Note Note)
{
    public string Title => Note.Title.Length > 0 ? Note.Title : Loc.Instance.Get("scratchpad.untitled");
    public string Edited => Formatting.RelativeDate(Note.UpdatedAt);
}

/// <summary>The Scratchpad: notes that save automatically, and dictation into a note.</summary>
public sealed partial class ScratchpadViewModel : ViewModelBase
{
    readonly INoteStore store;
    readonly INoteDictation dictation;
    readonly ISheetHost sheets;
    readonly Action<string> copy;
    bool applyingStore;

    /// <param name="copy">Puts text on the ordinary clipboard (the macOS copyToPasteboard).</param>
    public ScratchpadViewModel(INoteStore store, INoteDictation dictation, ISheetHost sheets, Action<string> copy)
    {
        this.store = store;
        this.dictation = dictation;
        this.sheets = sheets;
        this.copy = copy;
        store.PropertyChanged += OnStoreChanged;
        dictation.Changed += () => OnPropertyChanged(nameof(IsDictating));
        Refresh();
        SelectedId = store.Notes.FirstOrDefault()?.Id;
    }

    [ObservableProperty] public partial IReadOnlyList<NoteRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedRow), nameof(IsDictating))]
    public partial Guid? SelectedId { get; set; }

    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>Shows a check mark on the Copy button for a moment after a copy.</summary>
    public CopiedFlag Copied { get; } = new();

    public bool IsEmpty => Rows.Count == 0;
    public bool HasSelection => SelectedId is not null;
    public bool IsDictating => SelectedId is { } id && dictation.DictatingInto == id;

    public NoteRowViewModel? SelectedRow
    {
        get => Rows.FirstOrDefault(r => r.Note.Id == SelectedId);
        set => SelectedId = value?.Note.Id;
    }

    partial void OnSelectedIdChanged(Guid? value)
    {
        applyingStore = true;
        Text = store.Notes.FirstOrDefault(n => n.Id == value)?.Text ?? "";
        applyingStore = false;
    }

    /// <summary>Every edit goes to the store, which debounces the save.</summary>
    partial void OnTextChanged(string value)
    {
        if (applyingStore || SelectedId is not { } id || store.Notes.FirstOrDefault(n => n.Id == id) is not { } note || note.Text == value)
        {
            return;
        }
        store.Update(note with { Text = value, UpdatedAt = DateTimeOffset.UtcNow });
    }

    void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        Refresh();
        // Dictated text lands in the store; show it unless the user is mid-edit of the same text.
        if (SelectedId is { } id && store.Notes.FirstOrDefault(n => n.Id == id) is { } note && note.Text != Text)
        {
            applyingStore = true;
            Text = note.Text;
            applyingStore = false;
        }
    }

    void Refresh()
    {
        Rows = store.Notes.Select(n => new NoteRowViewModel(n)).ToList();
        if (SelectedId is { } id && Rows.All(r => r.Note.Id != id))
        {
            SelectedId = Rows.FirstOrDefault()?.Note.Id;
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectedRow));
    }

    [RelayCommand]
    void NewNote()
    {
        var note = store.Create("");
        SelectedId = note.Id;
    }

    /// <summary>Copies the note as it stands in the editor.</summary>
    [RelayCommand]
    void Copy()
    {
        copy(Text);
        Copied.Flash();
    }

    [RelayCommand]
    async Task DeleteAsync()
    {
        if (SelectedId is not { } id)
        {
            return;
        }
        var loc = Loc.Instance;
        if (await sheets.ConfirmAsync(loc.Get("scratchpad.delete.title"), loc.Get("scratchpad.delete.message"), loc.Get("common.delete")))
        {
            if (dictation.DictatingInto == id)
            {
                dictation.Finish();
            }
            store.Delete(id);
        }
    }

    [RelayCommand]
    void ToggleDictation()
    {
        if (IsDictating)
        {
            dictation.Finish();
            return;
        }
        if (SelectedId is not { } id)
        {
            id = store.Create("").Id;
            SelectedId = id;
        }
        dictation.Start(id);
    }
}
