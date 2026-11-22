using System.ComponentModel;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Tests;

/// <summary>The port of DictationEngineManagerTests.swift and FixCoreModelDeletionTests.swift.</summary>
public sealed class EngineManagerTests : IDisposable
{
    /// <summary>Records how many engines prepare at the same time.</summary>
    sealed class LoadingEngine(string id, Factory factory) : ITranscriptionEngine, IDisposable
    {
        public string Id => id;
        public int PrepareCount;
        public bool Disposed;

        public async Task PrepareAsync(CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref PrepareCount);
            factory.Events.Add($"prepare started {id}");
            var current = Interlocked.Increment(ref factory.Current);
            lock (factory)
            {
                factory.Maximum = Math.Max(factory.Maximum, current);
            }
            if (factory.Gate is { } gate)
            {
                await gate.WaitAsync();
            }
            Interlocked.Decrement(ref factory.Current);
            factory.Events.Add($"prepare finished {id}");
            if (factory.PrepareError is { } error)
            {
                throw error;
            }
        }

        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation = default) => Task.FromResult("");
        public Task<string> TranscribeFileAsync(string path, CancellationToken cancellation = default) => Task.FromResult("");
        public void Dispose() => Disposed = true;
    }

    sealed class Factory
    {
        public int Current;
        public int Maximum;
        public Gate? Gate;
        public Exception? PrepareError;
        public List<(EngineId Id, LanguageMode Mode)> Made { get; } = [];
        public List<LoadingEngine> Engines { get; } = [];
        public SynchronizedList Events { get; } = new();

        public ITranscriptionEngine Make(EngineId id, LanguageMode mode)
        {
            var engine = new LoadingEngine(id.RawValue(), this);
            lock (Made)
            {
                Made.Add((id, mode));
                Engines.Add(engine);
            }
            Events.Add("make");
            return engine;
        }
    }

    sealed class SynchronizedList
    {
        readonly List<string> items = [];

        public void Add(string item)
        {
            lock (items)
            {
                items.Add(item);
            }
        }

        public List<string> Snapshot()
        {
            lock (items)
            {
                return [.. items];
            }
        }
    }

    /// <summary>Model statuses set by the test (DictationFakes.Models); deletion is recorded.</summary>
    sealed class Models(SynchronizedList events) : IModelManager
    {
        public Dictionary<EngineId, ModelStatus> Statuses { get; set; } = [];
        public Dictionary<EngineId, ModelStatus>? StatusesAfterRefresh { get; set; }
        public int RefreshCount { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public ModelStatus Status(EngineId id) => Statuses.GetValueOrDefault(id, ModelStatus.Unknown);

        public void Refresh()
        {
            RefreshCount++;
            if (StatusesAfterRefresh is { } after)
            {
                Statuses = after;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status"));
        }

        public void StartDownload(EngineId id)
        {
        }

        public void CancelDownload(EngineId id)
        {
        }

        public void Delete(EngineId id)
        {
            events.Add("delete");
            Statuses[id] = ModelStatus.NotDownloaded(0);
        }

        public string? LastError(EngineId id) => null;
    }

    readonly FakeUptime uptime = new();
    readonly Factory factory = new();
    readonly Models models;
    AppSettings settings = new(SettingsStore.InMemory());
    EngineManager manager;

    public EngineManagerTests()
    {
        models = new Models(factory.Events);
        manager = new EngineManager(settings, models, factory.Make, uptime);
    }

    public void Dispose() => manager.Dispose();

    /// <summary>
    /// Starts over with an idle timeout read from settings.json. (An integer set in memory
    /// cannot be read back by SettingsStore.GetInt today; see the report.)
    /// </summary>
    void UseIdleTimeout(EngineIdleTimeout timeout)
    {
        manager.Dispose();
        var json = "{\"" + SettingsKey.IdleUnloadMinutes + "\": " + (int)timeout + "}";
        settings = new AppSettings(SettingsStore.InMemory(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject()));
        manager = new EngineManager(settings, models, factory.Make, uptime);
    }

    static ModelStatus Ready => ModelStatus.Ready(1);

    [Fact]
    public void EngineIdsFollowRecognitionSettings()
    {
        Assert.Equal(EngineId.GigaamRnnt, manager.EngineFor(EnginePurpose.Dictation(LanguageMode.Mixed)));
        Assert.Equal(EngineId.GigaamRnnt, manager.EngineFor(EnginePurpose.Dictation(LanguageMode.Russian)));
        Assert.Equal(EngineId.Parakeet, manager.EngineFor(EnginePurpose.Dictation(LanguageMode.English)));
        Assert.Equal(EngineId.GigaamCtc, manager.EngineFor(EnginePurpose.Meetings));

        settings.RussianEngine = EngineId.GigaamCtc;
        settings.EnglishEngine = EngineId.GigaamRnnt;
        settings.MeetingEngine = EngineId.Parakeet;
        Assert.Equal(EngineId.GigaamCtc, manager.EngineFor(EnginePurpose.Dictation(LanguageMode.Russian)));
        Assert.Equal(EngineId.GigaamRnnt, manager.EngineFor(EnginePurpose.Dictation(LanguageMode.English)));
        Assert.Equal(EngineId.Parakeet, manager.EngineFor(EnginePurpose.Meetings));
    }

    [Fact]
    public async Task AnEngineLoadsOnceAndIsReused()
    {
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        var changed = new List<EngineId>();
        manager.LoadStateChanged += id =>
        {
            lock (changed)
            {
                changed.Add(id);
            }
        };
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));

        var first = await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        var second = await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Single(factory.Made);
        Assert.Equal(1, factory.Engines[0].PrepareCount);
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.GigaamRnnt));
        Assert.Equal(EngineLoadState.Ready, manager.LoadStates[EngineId.GigaamRnnt]);
        Assert.Equal([EngineId.GigaamRnnt, EngineId.GigaamRnnt], changed);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneLoad()
    {
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        var gate = new Gate();
        factory.Gate = gate;

        var first = manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        var second = manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        Assert.Equal(EngineLoadState.Loading, manager.LoadState(EngineId.GigaamRnnt));

        gate.Open();
        Assert.Same(await first, await second);
        Assert.Single(factory.Made);
    }

    [Fact]
    public async Task UnusableModelsAreNeverLoaded()
    {
        var cases = new (EngineId Id, ModelStatus Status)[]
        {
            (EngineId.Parakeet, ModelStatus.NotDownloaded(10)),
            (EngineId.GigaamRnnt, ModelStatus.Damaged("truncated")),
            (EngineId.GigaamCtc, ModelStatus.Downloading(0.5)),
        };
        foreach (var (id, status) in cases)
        {
            models.Statuses[id] = status;
            var error = await Assert.ThrowsAsync<EngineUnavailableException>(() => manager.EngineAsync(id, TestContext.Current.CancellationToken));
            Assert.Equal(id, error.EngineId);
            Assert.Equal(status, error.Status);
            Assert.Contains(id.DisplayName(), error.Message);
        }
        Assert.Empty(factory.Made);
    }

    [Fact]
    public async Task AnUnknownStatusRefreshesTheModelsFirst()
    {
        models.StatusesAfterRefresh = new() { [EngineId.GigaamCtc] = Ready };
        await manager.EngineAsync(EngineId.GigaamCtc, TestContext.Current.CancellationToken);
        Assert.Equal(1, models.RefreshCount);
        Assert.Equal([EngineId.GigaamCtc], factory.Made.Select(made => made.Id));
    }

    [Fact]
    public async Task AFailedLoadIsReportedAndRetried()
    {
        models.Statuses[EngineId.Parakeet] = Ready;
        factory.PrepareError = new SlovoException("the model was refused");
        var error = await Assert.ThrowsAsync<SlovoException>(() => manager.EngineAsync(EngineId.Parakeet, TestContext.Current.CancellationToken));
        Assert.Equal("the model was refused", error.Message);
        Assert.Equal(EngineLoadState.Failed("the model was refused"), manager.LoadState(EngineId.Parakeet));
        Assert.True(factory.Engines[0].Disposed, "A failed engine releases what it loaded");

        factory.PrepareError = null;
        await manager.EngineAsync(EngineId.Parakeet, TestContext.Current.CancellationToken);
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.Parakeet));
        Assert.Equal(2, factory.Made.Count);
    }

    [Fact]
    public async Task IdleEnginesAreReleased()
    {
        UseIdleTimeout(EngineIdleTimeout.Minutes5);
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);

        uptime.Advance(200);
        manager.MarkUsed(EngineId.GigaamRnnt);
        uptime.Advance(200);
        Assert.Empty(manager.UnloadIdleEngines());

        uptime.Advance(101);
        Assert.Equal([EngineId.GigaamRnnt], manager.UnloadIdleEngines());
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));
        Assert.True(factory.Engines[0].Disposed, "Releasing an engine frees its model");

        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        Assert.Equal(2, factory.Made.Count);
    }

    [Fact]
    public async Task NeverUnloadKeepsEngines()
    {
        UseIdleTimeout(EngineIdleTimeout.Never);
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        uptime.Advance(1_000_000);
        Assert.Empty(manager.UnloadIdleEngines());
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.GigaamRnnt));
    }

    [Fact]
    public async Task TheIdleTimerReleasesEnginesByItself()
    {
        // The shortest timeout with the uptime already past it: the timer fires after a second.
        UseIdleTimeout(EngineIdleTimeout.Minutes5);
        models.Statuses[EngineId.GigaamCtc] = Ready;
        await manager.EngineAsync(EngineId.GigaamCtc, TestContext.Current.CancellationToken);
        uptime.Advance(301);
        manager.SettingsDidChange();
        await TestSupport.WaitUntil(() => manager.LoadState(EngineId.GigaamCtc) == EngineLoadState.Unloaded, timeoutSeconds: 5);
    }

    [Fact]
    public async Task LoadsRunOneAtATime()
    {
        models.Statuses = new() { [EngineId.GigaamRnnt] = Ready, [EngineId.Parakeet] = Ready };
        var gate = new Gate();
        factory.Gate = gate;

        var russian = manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        var english = manager.EngineAsync(EngineId.Parakeet, TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, gate.Arrived);

        gate.Open();
        await russian;
        await english;
        Assert.Equal(1, factory.Maximum);
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.GigaamRnnt));
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.Parakeet));
    }

    [Fact]
    public async Task PreloadLoadsTheCurrentDictationEngine()
    {
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        var gate = new Gate();
        factory.Gate = gate;
        manager.PreloadForCurrentSettings();
        Assert.Equal(EngineLoadState.Loading, manager.LoadState(EngineId.GigaamRnnt));
        gate.Open();

        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        Assert.Equal([EngineId.GigaamRnnt], factory.Made.Select(made => made.Id));
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.GigaamRnnt));
    }

    [Fact]
    public void PreloadSkipsModelsThatAreNotDownloaded()
    {
        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.NotDownloaded(1);
        manager.PreloadForCurrentSettings();
        Assert.Empty(factory.Made);
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));
    }

    [Fact]
    public async Task ASettingsChangeSwapsThePreloadedEngine()
    {
        models.Statuses = new() { [EngineId.GigaamRnnt] = Ready, [EngineId.GigaamCtc] = Ready };
        manager.PreloadForCurrentSettings();
        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        var gate = new Gate();
        factory.Gate = gate;

        // The settings store reports the change; the manager follows it.
        settings.RussianEngine = EngineId.GigaamCtc;
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));
        Assert.Equal(EngineLoadState.Loading, manager.LoadState(EngineId.GigaamCtc));
        gate.Open();

        await manager.EngineAsync(EngineId.GigaamCtc, TestContext.Current.CancellationToken);
        Assert.Equal([EngineId.GigaamRnnt, EngineId.GigaamCtc], factory.Made.Select(made => made.Id));
    }

    [Fact]
    public void SettingsChangesBeforeTheFirstPreloadLoadNothing()
    {
        models.Statuses[EngineId.GigaamCtc] = Ready;
        settings.RussianEngine = EngineId.GigaamCtc;
        Assert.Empty(factory.Made);
    }

    [Fact]
    public async Task UnloadDuringALoadDropsTheResult()
    {
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        var gate = new Gate();
        factory.Gate = gate;
        var pending = manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);

        manager.Unload(EngineId.GigaamRnnt);
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));
        gate.Open();
        await pending;
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));
        Assert.True(factory.Engines[0].Disposed, "A dropped load frees its model");

        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        Assert.Equal(2, factory.Made.Count);
    }

    [Fact]
    public async Task UnloadAllReleasesEverything()
    {
        models.Statuses = new() { [EngineId.GigaamRnnt] = Ready, [EngineId.Parakeet] = Ready };
        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        await manager.EngineAsync(EngineId.Parakeet, TestContext.Current.CancellationToken);
        manager.UnloadAll();
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.Parakeet));
        Assert.Empty(manager.LoadStates);
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheSharedLoadRunning()
    {
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        var gate = new Gate();
        factory.Gate = gate;
        using var cancellation = new CancellationTokenSource();
        var cancelled = manager.EngineAsync(EngineId.GigaamRnnt, cancellation.Token);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.Open();
        await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);
        Assert.Single(factory.Made);
    }

    // Deleting a model while its engine loads (FixCoreModelDeletionTests.swift). ONNX
    // Runtime aborts the whole process when a model file disappears in the middle of a
    // load, so deletion waits for the load and no new load may start before the files are gone.

    [Fact]
    public async Task DeletionWaitsForTheLoadAndRefusesNewLoadsMeanwhile()
    {
        models.Statuses[EngineId.GigaamRnnt] = Ready;
        var gate = new Gate();
        factory.Gate = gate;

        manager.PreloadForCurrentSettings();
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        Assert.Equal(EngineLoadState.Loading, manager.LoadState(EngineId.GigaamRnnt));

        var deletion = manager.DeleteModelAsync(EngineId.GigaamRnnt);
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));
        Assert.Equal(["make", "prepare started gigaam-v3-rnnt"], factory.Events.Snapshot());

        var refused = await Assert.ThrowsAsync<EngineUnavailableException>(() => manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken));
        Assert.Equal(EngineId.GigaamRnnt, refused.EngineId);
        manager.PreloadForCurrentSettings();
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));

        gate.Open();
        await deletion;
        Assert.Equal(["make", "prepare started gigaam-v3-rnnt", "prepare finished gigaam-v3-rnnt", "delete"], factory.Events.Snapshot());
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamRnnt));

        var missing = await Assert.ThrowsAsync<EngineUnavailableException>(() => manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken));
        Assert.Equal(ModelStatus.NotDownloaded(0), missing.Status);
        Assert.Single(factory.Made);
    }

    [Fact]
    public async Task DeletionWithoutALoadRemovesTheFilesAtOnce()
    {
        models.Statuses[EngineId.GigaamCtc] = Ready;
        await manager.EngineAsync(EngineId.GigaamCtc, TestContext.Current.CancellationToken);
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.GigaamCtc));

        Assert.True(manager.WaitForLoadAsync(EngineId.GigaamCtc).IsCompleted);
        await manager.DeleteModelAsync(EngineId.GigaamCtc);
        Assert.Equal("delete", factory.Events.Snapshot()[^1]);
        Assert.Equal(EngineLoadState.Unloaded, manager.LoadState(EngineId.GigaamCtc));

        // Nothing stays blocked after a deletion: a model downloaded again loads normally.
        models.Statuses[EngineId.GigaamCtc] = Ready;
        await manager.EngineAsync(EngineId.GigaamCtc, TestContext.Current.CancellationToken);
        Assert.Equal(EngineLoadState.Ready, manager.LoadState(EngineId.GigaamCtc));
    }

    [Fact]
    public async Task TheBundledEnginesNeverDownloadWhenFilesAreMissing()
    {
        using var temporary = new TemporaryFolder();
        var make = EngineManager.Factory(temporary.Path);

        var parakeet = make(EngineId.Parakeet, LanguageMode.Mixed);
        Assert.Equal("parakeet-v3", parakeet.Id);
        var parakeetError = await Assert.ThrowsAsync<SlovoException>(() => parakeet.PrepareAsync(TestContext.Current.CancellationToken));
        Assert.Contains(temporary.Path, parakeetError.Message);

        var gigaam = make(EngineId.GigaamCtc, LanguageMode.Mixed);
        var gigaamError = await Assert.ThrowsAsync<SlovoException>(() => gigaam.PrepareAsync(TestContext.Current.CancellationToken));
        Assert.Contains("model.int8.onnx", gigaamError.Message);
        Assert.Contains("tokens.txt", gigaamError.Message);

        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
        Assert.Throws<SlovoException>(() => make(EngineId.AppleDictation, LanguageMode.English));
    }
}
