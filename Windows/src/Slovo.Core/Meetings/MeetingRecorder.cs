using Slovo.Core.Audio;
using Slovo.Core.Dictation;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Meetings;

/// <summary>
/// Records a meeting (port of the macOS <c>MeetingRecorder</c>): the microphone into
/// <c>mic.caf</c> and, when enabled, everything the computer plays into <c>system.caf</c>.
/// Dictation is blocked meanwhile (<see cref="IMeetingActivity"/>).
/// </summary>
/// <remarks>
/// <para>
/// Both files grow on disk as the meeting goes on and share one 16 kHz timeline that starts
/// at the instant the recording starts, so a segment's start time means the same moment in
/// both tracks and a marked moment points at the same place in each. Every buffer is placed
/// at its capture time (<see cref="MeetingTrackWriter"/>); a track that receives nothing —
/// loopback while nothing plays, a microphone that is gone or switched off — is filled with
/// silence as the meeting goes on, and both tracks are filled to the same end when it stops.
/// </para>
/// <para>
/// A sleep is read from the clocks: when the wall clock moved further than the uptime clock
/// between two readings, the uptime clock stood still meanwhile, and both tracks are padded
/// with that much silence (queued on the writers, never written from the UI thread). When the
/// uptime clock counts sleep itself, the buffers after the wake carry times that far along and
/// the tracks fill themselves; only the disk is checked then.
/// </para>
/// <para>
/// Threading: create it anywhere, then use every member on the main (UI) thread, the
/// counterpart of Swift's <c>@MainActor</c>; PropertyChanged is raised there. Capture callbacks
/// arrive on the streams' threads and go straight to the track writers, each of which writes on
/// a serial queue of its own; levels and stream events are marshalled to the main thread
/// through <see cref="IMainThread"/>. Opening and closing streams and files runs on the thread
/// pool. Time comes from <see cref="IUptime"/> and the wall clock from a <see cref="TimeProvider"/>,
/// and timers go through <see cref="IScheduler"/>, so tests drive everything with a manual clock.
/// </para>
/// </remarks>
public sealed class MeetingRecorder : Observable, IMeetingActivity
{
    /// <summary>Everything the recorder talks to.</summary>
    public sealed class Dependencies
    {
        public required IMeetingStore Store { get; init; }
        public required AppSettings Settings { get; init; }
        public required IMeetingAudioSources Sources { get; init; }
        public required IMainThread MainThread { get; init; }
        public IUptime Uptime { get; init; } = SystemUptime.Shared;
        /// <summary>Null means a <see cref="MainThreadScheduler"/> on <see cref="MainThread"/>.</summary>
        public IScheduler? Scheduler { get; init; }
        /// <summary>The wall clock: meeting and highlight dates, and telling a sleep from the uptime clock.</summary>
        public TimeProvider Clock { get; init; } = TimeProvider.System;
        /// <summary>Played only while the sounds setting is on.</summary>
        public ISoundPlayer? Sounds { get; init; }
        /// <summary>A recording with the microphone on is refused while microphone access is denied.</summary>
        public IPermissions? Permissions { get; init; }
        /// <summary>Free bytes on the meetings volume, or null when unknown; null means <see cref="AvailableBytes"/> of the meetings folder.</summary>
        public Func<long?>? AvailableSpace { get; init; }
        /// <summary>Records the screen when <see cref="AppSettings.RecordScreen"/> is on; null where the screen cannot be recorded at all.</summary>
        public IScreenCapture? ScreenCapture { get; init; }
    }

    /// <summary>Two marks closer than this on the audio timeline count as one press.</summary>
    public const double MomentDeduplicationSeconds = 2;
    /// <summary>
    /// A sleep shorter than this is not padded. The wall clock the sleep is read against can be
    /// stepped by a time sync, and a real sleep and wake take seconds.
    /// </summary>
    public const double MinimumSleepSeconds = 2;
    /// <summary>An uptime jump this long between two readings is checked against the free space it will take on the tracks.</summary>
    public const double ClockJumpSeconds = 60;
    /// <summary>
    /// A recording is refused below this much free space: the two tracks take about 230 MB an
    /// hour, and a disk that fills mid-meeting costs the recording.
    /// </summary>
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>
    /// A recording ends by itself, saved, once the disk has less than this left: about a quarter
    /// of an hour of both tracks, enough to close the files and save the meeting, where a disk
    /// that fills completely fails every write at once.
    /// </summary>
    public const long MinimumFreeBytesToContinue = 100L * 1024 * 1024;
    /// <summary>
    /// System audio that stays silent this long from the start is reported as
    /// <see cref="SystemAudioStatusKind.Silent"/>. A call commonly opens with a few quiet
    /// seconds, which is not worth a warning.
    /// </summary>
    public const double SilenceTimeout = 8;
    /// <summary>How often the elapsed time and the clocks are read.</summary>
    public const double TickSeconds = 0.25;
    /// <summary>
    /// Tracks that receive nothing are filled with silence up to this long before now. Capture
    /// delivers well within it, so a buffer that arrives afterwards still finds its place free.
    /// </summary>
    public const double LiveFillLag = 2;
    /// <summary>The wait before trying again to open a microphone switched on during a recording that did not open.</summary>
    public const double MicrophoneRetrySeconds = 3;

    /// <summary>Every fourth tick (1 s): silence fill and the system audio silence check.</summary>
    const int FillTicks = 4;
    /// <summary>Every 120th tick (30 s): free space.</summary>
    const int HealthTicks = 120;

    static readonly Logger Logger = Log.For("MeetingRecorder");

    readonly Dependencies d;
    readonly IScheduler scheduler;

    MeetingRecorderState state = MeetingRecorderState.IdleState;
    bool isStarting;
    double elapsed;
    float microphoneLevel;
    float systemLevel;
    SystemAudioStatus systemAudioStatus = SystemAudioStatus.Off;
    ScreenRecordingStatus screenRecordingStatus = ScreenRecordingStatus.Off;
    string? screenCaptureAppName;
    MicrophoneStatus microphoneStatus = MicrophoneStatus.Capturing;
    bool isMicrophoneOn = true;
    MicrophoneSwitch? lastMicrophoneSwitch;
    MeetingHighlight? lastMarkedMoment;
    int markedCount;
    string? lastFailure;
    MeetingRecord? autoStoppedMeeting;

    RecordingSession? session;
    /// <summary>The session <see cref="StartAsync"/> is still opening tracks for.</summary>
    RecordingSession? startingSession;
    /// <summary>The last moment marked in the current recording, for de-duplication.</summary>
    MeetingHighlight? sessionMoment;
    bool starting;
    /// <summary>
    /// Track events that arrive while <see cref="StartAsync"/> is still opening the other
    /// tracks, before a session exists to apply them to; they run once it has one, so a
    /// failure in those first moments is acted on instead of dropped.
    /// </summary>
    List<Action> eventsDuringStart = [];
    IDisposable? ticker;
    int tick;

    public MeetingRecorder(Dependencies dependencies)
    {
        d = dependencies;
        scheduler = dependencies.Scheduler ?? new MainThreadScheduler(dependencies.MainThread);
        var folder = dependencies.Store.MeetingsFolder;
        AvailableSpace = dependencies.AvailableSpace ?? (() => AvailableBytes(folder));
    }

    /// <summary>Free space on the meetings volume, re-read while recording; tests replace it.</summary>
    internal Func<long?> AvailableSpace { get; set; }

    // MARK: - Observable state

    public MeetingRecorderState State
    {
        get => state;
        private set
        {
            if (SetField(ref state, value))
            {
                OnPropertyChanged(nameof(IsMeetingRecording));
                OnPropertyChanged(nameof(RecordingTracks));
            }
        }
    }

