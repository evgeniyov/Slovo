using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Text;

namespace Slovo.Core.Storage;

/// <summary>Dictations of one calendar day, for the Home list.</summary>
/// <param name="Day">Start of the day.</param>
/// <param name="Entries">Newest first.</param>
public sealed record HistoryDayGroup(DateTimeOffset Day, IReadOnlyList<HistoryEntry> Entries);

/// <summary>
/// <c>history.json</c> plus the WAV files history entries point to (port of the macOS
/// <c>HistoryStore</c>). Use it from the UI thread; changes are saved 500 ms after the last
/// one, and <see cref="Flush"/> writes them at once (call it before the app exits).
/// </summary>
public sealed class HistoryStore : Observable, IHistoryStore
{
    static readonly Logger Logger = Log.For("History");
    static readonly string[] IgnoredFolderFiles = [".DS_Store", "Thumbs.db", "desktop.ini"];

    readonly StoreFile<List<HistoryEntry>> storage;
    IReadOnlyList<HistoryEntry> entries;

    /// <summary>Loads the file at once; a damaged file is moved aside and the store starts empty.</summary>
    public HistoryStore(AppPaths paths, IMainThread mainThread, TimeProvider? time = null)
    {
        Paths = paths;
        storage = new StoreFile<List<HistoryEntry>>(paths.HistoryFile, mainThread, time);
        LoadWarning = storage.LoadWarning;
        entries = NewestFirst(storage.InitialValue ?? []);
    }

    public AppPaths Paths { get; }

    /// <summary>Set when <c>history.json</c> was damaged and moved aside at launch; localized.</summary>
    public string? LoadWarning { get; }

    /// <summary>Newest first. A new list after every change.</summary>
    public IReadOnlyList<HistoryEntry> Entries => entries;

    /// <summary>Adds an entry in date order; an entry with the same id is replaced.</summary>
    public void Add(HistoryEntry entry)
    {
        var list = entries.Where(e => e.Id != entry.Id).ToList();
        var index = list.FindIndex(e => e.Date <= entry.Date);
        list.Insert(index < 0 ? list.Count : index, entry);
        Commit(list);
    }

    public void Update(HistoryEntry entry)
    {
        var index = IndexOf(entry.Id);
        if (index < 0)
        {
            return;
        }
        var list = entries.ToList();
        var dateChanged = list[index].Date != entry.Date;
        list[index] = entry;
        Commit(dateChanged ? NewestFirst(list) : list);
    }

    /// <summary>Removes the entry and its audio file.</summary>
    public void Delete(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return;
        }
        if (entries[index].AudioFileName is { } path)
        {
            RemoveRecording(path);
        }
        var list = entries.ToList();
        list.RemoveAt(index);
        Commit(list);
    }

    /// <summary>Removes every entry and everything under <c>Recordings/</c>, and saves at once.</summary>
    public void DeleteAll()
    {
        var folder = Paths.RecordingsFolder;
        if (Directory.Exists(folder))
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(folder))
            {
                try
                {
                    if (Directory.Exists(item))
                    {
                        Directory.Delete(item, recursive: true);
                    }
                    else
                    {
                        File.Delete(item);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Logger.Error($"Could not remove {Path.GetFileName(item)}", error);
                }
            }
        }
        Commit([]);
        try
        {
            Flush();
        }
        catch (SlovoException error)
        {
            Logger.Error(error.Message);
        }
    }

    public string? AudioPath(HistoryEntry entry) =>
        entry.AudioFileName is { } relative && RecordingPath(relative) is { } path && File.Exists(path) ? path : null;

    /// <summary>Deletes an entry's audio file and clears its AudioFileName.</summary>
    public void RemoveAudio(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0 || entries[index].AudioFileName is not { } path)
        {
            return;
        }
        RemoveRecording(path);
        var list = entries.ToList();
        list[index] = list[index] with { AudioFileName = null };
        Commit(list);
    }

    /// <summary>Writes pending changes now. Throws SlovoException when the file cannot be written.</summary>
    public void Flush() => storage.Flush();

    /// <summary>Entries whose text contains the query, ignoring case and ё/е.</summary>
    public IReadOnlyList<HistoryEntry> Search(string query)
    {
        var needle = SearchKey(query.Trim());
        if (needle.Length == 0)
        {
            return entries;
        }
        return entries.Where(entry => SearchKey(entry.Text).Contains(needle, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Groups entries by day, newest day first.</summary>
    public static IReadOnlyList<HistoryDayGroup> GroupedByDay(IEnumerable<HistoryEntry> entries, DayCalendar? calendar = null)
    {
        calendar ??= DayCalendar.Current;
        return entries
            .GroupBy(entry => calendar.LocalDate(entry.Date))
            .OrderByDescending(group => group.Key)
            .Select(group => new HistoryDayGroup(calendar.StartOfDay(group.Key), NewestFirst(group)))
            .ToList();
    }

    /// <summary>Lowercased with ё folded into е, as Russian text is often typed without ё.</summary>
    internal static string SearchKey(string text) =>
        SwiftUnicode.Lowercased(SwiftString.Canonical(text)).Replace('ё', 'е');

    static List<HistoryEntry> NewestFirst(IEnumerable<HistoryEntry> list) => list.OrderByDescending(entry => entry.Date).ToList();

    int IndexOf(Guid id)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Id == id)
            {
                return index;
            }
        }
        return -1;
    }

    void Commit(List<HistoryEntry> list)
    {
        entries = list;
        OnPropertyChanged(nameof(Entries));
        storage.ScheduleSave(list);
    }

    /// <summary>
    /// The file for a stored relative path, refusing paths that leave <c>Recordings/</c>, so a
    /// hand-edited <c>history.json</c> can never make Slovo delete other files.
    /// </summary>
    string? RecordingPath(string relativePath)
    {
        var folder = Path.GetFullPath(Paths.RecordingsFolder);
        string path;
        try
        {
            path = Path.GetFullPath(Paths.RecordingPath(relativePath));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Logger.Error("Ignoring an unusable audio path");
            return null;
        }
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Logger.Error($"Ignoring an audio path outside Recordings: {relativePath}");
            return null;
        }
        return path;
    }

    void RemoveRecording(string relativePath)
    {
        if (RecordingPath(relativePath) is not { } path)
        {
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Could not remove {Path.GetFileName(path)}", error);
        }
        RemoveFolderIfEmpty(Path.GetDirectoryName(path), Paths.RecordingsFolder);
    }

    /// <summary>Drops a day folder once its last recording is gone (system clutter such as .DS_Store does not count).</summary>
    internal static void RemoveFolderIfEmpty(string? folder, string recordingsFolder)
    {
        if (folder is null || !Directory.Exists(folder)
            || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                             Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingsFolder)), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            if (Directory.EnumerateFileSystemEntries(folder).All(item => IgnoredFolderFiles.Contains(Path.GetFileName(item))))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Could not remove the folder {Path.GetFileName(folder)}", error);
        }
    }
}
