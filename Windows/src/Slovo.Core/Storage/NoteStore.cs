using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Text;

namespace Slovo.Core.Storage;

/// <summary>
/// <c>notes.json</c>: Scratchpad notes, saved automatically as they change (port of the macOS
/// <c>NoteStore</c>). Use it from the UI thread.
/// </summary>
public sealed class NoteStore : Observable, INoteStore
{
    readonly StoreFile<List<Note>> storage;
    readonly TimeProvider time;
    IReadOnlyList<Note> notes;

    public NoteStore(AppPaths paths, IMainThread mainThread, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        storage = new StoreFile<List<Note>>(paths.NotesFile, mainThread, this.time);
        LoadWarning = storage.LoadWarning;
        notes = RecentFirst(storage.InitialValue ?? []);
    }

    /// <summary>Set when <c>notes.json</c> was damaged and moved aside at launch; localized.</summary>
    public string? LoadWarning { get; }

    /// <summary>Most recently updated first. A new list after every change.</summary>
    public IReadOnlyList<Note> Notes => notes;

    public Note Create(string text)
    {
        var now = time.GetUtcNow();
        var note = new Note { Text = text, CreatedAt = now, UpdatedAt = now };
        var list = notes.ToList();
        list.Insert(0, note);
        Commit(list);
        return note;
    }

    /// <summary>Replaces the note with the same id; a changed text stamps UpdatedAt and moves the note to the top.</summary>
    public void Update(Note note)
    {
        var list = notes.ToList();
        var index = list.FindIndex(existing => existing.Id == note.Id);
        if (index < 0)
        {
            return;
        }
        var old = list[index];
        var updated = note;
        if (old.Text != note.Text && note.UpdatedAt == old.UpdatedAt)
        {
            updated = note with { UpdatedAt = time.GetUtcNow() };
        }
        if (updated == old)
        {
            return;
        }
        list[index] = updated;
        Commit(RecentFirst(list));
    }

    /// <summary>
    /// Appends dictated text, separated by a space unless the note is empty or already ends
    /// with whitespace or a new line.
    /// </summary>
    public void Append(string text, Guid noteId)
    {
        var addition = text.Trim();
        var list = notes.ToList();
        var index = list.FindIndex(existing => existing.Id == noteId);
        if (addition.Length == 0 || index < 0)
        {
            return;
        }
        list[index] = list[index] with { Text = Joined(list[index].Text, addition), UpdatedAt = time.GetUtcNow() };
        Commit(RecentFirst(list));
    }

    public void Delete(Guid id)
    {
        if (!notes.Any(note => note.Id == id))
        {
            return;
        }
        Commit(notes.Where(note => note.Id != id).ToList());
    }

    /// <summary>Writes pending changes now. Throws SlovoException when the file cannot be written.</summary>
    public void Flush() => storage.Flush();

    internal static string Joined(string existing, string addition)
    {
        var characters = TextElements.Characters(existing);
        if (characters.Length == 0)
        {
            return addition;
        }
        return SwiftCharacter.IsWhitespace(characters[^1]) ? existing + addition : existing + " " + addition;
    }

    static List<Note> RecentFirst(IEnumerable<Note> list) => list.OrderByDescending(note => note.UpdatedAt).ToList();

    void Commit(List<Note> list)
    {
        notes = list;
        OnPropertyChanged(nameof(Notes));
        storage.ScheduleSave(list);
    }
}
