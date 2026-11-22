using System.ComponentModel;
using Slovo.Core.Audio;
using Slovo.Core.Dictation;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Dictation;

// Test doubles for every service the dictation module talks to (the Swift DictationFakes).
// They are used on the test's main thread only, so none of them locks.

/// <summary>Suspends callers until it is opened.</summary>
sealed class Gate
{
    readonly TaskCompletionSource open = new();

    public int Arrived { get; private set; }

    public Task WaitAsync()
    {
        Arrived += 1;
        return open.Task;
    }

    public void Open() => open.TrySetResult();
}

sealed class FakeEngine(string id, string text) : ITranscriptionEngine
{
    public string Id => id;
    public string Text { get; set; } = text;
    public Exception? Error { get; set; }
    public Gate? Gate { get; set; }
    /// <summary>Samples in each file the engine was asked to transcribe; -1 when unreadable.</summary>
    public List<int> SampleCounts { get; } = [];

    public Task PrepareAsync(CancellationToken cancellation = default) => Task.CompletedTask;

    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation = default) => Task.FromResult(Text);

    public async Task<string> TranscribeFileAsync(string path, CancellationToken cancellation = default)
    {
        int count;
        try
        {
            count = WavFile.Read(path).Samples.Length;
        }
        catch (SlovoException)
        {
            count = -1;
        }
        SampleCounts.Add(count);
        if (Gate is { } gate)
        {
            await gate.WaitAsync();
        }
        if (Error is { } error)
        {
            throw error;
        }
        return Text;
    }
}

sealed class FakeRecorder : IAudioRecorder
{
    public bool IsRecording { get; set; }
    public RecordedAudio NextAudio { get; set; } = RecordedAudio.Empty;
    public Exception? StartError { get; set; }
    public List<string?> StartedDeviceUids { get; } = [];
    public int StopCount { get; private set; }
    public int CancelCount { get; private set; }
    public string? LastDeviceName { get; set; }
    public event Action<float>? LevelChanged;
    public bool HasLevelListeners => LevelChanged is not null;

    public void RaiseLevel(float value) => LevelChanged?.Invoke(value);

    public void Start(string? deviceUid)
    {
        if (StartError is { } error)
        {
            throw error;
        }
        StartedDeviceUids.Add(deviceUid);
        IsRecording = true;
    }

    public RecordedAudio Stop()
    {
        StopCount += 1;
        IsRecording = false;
        return NextAudio;
    }

    public void Cancel()
    {
        CancelCount += 1;
        IsRecording = false;
    }
}

class FakeHotkeys : IHotkeyMonitor
{
    public bool IsRunning { get; set; }
    public bool IsKeyDown { get; set; }
    public bool AllowsStart { get; set; } = true;
    public List<HotkeyConfiguration> StartedConfigurations { get; } = [];
    public List<HotkeyConfiguration> UpdatedConfigurations { get; } = [];
    public List<DictationMode?> SyncedModes { get; } = [];
    public int StopCount { get; private set; }
    public event Action<DictationTrigger>? Triggered;
    public event Action<bool>? KeyStateChanged { add { } remove { } }
    public bool HasTriggerListeners => Triggered is not null;

    public void Fire(DictationTrigger trigger) => Triggered?.Invoke(trigger);

    public bool Start(HotkeyConfiguration configuration)
    {
        StartedConfigurations.Add(configuration);
        IsRunning = AllowsStart;
        return AllowsStart;
    }

    public void Update(HotkeyConfiguration configuration) => UpdatedConfigurations.Add(configuration);

    public void SyncRecording(DictationMode? mode) => SyncedModes.Add(mode);

    public void Stop()
    {
        StopCount += 1;
        IsRunning = false;
    }
}

/// <summary>A key listener that also records what it is told about processing.</summary>
sealed class ProcessingHotkeys : FakeHotkeys, IProcessingAwareHotkeys
{
    public ProcessingHotkeys() => IsRunning = true;

    public List<bool> Processing { get; } = [];

    public void SyncProcessing(bool isProcessing) => Processing.Add(isProcessing);
}

sealed class FakeInserter : ITextInserter
{
    public InsertionResult Result { get; set; } = InsertionResult.PastedResult;
    public List<string> Inserted { get; } = [];
    public List<string> Copied { get; } = [];

    public Task<InsertionResult> InsertAsync(string text)
    {
        Inserted.Add(text);
        return Task.FromResult(text.Length == 0 ? InsertionResult.EmptyResult : Result);
    }

    public void CopyToClipboard(string text) => Copied.Add(text);
}

sealed class FakeForegroundApp : IForegroundApp
{
    public AppIdentity? App { get; set; } = new("slack.exe", "Slack");

