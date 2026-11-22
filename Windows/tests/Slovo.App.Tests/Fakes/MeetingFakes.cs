using System.ComponentModel;
using System.Text.Json;
using Slovo.App.ViewModels.Meetings;
using Slovo.Core;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Fakes;

sealed class FakeMeetingRecorder : IMeetingRecordingControl
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MeetingRecorderState State { get; private set; } = MeetingRecorderState.IdleState;
    /// <summary>Creates the meeting a recording writes into.</summary>
    public Func<Guid> CreateMeeting { get; set; } = Guid.NewGuid;
    public double Elapsed { get; set; }
    public float MicrophoneLevel { get; set; } = 0.4f;
    public float SystemLevel { get; set; } = 0.7f;
    public SystemAudioStatus SystemAudioStatus { get; set; } = SystemAudioStatus.Capturing;
    public MicrophoneStatus MicrophoneStatus { get; set; } = MicrophoneStatus.Capturing;
    public bool IsMicrophoneOn { get; private set; } = true;
    public ScreenRecordingStatus ScreenRecordingStatus { get; private set; } = ScreenRecordingStatus.Off;
    public string? ScreenCaptureAppName { get; private set; }
    public int Marks { get; private set; }
    public string? FailStart { get; set; }

    /// <summary>What the screen track is doing, as the recorder would say it.</summary>
    public void SetScreen(ScreenRecordingStatus status, string? appName = null)
    {
        ScreenRecordingStatus = status;
        ScreenCaptureAppName = appName;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenRecordingStatus)));
    }

    public void Start()
    {
        if (FailStart is { } message)
        {
            throw new SlovoException(message);
        }
        Set(new MeetingRecorderState.Recording(CreateMeeting(), 0));
    }

    public void Stop() => Set(MeetingRecorderState.IdleState);
    public void MarkMoment() => Marks++;

    public void ToggleMicrophone()
    {
        IsMicrophoneOn = !IsMicrophoneOn;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMicrophoneOn)));
    }

    public void Set(MeetingRecorderState state, double elapsed = 754)
    {
        State = state;
        Elapsed = elapsed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

sealed class FakeMeetingProcessing : IMeetingProcessingControl
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public Dictionary<Guid, TranscriptionProgress> Running { get; } = [];
    public List<Guid> Transcribed { get; } = [];
    public List<(Guid Id, ReprocessOptions Options)> Reprocessed { get; } = [];
    public List<Guid> Cancelled { get; } = [];
    public bool IsSpeakerModelReady { get; set; } = true;

    public TranscriptionProgress? Progress(Guid meetingId) => Running.TryGetValue(meetingId, out var p) ? p : null;
    public bool IsProcessing(Guid meetingId) => Running.ContainsKey(meetingId);
    public bool CanReprocessSpeakersOnly(MeetingRecord meeting) => meeting.HasAudio && IsSpeakerModelReady;
    public void Transcribe(Guid meetingId) => Transcribed.Add(meetingId);
    public void Reprocess(Guid meetingId, ReprocessOptions options) => Reprocessed.Add((meetingId, options));

    public void Cancel(Guid meetingId)
    {
        Cancelled.Add(meetingId);
        Running.Remove(meetingId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

sealed class FakeMeetingPlayback : IMeetingPlayback
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid? MeetingId { get; private set; }
    public bool IsPlaying { get; private set; }
    public double Position { get; private set; }
    public double Duration { get; private set; }
    public double Rate { get; private set; } = 1;
    /// <summary>Every Play call: the meeting and where it was asked to start.</summary>
    public List<(Guid Meeting, double? From)> Plays { get; } = [];
    /// <summary>Makes Play fail the way a missing output does.</summary>
    public string? FailPlay { get; set; }

    public void Play(MeetingRecord meeting, double? from = null)
    {
        if (FailPlay is { } message)
        {
            throw new SlovoException(message);
        }
        Plays.Add((meeting.Id, from));
        if (MeetingId != meeting.Id)
        {
            Position = 0;
        }
        MeetingId = meeting.Id;
        IsPlaying = true;
        Duration = meeting.Duration;
        Position = from is { } time ? Math.Clamp(time, 0, Duration) : Position;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Pause()
    {
        IsPlaying = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Stop()
    {
        IsPlaying = false;
        MeetingId = null;
        Position = 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Seek(double seconds)
    {
        if (MeetingId is null)
        {
            return;
        }
        Position = Math.Clamp(seconds, 0, Duration);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
    }

    public void SetRate(double rate)
    {
        Rate = rate;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rate)));
    }

    /// <summary>Moves the playhead as playing would.</summary>
    public void Advance(double seconds)
    {
        Position = Math.Clamp(Position + seconds, 0, Duration);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
    }
}

/// <summary>A real meeting store in a temporary folder, filled from the shared fixtures.</summary>
sealed class MeetingFixture : IDisposable
{
    public MeetingFixture(params string[] fixtures)
    {
        Root = Path.Combine(Path.GetTempPath(), "slovo-app-tests-" + Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        foreach (var name in fixtures)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "fixtures", "meetings", name + ".meeting.json");
            using var document = JsonDocument.Parse(File.ReadAllText(source));
            var id = Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
            Ids[name] = id;
            var folder = Paths.MeetingFolder(id);
            Directory.CreateDirectory(folder);
            File.Copy(source, Path.Combine(folder, MeetingStore.RecordFileName));
        }
        Store = new MeetingStore(Paths, ImmediateMainThread.Shared, saveDelay: TimeSpan.Zero);
        Recorder.CreateMeeting = () => Store.Create("Созвон с командой").Id;
        Settings = new AppSettings(SettingsStore.InMemory());
        Context = new MeetingsContext(Store, Recorder, Processing, Playback, Settings, Shell, new FakeModels(),
            Copied.Add, _ => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), _ => Task.CompletedTask, Settings_.Add);
    }

    /// <summary>Meeting ids by fixture name.</summary>
    public Dictionary<string, Guid> Ids { get; } = [];
    public string Root { get; }
    public AppPaths Paths { get; }
    public MeetingStore Store { get; }
    public AppSettings Settings { get; }
    public FakeMeetingRecorder Recorder { get; } = new();
    public FakeMeetingProcessing Processing { get; } = new();
    public FakeMeetingPlayback Playback { get; } = new();
    public FakeShell Shell { get; } = new();
    public List<string> Copied { get; } = [];
    public List<string> Settings_ { get; } = [];
    public MeetingsContext Context { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
