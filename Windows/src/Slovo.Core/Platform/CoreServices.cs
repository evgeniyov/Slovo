using System.ComponentModel;
using Slovo.Core.Model;

namespace Slovo.Core.Platform;

// Core services depend on each other only through these interfaces (the C# side of
// Sources/SlovoCore/Model/ServiceProtocols.swift), so each can be replaced by a fake in
// tests and implemented without editing the others. Stores and controllers raise
// PropertyChanged (with the name of the member that changed) on the UI thread.

/// <summary>A speech-to-text backend that turns a finished recording into text.</summary>
public interface ITranscriptionEngine
{
    /// <summary>Stable identifier used in settings and benchmark results (the EngineId raw value).</summary>
    string Id { get; }
    /// <summary>Loads models and checks that the engine can run. Safe to call more than once. Throws SlovoException.</summary>
    Task PrepareAsync(CancellationToken cancellation = default);
    /// <summary>Transcribes 16 kHz mono float samples. Throws SlovoException, OperationCanceledException.</summary>
    Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation = default);
    /// <summary>Transcribes a WAV or CAF file.</summary>
    Task<string> TranscribeFileAsync(string path, CancellationToken cancellation = default);
}

/// <summary>Loaded engines.</summary>
public interface IEngineProvider
{
    EngineId EngineFor(EnginePurpose purpose);
    EngineLoadState LoadState(EngineId id);
    /// <summary>Returns the loaded engine, loading it first when needed.</summary>
    Task<ITranscriptionEngine> EngineAsync(EngineId id, CancellationToken cancellation = default);
    /// <summary>Loads the engines current settings use, in the background.</summary>
    void PreloadForCurrentSettings();
    /// <summary>Restarts the idle-unload countdown of an engine.</summary>
    void MarkUsed(EngineId id);
    /// <summary>Releases a loaded engine, for example before its model files are deleted.</summary>
    void Unload(EngineId id);
    /// <summary>Completes once no load of the engine is still reading its model files.</summary>
    Task WaitForLoadAsync(EngineId id);
    /// <summary>Raised when a load state changes.</summary>
    event Action<EngineId>? LoadStateChanged;
}

/// <summary>Model files on disk.</summary>
public interface IModelManager : INotifyPropertyChanged
{
    ModelStatus Status(EngineId id);
    void Refresh();
    /// <summary>Starts a download the user asked for; progress appears in Status.</summary>
    void StartDownload(EngineId id);
    void CancelDownload(EngineId id);
    void Delete(EngineId id);
    /// <summary>The last download or delete failure for an engine, already localized.</summary>
    string? LastError(EngineId id);
}

/// <summary>The text pipeline; pure and synchronous.</summary>
public interface ITextProcessor
{
    TextPipelineResult Process(string raw, TextPipelineContext context);
}

public interface IHistoryStore : INotifyPropertyChanged
{
    /// <summary>Newest first.</summary>
    IReadOnlyList<HistoryEntry> Entries { get; }
    void Add(HistoryEntry entry);
    void Update(HistoryEntry entry);
    /// <summary>Also removes the entry's audio file.</summary>
    void Delete(Guid id);
    void DeleteAll();
    /// <summary>The audio file of an entry when it still exists.</summary>
    string? AudioPath(HistoryEntry entry);
    /// <summary>Deletes an entry's audio file and clears its AudioFileName.</summary>
    void RemoveAudio(Guid id);
    /// <summary>Writes pending changes now instead of after the debounce.</summary>
    void Flush();
}

public interface IDictionaryStore : INotifyPropertyChanged
{
    IReadOnlyList<DictionaryItem> Items { get; }
    void Add(DictionaryItem item);
    void Update(DictionaryItem item);
    void Delete(Guid id);
    void Flush();
}

public interface ISnippetStore : INotifyPropertyChanged
{
    IReadOnlyList<Snippet> Snippets { get; }
    void Add(Snippet snippet);
    void Update(Snippet snippet);
    void Delete(Guid id);
    void Flush();
}

