using System.ComponentModel;
using Avalonia.Threading;
using Slovo.App.ViewModels.Meetings;
using Slovo.Core.Audio;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.Composition;

/// <summary>The meeting recorder as the Meetings screen and the tray use it.</summary>
sealed class MeetingRecordingControl : IMeetingRecordingControl
{
    static readonly Logger Logger = Log.For("Meetings");
    readonly MeetingRecorder recorder;
    readonly Action<MeetingRecord> stopped;

    /// <param name="stopped">A recording the owner stopped, saved; the app transcribes it.</param>
    public MeetingRecordingControl(MeetingRecorder recorder, Action<MeetingRecord> stopped)
    {
        this.recorder = recorder;
        this.stopped = stopped;
        // The recorder raises its changes on the UI thread with the same property names.
        recorder.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MeetingRecorderState State => recorder.State;
    public double Elapsed => recorder.Elapsed;
    public float MicrophoneLevel => recorder.MicrophoneLevel;
    public float SystemLevel => recorder.SystemLevel;
    public SystemAudioStatus SystemAudioStatus => recorder.SystemAudioStatus;
    public MicrophoneStatus MicrophoneStatus => recorder.MicrophoneStatus;
    public bool IsMicrophoneOn => recorder.IsMicrophoneOn;
    public ScreenRecordingStatus ScreenRecordingStatus => recorder.ScreenRecordingStatus;
    public string? ScreenCaptureAppName => recorder.ScreenCaptureAppName;

    /// <summary>
    /// Starts in the background: opening devices can take seconds. A failure sets the
    /// recorder's state to Failed, which the recording panel shows.
    /// </summary>
    public void Start()
    {
        if (recorder.IsStarting || recorder.IsMeetingRecording)
        {
            return;
        }
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            await recorder.StartAsync();
        }
        catch (Exception error) when (error is SlovoException or OperationCanceledException)
        {
            Logger.Error("The meeting recording did not start: " + error.Message);
        }
    }

    public void Stop() => _ = StopAsync();

    async Task StopAsync()
    {
        try
        {
            if (await recorder.StopAsync() is { } meeting)
            {
                stopped(meeting);
            }
        }
        catch (Exception error)
        {
            Logger.Error("Stopping the meeting recording failed", error);
        }
    }

    public void MarkMoment() => recorder.MarkMoment();
    public void ToggleMicrophone() => recorder.ToggleMicrophone();
}

/// <summary>Transcription and "Process again…" through the meeting transcriber, in the background.</summary>
sealed class MeetingProcessingControl : IMeetingProcessingControl
{
    static readonly Logger Logger = Log.For("Meetings");
    readonly MeetingTranscriber transcriber;
    readonly IDiarizationModelManager speakerModel;
    readonly Action<string> failed;

    /// <param name="failed">Shows a processing failure (the message is localized).</param>
    public MeetingProcessingControl(MeetingTranscriber transcriber, IDiarizationModelManager speakerModel, Action<string> failed)
    {
        this.transcriber = transcriber;
        this.speakerModel = speakerModel;
        this.failed = failed;
        transcriber.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e);
        speakerModel.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeakerModelReady)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TranscriptionProgress? Progress(Guid meetingId) => transcriber.Progress.GetValueOrDefault(meetingId);
    public bool IsProcessing(Guid meetingId) => transcriber.IsTranscribing(meetingId);
    public bool CanReprocessSpeakersOnly(MeetingRecord meeting) => transcriber.CanProcess(meeting, MeetingProcessingMode.SpeakersOnly);
    public bool IsSpeakerModelReady => speakerModel.Status.IsUsable;

    public void Transcribe(Guid meetingId) => Run(meetingId, () => transcriber.TranscribeAsync(meetingId));

    public void Reprocess(Guid meetingId, ReprocessOptions options) => Run(meetingId, () => transcriber.TranscribeAsync(meetingId,
        new MeetingProcessingOptions(options.SpeakersOnly ? MeetingProcessingMode.SpeakersOnly : MeetingProcessingMode.Full,
            options.SeparateSpeakers, options.ExpectedSpeakers, options.Engine)));

    public void Cancel(Guid meetingId) => transcriber.Cancel(meetingId);

    /// <summary>The same, for a meeting imported from the Inbox (which waits for it).</summary>
    public Task TranscribeForInboxAsync(Guid meetingId, MeetingProcessingOptions options) => transcriber.TranscribeAsync(meetingId, options);

    void Run(Guid meetingId, Func<Task<MeetingRecord>> work)
    {
        if (transcriber.IsTranscribing(meetingId))
        {
            return;
        }
        _ = RunAsync(work);
    }

    async Task RunAsync(Func<Task<MeetingRecord>> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            // The message is also stored on the meeting, whose card shows it.
            Logger.Error("Meeting processing failed: " + error.Message);
            failed(error.Message);
        }
    }
}