    /// <summary>True while <see cref="StartAsync"/> opens the tracks, which can take seconds while devices wake up.</summary>
    public bool IsStarting
    {
        get => isStarting;
        private set => SetField(ref isStarting, value);
    }

    /// <summary>Seconds since the recording started, sleeps included, refreshed four times per second.</summary>
    public double Elapsed
    {
        get => elapsed;
        private set => SetField(ref elapsed, value);
    }

    /// <summary>The microphone meter from 0 to 1, about 30 times per second; 0 while the microphone is off.</summary>
    public float MicrophoneLevel
    {
        get => microphoneLevel;
        private set => SetField(ref microphoneLevel, value);
    }

    /// <summary>The system audio meter from 0 to 1.</summary>
    public float SystemLevel
    {
        get => systemLevel;
        private set => SetField(ref systemLevel, value);
    }

    public SystemAudioStatus SystemAudioStatus
    {
        get => systemAudioStatus;
        private set
        {
            if (SetField(ref systemAudioStatus, value))
            {
                OnPropertyChanged(nameof(RecordingTracks));
            }
        }
    }

    /// <summary>Whether the recording is also capturing the screen right now.</summary>
    public ScreenRecordingStatus ScreenRecordingStatus
    {
        get => screenRecordingStatus;
        private set => SetField(ref screenRecordingStatus, value);
    }

    /// <summary>
    /// The app a screen recording is scoped to, for the recording panel, whose system meter then
    /// shows that app's own sound; null for the whole display or when nothing is capturing.
    /// </summary>
    public string? ScreenCaptureAppName
    {
        get => screenCaptureAppName;
        private set => SetField(ref screenCaptureAppName, value);
    }

    /// <summary>
    /// Whether the microphone is still being recorded. A meeting outlives a lost microphone, so
    /// this degrades rather than ending the recording, and clears when a device comes back.
    /// </summary>
    public MicrophoneStatus MicrophoneStatus
    {
        get => microphoneStatus;
        private set
        {
            if (SetField(ref microphoneStatus, value))
            {
                OnPropertyChanged(nameof(RecordingTracks));
            }
        }
    }

    /// <summary>
    /// Whether the microphone is being recorded or gated to silence. A recording starts as
    /// <see cref="AppSettings.RecordMicrophone"/> says and can be switched while it lasts.
    /// </summary>
    public bool IsMicrophoneOn
    {
        get => isMicrophoneOn;
        private set
        {
            if (SetField(ref isMicrophoneOn, value))
            {
                OnPropertyChanged(nameof(RecordingTracks));
            }
        }
    }

    /// <summary>The switch made last, for the Flow bar's short confirmation; null until the microphone is switched during a recording.</summary>
    public MicrophoneSwitch? LastMicrophoneSwitch
    {
        get => lastMicrophoneSwitch;
        private set => SetField(ref lastMicrophoneSwitch, value);
    }

    /// <summary>The moment marked last, for the Flow bar's short confirmation.</summary>
    public MeetingHighlight? LastMarkedMoment
    {
        get => lastMarkedMoment;
        private set => SetField(ref lastMarkedMoment, value);
    }

    /// <summary>Moments marked in the current recording, for the recording panel.</summary>
    public int MarkedCount
    {
        get => markedCount;
        private set => SetField(ref markedCount, value);
    }

    /// <summary>
    /// The message of a recording that ended by itself, so the app can tell the owner even with
    /// no window open. Null until something fails after a start; cleared by the next start.
    /// </summary>
    public string? LastFailure
    {
        get => lastFailure;
        private set => SetField(ref lastFailure, value);
    }

    /// <summary>
    /// The meeting a recording that ended by itself left behind, saved and published after
    /// <see cref="LastFailure"/>, so the app transcribes it like one the owner stopped.
    /// </summary>
    public MeetingRecord? AutoStoppedMeeting
    {
        get => autoStoppedMeeting;
        private set => SetField(ref autoStoppedMeeting, value);
    }

    /// <summary>True while recording and while the files are being closed after stop.</summary>
    public bool IsMeetingRecording => state is MeetingRecorderState.Recording or MeetingRecorderState.Stopping;

    /// <summary>The id of the meeting being recorded.</summary>
    public Guid? CurrentMeetingId => session?.MeetingId;

    /// <summary>The tracks sound is going to right now; null unless a meeting is recording.</summary>
    public MeetingRecordingTracks? RecordingTracks =>
        state is MeetingRecorderState.Recording && session is not null
            ? MeetingRecordingTracksExtensions.From(isMicrophoneOn, microphoneStatus, systemAudioStatus)
            : null;

    // MARK: - Recording

    /// <summary>
    /// Creates the meeting and starts both tracks. A system audio failure does not stop the
    /// recording; it shows in <see cref="SystemAudioStatus"/>. Throws SlovoException (and sets
    /// <see cref="State"/> to Failed) when the meeting cannot be recorded at all.
    /// </summary>
    public async Task<MeetingRecord> StartAsync()
    {
        if (session is not null || starting || IsMeetingRecording)
        {
            throw MeetingErrors.AlreadyRecording();
        }
        starting = true;
        IsStarting = true;
        eventsDuringStart = [];
        try
        {
            return await StartRecordingAsync();
        }
        finally
        {
            starting = false;
            startingSession = null;
            eventsDuringStart = [];
            IsStarting = false;
        }
    }

