using System.ComponentModel;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Dictation;

/// <summary>
/// Connections around dictation that no single service owns (port of the macOS
/// <c>ServiceWiring</c>): the paste-last shortcut follows its setting, the dictation
/// engine is preloaded once its model becomes usable (a finished download), and an engine
/// is released before its model files are deleted. <see cref="Start"/> also starts the
/// <see cref="DictationController"/>, which runs the dictation key listener with
/// <see cref="AppSettings.HotkeyConfiguration"/>, forwards its triggers, follows key
/// settings and tells the listener what is recording and processing.
/// </summary>
/// <remarks>
/// The composition root creates it after the first <see cref="IModelManager.Refresh"/> and
/// calls <see cref="Start"/> once and <see cref="Stop"/> on exit. Use it on the main thread;
/// settings and model changes from other threads are marshalled there.
/// </remarks>
public sealed class DictationWiring(
    DictationController controller,
    AppSettings settings,
    IModelManager models,
    IEngineProvider engines,
    IGlobalShortcuts shortcuts,
    IMainThread mainThread) : Observable
{
    static readonly Logger Logger = Log.For("Wiring");

    /// <summary>
    /// The shortcut last handed to the system, registered or refused, so a refused one is
    /// not retried on every unrelated settings change.
    /// </summary>
    HotkeyShortcut? appliedShortcut;
    IDisposable? pasteLastRegistration;
    bool isPasteLastRegistered;
    /// <summary>Engines whose models were usable at the last check.</summary>
    HashSet<EngineId> usableEngines = [];
    bool isStarted;

    public DictationController Controller => controller;

    /// <summary>False when the system refused the paste-last shortcut (another app holds it); Settings says so.</summary>
    public bool IsPasteLastRegistered
    {
        get => isPasteLastRegistered;
        private set => SetField(ref isPasteLastRegistered, value);
    }

    /// <summary>
    /// Starts dictation, registers the paste-last shortcut, remembers which models are usable
    /// now and starts following settings and model statuses.
    /// </summary>
    public void Start()
    {
        controller.Start();
        usableEngines = CurrentlyUsableEngines();
        ApplySettings();
        if (isStarted)
        {
            return;
        }
        isStarted = true;
        settings.Store.Changed += SettingChanged;
        models.PropertyChanged += ModelsChanged;
    }

    public void Stop()
    {
        if (isStarted)
        {
            settings.Store.Changed -= SettingChanged;
            models.PropertyChanged -= ModelsChanged;
            isStarted = false;
        }
        pasteLastRegistration?.Dispose();
        pasteLastRegistration = null;
        appliedShortcut = null;
        IsPasteLastRegistered = false;
        controller.Stop();
    }

    /// <summary>
    /// Re-registers the paste-last shortcut when its setting changed. Returns whether the
    /// current shortcut is registered.
    /// </summary>
    public bool ApplySettings()
    {
        var shortcut = settings.PasteLastShortcut;
        if (shortcut == appliedShortcut)
        {
            return IsPasteLastRegistered;
        }
        appliedShortcut = shortcut;
        pasteLastRegistration?.Dispose();
        pasteLastRegistration = shortcuts.Register(shortcut, PasteLast);
        IsPasteLastRegistered = pasteLastRegistration is not null;
        if (!IsPasteLastRegistered)
        {
            Logger.Error($"The paste-last shortcut {shortcut.DisplayText} was refused by the system");
        }
        return IsPasteLastRegistered;
    }

    /// <summary>
    /// Call after model statuses change (it also follows <see cref="IModelManager"/>'s
    /// PropertyChanged). Preloads the dictation engine when its model has just become
    /// usable; the engine provider does not watch model files itself.
    /// </summary>
    public void ModelStatusesDidChange()
    {
        var usable = CurrentlyUsableEngines();
        var becameUsable = usable.Except(usableEngines).ToHashSet();
        usableEngines = usable;
        var wanted = engines.EngineFor(EnginePurpose.Dictation(settings.LanguageMode));
        if (!becameUsable.Contains(wanted))
        {
            return;
        }
        Logger.Info($"{wanted.RawValue()} became usable; preloading");
        engines.PreloadForCurrentSettings();
    }

    /// <summary>
    /// Releases the engine from memory, waits for a load that is still reading the files,
    /// then deletes them. ONNX Runtime aborts the whole app when a model file disappears in
    /// the middle of a load. No load can start between the wait and the removal: both run
    /// on the main thread with nothing in between, and the removal changes the model status
    /// at once. Throws what <see cref="IModelManager.Delete"/> throws.
    /// </summary>
    public async Task DeleteModelAsync(EngineId id)
    {
        engines.Unload(id);
        await engines.WaitForLoadAsync(id).ResumeOn(mainThread);
        try
        {
            models.Delete(id);
        }
        finally
        {
            usableEngines = CurrentlyUsableEngines();
        }
    }

    void PasteLast() => _ = PasteLastAsync();

    async Task PasteLastAsync()
    {
        try
        {
            await controller.PasteLastTranscriptAsync().ResumeOn(mainThread);
        }
        catch (Exception error)
        {
            Logger.Error("Paste last transcript failed", error);
        }
    }

    void SettingChanged(string key) => OnMainThread(() => ApplySettings());

    void ModelsChanged(object? sender, PropertyChangedEventArgs e) => OnMainThread(ModelStatusesDidChange);

    void OnMainThread(Action action)
    {
        if (mainThread.IsMainThread)
        {
            action();
        }
        else
        {
            mainThread.Post(action);
        }
    }

    HashSet<EngineId> CurrentlyUsableEngines() =>
        Enum.GetValues<EngineId>().Where(id => models.Status(id).IsUsable).ToHashSet();
}
