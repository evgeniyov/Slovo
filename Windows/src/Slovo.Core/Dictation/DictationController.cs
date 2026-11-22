using System.Globalization;
using System.Text;
using Slovo.Core.Audio;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Dictation;

/// <summary>
/// The dictation state machine: trigger → record → transcribe → pipeline → insert →
/// history (port of the macOS <c>DictationController</c>). Everything it talks to is an
/// interface, so tests use fakes.
/// </summary>
/// <remarks>
/// Threading: create it anywhere, then use every member on the main (UI) thread, the
/// counterpart of Swift's <c>@MainActor</c>. Events that arrive on other threads (input
/// levels, settings changes) are marshalled through <see cref="IMainThread"/>; the WAV
/// file is written on the thread pool; engines and the text inserter run wherever they
/// like and every <c>await</c> comes back to the main thread before touching state, so
/// PropertyChanged is always raised there. Timers go through <see cref="IScheduler"/>
/// and time through <see cref="IUptime"/>, so tests are deterministic.
/// </remarks>
public sealed class DictationController : Observable
{
    /// <summary>Everything the controller talks to.</summary>
    public sealed class Dependencies
    {
        public required IAudioRecorder Recorder { get; init; }
        public required IHotkeyMonitor Hotkeys { get; init; }
        public required ITextInserter Inserter { get; init; }
        public required IForegroundApp ForegroundApp { get; init; }
        public required IPermissions Permissions { get; init; }
        /// <summary>Also used as <see cref="ILanguageEngineProvider"/> when it implements it.</summary>
        public required IEngineProvider Engines { get; init; }
        public required ITextProcessor Pipeline { get; init; }
        public required IHistoryStore History { get; init; }
        public required IDictionaryStore Dictionary { get; init; }
        public required ISnippetStore Snippets { get; init; }
        public required INoteStore Notes { get; init; }
        public required IAppCategoryStore Categories { get; init; }
        public required IMeetingActivity Meetings { get; init; }
        /// <summary>Played only while the start/stop sounds setting is on.</summary>
        public ISoundPlayer? Sounds { get; init; }
        public required AppSettings Settings { get; init; }
        /// <summary>Kept recordings go to <see cref="AppPaths.RecordingsFolder"/>.</summary>
        public required AppPaths Paths { get; init; }
        public required IMainThread MainThread { get; init; }
        public IUptime Uptime { get; init; } = SystemUptime.Shared;
        /// <summary>Null means a <see cref="MainThreadScheduler"/> on <see cref="MainThread"/>.</summary>
        public IScheduler? Scheduler { get; init; }
    }

    /// <summary>How long result messages stay in the Flow bar, in seconds.</summary>
    public const double MessageDuration = 1.5;
    /// <summary>The Flow bar countdown appears this long before the hands-free limit.</summary>
    public const double CountdownThreshold = 30;
    /// <summary>
    /// How long a dictation may wait for its engine to load before it is abandoned. A first
    /// load of a big model from a slow disk can take minutes.
    /// </summary>
    public const double ModelLoadTimeout = 300;
    /// <summary>
    /// How long transcription may take, on top of the recording's own length, before the
    /// dictation is abandoned. Bundled engines need a small fraction of the audio length.
    /// </summary>
    public const double TranscriptionTimeout = 60;
    /// <summary>Prefix of the temporary WAV files engines read; see <see cref="RemoveLeftoverRecordings"/>.</summary>
    internal const string TemporaryRecordingPrefix = "Slovo-";

    static readonly Logger Logger = Log.For("Dictation");

    readonly Dependencies d;
    readonly IScheduler scheduler;

    DictationState state = DictationState.IdleState;
    float level;
    double? handsFreeRemaining;
    string? lastTranscript;
    string? practiceResult;

    bool isStarted;
    /// <summary>The app in front when the running recording began; null for notes and practice.</summary>
    AppIdentity? recordingApp;
    /// <summary>
    /// A push-to-talk recording started by the key whose hold is not confirmed yet. Capture
    /// runs from key down so the first syllable is kept, but the Flow bar and the start
    /// sound wait: a tap shorter than the minimum hold is not a dictation.
    /// </summary>
    RecordingInfo? unconfirmedRecording;
    /// <summary>
    /// Why a key press could not start recording, shown only if the key is still held at
    /// the hold check, so tapping the key flashes nothing.
    /// </summary>
    FlowMessage? unconfirmedRefusal;
    /// <summary>True once a hold check found the key released and waits for the tap to resolve.</summary>
    bool isWaitingForTapDecision;
    IDisposable? holdCheckTimer;
    /// <summary>The most recent dictation's work, also after it is abandoned, for tests.</summary>
    Task? latestProcessingTask;
    /// <summary>Cancels the engine of the dictation being transcribed when it is abandoned.</summary>
    CancellationTokenSource? processingCancellation;
    /// <summary>
    /// Abandons a dictation whose engine takes too long, so a load stuck behind a download
    /// or a hung engine cannot keep the Flow bar busy and refuse every new dictation.
    /// </summary>
    IDisposable? processingWatchdog;
    /// <summary>Bumped whenever a dictation in progress is abandoned, so its late results are dropped.</summary>
    int processingNumber;
    IDisposable? messageTimer;
    IDisposable? limitTimer;
    HotkeyConfiguration? appliedHotkeyConfiguration;

