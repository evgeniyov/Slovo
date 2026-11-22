namespace Slovo.Core.Model;

// Live state of recording and processing; never stored.

public abstract record MeetingRecorderState
{
    public sealed record Idle : MeetingRecorderState;
    /// <summary>StartedAt is monotonic seconds at the start.</summary>
    public sealed record Recording(Guid MeetingId, double StartedAt) : MeetingRecorderState;
    public sealed record Stopping : MeetingRecorderState;
    /// <summary>The message is already localized.</summary>
    public sealed record Failed(string Message) : MeetingRecorderState;

    public static readonly MeetingRecorderState IdleState = new Idle();
    public static readonly MeetingRecorderState StoppingState = new Stopping();
}

public enum SystemAudioStatusKind
{
    /// <summary>The setting is off or no meeting is recording.</summary>
    Off,
    Capturing,
    /// <summary>Only silence for a long time; on Windows this usually means nothing is playing.</summary>
    Silent,
    /// <summary>Loopback capture could not start; see Message.</summary>
    Unavailable,
}

public readonly record struct SystemAudioStatus(SystemAudioStatusKind Kind, string? Message = null)
{
    public static readonly SystemAudioStatus Off = new(SystemAudioStatusKind.Off);
    public static readonly SystemAudioStatus Capturing = new(SystemAudioStatusKind.Capturing);
}

public enum MicrophoneStatus
{
    Capturing,
    /// <summary>No microphone could be opened; the capture keeps trying.</summary>
    Lost,
}

/// <summary>Whether a meeting recording is also capturing the screen right now (ScreenRecordingStatus in Meeting.swift).</summary>
public enum ScreenRecordingStatusKind
{
    /// <summary>The setting is off, no meeting is recording, or the meeting itself never asked for the screen.</summary>
    Off,
    Capturing,
    /// <summary>The screen is locked. Capture resumes on unlock without ending the meeting.</summary>
    PausedScreenLocked,
    /// <summary>
    /// The capture stopped (the window or display went away, the graphics device was reset)
    /// and one restart already failed; the track is done for the rest of this meeting.
    /// </summary>
    Lost,
    /// <summary>Never started, or the video could no longer be written; see Message.</summary>
    Unavailable,
}

/// <summary>A <see cref="ScreenRecordingStatusKind"/> with the localized message of <see cref="ScreenRecordingStatusKind.Unavailable"/>.</summary>
public readonly record struct ScreenRecordingStatus(ScreenRecordingStatusKind Kind, string? Message = null)
{
    public static readonly ScreenRecordingStatus Off = new(ScreenRecordingStatusKind.Off);
    public static readonly ScreenRecordingStatus Capturing = new(ScreenRecordingStatusKind.Capturing);
    public static readonly ScreenRecordingStatus PausedScreenLocked = new(ScreenRecordingStatusKind.PausedScreenLocked);
    public static readonly ScreenRecordingStatus Lost = new(ScreenRecordingStatusKind.Lost);

    public static ScreenRecordingStatus Unavailable(string message) => new(ScreenRecordingStatusKind.Unavailable, message);
}

/// <summary>
/// What a screen recording captures (ScreenCaptureTarget in Meeting.swift): the whole main
/// display, whose audio is whatever <c>system.caf</c> already records; or one app's windows,
/// whose own audio replaces <c>system.caf</c>'s system-wide capture.
/// </summary>
public abstract record ScreenCaptureTarget
{
    public sealed record Display : ScreenCaptureTarget;

    /// <param name="BundleId">The app as <see cref="AppIdentity.BundleId"/> names it: the lower-case executable name on Windows ("ms-teams.exe").</param>
    public sealed record Application(string BundleId) : ScreenCaptureTarget;

    public static readonly ScreenCaptureTarget WholeDisplay = new Display();

    /// <summary>
    /// Null for the whole display; the app's id otherwise, the shape
    /// <see cref="Settings.SettingsKey.ScreenCaptureApp"/> stores.
    /// </summary>
    public string? StoredBundleId => this is Application application ? application.BundleId : null;

    /// <summary><see cref="Display"/> for an empty or missing stored value, <see cref="Application"/> otherwise.</summary>
    public static ScreenCaptureTarget FromStoredBundleId(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? WholeDisplay : new Application(stored.Trim());
}

public enum MeetingRecordingTracks
{
    MicrophoneAndSystem,
    MicrophoneOnly,
    SystemOnly,
    Neither,
}

public static class MeetingRecordingTracksExtensions
{
    public static MeetingRecordingTracks From(bool microphoneOn, MicrophoneStatus microphoneStatus, SystemAudioStatus systemAudioStatus)
    {
        var microphone = microphoneOn && microphoneStatus == MicrophoneStatus.Capturing;
        var system = systemAudioStatus.Kind is SystemAudioStatusKind.Capturing or SystemAudioStatusKind.Silent;
        return (microphone, system) switch
        {
            (true, true) => MeetingRecordingTracks.MicrophoneAndSystem,
            (true, false) => MeetingRecordingTracks.MicrophoneOnly,
            (false, true) => MeetingRecordingTracks.SystemOnly,
            _ => MeetingRecordingTracks.Neither,
        };
    }
}

public enum MeetingProcessingPhaseKind
{
    Preparing,
    LoadingSpeakerModel,
    SeparatingSpeakers,
    Transcribing,
}

public readonly record struct MeetingProcessingPhase(MeetingProcessingPhaseKind Kind, MeetingTrack Track = MeetingTrack.Microphone, double Fraction = 0)
{
    public static readonly MeetingProcessingPhase Preparing = new(MeetingProcessingPhaseKind.Preparing);
    public static readonly MeetingProcessingPhase LoadingSpeakerModel = new(MeetingProcessingPhaseKind.LoadingSpeakerModel);
    public static readonly MeetingProcessingPhase Transcribing = new(MeetingProcessingPhaseKind.Transcribing);
    public static MeetingProcessingPhase SeparatingSpeakers(MeetingTrack track, double fraction) =>
        new(MeetingProcessingPhaseKind.SeparatingSpeakers, track, fraction);
}

/// <summary>Progress of one meeting's processing run.</summary>
public sealed record TranscriptionProgress
{
    public int CompletedSegments { get; }
    public int TotalSegments { get; }
    public MeetingProcessingPhase Phase { get; }
    /// <summary>From 0 to 1 over the whole run; drives the progress bar.</summary>
    public double OverallFraction { get; }

    public TranscriptionProgress(int completedSegments, int totalSegments, MeetingProcessingPhase? phase = null, double? overallFraction = null)
    {
        CompletedSegments = completedSegments;
        TotalSegments = totalSegments;
        Phase = phase ?? MeetingProcessingPhase.Transcribing;
        var overall = overallFraction ?? Fraction;
        OverallFraction = double.IsFinite(overall) ? Math.Min(1, Math.Max(0, overall)) : 0;
    }

    public double Fraction => TotalSegments > 0 ? (double)CompletedSegments / TotalSegments : 0;
}

/// <summary>One stretch of speech attributed to one voice by a diarization run.</summary>
/// <param name="Cluster">0-based, local to one diarization run of one track.</param>
public readonly record struct SpeakerTurn(double Start, double End, int Cluster);

/// <summary>What is known about the number of voices on a track before separating them.</summary>
public readonly record struct SpeakerCountHint(int? Exactly = null, int? Maximum = null)
{
    public static readonly SpeakerCountHint Automatic = new();
}
