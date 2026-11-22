using System.ComponentModel;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.Tests.Fakes;

sealed class FakeDevices : IAudioDevices
{
    public IReadOnlyList<AudioInputDevice> InputDevices() =>
        [new("{0.0.1}.{mic}", "Микрофон (Realtek(R) Audio)"), new("{0.0.1}.{usb}", "Shure MV7")];
    public event Action? DevicesChanged { add { } remove { } }
}

sealed class FakeHotkeys : IHotkeyMonitor
{
    public bool IsRunning { get; set; } = true;
    public bool IsKeyDown { get; set; }
    public event Action<DictationTrigger>? Triggered { add { } remove { } }
    public event Action<bool>? KeyStateChanged { add { } remove { } }
    public bool Start(HotkeyConfiguration configuration) => true;
    public void Update(HotkeyConfiguration configuration) { }
    public void SyncRecording(DictationMode? mode) { }
    public void Stop() { }
}

sealed class FakeAutostart : IAutostart
{
    public bool IsEnabled { get; private set; }
    public bool SetEnabled(bool enabled) { IsEnabled = enabled; return true; }
}

sealed class FakeShell : IShell
{
    public List<string> Opened { get; } = [];
    public void OpenFolder(string path) => Opened.Add(path);
    public void RevealFile(string path) => Opened.Add(path);
    public void OpenUrl(string url) => Opened.Add(url);
}

sealed class FakeHistory : IHistoryStore
{
    public List<HistoryEntry> Items { get; } = [];
    public IReadOnlyList<HistoryEntry> Entries => Items;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Add(HistoryEntry entry) { Items.Insert(0, entry); Changed(); }
    public void Update(HistoryEntry entry) { var i = Items.FindIndex(e => e.Id == entry.Id); if (i >= 0) Items[i] = entry; Changed(); }
    public void Delete(Guid id) { Items.RemoveAll(e => e.Id == id); Changed(); }
    public void DeleteAll() { Items.Clear(); Changed(); }
    public string? AudioPath(HistoryEntry entry) => null;
    public void RemoveAudio(Guid id) { }
    public void Flush() { }
    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entries)));
}

sealed class FakeDictionary : IDictionaryStore
{
    public List<DictionaryItem> List { get; } = [];
    public IReadOnlyList<DictionaryItem> Items => List;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Add(DictionaryItem item) { List.Add(item); Changed(); }
    public void Update(DictionaryItem item) { var i = List.FindIndex(e => e.Id == item.Id); if (i >= 0) List[i] = item; Changed(); }
    public void Delete(Guid id) { List.RemoveAll(e => e.Id == id); Changed(); }
    public void Flush() { }
    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Items)));

    public static FakeDictionary Seeded()
    {
        var store = new FakeDictionary();
        var start = DateTimeOffset.Parse("2026-09-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        string[] terms = ["Kubernetes", "GitHub Actions", "UserDefaults", "Postgres", "Docker", "Claude"];
        for (var i = 0; i < terms.Length; i++)
        {
            store.List.Add(DictionaryItem.Term(terms[i], start.AddMinutes(i)));
        }
        store.List.Add(DictionaryItem.ReplacementRule("гитхаб", "GitHub", start.AddMinutes(10)));
        store.List.Add(DictionaryItem.ReplacementRule("тд", "т. д.", start.AddMinutes(11)));
        return store;
    }
}

sealed class FakeShellActions : Slovo.App.ViewModels.IShellActions
{
    /// <summary>What was asked for, such as "OpenSettings:Recognition" or "ShowTroubleshooting".</summary>
    public List<string> Calls { get; } = [];
    public void OpenSettings(string? section = null) => Calls.Add(section is null ? "OpenSettings" : "OpenSettings:" + section);
    public void ShowShortcuts() => Calls.Add("ShowShortcuts");
    public void ShowTroubleshooting() => Calls.Add("ShowTroubleshooting");
    public void OpenModelsFolder() => Calls.Add("OpenModelsFolder");
    public void OpenLogs() => Calls.Add("OpenLogs");
    public void ShowOnboarding() => Calls.Add("ShowOnboarding");
}

