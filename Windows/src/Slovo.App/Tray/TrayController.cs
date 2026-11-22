using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Slovo.App.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Tray;

/// <summary>What the tray menu shows; rebuilt whenever it changes.</summary>
public sealed record TrayState(
    bool IsRecording = false,
    bool IsHandsFree = false,
    bool IsProcessing = false,
    bool IsMeetingRecording = false,
    bool IsMeetingMicrophoneOn = true,
    MicrophoneStatus MeetingMicrophoneStatus = MicrophoneStatus.Capturing,
    bool CanPasteLast = false,
    string? PasteLastShortcut = null,
    string? MarkMomentShortcut = null,
    string? MicrophoneShortcut = null,
    bool ShowIcon = true,
    DictationKey DictationKey = DictationKey.CtrlWin);

/// <summary>The pictures the tray icon switches between (the SF Symbols of the macOS menu bar item).</summary>
public enum TrayPicture
{
    /// <summary>"waveform": nothing is recording.</summary>
    Idle,
    /// <summary>"waveform.circle.fill": a dictation records or is being recognized.</summary>
    Recording,
    /// <summary>"record.circle": a meeting records with the microphone.</summary>
    Meeting,
    /// <summary>"mic.slash.circle": a meeting records with the microphone switched off.</summary>
    MeetingMicrophoneOff,
    /// <summary>"exclamationmark.circle": a meeting records but the microphone went away.</summary>
    MeetingMicrophoneLost,
}

/// <summary>What the tray menu can ask the app to do.</summary>
public interface ITrayActions
{
    void StartHandsFree();
    void FinishDictation();
    void CancelDictation();
    void PasteLast();
    void NewMeeting();
    void StopMeeting();
    void MarkMoment();
    void ToggleMeetingMicrophone();
    void OpenMainWindow();
    void OpenSettings();
    void ShowShortcuts();
    void ShowTroubleshooting();
    void OpenModelsFolder();
    void OpenLogs();
    void ShowOnboarding();
    void Quit();
}

/// <summary>
/// The notification-area icon (the macOS menu bar item): its picture follows dictation and
/// meeting recording, a click opens the main window, the menu holds the common actions.
/// </summary>
public sealed class TrayController : IDisposable
{
    readonly ITrayActions actions;
    readonly TrayIcon icon;
    readonly WindowIcon idleIcon;
    readonly WindowIcon recordingIcon;
    readonly WindowIcon meetingIcon;
    readonly WindowIcon microphoneOffIcon;
    readonly WindowIcon microphoneLostIcon;
    readonly NativeMenu menu = new();
    TrayState state = new();

    public TrayController(Application application, ITrayActions actions)
    {
        this.actions = actions;
        idleIcon = Load("tray-idle.ico");
        recordingIcon = Load("tray-recording.ico");
        meetingIcon = Load("tray-meeting.ico");
        microphoneOffIcon = Load("tray-meeting-microphone-off.ico");
        microphoneLostIcon = Load("tray-meeting-microphone-lost.ico");
        FillMenu(Loc.Instance);
        icon = new TrayIcon { Icon = idleIcon, IsVisible = true, ToolTipText = Loc.Instance.Get("tray.tooltip"), Menu = menu };
        icon.Clicked += (_, _) => actions.OpenMainWindow();
        TrayIcon.SetIcons(application, [icon]);
        Loc.Instance.LanguageChanged += Rebuild;
    }

    public void Update(TrayState newState)
    {
        if (newState == state)
        {
            return;
        }
        state = newState;
        Rebuild();
    }

    /// <summary>
    /// "Show the icon in the notification area" can hide it; a meeting recording shows it
    /// anyway, so nobody is recorded unnoticed. Starting Slovo again opens the window.
    /// </summary>
    public static bool IsIconVisible(TrayState state) => state.ShowIcon || state.IsMeetingRecording;

    /// <summary>
    /// The icon's picture. A meeting is recorded with the window closed, so the icon is where a
    /// microphone that went away can be seen at all. The recording goes on without it, so the
    /// icon warns rather than stops, and returns to the plain dot as soon as a device comes
    /// back. A microphone switched off on purpose shows too, so the state can be checked
    /// without the window.
    /// </summary>
    public static TrayPicture Picture(TrayState state)
    {
        if (state.IsMeetingRecording)
        {
            if (state.MeetingMicrophoneStatus == MicrophoneStatus.Lost)
            {
                return TrayPicture.MeetingMicrophoneLost;
            }
            return state.IsMeetingMicrophoneOn ? TrayPicture.Meeting : TrayPicture.MeetingMicrophoneOff;
        }
        return state.IsRecording || state.IsProcessing ? TrayPicture.Recording : TrayPicture.Idle;
    }

    /// <summary>The hover text, which says what the picture shows.</summary>
    internal static string ToolTip(TrayState state, Loc loc) => loc.Get(Picture(state) switch
    {
        TrayPicture.MeetingMicrophoneLost => "tray.tooltip.meeting.microphoneLost",
        TrayPicture.MeetingMicrophoneOff => "tray.tooltip.meeting.microphoneOff",
        TrayPicture.Meeting => "tray.tooltip.meeting",
        _ when state.IsRecording => "tray.tooltip.recording",
        _ => "tray.tooltip",
    });

