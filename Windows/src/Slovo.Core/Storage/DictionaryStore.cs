using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Core.Text;

namespace Slovo.Core.Storage;

/// <summary><c>dictionary.json</c>: terms and replacements (port of the macOS <c>DictionaryStore</c>). Use it from the UI thread.</summary>
public sealed class DictionaryStore : Observable, IDictionaryStore
{
    static readonly Logger Logger = Log.For("Dictionary");

    readonly StoreFile<List<DictionaryItem>> storage;
    readonly TimeProvider time;
    IReadOnlyList<DictionaryItem> items;

    public DictionaryStore(AppPaths paths, IMainThread mainThread, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        storage = new StoreFile<List<DictionaryItem>>(paths.DictionaryFile, mainThread, this.time);
        LoadWarning = storage.LoadWarning;
        items = storage.InitialValue ?? [];
    }

    /// <summary>Set when <c>dictionary.json</c> was damaged and moved aside at launch; localized.</summary>
    public string? LoadWarning { get; }

    /// <summary>In the order they were added. A new list after every change.</summary>
    public IReadOnlyList<DictionaryItem> Items => items;

    public IReadOnlyList<DictionaryItem> Terms => items.Where(item => item.Kind == DictionaryItemKind.Term).ToList();

    public IReadOnlyList<DictionaryItem> Replacements => items.Where(item => item.Kind == DictionaryItemKind.Replacement).ToList();

    /// <summary>Appends an item; an item with the same id is replaced instead.</summary>
    public void Add(DictionaryItem item)
    {
        var list = items.ToList();
        var index = list.FindIndex(existing => existing.Id == item.Id);
        if (index >= 0)
        {
            list[index] = item;
        }
        else
        {
            list.Add(item);
        }
        Commit(list);
    }

    public void Update(DictionaryItem item)
    {
        var list = items.ToList();
        var index = list.FindIndex(existing => existing.Id == item.Id);
        if (index < 0)
        {
            return;
        }
        list[index] = item;
        Commit(list);
    }

    public void Delete(Guid id)
    {
        if (!items.Any(item => item.Id == id))
        {
            return;
        }
        Commit(items.Where(item => item.Id != id).ToList());
    }

    /// <summary>Writes pending changes now. Throws SlovoException when the file cannot be written.</summary>
    public void Flush() => storage.Flush();

    /// <summary>
    /// Adds the bundled vocabulary once, on first launch; <c>settings.DictionarySeeded</c>
    /// remembers it so deleted terms do not come back. A Dictionary seeded by an older version
    /// later gets only the terms added to the vocabulary since (<c>DictionarySeed.Additions</c>),
    /// once each.
    ///
    /// Terms already in the dictionary (ignoring case) are skipped. The flags are set only after
    /// the file was written, so a failed save seeds again next launch.
    /// </summary>
    public void SeedIfNeeded(AppSettings settings, IReadOnlyList<string>? terms = null)
    {
        terms ??= DictionarySeed.BundledTerms();
        var latest = DictionarySeed.LatestVersion;
        IEnumerable<string> newTerms;
        if (!settings.DictionarySeeded)
        {
            if (terms.Count == 0)
            {
                return;
            }
            newTerms = terms;
        }
        else if (settings.DictionarySeedVersion < latest)
        {
            var seededVersion = settings.DictionarySeedVersion;
            newTerms = DictionarySeed.Additions.Where(addition => addition.Version > seededVersion).SelectMany(addition => addition.Terms);
        }
        else
        {
            return;
        }
        var known = new HashSet<string>(Terms.Select(item => MatchKey(item.Text)), StringComparer.Ordinal);
        var now = time.GetUtcNow();
        var list = items.ToList();
        var added = false;
        foreach (var term in newTerms)
        {
            var trimmed = term.Trim();
            var key = MatchKey(trimmed);
            if (key.Length == 0 || !known.Add(key))
            {
                continue;
            }
            list.Add(DictionaryItem.Term(trimmed, now));
            added = true;
        }
        if (added || !settings.DictionarySeeded)
        {
            Commit(list);
        }
        try
        {
            Flush();
            settings.DictionarySeeded = true;
            settings.DictionarySeedVersion = latest;
        }
        catch (SlovoException error)
        {
            Logger.Error($"Could not save the seeded dictionary: {error.Message}");
        }
    }

    static string MatchKey(string text) => SwiftUnicode.Lowercased(SwiftString.Canonical(text.Trim()));

    void Commit(List<DictionaryItem> list)
    {
        items = list;
        OnPropertyChanged(nameof(Items));
        storage.ScheduleSave(list);
    }
}

/// <summary>The starter vocabulary: <c>Sources/SlovoCore/Resources/vocabulary.txt</c>, embedded in Slovo.Core.</summary>
public static class DictionarySeed
{
    /// <summary>
    /// Terms added to <c>vocabulary.txt</c> after the first release, by seed version, so a
    /// Dictionary seeded earlier gets them too. Version 1 is the original vocabulary.
    /// </summary>
    public static readonly IReadOnlyList<(int Version, IReadOnlyList<string> Terms)> Additions =
    [
        (2, ["GitHub"]),
        (3, ["iOS", "AppKit", "ChatGPT", "OpenAI", "DeepSeek"]),
    ];

    public static int LatestVersion => Additions.Count == 0 ? 1 : Additions.Max(addition => addition.Version);

    public const string ResourceName = "Slovo.Resources.vocabulary.txt";

    /// <summary>Non-empty lines that are not <c>#</c> comments.</summary>
    public static IReadOnlyList<string> BundledTerms()
    {
        using var stream = typeof(DictionarySeed).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            Log.For("Dictionary").Error("vocabulary.txt is missing from the resources");
            return [];
        }
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return TermsFromVocabulary(reader.ReadToEnd());
    }

    /// <summary>
    /// One term per line; blank lines and lines starting with <c>#</c> are skipped, and a term
    /// repeated with different case keeps its first spelling.
    /// </summary>
    public static IReadOnlyList<string> TermsFromVocabulary(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var line in text.Split(['\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029]))
        {
            var term = TextElements.TrimSpaces(line);
            if (term.Length == 0 || term.StartsWith('#') || !seen.Add(SwiftUnicode.Lowercased(SwiftString.Canonical(term))))
            {
                continue;
            }
            result.Add(term);
        }
        return result;
    }
}
