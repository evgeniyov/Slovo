using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Meetings;

/// <summary>Everything the Meetings screen needs from the app.</summary>
public sealed record MeetingsContext(
    IMeetingStore Store,
    IMeetingRecordingControl Recorder,
    IMeetingProcessingControl Processing,
    IMeetingPlayback Playback,
    AppSettings Settings,
    IShell Shell,
    IModelManager Models,
    Action<string> Copy,
    /// <summary>Asks where to save a file; null when cancelled.</summary>
    Func<string, Task<string?>> PickSaveFile,
    /// <summary>Asks for an audio file to import; null when cancelled.</summary>
    Func<Task<string?>> PickImportFile,
    /// <summary>Imports a file as a meeting; throws SlovoException.</summary>
    Func<string, Task> ImportFile,
    Action<string> OpenSettings,
    /// <summary>Running apps a screen recording can be scoped to; none where the screen cannot be recorded.</summary>
    Func<IReadOnlyList<AppIdentity>>? CapturableApps = null);

/// <summary>A meeting in the list (the macOS <c>MeetingListRow</c>).</summary>
/// <param name="liveElapsed">The recorder's elapsed time while it records this meeting; null otherwise.</param>
public sealed partial class MeetingRowViewModel(MeetingRecord meeting, TranscriptionProgress? progress, Func<double>? liveElapsed = null) : ViewModelBase
{
    public MeetingRecord Meeting { get; } = meeting;
    public Guid Id => Meeting.Id;
    public string Title => Meeting.Title;
    public string DateText => Formatting.RelativeDate(Meeting.CreatedAt);

    /// <summary>
    /// The duration. The stored duration is written when recording stops, so a meeting still
    /// recording counts up from the recorder's start instead; one that says it records while
    /// the recorder is not recording it has no duration to show.
    /// </summary>
    public string? DurationText => Meeting.Status != MeetingStatus.Recording ? Formatting.Clock(Meeting.Duration)
        : liveElapsed is { } elapsed ? Formatting.Clock(elapsed()) : null;

    public bool HasDuration => DurationText is not null;

    /// <summary>Moves a recording meeting's time on; the Meetings screen calls it every second.</summary>
    public void Tick()
    {
        if (Meeting.Status == MeetingStatus.Recording && liveElapsed is not null)
        {
            OnPropertyChanged(nameof(DurationText));
        }
    }

    public string? StatusText => Meeting.Status switch
    {
        MeetingStatus.Recording => Loc.Instance.Get("meetings.status.recording"),
        MeetingStatus.Recorded => Loc.Instance.Get("meetings.status.recorded"),
        MeetingStatus.Failed => Loc.Instance.Get("meetings.status.failed"),
        MeetingStatus.Transcribing when progress is { } p => $"{(int)Math.Round(p.OverallFraction * 100)} %",
        MeetingStatus.Transcribing => Loc.Instance.Get("meetings.transcribing.title"),
        _ => null,
    };

    public bool IsProblem => Meeting.Status is MeetingStatus.Failed;
    public bool IsLive => Meeting.Status is MeetingStatus.Recording or MeetingStatus.Transcribing;
    public bool FromPhone => Meeting.Source?.Origin == MeetingOrigin.Phone;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>The Meetings screen: new recording, the recording panel, the list and the selected meeting.</summary>
public sealed partial class MeetingsViewModel : ViewModelBase
{
    readonly MeetingsContext context;
    readonly ISheetHost sheets;
    Guid? recordingShown;