/// <summary>
/// Plays a meeting's microphone and system tracks together, streamed from their CAF files
/// (the macOS MeetingPlayback): play/pause, seeking to any moment and a speed. Both tracks
/// share one timeline, so the mixer keeps them in step and changes their speed together.
/// </summary>
sealed class MeetingPlayback : IMeetingPlayback, IDisposable
{
    /// <summary>How close to the end a stop counts as having played to the end.</summary>
    const double EndSlack = 0.1;

    readonly IAudioPlayer player;
    readonly IMeetingStore store;
    readonly DispatcherTimer ticker;
    List<CafAudioSource> sources = [];
    /// <summary>The player holds the tracks, playing or paused; false once they stopped by themselves.</summary>
    bool isOpen;
    /// <summary>Where playing starts again while the player does not hold the tracks.</summary>
    double parked;

    public MeetingPlayback(IAudioPlayer player, IMeetingStore store)
    {
        this.player = player;
        this.store = store;
        // The playhead moves ten times a second while playing, as on the Mac.
        ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Raise(nameof(Position)));
        player.StateChanged += () => Dispatcher.UIThread.Post(OnPlayerChanged);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid? MeetingId { get; private set; }
    public bool IsPlaying { get; private set; }
    public double Position => MeetingId is null ? 0 : isOpen ? Math.Clamp(player.Position, 0, Duration) : parked;
    public double Duration { get; private set; }
    public double Rate { get; private set; } = 1;

    public void Play(MeetingRecord meeting, double? from = null)
    {
        if (MeetingId != meeting.Id || sources.Count == 0)
        {
            Load(meeting);
        }
        try
        {
            if (isOpen)
            {
                if (from is { } time)
                {
                    player.Seek(Clamped(time));
                }
                if (!player.IsPlaying)
                {
                    player.Resume();
                }
            }
            else
            {
                player.Play(sources, Clamped(from ?? parked));
                isOpen = true;
            }
        }
        catch
        {
            Stop();
            throw;
        }
        SetPlaying(true);
    }

    public void Pause()
    {
        if (isOpen)
        {
            player.Pause();
        }
        SetPlaying(false);
    }

    public void Stop()
    {
        player.Stop();
        isOpen = false;
        parked = 0;
        sources.ForEach(source => source.Dispose());
        sources = [];
        Duration = 0;
        if (MeetingId is not null)
        {
            MeetingId = null;
            Raise(nameof(MeetingId));
        }
        SetPlaying(false);
    }

    public void Seek(double seconds)
    {
        if (MeetingId is null)
        {
            return;
        }
        if (isOpen)
        {
            player.Seek(Clamped(seconds));
        }
        else
        {
            parked = Clamped(seconds);
        }
        Raise(nameof(Position));
    }

    public void SetRate(double rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        Rate = rate;
        player.Rate = rate;
        Raise(nameof(Rate));
    }

    /// <summary>Opens a meeting's tracks, paused at the start; throws SlovoException when it has none.</summary>
    void Load(MeetingRecord meeting)
    {
        Stop();
        var opened = new List<CafAudioSource>();
        try
        {
            foreach (var path in new[] { store.MicrophoneAudioPath(meeting.Id), store.SystemAudioPath(meeting.Id) })
            {
                if (File.Exists(path))
                {
                    opened.Add(CafAudioSource.Open(path));
                }
            }
        }
        catch
        {
            opened.ForEach(source => source.Dispose());
            throw;
        }
        if (opened.Count == 0)
        {
            throw new SlovoException(Slovo.App.Localization.Loc.Instance.Get("meetings.error.playback.noAudio"));
        }
        sources = opened;
        Duration = opened.Max(source => source.Length / source.SampleRate);
        MeetingId = meeting.Id;
        Raise(nameof(MeetingId));
        Raise(nameof(Duration));
    }

    double Clamped(double seconds) => Math.Clamp(seconds, 0, Duration);

    /// <summary>
    /// The player stopped by itself: the tracks played to the end, or the output went away.
    /// The end rewinds to the start, as on the Mac; a lost output keeps its place. Either way
    /// the next play opens the output afresh.
    /// </summary>
    void OnPlayerChanged()
    {
        if (!IsPlaying || player.IsPlaying || !isOpen)
        {
            return;
        }
        var reached = player.Position;
        parked = reached >= Duration - EndSlack ? 0 : Clamped(reached);
        isOpen = false;
        player.Stop();
        SetPlaying(false);
    }

    void SetPlaying(bool playing)
    {
        if (playing)
        {
            ticker.Start();
        }
        else
        {
            ticker.Stop();
        }
        if (IsPlaying != playing)
        {
            IsPlaying = playing;
            Raise(nameof(IsPlaying));
        }
        Raise(nameof(Position));
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        Stop();
        player.Dispose();
    }
}
