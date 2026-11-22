using Slovo.Core.Logging;

namespace Slovo.Core.Inbox;

/// <summary>
/// Says when something in the inbox folder was added, renamed or rewritten, so a dropped clip
/// is looked at once it has had time to settle instead of at the next poll. A wrapper around
/// <see cref="FileSystemWatcher"/> that never throws: a watcher that cannot start (a folder on
/// a share that does not report changes, too many watchers) leaves the poll to do the work.
///
/// <see cref="Changed"/> is raised on a thread-pool thread, possibly many times for one copy;
/// the importer coalesces it.
/// </summary>
public sealed class InboxFolderWatcher : IDisposable
{
    static readonly Logger Logger = Log.For("Inbox");

    readonly FileSystemWatcher? watcher;

    public InboxFolderWatcher(string folder, Action changed)
    {
        Changed = changed;
        FileSystemWatcher? created = null;
        try
        {
            Directory.CreateDirectory(folder);
            created = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            created.Created += OnEvent;
            created.Changed += OnEvent;
            created.Renamed += OnEvent;
            // An overflowed buffer lost events; a look at the whole folder finds what they said.
            created.Error += (_, error) =>
            {
                Logger.Warning($"Watching the inbox lost events: {error.GetException().Message}");
                Changed();
            };
            created.EnableRaisingEvents = true;
            watcher = created;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or PlatformNotSupportedException)
        {
            created?.Dispose();
            Logger.Warning($"The inbox is polled only: its folder cannot be watched ({error.Message})");
        }
    }

    /// <summary>Whether change notifications arrive; otherwise only the poll finds new clips.</summary>
    public bool IsWatching => watcher is not null;

    Action Changed { get; }

    public void Dispose()
    {
        if (watcher is null)
        {
            return;
        }
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    void OnEvent(object sender, FileSystemEventArgs change) => Changed();
}
