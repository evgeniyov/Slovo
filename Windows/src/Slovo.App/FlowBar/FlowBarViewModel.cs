using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.App.ViewModels;
using Slovo.Core.Model;

namespace Slovo.App.FlowBar;

/// <summary>What the Flow bar pill shows.</summary>
public enum FlowBarShape
{
    Hidden,
    /// <summary>The small grey pill.</summary>
    Idle,
    /// <summary>"Hold Ctrl + Win to dictate", after the pointer rests on the idle pill.</summary>
    Hint,
    Recording,
    Processing,
    Message,
    /// <summary>"Marked 12:40" or "Microphone off" while a meeting records.</summary>
    MeetingNotice,
}

/// <summary>
/// The Flow bar's state (port of FlowBarContent's rules in the macOS FlowBar.swift). The
/// window only renders it; <see cref="Apply"/> is fed by the dictation controller.
/// </summary>
public sealed partial class FlowBarViewModel : ViewModelBase
{
    readonly Loc loc = Loc.Instance;

    [ObservableProperty] public partial FlowBarShape Shape { get; private set; } = FlowBarShape.Hidden;
    [ObservableProperty] public partial float Level { get; set; }
    [ObservableProperty] public partial bool IsHandsFree { get; private set; }
    [ObservableProperty] public partial string? RemainingText { get; private set; }
    [ObservableProperty] public partial bool RemainingIsLow { get; private set; }
    [ObservableProperty] public partial bool IsLoadingModel { get; private set; }
    [ObservableProperty] public partial string MessageText { get; private set; } = "";
    [ObservableProperty] public partial string MessageIconKey { get; private set; } = "Icon.Check";
    [ObservableProperty] public partial bool MessageIsWarning { get; private set; }
    [ObservableProperty] public partial bool MessageIsActionable { get; private set; }
    [ObservableProperty] public partial string HintText { get; private set; } = "";

    DictationState state = DictationState.IdleState;
    FlowMessage? message;
    bool isHovering;
    bool showWhenIdle = true;
    bool focusIsText = true;
    string keySymbol = DictationKey.CtrlWin.Symbol();
    double? remaining;
    string? nothingHeardDevice;
    double? markedMoment;
    bool? microphoneSwitch;

    /// <summary>Hands-free start from a click on the idle pill or the hint.</summary>
    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? CancelRequested;
    /// <summary>A click on an actionable message.</summary>
    public event Action<FlowMessage>? MessageTapped;

    public FlowBarViewModel()
    {
        loc.LanguageChanged += Refresh;
    }

    /// <summary>Takes the latest dictation state and settings, then recomputes the shape.</summary>
    public void Apply(DictationState newState, double? handsFreeRemaining, string? lastInputDevice,
                      bool showFlowBarWhenIdle, bool? focusIsTextField, DictationKey key)
    {
        state = newState;
        message = newState is DictationState.Message m ? m.Value : null;
        remaining = handsFreeRemaining;
        nothingHeardDevice = lastInputDevice;
        showWhenIdle = showFlowBarWhenIdle;
        // Unknown focus counts as a text field: better a pill too many than a missing one.
        focusIsText = focusIsTextField ?? true;
        keySymbol = key.Symbol();
        Refresh();
    }