    async Task<MeetingRecord> StartRecordingAsync()
    {
        var microphoneOn = d.Settings.RecordMicrophone;
        if (microphoneOn && d.Permissions?.Status(PermissionKind.Microphone) == PermissionStatus.Denied)
        {
            throw Fail(MeetingErrors.MicrophoneDenied());
        }
        // Refused before anything is created: the files are the point of a meeting, and a
        // disk that fills halfway through costs the part that matters most.
        if (LowSpaceError(AvailableSpace()) is { } lowSpace)
        {
            throw Fail(lowSpace);
        }
        LastFailure = null;

        MeetingRecord meeting;
        try
        {
            meeting = d.Store.Create(MeetingTitles.DefaultTitle(d.Clock.GetUtcNow(), d.Settings.AppLanguage));
        }
        catch (SlovoException error)
        {
            throw Fail(error);
        }

        // One origin for both tracks, taken before either capture starts.
        var origin = d.Uptime.Now;
        MeetingTrackWriter microphoneWriter;
        MeetingTrackWriter systemWriter;
        try
        {
            // Gated before capture starts, so a meeting that begins with the microphone off
            // never has a first buffer of the room on it.
            microphoneWriter = new MeetingTrackWriter(d.Store.MicrophoneAudioPath(meeting.Id), origin) { IsMuted = !microphoneOn };
        }
        catch (SlovoException error)
        {
            d.Store.Delete(meeting.Id);
            throw Fail(error);
        }
        try
        {
            systemWriter = new MeetingTrackWriter(d.Store.SystemAudioPath(meeting.Id), origin);
        }
        catch (SlovoException error)
        {
            microphoneWriter.Finish();
            d.Store.Delete(meeting.Id);
            throw Fail(error);
        }

        var s = new RecordingSession(meeting.Id, origin, microphoneWriter, systemWriter)
        {
            WallReference = d.Clock.GetUtcNow(),
        };
        startingSession = s;
        microphoneWriter.OnWriteFailure = error => OnMainThread(() => Forget(MicrophoneTrackFailedAsync(error, s)));
        systemWriter.OnWriteFailure = error => OnMainThread(() => Forget(SystemTrackFailedAsync(error, s, systemWriter)));

        if (microphoneOn)
        {
            IAudioStream microphone;
            try
            {
                microphone = d.Sources.Microphone(d.Settings.MicrophoneUid);
                await StartStreamAsync(s, microphone, isMicrophone: true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                microphoneWriter.Finish();
                systemWriter.Finish();
                d.Store.Delete(meeting.Id);
                throw Fail(error as SlovoException ?? MeetingErrors.MicrophoneFailed(error));
            }
            s.MicrophoneStream = microphone;
        }

        // A screen recording scoped to one app brings that app's own sound in place of the
        // system-wide capture; recording both would defeat the point of scoping down to it.
        var screenTarget = d.Settings.RecordScreen ? d.Settings.ScreenCaptureTarget : null;
        var recordingAppAudio = screenTarget is ScreenCaptureTarget.Application;
        if (!recordingAppAudio)
        {
            s.SystemStream = await StartSystemAudioAsync(s);
        }
        await StartScreenRecordingAsync(s, screenTarget);
        if (screenTarget is ScreenCaptureTarget.Application app)
        {
            if (s.ScreenCapture is not null)
            {
                s.SystemStream = await StartApplicationAudioAsync(s, app.BundleId);
            }
            // The app's own sound did not start (the app is not running, this Windows cannot
            // capture one app), so the system-wide capture records the call instead of no one's
            // side of it being recorded at all.
            s.SystemStream ??= await StartSystemAudioAsync(s);
        }
        meeting = (d.Store.Meeting(meeting.Id) ?? meeting) with
        {
            HasSystemAudio = s.SystemStream is not null,
            HasScreenRecording = s.ScreenWriter is not null,
        };
        d.Store.Update(meeting);

        var startedAt = d.Uptime.Now;
        s.StartedAt = startedAt;
        s.LastUptime = startedAt;
        s.LastDrift = WallSeconds(s) - startedAt;
        startingSession = null;
        session = s;
        sessionMoment = null;
        MarkedCount = 0;
        Elapsed = 0;
        MicrophoneStatus = MicrophoneStatus.Capturing;
        IsMicrophoneOn = microphoneOn;
        State = new MeetingRecorderState.Recording(meeting.Id, startedAt);
        StartTicker();
        Logger.Info("Meeting recording started");
        var events = eventsDuringStart;
        eventsDuringStart = [];
        foreach (var replay in events)
        {
            replay();
        }
        return d.Store.Meeting(meeting.Id) ?? meeting;
    }

    /// <summary>Stops both tracks and marks the meeting Recorded; null when nothing was recording.</summary>
    public async Task<MeetingRecord?> StopAsync()
    {
        // A sleep that ended since the last reading still belongs on the tracks and in the
        // length this meeting is saved with.
        CheckForSleep();
        if (session is not { } s)
        {
            return null;
        }
        var (plan, endTime, duration) = BeginStopping(s);
        var result = await Task.Run(() => plan.Finish(endTime)).ResumeOn(d.MainThread);
        return Finalize(s, result, duration);
    }

    /// <summary>Stops and closes the files on the calling thread, for app exit.</summary>
    public MeetingRecord? StopImmediately()
    {
        CheckForSleep();
        if (session is not { } s)
        {
            return null;
        }
        var (plan, endTime, duration) = BeginStopping(s);
        return Finalize(s, plan.Finish(endTime), duration);
    }

    /// <summary>
    /// Marks an important moment at the recording's current position on the tracks' shared
    /// timeline and saves it with the meeting. Two marks within 2 s count as one, and the
    /// second call returns the first highlight. Null unless recording.
    /// </summary>
    /// <remarks>
    /// The position is measured from the origin both track writers share, not from
    /// <see cref="MeetingRecorderState.Recording.StartedAt"/>: capture starts a moment after the
    /// origin is taken, and the start would place the mark that much later than the audio.
    /// </remarks>
    public MeetingHighlight? MarkMoment(string? note = null)
    {
        // A mark made in the first moments after a wake must not wait for the ticker to notice
        // the sleep, or it would sit that much earlier on the track.
        CheckForSleep();
        if (state is not MeetingRecorderState.Recording || session is not { } s)
        {
            return null;
        }
        var frame = MeetingTimeline.Frame(d.Uptime.Now, s.Origin);
        var time = (double)frame / MeetingTimeline.SampleRate + s.SleptSeconds;
        if (sessionMoment is { } previous && Math.Abs(time - previous.Time) < MomentDeduplicationSeconds)
        {
            return previous;
        }
        if (d.Store.Meeting(s.MeetingId) is not { } meeting)
        {
            return null;
        }
        var highlight = new MeetingHighlight
        {
            Time = MeetingTime.Clamped(time),
            Note = MeetingEdits.SanitizedNote(note),
            Source = HighlightSource.Recording,
            CreatedAt = d.Clock.GetUtcNow(),
        };
        d.Store.Update(meeting with { Highlights = [.. meeting.Highlights, highlight] });
        sessionMoment = highlight;
        MarkedCount++;
        LastMarkedMoment = highlight;
        return highlight;
    }

    /// <summary>
    /// Starts or stops recording the microphone in the current meeting, for this meeting only.
    /// The microphone track keeps the timeline: while off it receives silence of exactly the
    /// length it would have received sound. A microphone that was off from the start is opened
    /// now; once open it keeps running, so switching back is instant. Heard and shown, because it
    /// is switched without looking at Slovo. Returns whether anything changed.
    /// </summary>
    public bool SetMicrophoneOn(bool on)
    {
        if (state is not MeetingRecorderState.Recording || session is not { } s || on == IsMicrophoneOn)
        {
            return false;
        }
        s.MicrophoneWriter.IsMuted = !on;
        IsMicrophoneOn = on;
        if (!on)
        {
            MicrophoneLevel = 0;
        }
        Play(on ? SoundCue.Start : SoundCue.Stop);
        LastMicrophoneSwitch = new MicrophoneSwitch(on);
        Logger.Info($"Meeting microphone switched {(on ? "on" : "off")}");
        if (on && s.MicrophoneStream is null && !s.MicrophoneStarting && !s.MicrophoneTrackEnded)
        {
            Forget(OpenMicrophoneAsync(s));
        }
        else if (!on && s.MicrophoneStream is null && !s.MicrophoneTrackEnded && MicrophoneStatus == MicrophoneStatus.Lost)
        {
            // A microphone that never opened is not tried while it is switched off, so it is
            // not missing either.
            s.MicrophoneRetry?.Dispose();
            s.MicrophoneRetry = null;
            MicrophoneStatus = MicrophoneStatus.Capturing;
        }
        return true;
    }

    /// <summary>Flips the microphone of the current meeting; false unless recording.</summary>
    public bool ToggleMicrophone() => SetMicrophoneOn(!IsMicrophoneOn);

    // MARK: - Starting tracks

    /// <summary>
    /// Starts a stream off the main thread (opening a device can block for seconds while a
    /// Bluetooth headset switches profiles) after wiring its events to the session. A stream
    /// that does not start is disposed.
    /// </summary>
    async Task StartStreamAsync(RecordingSession s, IAudioStream stream, bool isMicrophone)
    {
        Action<float[], double?> callback;
        if (isMicrophone)
        {
            stream.Failed += message => OnMainThread(() => MicrophoneLost(message, s));
            stream.Resumed += () => OnMainThread(() => MicrophoneBack(s, stream));
            callback = MicrophoneCallback(s);
        }
        else
        {
            stream.Failed += message => OnMainThread(() => SystemAudioInterrupted(message, s, stream));
            stream.Resumed += () => OnMainThread(() => SystemAudioReconnected(s, stream));
            callback = SystemCallback(s);
        }
        try
        {
            await Task.Run(() => stream.Start(callback)).ResumeOn(d.MainThread);
        }
        catch
        {
            DisposeInBackground(stream);
            throw;
        }
    }

    async Task<IAudioStream?> StartSystemAudioAsync(RecordingSession s)
    {
        if (!d.Settings.RecordSystemAudio)
        {
            SystemAudioStatus = SystemAudioStatus.Off;
            return null;
        }
        try
        {
            if (d.Sources.SystemAudio() is not { } stream)
            {
                SystemAudioStatus = new SystemAudioStatus(SystemAudioStatusKind.Unavailable, MeetingErrors.SystemAudioUnsupported().Message);
                return null;
            }
            await StartStreamAsync(s, stream, isMicrophone: false);
            s.SystemStartedAt = d.Uptime.Now;
            SystemAudioStatus = SystemAudioStatus.Capturing;
            return stream;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = error is SlovoException ? error.Message : CoreStrings.Format("meetings.error.tapFailed", error.Message);
            Logger.Error($"System audio unavailable: {message}");
            SystemAudioStatus = new SystemAudioStatus(SystemAudioStatusKind.Unavailable, message);
            return null;
        }
    }

    /// <summary>
    /// The sound of the app a screen recording is scoped to, written to <c>system.caf</c> in place
    /// of everything the computer plays. Null when it did not start; the caller falls back to
    /// system audio. Unlike on macOS, where the app's sound rides on the screen capture, it is a
    /// capture of its own here, so it keeps recording if the app's video ends.
    /// </summary>
    async Task<IAudioStream?> StartApplicationAudioAsync(RecordingSession s, string bundleId)
    {
        try
        {
            if (d.Sources.ApplicationAudio(bundleId) is not { } stream)
            {
                Logger.Info("This system cannot record one app's sound; recording system audio instead");
                return null;
            }
            s.SystemIsApplicationAudio = true;
            await StartStreamAsync(s, stream, isMicrophone: false);
            s.SystemStartedAt = d.Uptime.Now;
            SystemAudioStatus = SystemAudioStatus.Capturing;
            return stream;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            s.SystemIsApplicationAudio = false;
            Logger.Error($"The app's sound did not start; recording system audio instead: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// Starts the optional screen track (<c>screen.mov</c>). Any failure leaves the meeting
    /// recording its audio and shows in <see cref="ScreenRecordingStatus"/>.
    /// </summary>
    async Task StartScreenRecordingAsync(RecordingSession s, ScreenCaptureTarget? target)
    {
        if (target is null)
        {
            ScreenRecordingStatus = ScreenRecordingStatus.Off;
            ScreenCaptureAppName = null;
            return;
        }
        if (d.ScreenCapture is not { } screen)
        {
            ScreenRecordingStatus = ScreenRecordingStatus.Unavailable(MeetingErrors.ScreenRecordingUnavailable().Message);
            ScreenCaptureAppName = null;
            return;
        }
        ScreenCaptureAppName = target is ScreenCaptureTarget.Application app ? AppName(screen, app.BundleId) : null;
        var path = d.Store.ScreenRecordingPath(s.MeetingId);
        var capture = new MeetingScreenCapture(screen, d.Uptime, s.Origin, ScreenCaptureOwnThread) { SleptSeconds = s.SleptSeconds };
        capture.OnLost = reason => OnMainThread(() => Forget(RetireScreenTrackAsync(ScreenRecordingStatus.Lost, s, capture, reason)));
        capture.OnPauseChanged = paused => OnMainThread(() => ScreenCapturePaused(paused, s, capture));
        capture.OnVideoFailed = error => OnMainThread(() => Forget(RetireScreenTrackAsync(
            ScreenRecordingStatus.Unavailable(FileError(error).Message), s, capture, error.Message)));
        MeetingScreenWriter? writer = null;
        try
        {
            await Task.Run(() => capture.Start(target, (width, height) => writer = new MeetingScreenWriter(path, width, height,
                error => OnMainThread(() => Forget(RetireScreenTrackAsync(
                    ScreenRecordingStatus.Unavailable(FileError(error).Message), s, capture, error.Message))),
                d.Clock.GetUtcNow()))).ResumeOn(d.MainThread);
            if (writer is null)
            {
                throw MeetingErrors.ScreenRecordingUnavailable();
            }
            s.ScreenCapture = capture;
            s.ScreenWriter = writer;
            ScreenRecordingStatus = ScreenRecordingStatus.Capturing;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = error is SlovoException ? error.Message : MeetingErrors.ScreenRecordingUnavailable().Message;
            Logger.Error($"Screen recording unavailable: {error.Message}");
            _ = Task.Run(capture.Stop);
            writer?.Finish();
            DeleteFile(path);
            ScreenRecordingStatus = ScreenRecordingStatus.Unavailable(message);
            ScreenCaptureAppName = null;
        }
    }

    /// <summary>The app's name as the picker shows it, or its id when it is not running.</summary>
    static string AppName(IScreenCapture screen, string bundleId)
    {
        try
        {
            return screen.CapturableApps().FirstOrDefault(app => app.BundleId == bundleId)?.Name ?? bundleId;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Error($"Listing apps failed: {error.Message}");
            return bundleId;
        }
    }

    /// <summary>
    /// Stops screen capture for the rest of this meeting, whatever the reason: a lost capture, a
    /// restart that failed, the encoder or the disk failing. The meeting keeps recording its audio
    /// regardless, the app's own sound included: here it is a capture of its own.
    /// </summary>
    /// <remarks>
    /// What the track recorded until now is kept: the fragmented video is readable up to the
    /// moment it stopped. Only a file that never received a picture is removed.
    /// <paramref name="capture"/> names the capture an event came from, so a late one from an
    /// earlier capture cannot end a newer one.
    /// </remarks>
    async Task RetireScreenTrackAsync(ScreenRecordingStatus status, RecordingSession s, MeetingScreenCapture capture, string reason)
    {
        if (Deferred(s, () => Forget(RetireScreenTrackAsync(status, s, capture, reason))))
        {
            return;
        }
        if (s.ScreenCapture != capture)
        {
            return;
        }
        var writer = s.ScreenWriter;
        s.ScreenCapture = null;
        s.ScreenWriter = null;
        // Said before waiting for the capture to close, so a stop that lands meanwhile ends on
        // the status it sets itself.
        ScreenRecordingStatus = status;
        if (!s.SystemIsApplicationAudio)
        {
            ScreenCaptureAppName = null;
        }
        var kept = await Task.Run(() =>
        {
            capture.Stop();
            return writer?.Finish() ?? false;
        }).ResumeOn(d.MainThread);
        if (!kept)
        {
            DeleteFile(d.Store.ScreenRecordingPath(s.MeetingId));
            if (d.Store.Meeting(s.MeetingId) is { HasScreenRecording: true } meeting)
            {
                d.Store.Update(meeting with { HasScreenRecording = false });
            }
        }
        Logger.Error($"The screen recording track stopped; what it recorded is kept: {reason}");
    }

    /// <summary>A locked screen pauses capture and unlocking resumes it; the panel says so.</summary>
    void ScreenCapturePaused(bool paused, RecordingSession s, MeetingScreenCapture capture)
    {
        if (Deferred(s, () => ScreenCapturePaused(paused, s, capture)) || s.ScreenCapture != capture)
        {
            return;
        }
        if (paused && ScreenRecordingStatus.Kind == ScreenRecordingStatusKind.Capturing)
        {
            ScreenRecordingStatus = ScreenRecordingStatus.PausedScreenLocked;
        }
        else if (!paused && ScreenRecordingStatus.Kind == ScreenRecordingStatusKind.PausedScreenLocked)
        {
            ScreenRecordingStatus = ScreenRecordingStatus.Capturing;
        }
    }

    /// <summary>
    /// Opens the microphone of a recording that started with it off, once it is switched on.
    /// A microphone that does not open shows as lost and is tried again every few seconds
    /// while it stays switched on.
    /// </summary>
    async Task OpenMicrophoneAsync(RecordingSession s)
    {
        s.MicrophoneStarting = true;
        s.MicrophoneRetry?.Dispose();
        s.MicrophoneRetry = null;
        IAudioStream stream;
        try
        {
            stream = d.Sources.Microphone(d.Settings.MicrophoneUid);
            await StartStreamAsync(s, stream, isMicrophone: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            s.MicrophoneStarting = false;
            if (session != s || s.MicrophoneTrackEnded)
            {
                return;
            }
            Logger.Error($"The microphone did not open; trying again: {error.Message}");
            MicrophoneLevel = 0;
            SetMicrophoneStatus(MicrophoneStatus.Lost);
            s.MicrophoneRetry = scheduler.Schedule(MicrophoneRetrySeconds, () =>
            {
                s.MicrophoneRetry = null;
                if (session == s && IsMicrophoneOn && s.MicrophoneStream is null && !s.MicrophoneStarting && !s.MicrophoneTrackEnded)
                {
                    Forget(OpenMicrophoneAsync(s));
                }
            });
            return;
        }
        s.MicrophoneStarting = false;
        if (session != s || s.MicrophoneTrackEnded)
        {
            // The recording ended while the device opened.
            DisposeInBackground(stream);
            return;
        }
        s.MicrophoneStream = stream;
        SetMicrophoneStatus(MicrophoneStatus.Capturing);
    }

    /// <summary>
    /// Runs on the microphone's delivery thread: boosts the samples, as on macOS, and hands
    /// them to the writer with their capture time. The level is measured on the boosted
    /// samples, which is what reaches the file.
    /// </summary>
    Action<float[], double?> MicrophoneCallback(RecordingSession s)
    {
        var meter = new LevelMeter();
        var gate = new Lock();
        return (samples, time) =>
        {
            if (samples.Length == 0)
            {
                return;
            }
            // Boosted here, not just for the meter: a quiet or distant voice should be easier to
            // make out in the saved track itself.
            var boosted = MeetingAudioMath.MicrophoneBoost(samples);
            s.MicrophoneWriter.Append(boosted, time);
            float? level;
            lock (gate)
            {
                level = meter.Consume(boosted);
            }
            if (level is { } value)
            {
                PostLevel(s, s.MicrophoneLevel, value, isMicrophone: true);
            }
        };
    }

    /// <summary>Runs on the system stream's delivery thread.</summary>
    Action<float[], double?> SystemCallback(RecordingSession s)
    {
        var writer = s.SystemWriter!;
        var meter = new LevelMeter();
        return (samples, time) =>
        {
            if (samples.Length == 0)
            {
                return;
            }
            writer.Append(samples, time);
            var allZero = MeetingAudioMath.IsSilent(samples);
            SilenceEvent? silence;
            float? level;
            lock (s.SilenceGate)
            {
                silence = s.Silence.Consume(samples.Length, MeetingTimeline.SampleRate, allZero);
                level = meter.Consume(samples);
            }
            if (silence is { } reported)
            {
                OnMainThread(() => SystemSilence(reported, s));
            }
            if (level is { } value)
            {
                PostLevel(s, s.SystemLevel, value, isMicrophone: false);
            }
        };
    }

    /// <summary>
    /// Hands the latest level to the main thread, one post at a time: when the main thread is
    /// busy the meter skips values instead of queueing them.
    /// </summary>
    void PostLevel(RecordingSession s, LevelRelay relay, float level, bool isMicrophone)
    {
        Volatile.Write(ref relay.Latest, level);
        if (Interlocked.Exchange(ref relay.Pending, 1) == 1)
        {
            return;
        }
        OnMainThread(() =>
        {
            Interlocked.Exchange(ref relay.Pending, 0);
            if (session != s)
            {
                return;
            }
            var latest = Volatile.Read(ref relay.Latest);
            if (isMicrophone)
            {
                ReceiveMicrophoneLevel(latest);
            }
            else if (s.SystemStream is not null)
            {
                SystemLevel = MeetingAudioMath.SmoothedLevel(SystemLevel, latest);
            }
        });
    }

    internal void ReceiveMicrophoneLevel(float level)
    {
        if (session is null)
        {
            return;
        }
        // Capture goes on while the microphone is off; the meter must not suggest that anything
        // of it reaches the track.
        MicrophoneLevel = IsMicrophoneOn ? MeetingAudioMath.SmoothedLevel(MicrophoneLevel, level) : 0;
    }

    // MARK: - Track events

    /// <summary>
    /// Whether an event of session <paramref name="s"/> must not run now: it is queued when
    /// <see cref="StartAsync"/> is still opening that session's tracks, and dropped when the
    /// session is over.
    /// </summary>
    bool Deferred(RecordingSession s, Action replay)
    {
        if (session == s)
        {
            return false;
        }
        if (starting && startingSession == s)
        {
            eventsDuringStart.Add(replay);
        }
        return true;
    }

    /// <summary>
    /// The microphone track can no longer be written. While system audio is still recording the
    /// meeting goes on as a one-track recording; with nothing left to write there is no
    /// recording to keep, and it ends rather than leaving the timer running over a file that
    /// stopped growing.
    /// </summary>
    async Task MicrophoneTrackFailedAsync(Exception error, RecordingSession s)
    {
        if (Deferred(s, () => Forget(MicrophoneTrackFailedAsync(error, s))))
        {
            return;
        }
        if (s.MicrophoneTrackEnded)
        {
            return;
        }
        // The track is closed for the rest of the meeting, which MicrophoneStatus.Lost does not
        // mean: that one clears when a device comes back.
        s.MicrophoneTrackEnded = true;
        var failure = FileError(error);
        if (!s.HasSystemTrack)
        {
            await CaptureFailedAsync(failure, s);
            return;
        }
        // Said before waiting for the capture to stop, so a stop that lands meanwhile ends on
        // the status it sets itself.
        MicrophoneLevel = 0;
        SetMicrophoneStatus(MicrophoneStatus.Lost);
        s.MicrophoneRetry?.Dispose();
        s.MicrophoneRetry = null;
        var stream = s.MicrophoneStream;
        s.MicrophoneStream = null;
        if (stream is not null)
        {
            await Task.Run(() => StopAndDispose(stream)).ResumeOn(d.MainThread);
        }
        Logger.Error($"The microphone track stopped; system audio keeps recording: {error.Message}");
    }

    /// <summary>
    /// Stops system audio and leaves the microphone recording. What the file received before
    /// the failure is kept: in a call it is the other side of the conversation up to that
    /// moment. Only a file that never held a sound is removed.
    /// </summary>
    async Task SystemTrackFailedAsync(Exception error, RecordingSession s, MeetingTrackWriter writer)
    {
        if (Deferred(s, () => Forget(SystemTrackFailedAsync(error, s, writer))))
        {
            return;
        }
        if (s.SystemWriter != writer)
        {
            return;
        }
        var failure = FileError(error);
        var stream = s.SystemStream;
        s.SystemWriter = null;
        s.SystemStream = null;
        SystemLevel = 0;
        if (stream is not null)
        {
            SystemAudioStatus = new SystemAudioStatus(SystemAudioStatusKind.Unavailable, failure.Message);
        }
        var hasSound = await Task.Run(() =>
        {
            if (stream is not null)
            {
                StopAndDispose(stream);
            }
            writer.Finish();
            return writer.HasSound;
        }).ResumeOn(d.MainThread);
        if (!hasSound)
        {
            DeleteFile(d.Store.SystemAudioPath(s.MeetingId));
            if (d.Store.Meeting(s.MeetingId) is { HasSystemAudio: true } meeting)
            {
                d.Store.Update(meeting with { HasSystemAudio = false });
            }
        }
        Logger.Error($"The system audio track stopped: {error.Message}");
        // The same end condition the microphone track is held to, read from the other side: a
        // meeting ends when nothing is left to write.
        if (s.MicrophoneTrackEnded)
        {
            await CaptureFailedAsync(failure, s);
        }
    }

    /// <summary>
    /// No microphone could be opened for the whole restart budget. The meeting keeps recording —
    /// the writer fills the outage with silence and system audio is untouched — and the capture
    /// keeps trying, so this clears by itself when a device comes back.
    /// </summary>
    void MicrophoneLost(string message, RecordingSession s)
    {
        if (Deferred(s, () => MicrophoneLost(message, s)) || s.MicrophoneTrackEnded || MicrophoneStatus == MicrophoneStatus.Lost)
        {
            return;
        }
        MicrophoneLevel = 0;
        SetMicrophoneStatus(MicrophoneStatus.Lost);
        Logger.Error($"The microphone is gone; the meeting keeps recording: {message}");
    }

    void MicrophoneBack(RecordingSession s, IAudioStream stream)
    {
        if (Deferred(s, () => MicrophoneBack(s, stream)) || s.MicrophoneTrackEnded || MicrophoneStatus != MicrophoneStatus.Lost)
        {
            return;
        }
        SetMicrophoneStatus(MicrophoneStatus.Capturing);
        Logger.Info("The microphone is back");
    }

    /// <summary>System audio stopped arriving and could not be reopened for a while; it keeps retrying.</summary>
    void SystemAudioInterrupted(string message, RecordingSession s, IAudioStream stream)
    {
        if (Deferred(s, () => SystemAudioInterrupted(message, s, stream)) || s.SystemStream != stream)
        {
            return;
        }
        SystemLevel = 0;
        SystemAudioStatus = new SystemAudioStatus(SystemAudioStatusKind.Unavailable, message);
        Logger.Error($"System audio stopped arriving: {message}");
    }

    void SystemAudioReconnected(RecordingSession s, IAudioStream stream)
    {
        if (Deferred(s, () => SystemAudioReconnected(s, stream)) || s.SystemStream != stream
            || SystemAudioStatus.Kind != SystemAudioStatusKind.Unavailable)
        {
            return;
        }
        SystemAudioStatus = SystemAudioStatus.Capturing;
        Logger.Info("System audio is back");
    }

    void SystemSilence(SilenceEvent silence, RecordingSession s)
    {
        if (Deferred(s, () => SystemSilence(silence, s)) || s.SystemStream is null || s.SystemIsApplicationAudio)
        {
            return;
        }
        if (silence == SilenceEvent.Silence && SystemAudioStatus.Kind == SystemAudioStatusKind.Capturing)
        {
            SystemAudioStatus = new SystemAudioStatus(SystemAudioStatusKind.Silent, MeetingErrors.SystemAudioDenied().Message);
        }
        else if (silence == SilenceEvent.SignalAfterSilence && SystemAudioStatus.Kind == SystemAudioStatusKind.Silent)
        {
            SystemAudioStatus = SystemAudioStatus.Capturing;
        }
    }

    /// <summary>
    /// The single place the status changes while a meeting records, so a microphone that
    /// vanishes is heard exactly once per outage: the capture retries for as long as the
    /// meeting lasts, and a sound on every attempt would fill the room the meeting is in.
    /// </summary>
    void SetMicrophoneStatus(MicrophoneStatus status)
    {
        if (MicrophoneStatus == status)
        {
            return;
        }
        MicrophoneStatus = status;
        if (status == MicrophoneStatus.Lost)
        {
            Play(SoundCue.Cancel);
        }
    }

    /// <summary>
    /// Nothing is left to record. What was captured is finalized and saved first, and only then
    /// is the failure published, so the app can report a recording that is already safe and
    /// transcribe it. <paramref name="failed"/> is the session the failure belongs to: one that
    /// ended meanwhile is not taken for a newer one.
    /// </summary>
    async Task CaptureFailedAsync(SlovoException error, RecordingSession failed)
    {
        if (session != failed)
        {
            return;
        }
        var stopped = await StopAsync();
        State = new MeetingRecorderState.Failed(error.Message);
        LastFailure = error.Message;
        if (stopped is not null)
        {
            AutoStoppedMeeting = stopped;
        }
    }

    // MARK: - Ticker, sleep and disk space

    void StartTicker()
    {
        StopTicker();
        tick = 0;
        ticker = scheduler.Schedule(TickSeconds, Tick);
    }

    void StopTicker()
    {
        ticker?.Dispose();
        ticker = null;
    }

    void Tick()
    {
        ticker = null;
        if (session is not { } s)
        {
            return;
        }
        CheckForSleep();
        if (session != s)
        {
            return;
        }
        Elapsed = Math.Max(0, d.Uptime.Now - s.StartedAt) + s.SleptSeconds;
        tick++;
        if (tick % FillTicks == 0)
        {
            FillQuietTracks(s);
            CheckSystemSilence(s);
        }
        // Every 30 s: cheap enough to poll, and nothing has to depend on a notification.
        if (tick % HealthTicks == 0)
        {
            CheckRecordingHealth();
        }
        if (session == s)
        {
            ticker = scheduler.Schedule(TickSeconds, Tick);
        }
    }

    /// <summary>
    /// Keeps tracks that receive nothing growing with the meeting, a little behind it, so a
    /// silent stretch is on disk as it happens rather than only once a buffer or the stop
    /// comes.
    /// </summary>
    void FillQuietTracks(RecordingSession s)
    {
        if (s.IsEnding)
        {
            return;
        }
        var until = d.Uptime.Now - LiveFillLag;
        if (!s.MicrophoneTrackEnded)
        {
            s.MicrophoneWriter.EnqueueFillTo(until);
        }
        s.SystemWriter?.EnqueueFillTo(until);
    }

    void CheckSystemSilence(RecordingSession s)
    {
        if (s.SystemStream is null || s.SystemIsApplicationAudio)
        {
            return;
        }
        SilenceEvent? silence;
        lock (s.SilenceGate)
        {
            silence = s.Silence.NoAudio(d.Uptime.Now - s.SystemStartedAt);
        }
        if (silence is { } reported)
        {
            SystemSilence(reported, s);
        }
    }

    /// <summary>
    /// A disk that is nearly full ends the recording, saved, before the writes start failing.
    /// Checked every 30 s, and from tests.
    /// </summary>
    internal void CheckRecordingHealth()
    {
        if (session is not { IsEnding: false } s)
        {
            return;
        }
        var space = AvailableSpace();
        if (space is { } available && available < MinimumFreeBytesToContinue)
        {
            s.IsEnding = true;
            Logger.Error("The disk is nearly full; ending the meeting recording");
            Forget(CaptureFailedAsync(MeetingErrors.LowDiskSpaceWhileRecording(available), s));
            return;
        }
        // A disk short of room for video lets the video go and keeps the audio recording.
        if (space is { } left && left < MinimumFreeBytes && s.ScreenCapture is { } capture
            && ScreenRecordingStatus.Kind is ScreenRecordingStatusKind.Capturing or ScreenRecordingStatusKind.PausedScreenLocked)
        {
            Forget(RetireScreenTrackAsync(ScreenRecordingStatus.Unavailable(MeetingErrors.LowDiskSpace(left).Message), s, capture,
                "the disk is nearly full"));
        }
    }

    /// <summary>
    /// Pads the tracks for whatever the clocks say the computer has slept since the last
    /// reading. Called from the ticker four times a second, and before any position is read,
    /// so nothing depends on a power notification arriving.
    /// </summary>
    internal void CheckForSleep()
    {
        if (session is not { IsEnding: false } s)
        {
            return;
        }
        var now = d.Uptime.Now;
        var drift = WallSeconds(s) - now;
        // Only a jump between two readings counts: a wall clock slewed or set back by a time
        // sync moves the drift slowly or backwards, and is absorbed here instead of adding up.
        var slept = drift - s.LastDrift;
        s.LastDrift = drift;
        var previousUptime = s.LastUptime;
        var jumped = now - previousUptime;
        s.LastUptime = now;
        if (slept >= MinimumSleepSeconds)
        {
            PadForSleep(slept);
            return;
        }
        // The uptime clock counted the sleep itself: the tracks fill up to the buffers after the
        // wake by themselves, and only the room for that has to be there.
        if (jumped >= ClockJumpSeconds && AvailableSpace() is { } available
            && !SleepPadFits(jumped, TrackCount(s), available))
        {
            s.IsEnding = true;
            // The recording ends where the computer went away; the time it was away is not
            // on the tracks and is not counted in the length.
            s.EndedAt = previousUptime;
            Logger.Error($"The computer was away for {jumped:0} s and the disk cannot hold it; ending the recording");
            Forget(CaptureFailedAsync(MeetingErrors.LowDiskSpaceWhileRecording(available), s));
        }
    }

    /// <summary>
    /// Fills both tracks with silence for a slept interval the uptime clock did not count.
    /// Without this the tracks would butt-join the two sides of the sleep, so the files would
    /// look continuous while minutes were missing and every later timestamp, marked moments
    /// included, would be early by the slept time.
    /// </summary>
    /// <remarks>
    /// The silence is queued on each writer rather than waited for: a lid closed overnight is
    /// hours of it. It must fit on the disk with room to spare; when it does not, the recording
    /// ends here with everything before the sleep saved, rather than filling the disk with silence.
    /// </remarks>
    internal void PadForSleep(double seconds)
    {
        if (session is not { IsEnding: false } s || !(seconds > 0))
        {
            return;
        }
        var padded = Math.Min(seconds, MeetingTimeline.MaximumSleepPad);
        if (AvailableSpace() is { } available && !SleepPadFits(padded, TrackCount(s), available))
        {
            s.IsEnding = true;
            Logger.Error($"The computer slept for {seconds:0.#} s and the disk cannot hold the pad; ending the recording");
            Forget(CaptureFailedAsync(MeetingErrors.LowDiskSpaceWhileRecording(available), s));
            return;
        }
        s.MicrophoneWriter.EnqueueSilence(padded);
        s.SystemWriter?.EnqueueSilence(padded);
        s.SleptSeconds += padded;
        // Pictures after the wake land where the padded audio does.
        if (s.ScreenCapture is { } capture)
        {
            capture.SleptSeconds = s.SleptSeconds;
        }
        Logger.Info($"The computer slept for {seconds:0.#} s; the tracks were padded by {padded:0.#} s");
    }

    static int TrackCount(RecordingSession s) => s.SystemWriter is null ? 1 : 2;

    /// <summary>
    /// Whether <paramref name="seconds"/> of silence on <paramref name="tracks"/> tracks leaves at
    /// least <see cref="MinimumFreeBytesToContinue"/> on a disk with <paramref name="availableBytes"/> free.
    /// </summary>
    public static bool SleepPadFits(double seconds, int tracks, long availableBytes)
    {
        var clamped = double.IsFinite(seconds) ? Math.Min(Math.Max(0, seconds), MeetingTimeline.MaximumSleepPad) : 0;
        var perTrack = (long)(clamped * MeetingTimeline.BytesPerSecond);
        return availableBytes - perTrack * Math.Max(1, tracks) >= MinimumFreeBytesToContinue;
    }

    /// <summary>
    /// The error for too little room to record, or null when there is enough. A capacity that
    /// cannot be read never refuses a recording.
    /// </summary>
    public static SlovoException? LowSpaceError(long? availableBytes) =>
        availableBytes is { } available && available < MinimumFreeBytes ? MeetingErrors.LowDiskSpace(available) : null;

    /// <summary>What the volume holding <paramref name="folder"/> has left for this user; null when it cannot be read.</summary>
    public static long? AvailableBytes(string folder)
    {
        try
        {
            var path = Path.GetFullPath(folder);
            DriveInfo? best = null;
            var bestLength = -1;
            foreach (var drive in DriveInfo.GetDrives())
            {
                string root;
                try
                {
                    root = drive.RootDirectory.FullName;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (root.Length > bestLength && IsInside(path, root))
                {
                    best = drive;
                    bestLength = root.Length;
                }
            }
            best ??= Path.GetPathRoot(path) is { Length: > 0 } pathRoot ? new DriveInfo(pathRoot) : null;
            return best is { IsReady: true } ? best.AvailableFreeSpace : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    static bool IsInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmed = root.Length > 1 ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : root;
        if (trimmed.Length == 0 || path.Equals(trimmed, comparison))
        {
            return trimmed.Length > 0;
        }
        var prefix = trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    // MARK: - Stopping

    (StopPlan Plan, double EndTime, double Duration) BeginStopping(RecordingSession s)
    {
        session = null;
        StopTicker();
        s.MicrophoneRetry?.Dispose();
        s.MicrophoneRetry = null;
        State = MeetingRecorderState.StoppingState;
        var now = s.EndedAt ?? d.Uptime.Now;
        var duration = Math.Max(0, now - s.StartedAt) + s.SleptSeconds;
        var plan = new StopPlan(s.MicrophoneStream, s.SystemStream, s.MicrophoneWriter, s.SystemWriter,
            FillToEnd: !s.IsEnding, MicrophoneTrackEnded: s.MicrophoneTrackEnded, s.ScreenCapture, s.ScreenWriter);
        s.MicrophoneStream = null;
        s.SystemStream = null;
        s.ScreenCapture = null;
        s.ScreenWriter = null;
        return (plan, now, duration);
    }

    MeetingRecord? Finalize(RecordingSession s, StopResult result, double duration)
    {
        MicrophoneLevel = 0;
        SystemLevel = 0;
        SystemAudioStatus = SystemAudioStatus.Off;
        ScreenRecordingStatus = ScreenRecordingStatus.Off;
        ScreenCaptureAppName = null;
        MicrophoneStatus = MicrophoneStatus.Capturing;
        Elapsed = duration;
        try
        {
            if (d.Store.Meeting(s.MeetingId) is not { } meeting)
            {
                return null;
            }
            meeting = meeting with { Status = MeetingStatus.Recorded, Duration = MeetingTime.Clamped(duration), HasAudio = true };
            if (result.HadSystemTrack)
            {
                if (result.SystemHasSound)
                {
                    meeting = meeting with { HasSystemAudio = true };
                }
                else
                {
                    // Only zeros: nothing to transcribe, and the file would only take space.
                    DeleteFile(d.Store.SystemAudioPath(meeting.Id));
                    meeting = meeting with { HasSystemAudio = false };
                }
            }
            if (result.HadScreenTrack && !result.WroteScreenRecording)
            {
                DeleteFile(d.Store.ScreenRecordingPath(meeting.Id));
                meeting = meeting with { HasScreenRecording = false };
            }
            d.Store.Update(meeting);
            try
            {
                d.Store.Flush();
            }
            catch (SlovoException error)
            {
                Logger.Error($"Saving the stopped meeting failed: {error.Message}");
            }
            Logger.Info($"Meeting recording stopped after {duration:0.#} s");
            return meeting;
        }
        finally
        {
            if (state is MeetingRecorderState.Stopping)
            {
                State = MeetingRecorderState.IdleState;
            }
        }
    }

    /// <summary>What a stop closes, taken on the main thread and finished on another.</summary>
    sealed record StopPlan(IAudioStream? Microphone, IAudioStream? System, MeetingTrackWriter MicrophoneWriter,
                           MeetingTrackWriter? SystemWriter, bool FillToEnd, bool MicrophoneTrackEnded,
                           MeetingScreenCapture? ScreenCapture, MeetingScreenWriter? ScreenWriter)
    {
        /// <summary>
        /// Stops capture first, so every buffer delivered is queued on its writer, then fills
        /// both tracks to the same end and closes the files once queued writes are done.
        /// </summary>
        public StopResult Finish(double endTime)
        {
            if (Microphone is not null)
            {
                StopAndDispose(Microphone);
            }
            if (System is not null)
            {
                StopAndDispose(System);
            }
            var wroteScreen = false;
            try
            {
                ScreenCapture?.Stop();
                wroteScreen = ScreenWriter?.Finish() ?? false;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Logger.Error("Closing the screen recording failed", error);
            }
            if (FillToEnd)
            {
                if (!MicrophoneTrackEnded)
                {
                    MicrophoneWriter.EnqueueFillTo(endTime);
                }
                SystemWriter?.EnqueueFillTo(endTime);
            }
            var microphoneFrames = MicrophoneWriter.Finish();
            var systemFrames = SystemWriter?.Finish() ?? 0;
            return new StopResult(microphoneFrames, systemFrames, SystemWriter is not null, SystemWriter?.HasSound ?? false,
                ScreenWriter is not null, wroteScreen);
        }
    }

    readonly record struct StopResult(long MicrophoneFrames, long SystemFrames, bool HadSystemTrack, bool SystemHasSound,
                                      bool HadScreenTrack, bool WroteScreenRecording);

    // MARK: - Helpers

    SlovoException Fail(SlovoException error)
    {
        Logger.Error($"The meeting recording did not start: {error.Message}");
        State = new MeetingRecorderState.Failed(error.Message);
        return error;
    }

    /// <summary>A write failure as the owner reads it; the writer's own errors are already localized.</summary>
    static SlovoException FileError(Exception error) => error as SlovoException ?? MeetingErrors.FileFailed(error);

    /// <summary>Seconds of wall-clock time since the session started.</summary>
    double WallSeconds(RecordingSession s) => (d.Clock.GetUtcNow() - s.WallReference).TotalSeconds;

    void Play(SoundCue cue)
    {
        if (d.Sounds is { } sounds && d.Settings.SoundsEnabled)
        {
            sounds.Play(cue);
        }
    }

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

    static void Forget(Task task) =>
        task.ContinueWith(static failed => Logger.Error("A meeting recorder task failed", failed.Exception!.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    static void StopAndDispose(IAudioStream stream)
    {
        try
        {
            stream.Stop();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Error("Stopping a meeting stream failed", error);
        }
        try
        {
            stream.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Error("Releasing a meeting stream failed", error);
        }
    }

    static void DisposeInBackground(IAudioStream stream) => Task.Run(() => StopAndDispose(stream));

    static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Removing {Path.GetFileName(path)} failed: {error.Message}");
        }
    }

    /// <summary>The latest level of one track and whether a post of it to the main thread is pending.</summary>
    sealed class LevelRelay
    {
        public float Latest;
        public int Pending;
    }

    /// <summary>The capture objects of one recording. Main thread only, except where noted.</summary>
    sealed class RecordingSession(Guid meetingId, double origin, MeetingTrackWriter microphoneWriter, MeetingTrackWriter systemWriter)
    {
        public Guid MeetingId { get; } = meetingId;
        /// <summary>The uptime both track writers count frames from.</summary>
        public double Origin { get; } = origin;
        /// <summary>The uptime once every track was started, what the elapsed time counts from.</summary>
        public double StartedAt { get; set; }
        public MeetingTrackWriter MicrophoneWriter { get; } = microphoneWriter;
        /// <summary>Cleared when the system track fails: the meeting goes on without it.</summary>
        public MeetingTrackWriter? SystemWriter { get; set; } = systemWriter;
        /// <summary>Null while the microphone has not been opened (it started off) or once its track ended.</summary>
        public IAudioStream? MicrophoneStream { get; set; }
        /// <summary>Null when system audio is off or unavailable, or once its track failed.</summary>
        public IAudioStream? SystemStream { get; set; }
        /// <summary>
        /// Whether <see cref="SystemStream"/> is the sound of the app a screen recording is scoped
        /// to rather than everything the computer plays: an app that stays quiet is no warning sign.
        /// </summary>
        public bool SystemIsApplicationAudio { get; set; }
        /// <summary>
        /// Cleared when the optional screen track fails, is lost, or is stopped: the meeting goes
        /// on without it, exactly like a lost system-audio track.
        /// </summary>
        public MeetingScreenCapture? ScreenCapture { get; set; }
        public MeetingScreenWriter? ScreenWriter { get; set; }
        public bool MicrophoneStarting { get; set; }
        public IDisposable? MicrophoneRetry { get; set; }
        /// <summary>
        /// Whether the microphone track is closed for good. A device that merely went away leaves
        /// it false: that writer is still open and the capture is still retrying.
        /// </summary>
        public bool MicrophoneTrackEnded { get; set; }
        /// <summary>
        /// Silence added to both tracks for sleeps the uptime clock did not count; every position
        /// taken from that clock adds it back.
        /// </summary>
        public double SleptSeconds { get; set; }
        /// <summary>
        /// Set once the recording is on its way to end by itself (the disk is nearly full), so
        /// nothing more is padded or checked in the moment before it stops.
        /// </summary>
        public bool IsEnding { get; set; }
        /// <summary>Where a recording that ends by itself stops, when that is before now.</summary>
        public double? EndedAt { get; set; }
        public DateTimeOffset WallReference { get; init; }
        /// <summary>Wall-clock seconds minus uptime at the last reading.</summary>
        public double LastDrift { get; set; }
        public double LastUptime { get; set; }
        public double SystemStartedAt { get; set; }
        /// <summary>Guards <see cref="Silence"/>, which the system stream's thread feeds too.</summary>
        public Lock SilenceGate { get; } = new();
        public SilenceDetector Silence { get; } = new(MeetingRecorder.SilenceTimeout);
        /// <summary>Touched from the streams' threads.</summary>
        public LevelRelay MicrophoneLevel { get; } = new();
        public LevelRelay SystemLevel { get; } = new();

        /// <summary>Whether system audio is still being recorded: a writer and a stream behind it.</summary>
        public bool HasSystemTrack => SystemWriter is not null && SystemStream is not null;
    }

    /// <summary>False in tests, which run the screen capture's thread work themselves.</summary>
    internal bool ScreenCaptureOwnThread { get; set; } = true;

    /// <summary>The screen capture of the current recording, for tests.</summary>
    internal MeetingScreenCapture? ScreenCaptureForTests => session?.ScreenCapture;

    /// <summary>The writers of the current recording, for tests.</summary>
    internal (MeetingTrackWriter Microphone, MeetingTrackWriter? System)? WritersForTests =>
        session is { } s ? (s.MicrophoneWriter, s.SystemWriter) : null;
}

/// <summary>
/// A microphone switch made during a recording. Each switch is a new value, so switching off,
/// on and off again is reported three times.
/// </summary>
public sealed record MicrophoneSwitch(bool IsOn)
{
    public Guid Id { get; } = Guid.NewGuid();
}
