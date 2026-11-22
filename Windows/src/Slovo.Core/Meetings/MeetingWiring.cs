using System.ComponentModel;
using Slovo.Core.Dictation;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Meetings;

/// <summary>
/// Connections between the meeting services that no single module owns (port of the macOS
/// <c>MeetingWiring</c>): the mark-moment and microphone shortcuts exist only while a meeting
/// records, and the speaker model leaves memory before its files are deleted.
/// </summary>
/// <remarks>
/// The shortcuts are global hot keys. Holding them outside recordings would take Ctrl+Alt+M and
/// Ctrl+Alt+U (or the chosen combinations) away from every other app for nothing, so they are
/// registered when a recording starts and removed when it stops. The composition root creates
/// the wiring once and calls <see cref="Start"/>, and <see cref="Stop"/> on exit. Use it on the
/// main thread; settings changes from other threads are marshalled there.
/// </remarks>
public sealed class MeetingWiring : Observable
{
    static readonly Logger Logger = Log.For("MeetingWiring");

    readonly AppSettings settings;
    readonly MeetingRecorder recorder;
    readonly IMainThread mainThread;
    readonly IDiarizationProvider? diarization;
    readonly IDiarizationModelManager? diarizationModels;
    readonly RecordingShortcut markShortcut;
    readonly RecordingShortcut microphoneShortcut;

    bool isRecording;
    bool isStarted;
    HotkeyShortcut? refusedShortcut;
    HotkeyShortcut? refusedMicrophoneShortcut;
    bool isShortcutRegistered;
    bool isMicrophoneShortcutRegistered;

    /// <param name="shortcuts">Registers both shortcuts; the mark-moment one marks a moment in
    /// <paramref name="recorder"/> and the microphone one switches its microphone.</param>
    /// <param name="diarization">With <paramref name="diarizationModels"/>, what
    /// <see cref="DeleteDiarizationModelAsync"/> releases and deletes.</param>
    public MeetingWiring(AppSettings settings, MeetingRecorder recorder, IGlobalShortcuts shortcuts, IMainThread mainThread,
                         IDiarizationProvider? diarization = null, IDiarizationModelManager? diarizationModels = null)
    {
        this.settings = settings;
        this.recorder = recorder;
        this.mainThread = mainThread;
        this.diarization = diarization;
        this.diarizationModels = diarizationModels;
        markShortcut = new RecordingShortcut(shortcuts, () => recorder.MarkMoment());
        microphoneShortcut = new RecordingShortcut(shortcuts, () => recorder.ToggleMicrophone());
    }

    /// <summary>
    /// The mark-moment shortcut the system refused at the last registration, until one
    /// succeeds. Settings asks for another combination while this one is still selected.
    /// </summary>
    public HotkeyShortcut? RefusedShortcut
    {
        get => refusedShortcut;
        private set => SetField(ref refusedShortcut, value);
    }

    /// <summary>The same for the microphone shortcut.</summary>
    public HotkeyShortcut? RefusedMicrophoneShortcut
    {
        get => refusedMicrophoneShortcut;
        private set => SetField(ref refusedMicrophoneShortcut, value);
    }

    public bool IsShortcutRegistered
    {
        get => isShortcutRegistered;
        private set => SetField(ref isShortcutRegistered, value);
    }

    public bool IsMicrophoneShortcutRegistered
    {
        get => isMicrophoneShortcutRegistered;
        private set => SetField(ref isMicrophoneShortcutRegistered, value);
    }

    /// <summary>Follows the recorder's state and the shortcut settings until <see cref="Stop"/>.</summary>
    public void Start()
    {
        if (isStarted)
        {
            return;
        }
        isStarted = true;
        // No hop to the main thread: the registration changes in the same turn as the
        // recorder's state, so a press right after Start already marks a moment.
        recorder.PropertyChanged += RecorderChanged;
        settings.Store.Changed += SettingChanged;
        isRecording = IsRecording(recorder.State);
        ApplySettings();
    }

