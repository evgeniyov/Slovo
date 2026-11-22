namespace Slovo.Core.Model;

/// <summary>Where the text of a dictation goes.</summary>
public abstract record DictationTarget
{
    /// <summary>Pasted into the focused field of the foreground app.</summary>
    public sealed record ForegroundApp : DictationTarget;
    /// <summary>Appended to a Scratchpad note.</summary>
    public sealed record Note(Guid Id) : DictationTarget;
    /// <summary>Returned to onboarding's practice step without pasting anywhere.</summary>
    public sealed record Practice : DictationTarget;

    public static readonly DictationTarget Foreground = new ForegroundApp();
    public static readonly DictationTarget PracticeTarget = new Practice();
}

/// <summary>Semantic events produced by the hotkey state machine (or the Flow bar) for the controller.</summary>
public abstract record DictationTrigger
{
    /// <summary>The dictation key went down: start capturing at once so no speech is lost.</summary>
    public sealed record Begin(DictationMode Mode) : DictationTrigger;
    /// <summary>A double tap was confirmed: keep the running recording going after release.</summary>
    public sealed record PromoteToHandsFree : DictationTrigger;
    /// <summary>Stop and transcribe.</summary>
    public sealed record Finish : DictationTrigger;
    /// <summary>A lone tap shorter than the minimum hold: stop silently.</summary>
    public sealed record Discard : DictationTrigger;
    /// <summary>Esc while recording: stop, nothing is transcribed or inserted.</summary>
    public sealed record Cancel : DictationTrigger;

    public static readonly DictationTrigger BeginPushToTalk = new Begin(DictationMode.PushToTalk);
    public static readonly DictationTrigger BeginHandsFree = new Begin(DictationMode.HandsFree);
    public static readonly DictationTrigger Promote = new PromoteToHandsFree();
    public static readonly DictationTrigger FinishRecording = new Finish();
    public static readonly DictationTrigger DiscardRecording = new Discard();
    public static readonly DictationTrigger CancelRecording = new Cancel();
}

/// <summary>Information about the recording in progress.</summary>
/// <param name="StartedAt">Monotonic seconds (<see cref="IUptime"/>) when capture began.</param>
public sealed record RecordingInfo(DictationMode Mode, double StartedAt, DictationTarget Target);

public enum DictationFailureKind
{
    MicrophoneDenied,
    AccessibilityMissing,
    InputMonitoringMissing,
    ModelMissing,
    EngineFailed,
    RecordingFailed,
}

/// <summary>Short errors the Flow bar can show; each may point at the settings that fix it.</summary>
public sealed record DictationFailure(DictationFailureKind Kind, EngineId? Engine = null, string? Message = null)
{
    public static readonly DictationFailure MicrophoneDenied = new(DictationFailureKind.MicrophoneDenied);
    public static DictationFailure ModelMissing(EngineId id) => new(DictationFailureKind.ModelMissing, id);
    public static DictationFailure EngineFailed(string message) => new(DictationFailureKind.EngineFailed, Message: message);
    public static DictationFailure RecordingFailed(string message) => new(DictationFailureKind.RecordingFailed, Message: message);

    public PermissionKind? Permission => Kind switch
    {
        DictationFailureKind.MicrophoneDenied => PermissionKind.Microphone,
        DictationFailureKind.AccessibilityMissing => PermissionKind.Accessibility,
        DictationFailureKind.InputMonitoringMissing => PermissionKind.InputMonitoring,
        _ => null,
    };
}

public enum FlowMessageKind
{
    /// <summary>"Copied — press Ctrl+V": pasting was not possible, the text is on the clipboard.</summary>
    Copied,
    NothingHeard,
    BlockedByMeeting,
    Failure,
}

/// <summary>Result states the Flow bar shows for about 1.5 s.</summary>
public sealed record FlowMessage(FlowMessageKind Kind, DictationFailure? Failure = null)
{
    public static readonly FlowMessage Copied = new(FlowMessageKind.Copied);
    public static readonly FlowMessage NothingHeard = new(FlowMessageKind.NothingHeard);
    public static readonly FlowMessage BlockedByMeeting = new(FlowMessageKind.BlockedByMeeting);
    public static FlowMessage For(DictationFailure failure) => new(FlowMessageKind.Failure, failure);
}

/// <summary>The dictation state the Flow bar, tray menu and screens render.</summary>
public abstract record DictationState
{
    public sealed record Idle : DictationState;
    public sealed record Recording(RecordingInfo Info) : DictationState;
    /// <summary>IsLoadingModel is true while waiting for the engine to load ("Loading model…").</summary>
    public sealed record Processing(bool IsLoadingModel) : DictationState;
    public sealed record Message(FlowMessage Value) : DictationState;

    public static readonly DictationState IdleState = new Idle();

    public bool IsRecording => this is Recording;
}

/// <summary>Why text went to the clipboard instead of being pasted.</summary>
public enum CopyReason
{
    /// <summary>The foreground window runs elevated or on the secure desktop; input would not reach it.</summary>
    SecureInput,
    AccessibilityMissing,
    PasteFailed,
}

public abstract record InsertionResult
{
    public sealed record Pasted : InsertionResult;
    public sealed record Copied(CopyReason Reason) : InsertionResult;
    public sealed record Empty : InsertionResult;

    public static readonly InsertionResult PastedResult = new Pasted();
    public static readonly InsertionResult EmptyResult = new Empty();
}
