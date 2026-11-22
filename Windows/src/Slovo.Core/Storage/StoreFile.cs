using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Storage;

/// <summary>
/// Coalesces rapid changes into one save after a short delay (port of the macOS
/// <c>SaveDebouncer</c>). Call it from the UI thread; the action runs there too. The delay
/// comes from a <see cref="TimeProvider"/>, so tests advance a fake clock instead of waiting.
/// </summary>
public sealed class SaveDebouncer : IDisposable
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(500);

    readonly Action action;
    readonly IMainThread mainThread;
    readonly TimeProvider time;
    ITimer? timer;
    int generation;

    public SaveDebouncer(Action action, IMainThread mainThread, TimeProvider? time = null, TimeSpan? delay = null)
    {
        this.action = action;
        this.mainThread = mainThread;
        this.time = time ?? TimeProvider.System;
        Delay = delay ?? DefaultDelay;
    }

    public TimeSpan Delay { get; }

    /// <summary>Whether an action is waiting for its delay.</summary>
    public bool IsPending => timer is not null;

    /// <summary>Schedules the action, replacing any pending one.</summary>
    public void Schedule()
    {
        timer?.Dispose();
        var scheduled = ++generation;
        timer = time.CreateTimer(_ => mainThread.Post(() =>
        {
            // A newer Schedule, a Flush or a Cancel since then wins.
            if (scheduled == generation && timer is not null)
            {
                Fire();
            }
        }), null, Delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Runs a pending action now.</summary>
    public void Flush()
    {
        if (timer is not null)
        {
            Fire();
        }
    }

    /// <summary>Drops a pending action without running it.</summary>
    public void Cancel()
    {
        timer?.Dispose();
        timer = null;
        generation++;
    }

    public void Dispose() => Cancel();

    void Fire()
    {
        Cancel();
        action();
    }
}

/// <summary>
/// The file behind one store (port of the macOS <c>StoreFile</c>): loads it once, debounces
/// saves of the latest value by 500 ms and writes anything pending on <see cref="Flush"/>,
/// which the app calls before it exits. Use it from the UI thread.
/// </summary>
public sealed class StoreFile<T> where T : class
{
    static readonly Logger Logger = Log.For("Storage");

    readonly SaveDebouncer debouncer;
    T? pending;

    public StoreFile(string path, IMainThread mainThread, TimeProvider? time = null, TimeSpan? delay = null)
    {
        File = new JsonFile<T>(path);
        var name = System.IO.Path.GetFileName(path);
        var load = File.Load();
        switch (load.Kind)
        {
            case JsonFileLoadKind.Loaded:
                InitialValue = load.Value;
                break;
            case JsonFileLoadKind.Missing:
                break;
            case JsonFileLoadKind.Corrupt when load.MovedTo is not null:
                LoadWarning = CoreStrings.Format("storage.error.corruptMovedAside", name);
                break;
            case JsonFileLoadKind.Partial when load.MovedTo is not null:
                InitialValue = load.Value;
                LoadWarning = CoreStrings.Format("storage.error.entriesSkipped", name, System.IO.Path.GetFileName(load.MovedTo));
                break;
            case JsonFileLoadKind.Partial:
                // The readable entries are shown, but the file they came from is the only
                // copy of the rest, so it is not replaced.
                InitialValue = load.Value;
                LoadWarning = CoreStrings.Format("storage.error.notSetAside", name);
                SavingDisabled = true;
                break;
            default:
                // Damaged and not set aside, or unreadable right now: saving would replace
                // the only copy, so changes stay in memory.
                LoadWarning = CoreStrings.Format("storage.error.notSetAside", name);
                SavingDisabled = true;
                break;
        }
        if (SavingDisabled)
        {
            Logger.Error($"Not saving {name}: its unreadable original could not be set aside");
        }
        debouncer = new SaveDebouncer(SavePendingLogged, mainThread, time, delay);
    }

    public JsonFile<T> File { get; }

    /// <summary>The decoded file, null when it was missing or damaged.</summary>
    public T? InitialValue { get; }

    /// <summary>A localized note that the file was damaged and moved aside, for the interface.</summary>
    public string? LoadWarning { get; }

    /// <summary>
    /// True when the file could not be read and could not be set aside either: saving would
    /// replace the only copy, so the store keeps its changes in memory only.
    /// </summary>
    public bool SavingDisabled { get; }

    /// <summary>Whether a change is waiting to be written.</summary>
    public bool HasPendingChanges => pending is not null;

    /// <summary>Remembers the latest value and saves it after the debounce.</summary>
    public void ScheduleSave(T value)
    {
        pending = value;
        if (!SavingDisabled)
        {
            debouncer.Schedule();
        }
    }

    /// <summary>
    /// Writes the pending value now. Throws SlovoException without writing when saving is
    /// disabled, so the unreadable original is never replaced, or when the write failed (the
    /// value stays pending).
    /// </summary>
    public void Flush()
    {
        debouncer.Cancel();
        if (pending is not { } value)
        {
            return;
        }
        if (SavingDisabled)
        {
            throw new SlovoException(CoreStrings.Format("storage.error.savingDisabled", System.IO.Path.GetFileName(File.Path)));
        }
        File.Save(value);
        pending = null;
    }

    void SavePendingLogged()
    {
        try
        {
            Flush();
        }
        catch (SlovoException error)
        {
            Logger.Error(error.Message);
        }
    }
}