    public DictationController(Dependencies dependencies)
    {
        d = dependencies;
        scheduler = dependencies.Scheduler ?? new MainThreadScheduler(dependencies.MainThread);
        // "Paste last transcript" keeps working after a relaunch.
        lastTranscript = dependencies.History.Entries.FirstOrDefault()?.Text;
    }

    // MARK: - Observable state

    /// <summary>What the Flow bar, the tray menu and the screens show.</summary>
    public DictationState State
    {
        get => state;
        private set
        {
            var wasProcessing = state is DictationState.Processing;
            if (!SetField(ref state, value))
            {
                return;
            }
            var isProcessing = value is DictationState.Processing;
            if (isProcessing != wasProcessing)
            {
                (d.Hotkeys as IProcessingAwareHotkeys)?.SyncProcessing(isProcessing);
            }
        }
    }

    /// <summary>Smoothed input level from 0 to 1 while recording.</summary>
    public float Level
    {
        get => level;
        private set => SetField(ref level, value);
    }

    /// <summary>Seconds left in a hands-free recording during its last 30 s, else null.</summary>
    public double? HandsFreeRemaining
    {
        get => handsFreeRemaining;
        private set => SetField(ref handsFreeRemaining, value);
    }

    /// <summary>The last inserted text, for "Paste last transcript".</summary>
    public string? LastTranscript
    {
        get => lastTranscript;
        private set => SetField(ref lastTranscript, value);
    }

    /// <summary>The result of a dictation whose target was <see cref="DictationTarget.Practice"/>.</summary>
    public string? PracticeResult
    {
        get => practiceResult;
        private set => SetField(ref practiceResult, value);
    }

    /// <summary>
    /// Where recordings started with the dictation key go. Onboarding sets
    /// <see cref="DictationTarget.PracticeTarget"/> during its practice step, so holding the
    /// key fills the practice field instead of pasting into another app.
    /// </summary>
    public DictationTarget KeyTarget { get; set; } = DictationTarget.Foreground;

    /// <summary>
    /// The microphone the last recording used; the Flow bar names it when nothing was heard,
    /// which is how a silent headset input shows up.
    /// </summary>
    public string? LastInputDeviceName => d.Recorder.LastDeviceName;

    // MARK: - Tunables for tests

    /// <summary>How long result messages stay; tests shorten it.</summary>
    internal double MessageLifetime { get; set; } = MessageDuration;
    /// <summary>The processing time limits; tests shorten them.</summary>
    internal double ModelLoadTimeLimit { get; set; } = ModelLoadTimeout;
    internal double TranscriptionTimeLimit { get; set; } = TranscriptionTimeout;
    /// <summary>How often a running recording checks its time limit.</summary>
    internal double LimitCheckInterval { get; set; } = 0.25;
    /// <summary>Replaces the delays of the hold checks; tests set it so they run the checks by hand.</summary>
    internal double? HoldCheckDelay { get; set; }
    /// <summary>Where recordings wait while an engine reads them.</summary>
    internal string TemporaryFolder { get; set; } = Path.GetTempPath();

    // MARK: - Lifecycle

    /// <summary>Connects the hotkey monitor and the recorder's level, and follows key settings.</summary>
    public void Start()
    {
        if (isStarted)
        {
            return;
        }
        isStarted = true;
        d.Hotkeys.Triggered += HotkeyTriggered;
        d.Recorder.LevelChanged += RecorderLevelChanged;
        RefreshHotkeys();
        d.Settings.Store.Changed += SettingChanged;
        // A crash while an engine read a recording leaves its temporary copy behind, even
        // when dictation audio is never kept.
        var folder = TemporaryFolder;
        _ = Task.Run(() => RemoveLeftoverRecordings(folder, DateTime.UtcNow));
    }

    public void Stop()
    {
        if (!isStarted)
        {
            return;
        }
        isStarted = false;
        d.Settings.Store.Changed -= SettingChanged;
        StopRecording(sound: null, keepAudio: false, fromTrigger: true);
        ClearUnconfirmedRecording();
        AbandonProcessing();
        if (State is DictationState.Processing)
        {
            State = DictationState.IdleState;
        }
        d.Hotkeys.Triggered -= HotkeyTriggered;
        d.Hotkeys.Stop();
        d.Recorder.LevelChanged -= RecorderLevelChanged;
        appliedHotkeyConfiguration = null;
    }