    /// <summary>Removes the shortcuts and stops following the recorder and settings.</summary>
    public void Stop()
    {
        if (isStarted)
        {
            recorder.PropertyChanged -= RecorderChanged;
            settings.Store.Changed -= SettingChanged;
            isStarted = false;
        }
        isRecording = false;
        Unregister();
    }

    /// <summary>
    /// Registers the current shortcuts while recording and removes them otherwise. Returns
    /// whether the mark-moment shortcut is registered now.
    /// </summary>
    public bool ApplySettings()
    {
        if (!isRecording)
        {
            Unregister();
            return false;
        }
        var wanted = settings.HighlightShortcut;
        if (markShortcut.Apply(wanted) is { } registered)
        {
            RefusedShortcut = Report(registered, wanted, "mark-moment");
        }
        var wantedMicrophone = settings.MicrophoneShortcut;
        if (microphoneShortcut.Apply(wantedMicrophone) is { } microphoneRegistered)
        {
            RefusedMicrophoneShortcut = Report(microphoneRegistered, wantedMicrophone, "microphone");
        }
        UpdateRegistered();
        return IsShortcutRegistered;
    }

    /// <summary>
    /// Unloads the speaker model, waits for a load that is still reading the files, then deletes
    /// them. ONNX Runtime must not lose a model file in the middle of a load. No load can start
    /// between the wait and the removal: both run on the main thread with nothing in between.
    /// Throws what <see cref="IDiarizationModelManager.Delete"/> throws.
    /// </summary>
    public async Task DeleteDiarizationModelAsync()
    {
        if (diarization is null || diarizationModels is null)
        {
            throw new InvalidOperationException("The meeting wiring was created without the speaker model services.");
        }
        diarization.Release();
        await diarization.WaitForLoadAsync().ResumeOn(mainThread);
        diarizationModels.Delete();
    }

    static bool IsRecording(MeetingRecorderState state) => state is MeetingRecorderState.Recording;

    /// <summary>The refusal to publish: cleared once a registration succeeds, the shortcut when it was refused.</summary>
    static HotkeyShortcut? Report(bool registered, HotkeyShortcut shortcut, string name)
    {
        if (registered)
        {
            return null;
        }
        Logger.Error($"The {name} shortcut {shortcut.DisplayText} was refused by the system");
        return shortcut;
    }

    // Only Recording counts: while the files close after Stop there is nothing to mark.
    void RecorderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeetingRecorder.State))
        {
            OnMainThread(() => RecordingDidChange(IsRecording(recorder.State)));
        }
    }

    void RecordingDidChange(bool recording)
    {
        if (recording == isRecording)
        {
            return;
        }
        isRecording = recording;
        ApplySettings();
    }

    void SettingChanged(string key) => OnMainThread(() => ApplySettings());

    void Unregister()
    {
        markShortcut.Unregister();
        microphoneShortcut.Unregister();
        UpdateRegistered();
    }

    void UpdateRegistered()
    {
        IsShortcutRegistered = markShortcut.IsRegistered;
        IsMicrophoneShortcutRegistered = microphoneShortcut.IsRegistered;
    }

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

    /// <summary>One global shortcut that exists only while a meeting records.</summary>
    sealed class RecordingShortcut(IGlobalShortcuts shortcuts, Action action)
    {
        /// <summary>
        /// The shortcut last handed to the system, registered or refused, so a refused one is not
        /// retried on every unrelated settings change.
        /// </summary>
        HotkeyShortcut? applied;
        IDisposable? registration;

        public bool IsRegistered => registration is not null;

        /// <summary>
        /// Registers <paramref name="wanted"/> unless it is the shortcut already handed over.
        /// Returns whether the system accepted it, or null when nothing was attempted.
        /// </summary>
        public bool? Apply(HotkeyShortcut wanted)
        {
            if (wanted == applied)
            {
                return null;
            }
            applied = wanted;
            registration?.Dispose();
            registration = shortcuts.Register(wanted, action);
            return registration is not null;
        }

        public void Unregister()
        {
            registration?.Dispose();
            registration = null;
            applied = null;
        }
    }
}
