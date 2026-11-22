using Slovo.Core;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Engines.Recognition;

namespace Slovo.Engines;

/// <summary>Creates an engine for an id; the language mode would pick Apple Dictation's locale on a Mac.</summary>
public delegate ITranscriptionEngine EngineFactory(EngineId id, LanguageMode languageMode);

/// <summary>
/// Chooses engines from Recognition settings, preloads them and releases idle ones (the
/// port of EngineManager.swift).
///
/// Loads run one at a time, and a preload for new settings first releases the engine
/// the old settings used, so two big models are never held or loaded for nothing.
/// Thread-safe; <see cref="LoadStateChanged"/> is raised through the main-thread dispatcher.
/// </summary>
public sealed class EngineManager : IEngineProvider, IDisposable
{
    static readonly Logger Logger = Log.For("Engines");

    readonly AppSettings settings;
    readonly IModelManager models;
    readonly EngineFactory factory;
    readonly IUptime uptime;
    readonly IMainThread mainThread;
    readonly Lock gate = new();

    sealed record Load(int Number, LanguageMode Variant, Task<ITranscriptionEngine> Task);

    readonly record struct Loaded(ITranscriptionEngine Engine, LanguageMode Variant);

    readonly Dictionary<EngineId, EngineLoadState> loadStates = [];
    readonly Dictionary<EngineId, Loaded> loaded = [];
    readonly Dictionary<EngineId, Load> loads = [];
    /// <summary>
    /// Loads whose task has not finished, including ones <see cref="Unload"/> dropped:
    /// they still read the model files.
    /// </summary>
    readonly Dictionary<EngineId, Load> running = [];
    /// <summary>
    /// Engines <see cref="WaitForLoadAsync"/> is waiting for, with how many callers wait.
    /// Their files are about to be deleted, so no new load of them may start.
    /// </summary>
    readonly Dictionary<EngineId, int> draining = [];
    readonly Dictionary<EngineId, double> lastUsed = [];
    /// <summary>Completes when the most recently started load has finished.</summary>
    Task lastLoad = Task.CompletedTask;
    int loadCount;
    CancellationTokenSource? idleTimer;
    /// <summary>The dictation engine the last preload chose; null until the app first preloads.</summary>
    EngineId? preloadedId;
    bool disposed;

    /// <param name="factory">Creates engines: <see cref="Factory"/> with the models folder in the app, a fake in tests.</param>
    /// <param name="mainThread">Where <see cref="LoadStateChanged"/> is raised; the immediate dispatcher by default.</param>
    public EngineManager(AppSettings settings, IModelManager models, EngineFactory factory,
        IUptime? uptime = null, IMainThread? mainThread = null)
    {
        this.settings = settings;
        this.models = models;
        this.factory = factory;
        this.uptime = uptime ?? SystemUptime.Shared;
        this.mainThread = mainThread ?? ImmediateMainThread.Shared;
        settings.Store.Changed += SettingChanged;
    }

    public event Action<EngineId>? LoadStateChanged;

    public EngineId EngineFor(EnginePurpose purpose) => settings.EngineFor(purpose);

    public EngineLoadState LoadState(EngineId id)
    {
        lock (gate)
        {
            return loadStates.GetValueOrDefault(id, EngineLoadState.Unloaded);
        }
    }

    /// <summary>A snapshot of every engine's load state that is not <c>Unloaded</c>.</summary>
    public IReadOnlyDictionary<EngineId, EngineLoadState> LoadStates
    {
        get
        {
            lock (gate)
            {
                return loadStates.Where(pair => pair.Value.Kind != EngineLoadStateKind.Unloaded)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
            }
        }
    }

