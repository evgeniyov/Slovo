using Slovo.Core.Model;

namespace Slovo.Core.Input;

// Port of Sources/SlovoCore/Input/HotkeyStateMachine.swift. The macOS event tap reports
// modifier keys as `flagsChanged` events whose flags must be decoded; the Windows
// low-level keyboard hook reports every key, modifiers included, as a key down or key
// up with a left/right-specific virtual-key code, so the decoding step is replaced by
// HotkeyKeys.Normalize and the rest of the logic is unchanged.

/// <summary>One keyboard event from the hook, reduced to the fields the state machine reads.</summary>
/// <param name="VirtualKey">A left/right-specific virtual-key code (see <see cref="HotkeyKeys.Normalize"/>).</param>
/// <param name="Timestamp">Monotonic seconds (<see cref="IUptime"/>).</param>
public readonly record struct RawKeyEvent(RawKeyEventKind Kind, int VirtualKey, double Timestamp);

public enum RawKeyEventKind
{
    /// <summary>A key went down, or auto-repeats while held.</summary>
    KeyDown,
    KeyUp,
}

/// <summary>Inputs of the hotkey state machine.</summary>
public abstract record HotkeyInput
{
    private HotkeyInput()
    {
    }

    public sealed record Key(RawKeyEvent Event) : HotkeyInput;

    /// <summary>The deadline the machine asked for (<see cref="HotkeyStateMachine.NextDeadline"/>) has passed.</summary>
    public sealed record Deadline(double Time) : HotkeyInput;

    /// <summary>What the controller is actually doing, null when not recording.</summary>
    public sealed record RecordingChanged(DictationMode? Mode, double At) : HotkeyInput;
}

/// <summary>
/// The dictation key logic as a pure function of events: push-to-talk with a minimum
/// hold, double-tap hands-free, the finishing tap and Esc.
/// </summary>
/// <remarks>
/// The keyboard hook feeds it events and schedules a timer for <see cref="NextDeadline"/>;
/// tests feed it synthetic events.
///
/// Capture starts on key down (<c>Begin(PushToTalk)</c>) so the first syllable is never
/// lost. A release after <c>MinimumHold</c> finishes. A shorter release is a tap: the
/// recording keeps running for <c>DoubleTapWindow</c> after the release, and a second press
/// in that window promotes it to hands-free; otherwise the tap is discarded. In
/// hands-free the next press finishes. Esc cancels any recording. Any other key pressed
/// while the dictation key is held turns the press into a shortcut, which is discarded.
///
/// Not thread-safe: one thread (the hook thread) owns it.
/// </remarks>
public sealed class HotkeyStateMachine
{
    HotkeyConfiguration configuration;
    Phase phase = Phase.Idle;
    double phaseTime;
    readonly HashSet<int> heldKeys = [];