sealed class FakeSnippets : ISnippetStore
{
    public List<Snippet> List { get; } = [];
    public IReadOnlyList<Snippet> Snippets => List;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Add(Snippet snippet) { List.Add(snippet); Changed(); }
    public void Update(Snippet snippet) { var i = List.FindIndex(e => e.Id == snippet.Id); if (i >= 0) List[i] = snippet; Changed(); }
    public void Delete(Guid id) { List.RemoveAll(e => e.Id == id); Changed(); }
    public void Flush() { }
    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Snippets)));
}

sealed class FakeNotes : INoteStore
{
    public List<Note> List { get; } = [];
    public IReadOnlyList<Note> Notes => List.OrderByDescending(n => n.UpdatedAt).ToList();
    public event PropertyChangedEventHandler? PropertyChanged;
    public Note Create(string text) { var note = new Note { Text = text }; List.Add(note); Changed(); return note; }
    public void Update(Note note) { var i = List.FindIndex(e => e.Id == note.Id); if (i >= 0) List[i] = note; Changed(); }
    public void Append(string text, Guid noteId)
    {
        var i = List.FindIndex(e => e.Id == noteId);
        if (i >= 0) { List[i] = List[i] with { Text = (List[i].Text + " " + text).Trim(), UpdatedAt = DateTimeOffset.UtcNow }; Changed(); }
    }
    public void Delete(Guid id) { List.RemoveAll(e => e.Id == id); Changed(); }
    public void Flush() { }
    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Notes)));
}

sealed class FakeNoteDictation : Slovo.App.ViewModels.Screens.INoteDictation
{
    public Guid? DictatingInto { get; private set; }
    public event Action? Changed;
    public void Start(Guid noteId) { DictatingInto = noteId; Changed?.Invoke(); }
    public void Finish() { DictatingInto = null; Changed?.Invoke(); }
}

sealed class FakeCategories : IAppCategoryStore
{
    public List<AppCategoryAssignment> List { get; } = [];
    public IReadOnlyList<AppCategoryAssignment> Assignments => List;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AppCategory CategoryFor(string? bundleId) =>
        List.FirstOrDefault(a => string.Equals(a.BundleId, bundleId, StringComparison.OrdinalIgnoreCase))?.Category
        ?? (bundleId?.ToLowerInvariant() switch
        {
            "telegram.exe" => AppCategory.Personal,
            "slack.exe" or "ms-teams.exe" => AppCategory.Work,
            "outlook.exe" => AppCategory.Email,
            _ => AppCategory.Other,
        });
    public AppCategory CategoryFor(AppIdentity app) => CategoryFor(app.BundleId);
    public void SetCategory(AppCategory category, AppIdentity app)
    {
        List.RemoveAll(a => a.BundleId == app.BundleId);
        List.Add(new AppCategoryAssignment(app.BundleId, app.Name, category));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Assignments)));
    }
    public void Flush() { }
}

sealed class FakeHistoryActions : Slovo.App.ViewModels.Screens.IHistoryActions
{
    public string? Copied { get; private set; }
    public Guid? PlayingId { get; private set; }
    public event Action? PlaybackChanged;
    public IReadOnlyList<EngineId> ReadyEngines(EngineId except) => new[] { EngineId.GigaamRnnt, EngineId.Parakeet }.Where(e => e != except).ToList();
    public Task RerunAsync(HistoryEntry entry, EngineId engine) => Task.CompletedTask;
    public void Copy(string text) => Copied = text;
    public bool Play(HistoryEntry entry) { PlayingId = entry.Id; PlaybackChanged?.Invoke(); return true; }
    public void StopPlayback() { PlayingId = null; PlaybackChanged?.Invoke(); }
}