    public MeetingsViewModel(MeetingsContext context, ISheetHost sheets)
    {
        this.context = context;
        this.sheets = sheets;
        Recording = new RecordingPanelViewModel(context.Recorder, context.Store, context.Settings);
        // The panel's one-second tick also moves the recording meeting's time in the list.
        Recording.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordingPanelViewModel.ElapsedText))
            {
                foreach (var row in Rows)
                {
                    row.Tick();
                }
            }
        };
        ScreenChoice = new ScreenChoiceViewModel(context.Settings, context.CapturableApps ?? (() => []));
        context.Store.PropertyChanged += OnStoreChanged;
        context.Processing.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshList);
        context.Recorder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IMeetingRecordingControl.ScreenRecordingStatus) or null or "")
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ConsentText)));
            }
            if (e.PropertyName is nameof(IMeetingRecordingControl.State) or null or "")
            {
                OnPropertyChanged(nameof(CanStart));
                // A new recording opens in the detail pane, as on macOS.
                if (context.Recorder.State is MeetingRecorderState.Recording recording && recording.MeetingId != recordingShown)
                {
                    recordingShown = recording.MeetingId;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        RefreshList();
                        Select(recording.MeetingId);
                    });
                }
            }
        };
        RefreshList();
        Select(Rows.FirstOrDefault()?.Id);
    }

    public RecordingPanelViewModel Recording { get; }

    [ObservableProperty] public partial IReadOnlyList<MeetingRowViewModel> Rows { get; private set; } = [];
    [ObservableProperty] public partial MeetingDetailViewModel? Detail { get; private set; }

    public bool IsEmpty => Rows.Count == 0;
    public bool CanStart => context.Recorder.State is MeetingRecorderState.Idle or MeetingRecorderState.Failed;

    /// <summary>The "with screen" switch and what it captures, next to the new-recording button.</summary>
    public ScreenChoiceViewModel ScreenChoice { get; }

    /// <summary>The reminder under the buttons; it names the screen while the screen is recorded too.</summary>
    public string ConsentText => Loc.Instance.Get(context.Recorder.ScreenRecordingStatus.Kind == ScreenRecordingStatusKind.Capturing
        ? "meetings.consent.screen" : "meetings.consent");

    void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshList);

    void RefreshList()
    {
        var selected = Detail?.Id;
        Rows = context.Store.Meetings.Select(m => new MeetingRowViewModel(m, context.Processing.Progress(m.Id), LiveElapsed(m.Id)) { IsSelected = m.Id == selected })
            .ToList();
        OnPropertyChanged(nameof(IsEmpty));
        if (selected is { } id && Rows.All(r => r.Id != id))
        {
            Select(Rows.FirstOrDefault()?.Id);
        }
        else if (selected is { } current && context.Store.Meeting(current) is { } fresh)
        {
            Detail?.Update(fresh);
        }
        else if (selected is null && Rows.Count > 0)
        {
            Select(Rows[0].Id);
        }
    }

    /// <summary>The same elapsed time the recording panel shows, for the meeting the recorder is recording now.</summary>
    Func<double>? LiveElapsed(Guid meetingId) =>
        context.Recorder.State is MeetingRecorderState.Recording recording && recording.MeetingId == meetingId ? () => context.Recorder.Elapsed : null;

    [RelayCommand]
    public void Select(Guid? id)
    {
        foreach (var row in Rows)
        {
            row.IsSelected = row.Id == id;
        }
        if (id is not { } meetingId || context.Store.Meeting(meetingId) is not { } meeting)
        {
            Detail?.Detach();
            Detail = null;
            return;
        }
        if (Detail?.Id == meetingId)
        {
            Detail.Update(meeting);
            return;
        }
        Detail?.Detach();
        Detail = new MeetingDetailViewModel(meeting, context, sheets);
    }

    [RelayCommand]
    void NewRecording()
    {
        try
        {
            context.Recorder.Start();
        }
        catch (SlovoException error)
        {
            sheets.ShowAlert(Loc.Instance.Get("meetings.error.start"), error.Message);
        }
    }

    [RelayCommand]
    async Task ImportAsync()
    {
        if (await context.PickImportFile() is not { } path)
        {
            return;
        }
        try
        {
            await context.ImportFile(path);
        }
        catch (Exception error) when (error is SlovoException or IOException or UnauthorizedAccessException)
        {
            sheets.ShowAlert(Loc.Instance.Get("meetings.error.import"), error.Message);
        }
    }
}

/// <summary>The panel shown while a meeting records: timer, levels, statuses and the recording's buttons.</summary>
public sealed partial class RecordingPanelViewModel : ViewModelBase
{
    readonly IMeetingRecordingControl recorder;
    readonly IMeetingStore store;
    readonly AppSettings settings;
    readonly Avalonia.Threading.DispatcherTimer ticker;

    public RecordingPanelViewModel(IMeetingRecordingControl recorder, IMeetingStore store, AppSettings settings)
    {
        this.recorder = recorder;
        this.store = store;
        this.settings = settings;
        recorder.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
        ticker = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
        ticker.Start();
    }