    public HotkeyStateMachine(HotkeyConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public HotkeyConfiguration Configuration
    {
        get => configuration;
        set
        {
            var oldKey = configuration.Key;
            configuration = value;
            if (value.Key == oldKey)
            {
                return;
            }
            // The old key's release will never be matched against the new key, so a
            // held push-to-talk would never end; let the next tap of the new key end it.
            heldKeys.Clear();
            IsKeyDown = false;
            if (phase == Phase.Holding)
            {
                phase = Phase.HandsFree;
            }
        }
    }

    /// <summary>Whether the configured dictation key is held.</summary>
    public bool IsKeyDown { get; private set; }

    /// <summary>
    /// True while the controller transcribes a finished dictation; Esc then cancels it
    /// although nothing records any more.
    /// </summary>
    public bool IsProcessing { get; set; }

    /// <summary>When the machine next needs <see cref="HotkeyInput.Deadline"/>, for example the end of the double-tap window.</summary>
    public double? NextDeadline { get; private set; }

    enum Phase
    {
        /// <summary>
        /// Not recording. The key may still be held after a cancel or a finishing tap;
        /// its release is ignored.
        /// </summary>
        Idle,
        /// <summary>Push-to-talk: the key went down at <c>phaseTime</c> and capture is running.</summary>
        Holding,
        /// <summary>
        /// A short tap was released; capture keeps running until the deadline in
        /// <c>phaseTime</c> in case a second tap follows.
        /// </summary>
        AwaitingSecondTap,
        /// <summary>Capture continues without the key; the next press finishes.</summary>
        HandsFree,
    }

    /// <summary>
    /// The recording the machine believes is running, null when idle. A pending tap
    /// counts as push-to-talk because its capture is still running.
    /// </summary>
    public DictationMode? RecordingMode => phase switch
    {
        Phase.Idle => null,
        Phase.Holding or Phase.AwaitingSecondTap => DictationMode.PushToTalk,
        _ => DictationMode.HandsFree,
    };

    /// <summary>Advances the machine and returns the triggers to send, in order.</summary>
    public IReadOnlyList<DictationTrigger> Handle(HotkeyInput input)
    {
        var triggers = new List<DictationTrigger>();
        switch (input)
        {
            case HotkeyInput.Key key:
                ExpireTap(key.Event.Timestamp, inclusive: false, triggers);
                HandleKey(key.Event, triggers);
                break;
            case HotkeyInput.Deadline deadline:
                ExpireTap(deadline.Time, inclusive: true, triggers);
                break;
            case HotkeyInput.RecordingChanged changed:
                SyncRecording(changed.Mode, changed.At);
                break;
        }
        UpdateDeadline();
        return triggers;
    }

    /// <summary>
    /// Forgets which keys are held, as if they were all released at <paramref name="timestamp"/>.
    /// The keyboard hook calls it when it is reinstalled after sleep, a session switch or
    /// the secure desktop, when releases may have been missed.
    /// </summary>
    public IReadOnlyList<DictationTrigger> ReleaseKeys(double timestamp)
    {
        var triggers = new List<DictationTrigger>();
        ExpireTap(timestamp, inclusive: false, triggers);
        var wasDown = IsKeyDown;
        heldKeys.Clear();
        IsKeyDown = false;
        if (wasDown)
        {
            KeyReleased(timestamp, triggers);
        }
        UpdateDeadline();
        return triggers;
    }

    // Keys

    void HandleKey(RawKeyEvent keyEvent, List<DictationTrigger> triggers)
    {
        var keyCodes = configuration.Key.KeyCodes();
        if (!keyCodes.Contains(keyEvent.VirtualKey))
        {
            if (keyEvent.Kind == RawKeyEventKind.KeyDown)
            {
                OtherKeyPressed(keyEvent, triggers);
            }
            return;
        }
        if (keyEvent.Kind == RawKeyEventKind.KeyDown)
        {
            heldKeys.Add(keyEvent.VirtualKey);
        }
        else
        {
            heldKeys.Remove(keyEvent.VirtualKey);
        }
        var wasDown = IsKeyDown;
        IsKeyDown = keyCodes.All(heldKeys.Contains);
        if (!wasDown && IsKeyDown)
        {
            KeyPressed(keyEvent.Timestamp, triggers);
        }
        else if (wasDown && !IsKeyDown)
        {
            KeyReleased(keyEvent.Timestamp, triggers);
        }
    }

    void KeyPressed(double time, List<DictationTrigger> triggers)
    {
        switch (phase)
        {
            case Phase.Idle:
                phase = Phase.Holding;
                phaseTime = time;
                triggers.Add(DictationTrigger.BeginPushToTalk);
                break;
            case Phase.AwaitingSecondTap:
                phase = Phase.HandsFree;
                triggers.Add(DictationTrigger.Promote);
                break;
            case Phase.HandsFree:
                phase = Phase.Idle;
                triggers.Add(DictationTrigger.FinishRecording);
                break;
            case Phase.Holding:
                break;
        }
    }

    void KeyReleased(double time, List<DictationTrigger> triggers)
    {
        if (phase != Phase.Holding)
        {
            return;
        }
        if (time - phaseTime >= configuration.MinimumHold)
        {
            phase = Phase.Idle;
            triggers.Add(DictationTrigger.FinishRecording);
            return;
        }
        if (configuration.HandsFreeEnabled)
        {
            phase = Phase.AwaitingSecondTap;
            phaseTime = time + configuration.DoubleTapWindow;
            return;
        }
        phase = Phase.Idle;
        triggers.Add(DictationTrigger.DiscardRecording);
    }

    /// <summary>
    /// Esc cancels. Any other key pressed while the dictation key is held, or during a
    /// pending tap, means the key was part of a shortcut, however long it was held:
    /// Right Ctrl+Tab switches tabs, Ctrl+Win+Left switches desktops. Finishing would
    /// paste whatever the microphone picked up meanwhile.
    /// </summary>
    void OtherKeyPressed(RawKeyEvent keyEvent, List<DictationTrigger> triggers)
    {
        // Modifiers never form a shortcut on their own, and neither do the unassigned
        // codes other tools inject to mask Win and Alt (AutoHotkey's vkE8) or that some
        // keyboards send for Fn (vkFF); counting them as a chord would throw the
        // dictation away.
        if (VirtualKey.IsModifier(keyEvent.VirtualKey) || HotkeyKeys.IsNonKey(keyEvent.VirtualKey))
        {
            return;
        }
        if (keyEvent.VirtualKey == VirtualKey.Escape)
        {
            if (!configuration.EscapeCancels || (phase == Phase.Idle && !IsProcessing))
            {
                return;
            }
            phase = Phase.Idle;
            triggers.Add(DictationTrigger.CancelRecording);
            return;
        }
        if (phase is Phase.Holding or Phase.AwaitingSecondTap)
        {
            phase = Phase.Idle;
            triggers.Add(DictationTrigger.DiscardRecording);
        }
    }

    // Time and controller state

    void ExpireTap(double time, bool inclusive, List<DictationTrigger> triggers)
    {
        if (phase != Phase.AwaitingSecondTap)
        {
            return;
        }
        var expired = inclusive ? time >= phaseTime : time > phaseTime;
        if (!expired)
        {
            return;
        }
        phase = Phase.Idle;
        triggers.Add(DictationTrigger.DiscardRecording);
    }

    void SyncRecording(DictationMode? mode, double time)
    {
        switch (mode)
        {
            case null:
                phase = Phase.Idle;
                break;
            case DictationMode.HandsFree:
                phase = Phase.HandsFree;
                break;
            case DictationMode.PushToTalk:
                if (phase != Phase.Idle)
                {
                    return;
                }
                // Push-to-talk without the key held cannot end on a release, so a tap ends it.
                if (IsKeyDown)
                {
                    phase = Phase.Holding;
                    phaseTime = time;
                }
                else
                {
                    phase = Phase.HandsFree;
                }
                break;
        }
    }

    void UpdateDeadline() => NextDeadline = phase == Phase.AwaitingSecondTap ? phaseTime : null;
}