    /// <summary>
    /// Starts the key listener if it is not running (it could not install its hook before)
    /// and applies the current key settings.
    /// </summary>
    public void RefreshHotkeys()
    {
        if (!isStarted)
        {
            return;
        }
        var configuration = d.Settings.HotkeyConfiguration;
        if (!d.Hotkeys.IsRunning)
        {
            appliedHotkeyConfiguration = configuration;
            if (!d.Hotkeys.Start(configuration))
            {
                Logger.Info("The dictation key listener is not running");
            }
        }
        else if (configuration != appliedHotkeyConfiguration)
        {
            appliedHotkeyConfiguration = configuration;
            d.Hotkeys.Update(configuration);
        }
    }

    internal void SettingsDidChange()
    {
        var configuration = d.Settings.HotkeyConfiguration;
        if (!d.Hotkeys.IsRunning || configuration == appliedHotkeyConfiguration)
        {
            return;
        }
        appliedHotkeyConfiguration = configuration;
        d.Hotkeys.Update(configuration);
    }

    /// <summary>
    /// Deletes temporary dictation recordings (<c>Slovo-&lt;uuid&gt;.wav</c>) older than
    /// <paramref name="minimumAge"/> seconds, so a recording another running copy of Slovo is
    /// still transcribing is left alone. Returns how many files were removed.
    /// </summary>
    internal static int RemoveLeftoverRecordings(string folder, DateTime nowUtc, double minimumAge = 3600)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, TemporaryRecordingPrefix + "*.wav").ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        var removed = 0;
        foreach (var path in files)
        {
            if (!IsTemporaryRecording(Path.GetFileName(path)))
            {
                continue;
            }
            try
            {
                if ((nowUtc - File.GetLastWriteTimeUtc(path)).TotalSeconds < minimumAge)
                {
                    continue;
                }
                File.Delete(path);
                removed += 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // In use or gone; the next launch tries again.
            }
        }
        return removed;
    }

    /// <summary><c>Slovo-&lt;uuid&gt;.wav</c> exactly; meeting work folders share the prefix but not the shape.</summary>
    internal static bool IsTemporaryRecording(string name)
    {
        const string suffix = ".wav";
        if (!name.StartsWith(TemporaryRecordingPrefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
            || name.Length < TemporaryRecordingPrefix.Length + suffix.Length)
        {
            return false;
        }
        var middle = name[TemporaryRecordingPrefix.Length..^suffix.Length];
        return Guid.TryParseExact(middle, "D", out _);
    }

    void HotkeyTriggered(DictationTrigger trigger) => OnMainThread(() => Handle(trigger));

    void RecorderLevelChanged(float value) => OnMainThread(() => RecordingLevelChanged(value));

    void SettingChanged(string key) => OnMainThread(SettingsDidChange);

    void OnMainThread(Action action)
    {
        if (d.MainThread.IsMainThread)
        {
            action();
        }
        else
        {
            d.MainThread.Post(action);
        }
    }

    // MARK: - Commands

    /// <summary>Applies a trigger from the hotkey state machine.</summary>
    public void Handle(DictationTrigger trigger)
    {
        switch (trigger)
        {
            case DictationTrigger.Begin begin:
                BeginRecording(begin.Mode, KeyTarget, fromTrigger: true);
                break;
            case DictationTrigger.PromoteToHandsFree:
                PromoteToHandsFree();
                break;
            case DictationTrigger.Finish:
                FinishRecording(fromTrigger: true);
                break;
            case DictationTrigger.Discard:
                StopRecording(sound: null, keepAudio: false, fromTrigger: true);
                break;
            case DictationTrigger.Cancel:
                CancelDictation(fromTrigger: true);
                break;
        }
    }

    /// <summary>Starts a hands-free recording from the Flow bar, Scratchpad or onboarding; null means the foreground app.</summary>
    public void StartHandsFree(DictationTarget? target = null) =>
        BeginRecording(DictationMode.HandsFree, target ?? DictationTarget.Foreground, fromTrigger: false);

    /// <summary>Stops the current recording and transcribes it.</summary>
    public void Finish() => FinishRecording(fromTrigger: false);

    /// <summary>
    /// Stops the current recording without transcribing, or abandons a dictation that is
    /// still being transcribed.
    /// </summary>
    public void Cancel() => CancelDictation(fromTrigger: false);

    /// <summary>Inserts <see cref="LastTranscript"/> again.</summary>
    public async Task PasteLastTranscriptAsync()
    {
        if (LastTranscript is not { Length: > 0 } text)
        {
            return;
        }
        var result = await d.Inserter.InsertAsync(text).ResumeOn(d.MainThread);
        if (result is not InsertionResult.Copied)
        {
            return;
        }
        if (State is DictationState.Idle or DictationState.Message)
        {
            ShowMessage(FlowMessage.Copied);
        }
    }

    /// <summary>
    /// Transcribes a history entry's audio with another engine and replaces its text. Uses
    /// the language the entry was dictated in. Throws SlovoException with a localized message.
    /// </summary>
    public async Task RerunAsync(Guid entryId, EngineId engineId, CancellationToken cancellation = default)
    {
        var entry = d.History.Entries.FirstOrDefault(e => e.Id == entryId)
            ?? throw new SlovoException(CoreStrings.Get("dictation.error.entryMissing"));
        var audioPath = d.History.AudioPath(entry)
            ?? throw new SlovoException(CoreStrings.Get("dictation.error.audioMissing"));
        var engine = await EngineAsync(engineId, entry.LanguageMode, cancellation).ResumeOn(d.MainThread);
        var rawText = await engine.TranscribeFileAsync(audioPath, cancellation).ResumeOn(d.MainThread);
        d.Engines.MarkUsed(engineId);

        var result = d.Pipeline.Process(rawText, PipelineContext(entry.Category, entry.LanguageMode));
        var text = result.Text.Trim();
        if (!HasContent(text))
        {
            throw new SlovoException(CoreStrings.Get("dictation.error.rerunEmpty"));
        }
        // The entry may have been edited or deleted while the engine worked.
        var current = d.History.Entries.FirstOrDefault(e => e.Id == entryId)
            ?? throw new SlovoException(CoreStrings.Get("dictation.error.entryMissing"));
        var isLatest = d.History.Entries.FirstOrDefault()?.Id == entryId;
        d.History.Update(current with
        {
            Text = text,
            RawText = rawText,
            EngineId = engineId,
            WordCount = HistoryEntry.CountWords(text),
            FixesCount = result.FixesCount,
        });
        if (isLatest)
        {
            LastTranscript = text;
        }
    }

    /// <summary>Clears a result message before its timeout, for example when it is clicked.</summary>
    public void DismissMessage()
    {
        if (State is not DictationState.Message)
        {
            return;
        }
        CancelMessageTimer();
        State = DictationState.IdleState;
    }

    // MARK: - Recording

    void BeginRecording(DictationMode mode, DictationTarget target, bool fromTrigger)
    {
        if (unconfirmedRecording is { } pending)
        {
            if (fromTrigger)
            {
                d.Hotkeys.SyncRecording(pending.Mode);
            }
            else
            {
                // The Flow bar was clicked while the key is held: keep the capture going hands-free.
                d.Hotkeys.SyncRecording(DictationMode.HandsFree);
                ShowRecording(pending with { Mode = DictationMode.HandsFree });
            }
            return;
        }
        // A key press may still turn out to be a tap, which must change nothing on screen.
        var waitsForHold = fromTrigger && mode == DictationMode.PushToTalk;
        unconfirmedRefusal = null;
        switch (State)
        {
            case DictationState.Recording recording:
                // Already recording: keep the key state machine in step with what really runs.
                if (fromTrigger)
                {
                    d.Hotkeys.SyncRecording(recording.Info.Mode);
                }
                return;
            case DictationState.Processing:
                // One dictation at a time; the previous one is still being transcribed.
                if (fromTrigger)
                {
                    d.Hotkeys.SyncRecording(null);
                }
                return;
            case DictationState.Message:
                if (!waitsForHold)
                {
                    CancelMessageTimer();
                    State = DictationState.IdleState;
                }
                break;
        }

        if (d.Meetings.IsMeetingRecording)
        {
            Refuse(FlowMessage.BlockedByMeeting, fromTrigger, waitsForHold);
            return;
        }
        if (d.Permissions.Status(PermissionKind.Microphone) == PermissionStatus.Denied)
        {
            Refuse(FlowMessage.For(DictationFailure.MicrophoneDenied), fromTrigger, waitsForHold);
            return;
        }
        var app = target is DictationTarget.ForegroundApp ? d.ForegroundApp.CurrentApp() : null;
        try
        {
            d.Recorder.Start(d.Settings.MicrophoneUid);
        }
        catch (Exception error)
        {
            Logger.Error("Could not start recording", error);
            Refuse(FlowMessage.For(DictationFailure.RecordingFailed(error.Message)), fromTrigger, waitsForHold);
            return;
        }

        recordingApp = app;
        Level = 0;
        HandsFreeRemaining = null;
        var info = new RecordingInfo(mode, d.Uptime.Now, target);
        if (waitsForHold)
        {
            unconfirmedRecording = info;
            ScheduleHoldCheck(HoldCheckDelay ?? d.Settings.HotkeyConfiguration.MinimumHold);
            return;
        }
        if (!fromTrigger)
        {
            d.Hotkeys.SyncRecording(mode);
        }
        ShowRecording(info);
    }

    /// <summary>Makes a running recording visible: the Flow bar pill, the start sound, the time limit.</summary>
    void ShowRecording(RecordingInfo info)
    {
        // The recorder opens the device in the background and stops by itself when that fails.
        if (!d.Recorder.IsRecording)
        {
            MicrophoneFailed();
            return;
        }
        ClearUnconfirmedRecording();
        CancelMessageTimer();
        State = new DictationState.Recording(info);
        Play(SoundCue.Start);
        StartLimitTimer();
    }

    void ScheduleHoldCheck(double delay)
    {
        holdCheckTimer?.Dispose();
        holdCheckTimer = scheduler.Schedule(delay, () =>
        {
            holdCheckTimer = null;
            HoldCheckDue();
        });
    }

    /// <summary>
    /// The minimum hold has passed since the key went down. A key still held is a dictation.
    /// A released key was a tap, which the key state machine promotes to hands-free or
    /// discards within the double-tap window; a second check after that window stops a tap
    /// nobody resolved, so the microphone never runs unseen.
    /// </summary>
    internal void HoldCheckDue()
    {
        if (unconfirmedRefusal is { } refusal)
        {
            unconfirmedRefusal = null;
            if (d.Hotkeys.IsKeyDown)
            {
                ShowMessage(refusal);
            }
            return;
        }
        if (unconfirmedRecording is not { } info)
        {
            return;
        }
        if (d.Hotkeys.IsKeyDown)
        {
            ShowRecording(info);
        }
        else if (!isWaitingForTapDecision)
        {
            isWaitingForTapDecision = true;
            ScheduleHoldCheck(HoldCheckDelay ?? d.Settings.HotkeyConfiguration.DoubleTapWindow + 0.1);
        }
        else
        {
            Logger.Info("A tap of the dictation key was never resolved; stopping its capture");
            StopRecording(sound: null, keepAudio: false, fromTrigger: false);
        }
    }

    void ClearUnconfirmedRecording()
    {
        unconfirmedRecording = null;
        unconfirmedRefusal = null;
        isWaitingForTapDecision = false;
        holdCheckTimer?.Dispose();
        holdCheckTimer = null;
    }

    void Refuse(FlowMessage message, bool fromTrigger, bool waitsForHold)
    {
        // The key state machine already believes a recording started.
        if (fromTrigger)
        {
            d.Hotkeys.SyncRecording(null);
        }
        if (!waitsForHold)
        {
            ShowMessage(message);
            return;
        }
        unconfirmedRefusal = message;
        ScheduleHoldCheck(HoldCheckDelay ?? d.Settings.HotkeyConfiguration.MinimumHold);
    }

    /// <summary>
    /// The recorder stopped by itself: the microphone could not be opened, or it was lost
    /// mid-recording and no other input took over. What it captured before that is still a
    /// dictation; a recording that kept no speech ends with an error, not "Nothing heard".
    /// </summary>
    void MicrophoneFailed()
    {
        Logger.Error("The microphone stopped before the recording ended");
        if (State.IsRecording || unconfirmedRecording is not null)
        {
            FinishRecording(fromTrigger: false);
            return;
        }
        // Shown before anything could be captured, such as a Flow bar start whose device
        // failed at once.
        d.Recorder.Cancel();
        recordingApp = null;
        Level = 0;
        HandsFreeRemaining = null;
        d.Hotkeys.SyncRecording(null);
        ShowMicrophoneFailure();
    }

    void ShowMicrophoneFailure() =>
        ShowMessage(FlowMessage.For(DictationFailure.RecordingFailed(CoreStrings.Get("dictation.error.microphoneFailed"))));

    void PromoteToHandsFree()
    {
        if (unconfirmedRecording is { } pending)
        {
            ShowRecording(pending with { Mode = DictationMode.HandsFree });
            CheckRecordingLimit();
            return;
        }
        if (State is not DictationState.Recording { Info: { Mode: DictationMode.PushToTalk } info })
        {
            return;
        }
        State = new DictationState.Recording(info with { Mode = DictationMode.HandsFree });
        CheckRecordingLimit();
    }

    sealed record FinishedRecording(RecordedAudio Audio, RecordingInfo Info, AppIdentity? App);

    /// <summary>Stops capture and resets the recording state; null when nothing was recording.</summary>
    FinishedRecording? StopRecording(SoundCue? sound, bool keepAudio, bool fromTrigger)
    {
        RecordingInfo info;
        bool wasVisible;
        if (State is DictationState.Recording current)
        {
            info = current.Info;
            wasVisible = true;
        }
        else if (unconfirmedRecording is { } unconfirmed)
        {
            info = unconfirmed;
            wasVisible = false;
        }
        else
        {
            return null;
        }
        ClearUnconfirmedRecording();
        limitTimer?.Dispose();
        limitTimer = null;
        RecordedAudio audio;
        if (keepAudio)
        {
            audio = d.Recorder.Stop();
        }
        else
        {
            d.Recorder.Cancel();
            audio = RecordedAudio.Empty;
        }
        var app = recordingApp;
        recordingApp = null;
        Level = 0;
        HandsFreeRemaining = null;
        if (wasVisible)
        {
            State = DictationState.IdleState;
        }
        // A capture nobody saw ends silently, even when it is kept as a dictation: a stop cue
        // with no start cue before it sounds like a glitch.
        if (sound is { } cue && wasVisible)
        {
            Play(cue);
        }
        if (!fromTrigger)
        {
            d.Hotkeys.SyncRecording(null);
        }
        return new FinishedRecording(audio, info, app);
    }

    void CancelDictation(bool fromTrigger)
    {
        if (unconfirmedRecording is not null)
        {
            StopRecording(SoundCue.Cancel, keepAudio: false, fromTrigger);
            return;
        }
        switch (State)
        {
            case DictationState.Recording:
                StopRecording(SoundCue.Cancel, keepAudio: false, fromTrigger);
                break;
            case DictationState.Processing:
                AbandonProcessing();
                State = DictationState.IdleState;
                break;
        }
    }

    void FinishRecording(bool fromTrigger)
    {
        if (!State.IsRecording && unconfirmedRecording is null)
        {
            return;
        }
        // False when the recorder stopped by itself; Stop() still returns what it kept.
        var microphoneWorked = d.Recorder.IsRecording;
        if (StopRecording(microphoneWorked ? SoundCue.Stop : null, keepAudio: true, fromTrigger) is not { } recording)
        {
            return;
        }
        if (SpeechEnergy.IsEffectivelySilent(recording.Audio))
        {
            if (microphoneWorked)
            {
                ShowMessage(FlowMessage.NothingHeard);
            }
            else
            {
                ShowMicrophoneFailure();
            }
            return;
        }
        if (!microphoneWorked)
        {
            Logger.Info("Transcribing what was captured before the microphone stopped");
        }
        var languageMode = d.Settings.LanguageMode;
        var engineId = d.Engines.EngineFor(EnginePurpose.Dictation(languageMode));
        var isLoadingModel = d.Engines.LoadState(engineId).Kind != EngineLoadStateKind.Ready;
        State = new DictationState.Processing(isLoadingModel);
        processingNumber += 1;
        var number = processingNumber;
        StartProcessingWatchdog(number, isLoadingModel ? ModelLoadTimeLimit : TranscriptionTimeLimit + recording.Audio.Duration);
        var cancellation = new CancellationTokenSource();
        processingCancellation = cancellation;
        latestProcessingTask = ProcessAsync(recording, engineId, languageMode, number, cancellation.Token);
    }

    /// <summary>
    /// Abandons dictation <paramref name="number"/> with an error once <paramref name="limit"/>
    /// seconds pass. The engine may still finish in the background; its result is dropped
    /// like a cancelled one.
    /// </summary>
    void StartProcessingWatchdog(int number, double limit)
    {
        processingWatchdog?.Dispose();
        processingWatchdog = scheduler.Schedule(limit, () =>
        {
            if (!IsCurrent(number))
            {
                return;
            }
            Logger.Error(string.Create(CultureInfo.InvariantCulture, $"Dictation {number} took longer than {(int)limit} s; abandoning it"));
            processingWatchdog = null;
            AbandonProcessing();
            ShowMessage(FlowMessage.For(DictationFailure.EngineFailed(CoreStrings.Get("dictation.error.timedOut"))));
        });
    }

    void StopProcessingWatchdog(int number)
    {
        if (number != processingNumber)
        {
            return;
        }
        processingWatchdog?.Dispose();
        processingWatchdog = null;
    }

    void RecordingLevelChanged(float value)
    {
        if (!State.IsRecording)
        {
            return;
        }
        Level = value;
    }

    void StartLimitTimer()
    {
        limitTimer?.Dispose();
        limitTimer = scheduler.Schedule(LimitCheckInterval, LimitTimerFired);
    }

    void LimitTimerFired()
    {
        limitTimer = scheduler.Schedule(LimitCheckInterval, LimitTimerFired);
        CheckRecordingLimit();
    }

    /// <summary>
    /// Updates the countdown and finishes a recording that reached the maximum length.
    /// Push-to-talk has the same limit, so a key stuck down cannot record forever. Also
    /// notices a microphone that could not be opened while the recording is shown.
    /// </summary>
    internal void CheckRecordingLimit()
    {
        if (State is not DictationState.Recording { Info: var info })
        {
            return;
        }
        if (!d.Recorder.IsRecording)
        {
            MicrophoneFailed();
            return;
        }
        var elapsed = d.Uptime.Now - info.StartedAt;
        var remaining = d.Settings.MaxHandsFreeSeconds - elapsed;
        if (!(remaining > 0))
        {
            Logger.Info("The recording reached its time limit");
            FinishRecording(fromTrigger: false);
            return;
        }
        double? countdown = info.Mode == DictationMode.HandsFree && remaining <= CountdownThreshold ? Math.Ceiling(remaining) : null;
        if (countdown != HandsFreeRemaining)
        {
            HandsFreeRemaining = countdown;
        }
    }

    // MARK: - Transcription and delivery

    async Task ProcessAsync(FinishedRecording recording, EngineId engineId, LanguageMode languageMode, int number,
                            CancellationToken cancellation)
    {
        try
        {
            await TranscribeAsync(recording, engineId, languageMode, number, cancellation).ResumeOn(d.MainThread);
        }
        catch (Exception error)
        {
            // Nothing below should throw; a store or inserter that does must not leave the
            // Flow bar busy until the time limit.
            Logger.Error("Dictation failed", error);
            if (IsCurrent(number))
            {
                ShowMessage(FlowMessage.For(DictationFailure.EngineFailed(error.Message)));
            }
        }
        finally
        {
            StopProcessingWatchdog(number);
            if (number == processingNumber)
            {
                processingCancellation = null;
            }
        }
    }

    async Task TranscribeAsync(FinishedRecording recording, EngineId engineId, LanguageMode languageMode, int number,
                               CancellationToken cancellation)
    {
        var entryId = Guid.NewGuid();
        var date = DateTimeOffset.UtcNow;
        var target = recording.Info.Target;
        var keepsAudio = target is not DictationTarget.Practice && d.Settings.KeepDictationAudio != RetentionPeriod.Never;
        // Engines read files, so the audio is written to a temporary file first. It moves
        // into Recordings/ only together with its history entry, so retention never finds
        // the file of a dictation that is still being transcribed.
        var path = Path.Combine(TemporaryFolder, TemporaryRecordingPrefix + entryId.ToString("D").ToUpperInvariant() + ".wav");
        try
        {
            try
            {
                await Task.Run(() => WavFile.Write(recording.Audio, path), CancellationToken.None).ResumeOn(d.MainThread);
            }
            catch (Exception error)
            {
                if (!IsCurrent(number))
                {
                    return;
                }
                Logger.Error("Could not write the recording", error);
                ShowMessage(FlowMessage.For(DictationFailure.RecordingFailed(error.Message)));
                return;
            }
            // Abandoned while the file was written: no engine is loaded for it.
            if (!IsCurrent(number))
            {
                return;
            }

            string rawText;
            try
            {
                var engine = await EngineAsync(engineId, languageMode, cancellation).ResumeOn(d.MainThread);
                if (!IsCurrent(number))
                {
                    return;
                }
                if (State is DictationState.Processing { IsLoadingModel: true })
                {
                    // The load is over; transcription gets its own, shorter limit.
                    StartProcessingWatchdog(number, TranscriptionTimeLimit + recording.Audio.Duration);
                }
                State = new DictationState.Processing(false);
                rawText = await engine.TranscribeFileAsync(path, cancellation).ResumeOn(d.MainThread);
                d.Engines.MarkUsed(engineId);
            }
            catch (Exception error)
            {
                if (!IsCurrent(number))
                {
                    return;
                }
                Logger.Error($"Transcription with {engineId.RawValue()} failed", error);
                ShowMessage(error is EngineUnavailableException unavailable
                    ? FlowMessage.For(DictationFailure.ModelMissing(unavailable.EngineId))
                    : FlowMessage.For(DictationFailure.EngineFailed(error.Message)));
                return;
            }
            if (!IsCurrent(number))
            {
                return;
            }
            // The engine is done; delivering the text must not be cut short.
            StopProcessingWatchdog(number);

            var category = target is DictationTarget.ForegroundApp && recording.App is { } app
                ? d.Categories.CategoryFor(app)
                : AppCategory.Other;
            var result = d.Pipeline.Process(rawText, PipelineContext(category, languageMode));
            var text = result.Text.Trim();
            if (!HasContent(text))
            {
                ShowMessage(FlowMessage.NothingHeard);
                return;
            }

            switch (target)
            {
                case DictationTarget.ForegroundApp:
                    var insertion = await d.Inserter.InsertAsync(text).ResumeOn(d.MainThread);
                    // Cancelling while the paste runs cannot take the text back, so history still records it.
                    var isStillCurrent = IsCurrent(number);
                    switch (insertion)
                    {
                        case InsertionResult.Pasted:
                            if (isStillCurrent)
                            {
                                State = DictationState.IdleState;
                            }
                            break;
                        case InsertionResult.Copied copied:
                            Logger.Info($"Copied instead of pasting: {copied.Reason}");
                            if (isStillCurrent)
                            {
                                ShowMessage(FlowMessage.Copied);
                            }
                            break;
                        default:
                            if (isStillCurrent)
                            {
                                ShowMessage(FlowMessage.NothingHeard);
                            }
                            return;
                    }
                    break;
                case DictationTarget.Note note:
                    d.Notes.Append(text, note.Id);
                    State = DictationState.IdleState;
                    break;
                default:
                    PracticeResult = text;
                    State = DictationState.IdleState;
                    return;
            }

            LastTranscript = text;
            var audioFileName = keepsAudio ? KeepRecording(path, entryId, date) : null;
            d.History.Add(new HistoryEntry
            {
                Id = entryId,
                Date = date,
                Text = text,
                RawText = rawText,
                EngineId = engineId,
                LanguageMode = languageMode,
                AppBundleId = recording.App?.BundleId,
                AppName = recording.App?.Name,
                Category = category,
                Duration = recording.Audio.Duration,
                WordCount = HistoryEntry.CountWords(text),
                FixesCount = result.FixesCount,
                AudioFileName = audioFileName,
            });
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    /// <summary>
    /// Moves a transcribed recording to <c>Recordings/&lt;day&gt;/&lt;id&gt;.wav</c> and returns
    /// that relative path; null when the move fails, and the entry is kept without audio.
    /// </summary>
    string? KeepRecording(string path, Guid id, DateTimeOffset date)
    {
        var relativePath = WavFile.RelativePath(id, date);
        var destination = d.Paths.RecordingPath(relativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(path, destination);
            return relativePath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Could not keep the recording", error);
            return null;
        }
    }

    static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere; the sweep at the next start removes it.
        }
    }

    TextPipelineContext PipelineContext(AppCategory category, LanguageMode languageMode) => new()
    {
        Dictionary = d.Dictionary.Items,
        Snippets = d.Snippets.Snippets,
        Cleanup = d.Settings.Cleanup,
        Style = d.Settings.Style(category),
        LanguageMode = languageMode,
    };

    /// <summary>
    /// Whether a result is worth delivering. Cleanup turns a lone filler ("Эм.", "Hmm?",
    /// "Э...") into bare punctuation, which is not. Letters and digits count, and so do
    /// symbols, so a snippet that expands to an emoji still arrives.
    /// </summary>
    internal static bool HasContent(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.MathSymbol
                    or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol)
            {
                return true;
            }
        }
        return false;
    }

    bool IsCurrent(int number) => number == processingNumber && State is DictationState.Processing;

    void AbandonProcessing()
    {
        processingNumber += 1;
        var cancellation = processingCancellation;
        processingCancellation = null;
        processingWatchdog?.Dispose();
        processingWatchdog = null;
        // Last: an engine that honours the token may finish the abandoned dictation right here.
        cancellation?.Cancel();
    }

    /// <summary>
    /// The engine for a language mode when the provider tells modes apart, otherwise the one
    /// current settings use.
    /// </summary>
    Task<ITranscriptionEngine> EngineAsync(EngineId id, LanguageMode languageMode, CancellationToken cancellation) =>
        d.Engines is ILanguageEngineProvider languages
            ? languages.EngineAsync(id, languageMode, cancellation)
            : d.Engines.EngineAsync(id, cancellation);

    /// <summary>Waits for the most recent dictation's work, also an abandoned one; for tests.</summary>
    internal Task WaitForProcessingAsync() => latestProcessingTask ?? Task.CompletedTask;

    void ShowMessage(FlowMessage message)
    {
        messageTimer?.Dispose();
        var shown = new DictationState.Message(message);
        State = shown;
        messageTimer = scheduler.Schedule(MessageLifetime, () =>
        {
            if (State != shown)
            {
                return;
            }
            State = DictationState.IdleState;
            messageTimer = null;
        });
    }

    void CancelMessageTimer()
    {
        messageTimer?.Dispose();
        messageTimer = null;
    }

    void Play(SoundCue cue)
    {
        if (d.Sounds is { } sounds && d.Settings.SoundsEnabled)
        {
            sounds.Play(cue);
        }
    }
}