    public AppIdentity? CurrentApp() => App;
}

sealed class FakePermissions : IPermissions
{
    /// <summary>Windows reports Accessibility and Input Monitoring as always granted.</summary>
    public Dictionary<PermissionKind, PermissionStatus> Statuses { get; } = new()
    {
        [PermissionKind.Microphone] = PermissionStatus.Granted,
        [PermissionKind.Accessibility] = PermissionStatus.Granted,
        [PermissionKind.InputMonitoring] = PermissionStatus.Granted,
    };
    public bool IsSecureInputActive { get; set; }

    public PermissionStatus Status(PermissionKind kind) => Statuses.GetValueOrDefault(kind, PermissionStatus.Unknown);

    public Task<PermissionStatus> RequestAsync(PermissionKind kind) => Task.FromResult(Status(kind));

    public void OpenSystemSettings(PermissionKind kind) { }
}

class FakeEngines : IEngineProvider
{
    public EngineId DictationEngine { get; set; } = EngineId.GigaamRnnt;
    public Dictionary<EngineId, FakeEngine> Engines { get; } = [];
    public Dictionary<EngineId, ModelStatus> Unavailable { get; } = [];
    public Dictionary<EngineId, EngineLoadState> LoadStates { get; } = [];
    public List<EngineId> Requested { get; } = [];
    public List<EngineId> MarkedUsed { get; } = [];
    /// <summary>Preloads, unloads and waits, in order; shared with <see cref="FakeModels"/> by the wiring tests.</summary>
    public List<string> Events { get; set; } = [];
    public event Action<EngineId>? LoadStateChanged { add { } remove { } }

    public EngineId EngineFor(EnginePurpose purpose) => purpose.IsMeetings ? EngineId.Parakeet : DictationEngine;

    public EngineLoadState LoadState(EngineId id) => LoadStates.GetValueOrDefault(id, EngineLoadState.Unloaded);

    public Task<ITranscriptionEngine> EngineAsync(EngineId id, CancellationToken cancellation = default)
    {
        Requested.Add(id);
        if (Unavailable.TryGetValue(id, out var status))
        {
            return Task.FromException<ITranscriptionEngine>(new EngineUnavailableException(id, status));
        }
        return Engines.TryGetValue(id, out var engine)
            ? Task.FromResult<ITranscriptionEngine>(engine)
            : Task.FromException<ITranscriptionEngine>(new SlovoException($"No fake engine for {id.RawValue()}"));
    }

    public void PreloadForCurrentSettings() => Events.Add("preload");

    public void MarkUsed(EngineId id) => MarkedUsed.Add(id);

    public void Unload(EngineId id) => Events.Add("unload:" + id.RawValue());

    public Task WaitForLoadAsync(EngineId id)
    {
        Events.Add("wait:" + id.RawValue());
        return Task.CompletedTask;
    }
}

/// <summary>Engines that tell language modes apart, with an optional gate on handing one out.</summary>
sealed class LanguageEngines : FakeEngines, ILanguageEngineProvider
{
    public Gate? LoadGate { get; set; }
    public List<LanguageMode> Languages { get; } = [];

    public async Task<ITranscriptionEngine> EngineAsync(EngineId id, LanguageMode languageMode, CancellationToken cancellation = default)
    {
        Languages.Add(languageMode);
        if (LoadGate is { } gate)
        {
            await gate.WaitAsync();
        }
        return await EngineAsync(id, cancellation);
    }
}

sealed class FakePipeline : ITextProcessor
{
    public List<TextPipelineContext> Contexts { get; } = [];
    public Func<string, TextPipelineResult> Transform { get; set; } = raw => new TextPipelineResult(raw);

    public TextPipelineResult Process(string raw, TextPipelineContext context)
    {
        Contexts.Add(context);
        return Transform(raw);
    }
}

sealed class FakeHistory(AppPaths paths) : IHistoryStore
{
    public List<HistoryEntry> Items { get; set; } = [];
    public IReadOnlyList<HistoryEntry> Entries => Items;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public void Add(HistoryEntry entry) => Items.Insert(0, entry);

    public void Update(HistoryEntry entry)
    {
        var index = Items.FindIndex(e => e.Id == entry.Id);
        if (index >= 0)
        {
            Items[index] = entry;
        }
    }

    public void Delete(Guid id) => Items.RemoveAll(e => e.Id == id);

    public void DeleteAll() => Items.Clear();

    public string? AudioPath(HistoryEntry entry) =>
        entry.AudioFileName is { } name && File.Exists(paths.RecordingPath(name)) ? paths.RecordingPath(name) : null;

    public void RemoveAudio(Guid id) { }

    public void Flush() { }
}