public interface INoteStore : INotifyPropertyChanged
{
    /// <summary>Most recently updated first.</summary>
    IReadOnlyList<Note> Notes { get; }
    Note Create(string text);
    void Update(Note note);
    /// <summary>Appends dictated text to a note, separated by a space or a new line.</summary>
    void Append(string text, Guid noteId);
    void Delete(Guid id);
    void Flush();
}

public interface IAppCategoryStore : INotifyPropertyChanged
{
    IReadOnlyList<AppCategoryAssignment> Assignments { get; }
    /// <summary>The user's assignment, else the built-in default, else Other.</summary>
    AppCategory CategoryFor(string? bundleId);
    /// <summary>Also matches apps by the name they report.</summary>
    AppCategory CategoryFor(AppIdentity app);
    void SetCategory(AppCategory category, AppIdentity app);
    void Flush();
}

public interface IMeetingStore : INotifyPropertyChanged
{
    /// <summary>Newest first.</summary>
    IReadOnlyList<MeetingRecord> Meetings { get; }
    MeetingRecord? Meeting(Guid id);
    /// <summary>Creates the meeting folder and its meeting.json. Throws SlovoException.</summary>
    MeetingRecord Create(string title);
    void Update(MeetingRecord meeting);
    /// <summary>Removes the whole meeting folder.</summary>
    void Delete(Guid id);
    /// <summary>Removes the audio files and sets HasAudio (and HasScreenRecording) to false.</summary>
    void DeleteAudio(Guid id);
    /// <summary>Writes or clears Claude's notes (the one change the card makes to them).</summary>
    void SetClaudeNotes(MeetingNotes? notes, Guid id);
    /// <summary>Picks up notes and highlights the MCP process wrote since the last look.</summary>
    void RefreshFromDisk();
    string MeetingsFolder { get; }
    string Folder(Guid id);
    string MicrophoneAudioPath(Guid id);
    string SystemAudioPath(Guid id);
    /// <summary><c>screen.mov</c>, the meeting's optional screen video.</summary>
    string ScreenRecordingPath(Guid id) => Path.Combine(Folder(id), Audio.MeetingAudioFormat.ScreenFileName);
    string TranscriptPath(Guid id);
    void Flush();
}

/// <summary>Lets dictation refuse to start while a meeting records.</summary>
public interface IMeetingActivity
{
    bool IsMeetingRecording { get; }
}

/// <summary>One loaded diarizer. Calls are serialized by the implementation.</summary>
public interface ISpeakerDiarizer
{
    /// <summary>Turns of 16 kHz mono samples, sorted, possibly empty. Progress 0…1 on any thread.</summary>
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(float[] samples, SpeakerCountHint hint, IProgress<double>? progress, CancellationToken cancellation);
}

/// <summary>Loaded diarizers for the meeting pipeline.</summary>
public interface IDiarizationProvider
{
    ModelStatus ModelStatus { get; }
    bool IsSupportedOnThisSystem { get; }
    /// <summary>Loads the model when needed; throws when it is not ready. Never downloads.</summary>
    Task<ISpeakerDiarizer> DiarizerAsync(CancellationToken cancellation);
    void Release();
    Task WaitForLoadAsync();
    /// <summary>"pyannote-segmentation-3.0+3dspeaker-eres2net@sherpa-onnx", stored in meeting.json.</summary>
    string ModelName { get; }
    /// <summary>The tuning in effect, stored in meeting.json.</summary>
    string Configuration { get; }
}

/// <summary>The speaker model files on disk.</summary>
public interface IDiarizationModelManager : INotifyPropertyChanged
{
    ModelStatus Status { get; }
    string? LastError { get; }
    /// <summary>Whether the model is being read into memory; its files wait until it is done.</summary>
    EngineLoadState LoadState { get; }
    void Refresh();
    void StartDownload();
    void CancelDownload();
    void Delete();
}

/// <summary>Marshals work to the UI thread; tests use an immediate implementation.</summary>
public interface IMainThread
{
    bool IsMainThread { get; }
    void Post(Action action);
}

public sealed class ImmediateMainThread : IMainThread
{
    public static readonly ImmediateMainThread Shared = new();
    public bool IsMainThread => true;
    public void Post(Action action) => action();
}