    static WindowIcon Load(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Slovo/Assets/{name}"));
        return new WindowIcon(stream);
    }

    void Rebuild()
    {
        var loc = Loc.Instance;
        icon.Icon = Picture(state) switch
        {
            TrayPicture.MeetingMicrophoneLost => microphoneLostIcon,
            TrayPicture.MeetingMicrophoneOff => microphoneOffIcon,
            TrayPicture.Meeting => meetingIcon,
            TrayPicture.Recording => recordingIcon,
            _ => idleIcon,
        };
        icon.ToolTipText = ToolTip(state, loc);
        icon.IsVisible = IsIconVisible(state);
        // One menu for the icon's lifetime, refilled in place: assigning a new NativeMenu to a
        // tray icon that already has one is refused by the native menu exporters.
        menu.Items.Clear();
        FillMenu(loc);
    }

    void FillMenu(Loc loc) => Fill(menu, state, actions, loc);

    /// <summary>
    /// The menu for a state, in the macOS menu's order: what dictation is doing, dictation,
    /// meetings, the windows, Help, Quit.
    /// </summary>
    internal static void Fill(NativeMenu menu, TrayState state, ITrayActions actions, Loc loc)
    {
        menu.Add(Disabled(StatusLine(state, loc)));
        menu.Add(new NativeMenuItemSeparator());
        if (state.IsRecording)
        {
            if (state.IsHandsFree)
            {
                menu.Add(Item(loc.Get("menu.finishDictation"), actions.FinishDictation));
            }
            menu.Add(Item(loc.Get("menu.cancelDictation"), actions.CancelDictation));
        }
        else if (state.IsProcessing)
        {
            // A dictation still being recognized can be abandoned; a new one cannot start.
            menu.Add(Item(loc.Get("menu.cancelDictation"), actions.CancelDictation));
        }
        else
        {
            var start = Item(loc.Get("menu.startHandsFree"), actions.StartHandsFree);
            start.IsEnabled = !state.IsMeetingRecording;
            menu.Add(start);
        }
        var paste = Item(WithShortcut(loc.Get("menu.pasteLast"), state.PasteLastShortcut), actions.PasteLast);
        paste.IsEnabled = state.CanPasteLast;
        menu.Add(paste);
        menu.Add(new NativeMenuItemSeparator());

        if (state.IsMeetingRecording)
        {
            menu.Add(Item(loc.Get("menu.stopMeeting"), actions.StopMeeting));
            // During a call the window is behind the call app, so the switch is here too;
            // the check mark is its state.
            var microphone = Item(WithShortcut(loc.Get("menu.recordMicrophone"), state.MicrophoneShortcut), actions.ToggleMeetingMicrophone);
            microphone.ToggleType = MenuItemToggleType.CheckBox;
            microphone.IsChecked = state.IsMeetingMicrophoneOn;
            menu.Add(microphone);
            menu.Add(Item(WithShortcut(loc.Get("menu.markMoment"), state.MarkMomentShortcut), actions.MarkMoment));
        }
        else
        {
            menu.Add(Item(loc.Get("menu.newMeeting"), actions.NewMeeting));
        }
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(loc.Get("menu.openSlovo"), actions.OpenMainWindow));
        menu.Add(Item(loc.Get("menu.settings"), actions.OpenSettings));
        var help = new NativeMenuItem(loc.Get("sidebar.help")) { Menu = new NativeMenu() };
        help.Menu.Add(Item(loc.Get("help.shortcuts"), actions.ShowShortcuts));
        help.Menu.Add(Item(loc.Get("help.troubleshooting"), actions.ShowTroubleshooting));
        help.Menu.Add(Item(loc.Get("help.openModels"), actions.OpenModelsFolder));
        help.Menu.Add(Item(loc.Get("help.openLogs"), actions.OpenLogs));
        help.Menu.Add(new NativeMenuItemSeparator());
        help.Menu.Add(Item(loc.Get("help.showOnboarding"), actions.ShowOnboarding));
        menu.Add(help);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(loc.Get("menu.quit"), actions.Quit));
    }

    /// <summary>The disabled first line: what dictation is doing, a meeting recording, or how to dictate.</summary>
    internal static string StatusLine(TrayState state, Loc loc) =>
        state.IsRecording ? loc.Get("menu.status.recording")
        : state.IsProcessing ? loc.Get("menu.status.processing")
        : state.IsMeetingRecording ? loc.Get("flowbar.meetingInProgress")
        : loc.Format("flowbar.hint", state.DictationKey.Symbol());

    static string WithShortcut(string title, string? shortcut) => shortcut is null ? title : $"{title}\t{shortcut}";

    static NativeMenuItem Item(string title, Action action)
    {
        var item = new NativeMenuItem(title);
        item.Click += (_, _) => action();
        return item;
    }

    static NativeMenuItem Disabled(string title) => new(title) { IsEnabled = false };

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= Rebuild;
        icon.IsVisible = false;
        icon.Dispose();
    }
}
