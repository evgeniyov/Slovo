using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Storage;

/// <summary><c>snippets.json</c> (port of the macOS <c>SnippetStore</c>). Use it from the UI thread.</summary>
public sealed class SnippetStore : Observable, ISnippetStore
{
    readonly StoreFile<List<Snippet>> storage;
    readonly TimeProvider time;
    IReadOnlyList<Snippet> snippets;

    public SnippetStore(AppPaths paths, IMainThread mainThread, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        storage = new StoreFile<List<Snippet>>(paths.SnippetsFile, mainThread, this.time);
        LoadWarning = storage.LoadWarning;
        snippets = storage.InitialValue ?? [];
    }

    /// <summary>Set when <c>snippets.json</c> was damaged and moved aside at launch; localized.</summary>
    public string? LoadWarning { get; }

    /// <summary>In the order they were added. A new list after every change.</summary>
    public IReadOnlyList<Snippet> Snippets => snippets;

    /// <summary>Appends a snippet; a snippet with the same id is replaced instead.</summary>
    public void Add(Snippet snippet)
    {
        var list = snippets.ToList();
        var index = list.FindIndex(existing => existing.Id == snippet.Id);
        if (index >= 0)
        {
            list[index] = snippet;
        }
        else
        {
            list.Add(snippet);
        }
        Commit(list);
    }

    /// <summary>Replaces the snippet with the same id and stamps UpdatedAt when its text changed.</summary>
    public void Update(Snippet snippet)
    {
        var list = snippets.ToList();
        var index = list.FindIndex(existing => existing.Id == snippet.Id);
        if (index < 0)
        {
            return;
        }
        var old = list[index];
        var updated = snippet;
        if ((old.Trigger != snippet.Trigger || old.Expansion != snippet.Expansion) && snippet.UpdatedAt == old.UpdatedAt)
        {
            updated = snippet with { UpdatedAt = time.GetUtcNow() };
        }
        list[index] = updated;
        Commit(list);
    }

    public void Delete(Guid id)
    {
        if (!snippets.Any(snippet => snippet.Id == id))
        {
            return;
        }
        Commit(snippets.Where(snippet => snippet.Id != id).ToList());
    }

    /// <summary>Writes pending changes now. Throws SlovoException when the file cannot be written.</summary>
    public void Flush() => storage.Flush();

    void Commit(List<Snippet> list)
    {
        snippets = list;
        OnPropertyChanged(nameof(Snippets));
        storage.ScheduleSave(list);
    }
}