    /// <summary>
    /// Returns the loaded engine, loading it first. Throws <see cref="EngineUnavailableException"/>
    /// when the model is not downloaded, damaged or still downloading, and whatever the
    /// load throws (a localized <see cref="SlovoException"/>). The token cancels this
    /// caller's wait, not a load other callers may share.
    /// </summary>
    public async Task<ITranscriptionEngine> EngineAsync(EngineId id, CancellationToken cancellation = default)
    {
        Task<ITranscriptionEngine> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Its model files are about to be deleted; a load now would read files that
            // disappear halfway, which aborts the app.
            if (draining.ContainsKey(id))
            {
                throw new EngineUnavailableException(id, models.Status(id));
            }
            var variant = Variant(id);
            if (loaded.TryGetValue(id, out var current))
            {
                if (current.Variant == variant)
                {
                    MarkUsedLocked(id);
                    return current.Engine;
                }
                UnloadLocked(id);
            }
            if (loads.TryGetValue(id, out var load) && load.Variant == variant)
            {
                task = load.Task;
            }
            else
            {
                CheckModel(id);
                task = StartLoadLocked(id, variant);
            }
        }
        return await task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the dictation engine for the current language mode in the background and
    /// releases the one previous settings chose.
    /// </summary>
    public void PreloadForCurrentSettings()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            var wanted = EngineFor(EnginePurpose.Dictation(settings.LanguageMode));
            if (preloadedId is { } previous && previous != wanted)
            {
                UnloadLocked(previous);
            }
            preloadedId = wanted;
            var variant = Variant(wanted);
            if (loaded.TryGetValue(wanted, out var current))
            {
                if (current.Variant == variant)
                {
                    return;
                }
                UnloadLocked(wanted);
            }
            if (loads.TryGetValue(wanted, out var load) && load.Variant == variant)
            {
                return;
            }
            if (draining.ContainsKey(wanted))
            {
                Logger.Info($"Not preloading {wanted.RawValue()}: its model is being deleted");
                return;
            }
            try
            {
                CheckModel(wanted);
            }
            catch (EngineUnavailableException error)
            {
                Logger.Info($"Not preloading {wanted.RawValue()}: {error.Message}");
                return;
            }
            var task = StartLoadLocked(wanted, variant);
            // A failed preload is reported through the load state; nobody awaits it.
            _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public void MarkUsed(EngineId id)
    {
        lock (gate)
        {
            MarkUsedLocked(id);
        }
    }

    /// <summary>
    /// Releases an engine. Callers still holding it keep using it (a released sherpa-onnx
    /// engine loads its model again when used); its memory is freed once they are done.
    /// </summary>
    public void Unload(EngineId id)
    {
        lock (gate)
        {
            UnloadLocked(id);
        }
    }

    public void UnloadAll()
    {
        lock (gate)
        {
            foreach (var id in loaded.Keys.Union(loads.Keys).ToList())
            {
                UnloadLocked(id);
            }
        }
    }

    /// <summary>
    /// Completes once no load of the engine is running, including loads <see cref="Unload"/>
    /// dropped. A load cannot be interrupted, and ONNX Runtime aborts the whole process
    /// when its model files disappear mid-load, so deleting files waits for this.
    ///
    /// While it waits, <see cref="EngineAsync"/> refuses the engine and preloading skips
    /// it, so the wait cannot be extended by a new load that would read the files again.
    /// It completes synchronously when nothing is loading, so the caller can remove the
    /// files before anything else starts a load.
    /// </summary>
    public Task WaitForLoadAsync(EngineId id)
    {
        lock (gate)
        {
            if (!running.ContainsKey(id))
            {
                return Task.CompletedTask;
            }
            draining[id] = draining.GetValueOrDefault(id) + 1;
        }
        return Drain(id);
    }