    /// <summary>
    /// The bar sits where the pointer travels to the taskbar or a video's controls; passing
    /// through should not open it, only resting on it for a moment (<see cref="HoverDwell"/>). Leaving
    /// takes effect at once.
    /// </summary>
    public void SetHoveringAfterDwell(bool inside)
    {
        hoverTimer?.Stop();
        hoverTimer = null;
        // The pill answers the pointer at once with a small nudge; the hint opens after the dwell.
        IsPointerInside = inside;
        if (!inside)
        {
            SetHovering(false);
            return;
        }
        hoverTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(HoverDwell) };
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer?.Stop();
            hoverTimer = null;
            SetHovering(true);
        };
        hoverTimer.Start();
    }

    /// <summary>How long the pointer rests on the idle pill before the hint opens (as on macOS).</summary>
    public const double HoverDwell = 0.22;

    /// <summary>True as soon as the pointer is over the bar, before the dwell has passed.</summary>
    [ObservableProperty] public partial bool IsPointerInside { get; private set; }

    Avalonia.Threading.DispatcherTimer? hoverTimer;

    public void SetHovering(bool hovering)
    {
        if (isHovering == hovering)
        {
            return;
        }
        isHovering = hovering;
        Refresh();
    }

    /// <summary>Shows «Marked 12:40» for a moment.</summary>
    public void ShowMarkedMoment(double seconds)
    {
        microphoneSwitch = null;
        markedMoment = seconds;
        Refresh();
    }

    public void ShowMicrophoneSwitch(bool isOn)
    {
        markedMoment = null;
        microphoneSwitch = isOn;
        Refresh();
    }

    public void ClearMeetingNotice()
    {
        markedMoment = null;
        microphoneSwitch = null;
        Refresh();
    }

    [RelayCommand]
    void Start() => StartRequested?.Invoke();

    [RelayCommand]
    void Stop() => StopRequested?.Invoke();

    [RelayCommand]
    void Cancel() => CancelRequested?.Invoke();

    [RelayCommand]
    void TapMessage()
    {
        if (message is { } current && MessageIsActionable)
        {
            MessageTapped?.Invoke(current);
        }
    }

    /// <summary>A meeting notice shows only while dictation is idle or showing a message.</summary>
    bool ShowsMeetingNotice => (markedMoment is not null || microphoneSwitch is not null)
        && state is DictationState.Idle or DictationState.Message;

    void Refresh()
    {
        HintText = loc.Format("flowbar.hint", keySymbol);
        if (ShowsMeetingNotice)
        {
            Shape = FlowBarShape.MeetingNotice;
            if (microphoneSwitch is { } on)
            {
                MessageText = loc.Get(on ? "flowbar.microphoneOn" : "flowbar.microphoneOff");
                MessageIconKey = on ? "Icon.Mic" : "Icon.MicOff";
                MessageIsWarning = !on;
            }
            else
            {
                MessageText = loc.Format("flowbar.momentMarked", Formatting.Clock(markedMoment ?? 0));
                MessageIconKey = "Icon.Star";
                MessageIsWarning = true;
            }
            MessageIsActionable = false;
            return;
        }
        switch (state)
        {
            case DictationState.Recording recording:
                Shape = FlowBarShape.Recording;
                IsHandsFree = recording.Info.Mode == DictationMode.HandsFree;
                RemainingText = remaining is { } left ? Formatting.Clock(Math.Ceiling(left)) : null;
                RemainingIsLow = remaining is <= 10;
                break;
            case DictationState.Processing processing:
                Shape = FlowBarShape.Processing;
                IsLoadingModel = processing.IsLoadingModel;
                break;
            case DictationState.Message shown:
                Shape = FlowBarShape.Message;
                ApplyMessage(shown.Value);
                break;
            default:
                if (isHovering)
                {
                    Shape = FlowBarShape.Hint;
                }
                else
                {
                    Shape = showWhenIdle && focusIsText ? FlowBarShape.Idle : FlowBarShape.Hidden;
                }
                break;
        }
    }

    void ApplyMessage(FlowMessage value)
    {
        MessageIsWarning = value.Kind == FlowMessageKind.Failure;
        MessageIconKey = value.Kind switch
        {
            FlowMessageKind.Copied => "Icon.Copy",
            FlowMessageKind.NothingHeard => "Icon.MicOff",
            FlowMessageKind.BlockedByMeeting => "Icon.People",
            _ => "Icon.Warning",
        };
        MessageIsActionable = value.Kind == FlowMessageKind.NothingHeard
            || value.Failure?.Permission is not null
            || value.Failure?.Kind == DictationFailureKind.ModelMissing;
        MessageText = value.Kind switch
        {
            FlowMessageKind.Copied => loc.Get("flowbar.copied"),
            FlowMessageKind.NothingHeard => nothingHeardDevice is { } device
                ? loc.Format("flowbar.nothingHeard.device", device)
                : loc.Get("flowbar.nothingHeard"),
            FlowMessageKind.BlockedByMeeting => loc.Get("flowbar.meetingInProgress"),
            _ => FailureText(value.Failure),
        };
    }

    string FailureText(DictationFailure? failure) => failure?.Kind switch
    {
        DictationFailureKind.MicrophoneDenied => loc.Get("flowbar.error.microphone"),
        DictationFailureKind.AccessibilityMissing => loc.Get("flowbar.error.accessibility"),
        DictationFailureKind.InputMonitoringMissing => loc.Get("flowbar.error.inputMonitoring"),
        DictationFailureKind.ModelMissing => loc.Get("flowbar.error.model"),
        // Service messages are localized already; long ones fall back to a short line.
        _ => failure?.Message is { Length: > 0 and <= 48 } text ? text : loc.Get("flowbar.error.generic"),
    };
}
