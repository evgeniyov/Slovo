using System.ComponentModel;
using Slovo.Core.Model;

namespace Slovo.App.ViewModels.Meetings;

/// <summary>The meeting recorder as the Meetings screen and the tray see it.</summary>
public interface IMeetingRecordingControl : INotifyPropertyChanged
{
    MeetingRecorderState State { get; }
    /// <summary>Seconds since the recording started, sleep included.</summary>
    double Elapsed { get; }
    float MicrophoneLevel { get; }
    float SystemLevel { get; }
    SystemAudioStatus SystemAudioStatus { get; }
    MicrophoneStatus MicrophoneStatus { get; }
    bool IsMicrophoneOn { get; }
    /// <summary>Whether the recording is also capturing the screen right now.</summary>
    ScreenRecordingStatus ScreenRecordingStatus => ScreenRecordingStatus.Off;
    /// <summary>The app a screen recording is scoped to, whose sound the system meter then shows; null otherwise.</summary>
    string? ScreenCaptureAppName => null;
    /// <summary>Starts a recording; throws SlovoException with a localized message.</summary>
    void Start();
    void Stop();
    void MarkMoment();
    void ToggleMicrophone();
}

/// <summary>Transcription and re-processing of stored meetings.</summary>
public interface IMeetingProcessingControl : INotifyPropertyChanged
{
    /// <summary>Progress of a meeting being processed, or null.</summary>
    TranscriptionProgress? Progress(Guid meetingId);
    bool IsProcessing(Guid meetingId);
    /// <summary>Whether speakers alone can be recalculated (audio kept, format 2, speaker model ready).</summary>
    bool CanReprocessSpeakersOnly(MeetingRecord meeting);
    bool IsSpeakerModelReady { get; }
    void Transcribe(Guid meetingId);
    void Reprocess(Guid meetingId, ReprocessOptions options);
    void Cancel(Guid meetingId);
}

/// <summary>What "Process again…" asks for.</summary>
public sealed record ReprocessOptions(bool SpeakersOnly, bool SeparateSpeakers, int ExpectedSpeakers, EngineId Engine);

/// <summary>Plays a meeting's tracks together (the macOS MeetingPlayback): play/pause, seeking and a speed.</summary>
public interface IMeetingPlayback : INotifyPropertyChanged
{
    /// <summary>The meeting loaded for playback, playing or paused; null when none is.</summary>
    Guid? MeetingId { get; }
    bool IsPlaying { get; }
    /// <summary>The playhead in seconds, raised about ten times a second while playing.</summary>
    double Position { get; }
    /// <summary>Seconds of the loaded meeting's longest track; 0 when none is loaded.</summary>
    double Duration { get; }
    /// <summary>Playback speed, 1 by default; kept when another meeting is played.</summary>
    double Rate { get; }
    /// <summary>
    /// Starts or resumes a meeting, from <paramref name="from"/> when given (a meeting already
    /// loaded only jumps there); throws SlovoException.
    /// </summary>
    void Play(MeetingRecord meeting, double? from = null);
    void Pause();
    void Stop();
    /// <summary>Moves the playhead of the loaded meeting, kept within its length, without starting or pausing it.</summary>
    void Seek(double seconds);
    void SetRate(double rate);
}