    async Task Drain(EngineId id)
    {
        try
        {
            // Loads run one after another, so once the newest load of the engine is done,
            // every earlier one is done too.
            while (true)
            {
                Load? load;
                lock (gate)
                {
                    if (!running.TryGetValue(id, out load))
                    {
                        return;
                    }
                }
                try
                {
                    await load.Task.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The load's outcome is its own business; only its end matters here.
                }
                lock (gate)
                {
                    if (running.TryGetValue(id, out var current) && current.Number == load.Number)
                    {
                        running.Remove(id);
                    }
                }
            }
        }
        finally
        {
            lock (gate)
            {
                if (draining.TryGetValue(id, out var count) && count > 1)
                {
                    draining[id] = count - 1;
                }
                else
                {
                    draining.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Deletes an engine's model files the safe way (the port of ServiceWiring.deleteModel):
    /// releases the engine, waits until no load reads the files, then deletes them. A
    /// failure is reported in <see cref="IModelManager.LastError"/>.
    /// </summary>
    public async Task DeleteModelAsync(EngineId id)
    {
        Unload(id);
        await WaitForLoadAsync(id).ConfigureAwait(false);
        models.Delete(id);
    }

    /// <summary>The engines Slovo ships with models under <paramref name="modelsRoot"/>. Nothing downloads while loading.</summary>
    public static EngineFactory Factory(string modelsRoot) => (id, _) => id switch
    {
        EngineId.GigaamRnnt or EngineId.GigaamCtc or EngineId.Parakeet => new SherpaTranscriptionEngine(id, modelsRoot),
        _ => throw new SlovoException(CoreStrings.Get("engines.apple.unavailable")),
    };

    public void Dispose()
    {
        settings.Store.Changed -= SettingChanged;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            foreach (var id in loaded.Keys.Union(loads.Keys).ToList())
            {
                UnloadLocked(id);
            }
            disposed = true;
            idleTimer?.Cancel();
            idleTimer?.Dispose();
            idleTimer = null;
        }
    }

    // Loading.

    /// <summary>Engines are shared across language modes; only Apple Dictation's locale followed the mode.</summary>
    static LanguageMode Variant(EngineId id) => LanguageMode.Mixed;

    void CheckModel(EngineId id)
    {
        var status = models.Status(id);
        if (status.Kind == ModelStatusKind.Unknown)
        {
            models.Refresh();
            status = models.Status(id);
        }
        if (!status.IsUsable)
        {
            throw new EngineUnavailableException(id, status);
        }
    }

    Task<ITranscriptionEngine> StartLoadLocked(EngineId id, LanguageMode variant)
    {
        var number = ++loadCount;
        var previous = lastLoad;
        SetStateLocked(id, EngineLoadState.Loading);
        Logger.Info($"Loading {id.RawValue()}");
        var task = Task.Run(async () =>
        {
            // One load at a time: two big models loading together double peak memory and
            // compete for the same cores.
            await previous.ConfigureAwait(false);
            ITranscriptionEngine? engine = null;
            try
            {
                engine = factory(id, variant);
                await engine.PrepareAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                LoadFinished(id, number, variant, null, error);
                (engine as IDisposable)?.Dispose();
                throw;
            }
            LoadFinished(id, number, variant, engine, null);
            return engine;
        });
        var load = new Load(number, variant, task);
        loads[id] = load;
        running[id] = load;
        lastLoad = task.ContinueWith(static _ => { }, TaskScheduler.Default);
        return task;
    }

    void LoadFinished(EngineId id, int number, LanguageMode variant, ITranscriptionEngine? engine, Exception? error)
    {
        lock (gate)
        {
            if (running.TryGetValue(id, out var run) && run.Number == number)
            {
                running.Remove(id);
            }
            // Unloaded or replaced while loading: drop the result. A caller that waited for
            // this load still gets the engine; releasing it frees the model once unused.
            if (!loads.TryGetValue(id, out var load) || load.Number != number)
            {
                (engine as IDisposable)?.Dispose();
                return;
            }
            loads.Remove(id);
            if (engine is not null)
            {
                loaded[id] = new Loaded(engine, variant);
                lastUsed[id] = uptime.Now;
                SetStateLocked(id, EngineLoadState.Ready);
                Logger.Info($"Loaded {id.RawValue()}");
                ScheduleIdleUnloadLocked();
                return;
            }
            if (error is OperationCanceledException)
            {
                SetStateLocked(id, EngineLoadState.Unloaded);
                return;
            }
            SetStateLocked(id, EngineLoadState.Failed(error?.Message ?? ""));
            Logger.Error($"Loading {id.RawValue()} failed: {error?.Message}");
        }
    }

    void MarkUsedLocked(EngineId id)
    {
        if (!loaded.ContainsKey(id))
        {
            return;
        }
        lastUsed[id] = uptime.Now;
        ScheduleIdleUnloadLocked();
    }

    void UnloadLocked(EngineId id)
    {
        var wasActive = loaded.ContainsKey(id) || loads.ContainsKey(id);
        // A load in progress finishes anyway (it cannot be interrupted) and its result is dropped.
        loads.Remove(id);
        if (loaded.Remove(id, out var engine))
        {
            (engine.Engine as IDisposable)?.Dispose();
        }
        lastUsed.Remove(id);
        if (loadStates.TryGetValue(id, out var state) && state.Kind != EngineLoadStateKind.Unloaded)
        {
            SetStateLocked(id, EngineLoadState.Unloaded);
        }
        if (wasActive)
        {
            Logger.Info($"Released {id.RawValue()}");
        }
        ScheduleIdleUnloadLocked();
    }

    void SetStateLocked(EngineId id, EngineLoadState state)
    {
        if (loadStates.TryGetValue(id, out var current) && current == state)
        {
            return;
        }
        loadStates[id] = state;
        mainThread.Post(() => LoadStateChanged?.Invoke(id));
    }

    // Settings and idle unload.

    void SettingChanged(string key)
    {
        if (key is SettingsKey.LanguageMode or SettingsKey.RussianEngine or SettingsKey.EnglishEngine
            or SettingsKey.MeetingEngine or SettingsKey.IdleUnloadMinutes)
        {
            SettingsDidChange();
        }
    }

    internal void SettingsDidChange()
    {
        lock (gate)
        {
            ScheduleIdleUnloadLocked();
            // Before the first preload the app is still starting and preloads on its own.
            if (preloadedId is not { } preloaded || disposed)
            {
                return;
            }
            var wanted = EngineFor(EnginePurpose.Dictation(settings.LanguageMode));
            var variant = Variant(wanted);
            LanguageMode? current = loaded.TryGetValue(wanted, out var engine) ? engine.Variant
                : loads.TryGetValue(wanted, out var load) ? load.Variant : null;
            if (wanted == preloaded && (current is null || current == variant))
            {
                return;
            }
        }
        PreloadForCurrentSettings();
    }

    /// <summary>The idle timeout in seconds; null for "never".</summary>
    double? IdleTimeoutSeconds()
    {
        EngineIdleTimeout timeout;
        try
        {
            timeout = settings.IdleUnload;
        }
        catch (InvalidOperationException error)
        {
            // A settings value that cannot be read must not take dictation down with it.
            Logger.Error("Could not read the idle unload setting", error);
            timeout = EngineIdleTimeout.Minutes15;
        }
        return timeout == EngineIdleTimeout.Never ? null : (int)timeout * 60.0;
    }

    /// <summary>Releases engines unused for longer than the idle setting; returns their ids.</summary>
    internal IReadOnlyList<EngineId> UnloadIdleEngines()
    {
        lock (gate)
        {
            if (IdleTimeoutSeconds() is not { } timeout)
            {
                return [];
            }
            var now = uptime.Now;
            var expired = loaded.Keys.Where(id => now - lastUsed.GetValueOrDefault(id, now) >= timeout)
                .OrderBy(id => id.RawValue(), StringComparer.Ordinal).ToList();
            foreach (var id in expired)
            {
                UnloadLocked(id);
            }
            return expired;
        }
    }

    void ScheduleIdleUnloadLocked()
    {
        idleTimer?.Cancel();
        idleTimer?.Dispose();
        idleTimer = null;
        if (disposed || IdleTimeoutSeconds() is not { } timeout || loaded.Count == 0)
        {
            return;
        }
        var now = uptime.Now;
        var deadline = loaded.Keys.Min(id => lastUsed.GetValueOrDefault(id, now) + timeout);
        var delay = TimeSpan.FromSeconds(Math.Max(1, deadline - now));
        var timer = new CancellationTokenSource();
        idleTimer = timer;
        var token = timer.Token;
        _ = Task.Delay(delay, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            UnloadIdleEngines();
            lock (gate)
            {
                if (idleTimer == timer)
                {
                    ScheduleIdleUnloadLocked();
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