sealed class FakeModels : IModelManager
{
    public Dictionary<EngineId, ModelStatus> Statuses { get; } = new()
    {
        [EngineId.GigaamRnnt] = ModelStatus.Ready(243_000_000),
        [EngineId.GigaamCtc] = ModelStatus.Downloading(0.42),
        [EngineId.Parakeet] = ModelStatus.NotDownloaded(671_000_000),
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    public ModelStatus Status(EngineId id) => Statuses.TryGetValue(id, out var s) ? s : ModelStatus.Unknown;
    public void Refresh() { }
    public void StartDownload(EngineId id) { Statuses[id] = ModelStatus.Downloading(0); Changed(); }
    public void CancelDownload(EngineId id) { Statuses[id] = ModelStatus.NotDownloaded(1); Changed(); }
    public void Delete(EngineId id) { Statuses[id] = ModelStatus.NotDownloaded(1); Changed(); }
    public string? LastError(EngineId id) => null;
    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status"));
}

sealed class FakeEngines : IEngineProvider
{
    public List<EngineId> Unloaded { get; } = [];
    readonly Dictionary<EngineId, EngineLoadState> loadStates = [];
    public EngineId EngineFor(EnginePurpose purpose) => EngineId.GigaamRnnt;
    public EngineLoadState LoadState(EngineId id) => loadStates.GetValueOrDefault(id, EngineLoadState.Unloaded);
    public void SetLoadState(EngineId id, EngineLoadState state) { loadStates[id] = state; LoadStateChanged?.Invoke(id); }
    public Task<ITranscriptionEngine> EngineAsync(EngineId id, CancellationToken cancellation = default) => throw new NotSupportedException();
    public void PreloadForCurrentSettings() { }
    public void MarkUsed(EngineId id) { }
    public void Unload(EngineId id) => Unloaded.Add(id);
    public Task WaitForLoadAsync(EngineId id) => Task.CompletedTask;
    public event Action<EngineId>? LoadStateChanged;
}

sealed class FakeSpeakerModel : IDiarizationModelManager, IDiarizationProvider
{
    public ModelStatus Status { get; set; } = ModelStatus.NotDownloaded(41_000_000);
    public ModelStatus ModelStatus => Status;
    public string? LastError => null;
    public EngineLoadState LoadState { get; set; } = EngineLoadState.Unloaded;
    public bool IsSupportedOnThisSystem => true;
    public string ModelName => "test";
    public string Configuration => "test";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { }
    public void StartDownload() { Status = ModelStatus.Downloading(0.1); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    public void CancelDownload() { }
    public void Delete() { }
    public Task<ISpeakerDiarizer> DiarizerAsync(CancellationToken cancellation) => throw new NotSupportedException();
    public void Release() { }
    public Task WaitForLoadAsync() => Task.CompletedTask;
}

sealed class FakePermissions : IPermissions
{
    public PermissionStatus Microphone { get; set; } = PermissionStatus.Granted;
    public PermissionStatus Status(PermissionKind kind) => kind == PermissionKind.Microphone ? Microphone : PermissionStatus.Granted;
    public Task<PermissionStatus> RequestAsync(PermissionKind kind) => Task.FromResult(Status(kind));
    public List<PermissionKind> Opened { get; } = [];
    public void OpenSystemSettings(PermissionKind kind) => Opened.Add(kind);
    public bool IsSecureInputActive => false;
}

sealed class FakePractice : Slovo.App.Onboarding.IPracticeDictation
{
    public bool Active { get; private set; }
    public bool IsRecording { get; private set; }
    public event Action<string>? Transcribed;
    public event Action? StateChanged;
    public void SetActive(bool active) => Active = active;
    public void StartHandsFree() { IsRecording = true; StateChanged?.Invoke(); }
    public void Finish() { IsRecording = false; StateChanged?.Invoke(); Transcribed?.Invoke("Привет, это проверка диктовки."); }
}