    Guid? MeetingId => recorder.State is MeetingRecorderState.Recording recording ? recording.MeetingId : null;

    public bool IsVisible => recorder.State is MeetingRecorderState.Recording or MeetingRecorderState.Stopping;
    public bool IsStopping => recorder.State is MeetingRecorderState.Stopping;
    public bool IsRecording => recorder.State is MeetingRecorderState.Recording;
    public string? FailureText => recorder.State is MeetingRecorderState.Failed failed ? failed.Message : null;
    public bool HasFailure => FailureText is not null;
    public string StatusTitle => Loc.Instance.Get(IsStopping ? "meetings.recording.stopping" : "meetings.recording.title");
    public string ElapsedText => Formatting.Clock(recorder.Elapsed);
    public string? Title => MeetingId is { } id ? store.Meeting(id)?.Title : null;
    public float MicrophoneLevel => recorder.MicrophoneLevel;
    public float SystemLevel => recorder.SystemLevel;
    public bool IsMicrophoneOn => recorder.IsMicrophoneOn;
    public string MicrophoneButtonText => Loc.Instance.Get(IsMicrophoneOn ? "meetings.recording.micOn" : "meetings.recording.micOff");
    public string MicrophoneShortcut => settings.MicrophoneShortcut.DisplayText;
    public string MarkShortcut => settings.HighlightShortcut.DisplayText;

    public MeetingRecordingTracks Tracks => MeetingRecordingTracksExtensions.From(recorder.IsMicrophoneOn, recorder.MicrophoneStatus, recorder.SystemAudioStatus);

    public string TracksText => Loc.Instance.Get(Tracks switch
    {
        MeetingRecordingTracks.MicrophoneAndSystem => "meetings.tracks.both",
        MeetingRecordingTracks.MicrophoneOnly => "meetings.detail.micOnly",
        MeetingRecordingTracks.SystemOnly => "meetings.tracks.systemOnly",
        _ => "meetings.tracks.neither",
    });

    public bool TracksWarning => Tracks == MeetingRecordingTracks.Neither;

    public string? MicrophoneStatusText => recorder.MicrophoneStatus == MicrophoneStatus.Lost ? Loc.Instance.Get("meetings.microphone.lost") : null;

    public string? SystemStatusText => recorder.SystemAudioStatus.Kind switch
    {
        SystemAudioStatusKind.Off => Loc.Instance.Get("meetings.system.off"),
        SystemAudioStatusKind.Silent => Loc.Instance.Get("meetings.system.silent"),
        SystemAudioStatusKind.Unavailable => recorder.SystemAudioStatus.Message,
        _ => null,
    };

    /// <summary>The system meter reads the app's name while a screen recording scoped to it records its sound instead.</summary>
    public string SystemMeterTitle => recorder.ScreenCaptureAppName ?? Loc.Instance.Get("meetings.meter.system");

    /// <summary>
    /// Screen capture is fully optional, so off and capturing say nothing; only a state the owner
    /// might want to notice gets a line.
    /// </summary>
    public string? ScreenStatusText => recorder.ScreenRecordingStatus.Kind switch
    {
        ScreenRecordingStatusKind.PausedScreenLocked => Loc.Instance.Get("meetings.screen.locked"),
        ScreenRecordingStatusKind.Lost => Loc.Instance.Get("meetings.screen.lost"),
        ScreenRecordingStatusKind.Unavailable => recorder.ScreenRecordingStatus.Message,
        _ => null,
    };

    /// <summary>A pause is information; a lost or failed screen track is a warning.</summary>
    public bool ScreenStatusIsWarning => recorder.ScreenRecordingStatus.Kind is ScreenRecordingStatusKind.Lost or ScreenRecordingStatusKind.Unavailable;

    public string? MarkedText
    {
        get
        {
            var count = MeetingId is { } id ? store.Meeting(id)?.Highlights.Count ?? 0 : 0;
            return count > 0 ? Loc.Instance.Format("meetings.recording.marked", Loc.Instance.Plural("plural.moments", count)) : null;
        }
    }

    [RelayCommand]
    void Stop() => recorder.Stop();

    [RelayCommand]
    void Mark()
    {
        recorder.MarkMoment();
        OnPropertyChanged(nameof(MarkedText));
    }

    [RelayCommand]
    void ToggleMicrophone() => recorder.ToggleMicrophone();
}