sealed class FakeDictionary : IDictionaryStore
{
    public List<DictionaryItem> List { get; set; } = [];
    public IReadOnlyList<DictionaryItem> Items => List;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public void Add(DictionaryItem item) => List.Add(item);

    public void Update(DictionaryItem item) { }

    public void Delete(Guid id) { }

    public void Flush() { }
}

sealed class FakeSnippets : ISnippetStore
{
    public List<Snippet> List { get; set; } = [];
    public IReadOnlyList<Snippet> Snippets => List;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public void Add(Snippet snippet) => List.Add(snippet);

    public void Update(Snippet snippet) { }

    public void Delete(Guid id) { }

    public void Flush() { }
}

sealed class FakeNotes : INoteStore
{
    public List<Note> List { get; } = [];
    public IReadOnlyList<Note> Notes => List;
    public List<(string Text, Guid NoteId)> Appended { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public Note Create(string text)
    {
        var note = new Note { Text = text };
        List.Insert(0, note);
        return note;
    }

    public void Update(Note note) { }

    public void Append(string text, Guid noteId) => Appended.Add((text, noteId));

    public void Delete(Guid id) { }

    public void Flush() { }
}

sealed class FakeCategories : IAppCategoryStore
{
    /// <summary>By identifier (the executable name on Windows).</summary>
    public Dictionary<string, AppCategory> Mapping { get; } = [];
    /// <summary>By the name the app reports, for apps with no fixed identifier.</summary>
    public Dictionary<string, AppCategory> NameMapping { get; } = [];
    public IReadOnlyList<AppCategoryAssignment> Assignments => [];
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public AppCategory CategoryFor(string? bundleId) =>
        bundleId is not null && Mapping.TryGetValue(bundleId, out var category) ? category : AppCategory.Other;

    public AppCategory CategoryFor(AppIdentity app) =>
        Mapping.TryGetValue(app.BundleId, out var category) ? category
            : NameMapping.TryGetValue(app.Name, out var named) ? named
            : AppCategory.Other;

    public void SetCategory(AppCategory category, AppIdentity app) => Mapping[app.BundleId] = category;

    public void Flush() { }
}

sealed class FakeMeetings : IMeetingActivity
{
    public bool IsMeetingRecording { get; set; }
}

sealed class FakeSounds : ISoundPlayer
{
    public List<SoundCue> Played { get; } = [];

    public void Play(SoundCue cue) => Played.Add(cue);
}

sealed class FakeModels : IModelManager
{
    public Dictionary<EngineId, ModelStatus> Statuses { get; set; } = [];
    public Exception? DeleteError { get; set; }
    public List<string> Events { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaiseChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));

    public ModelStatus Status(EngineId id) => Statuses.GetValueOrDefault(id, ModelStatus.Unknown);

    public void Refresh() { }

    public void StartDownload(EngineId id) { }

    public void CancelDownload(EngineId id) { }

    public void Delete(EngineId id)
    {
        Events.Add("delete:" + id.RawValue());
        if (DeleteError is { } error)
        {
            throw error;
        }
        Statuses[id] = ModelStatus.NotDownloaded(0);
    }

    public string? LastError(EngineId id) => null;
}

sealed class FakeShortcuts : IGlobalShortcuts
{
    public bool Refuses { get; set; }
    public List<HotkeyShortcut> Registrations { get; } = [];
    /// <summary>The shortcut registered now; null after a refusal or once its registration is disposed.</summary>
    public HotkeyShortcut? Shortcut => active?.Shortcut;
    Registration? active;

    public IDisposable? Register(HotkeyShortcut shortcut, Action action)
    {
        Registrations.Add(shortcut);
        if (Refuses)
        {
            return null;
        }
        var registration = new Registration(this, shortcut, action);
        active = registration;
        return registration;
    }

    public void Fire() => active?.Action();

    sealed class Registration(FakeShortcuts owner, HotkeyShortcut shortcut, Action action) : IDisposable
    {
        public HotkeyShortcut Shortcut => shortcut;
        public Action Action => action;

        public void Dispose()
        {
            if (owner.active == this)
            {
                owner.active = null;
            }
        }
    }
}

static class TestAudio
{
    /// <summary>A tone loud enough to count as speech.</summary>
    public static RecordedAudio Speech(double seconds, float amplitude = 0.2f)
    {
        var count = (int)(seconds * 16_000);
        return new RecordedAudio(Enumerable.Range(0, count).Select(i => (float)Math.Sin(i * 2 * Math.PI * 220 / 16_000) * amplitude).ToArray());
    }

    /// <summary>Polls a condition on the test's main thread until it holds or the timeout passes.</summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, double timeoutSeconds = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(5);
        }
        return true;
    }
}
