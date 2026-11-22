using System.ComponentModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Slovo.App.Localization;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Meetings;
using Slovo.App.ViewModels.Settings;
using Slovo.Core.Inbox;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.Composition;

/// <summary>Meetings: the recorder and its shortcuts, transcription, the Inbox and playback.</summary>
sealed partial class AppController
{
    /// <summary>How long the Flow bar confirms a marked moment or a microphone switch.</summary>
    static readonly TimeSpan MeetingNoticeDuration = TimeSpan.FromSeconds(1.5);

    MeetingRecorder recorder = null!;
    MeetingWiring meetingWiring = null!;
    MeetingTranscriber transcriber = null!;
    InboxImporter inbox = null!;
    PhoneClipCollector phoneClips = null!;
    MeetingRecordingControl recordingControl = null!;
    MeetingProcessingControl processingControl = null!;
    MeetingPlayback meetingPlayback = null!;
    IDisposable? meetingNoticeTimer;

    IMeetingActivity MeetingActivity => recorder;
    bool IsMeetingMicrophoneOn => recorder.IsMicrophoneOn;
    MicrophoneStatus MeetingMicrophoneStatus => recorder.MicrophoneStatus;

    void StartMeetings()
    {
        recorder = new MeetingRecorder(new MeetingRecorder.Dependencies
        {
            Store = meetings,
            Settings = settings,
            Sources = platform.MeetingAudio,
            MainThread = mainThread,
            Uptime = uptime,
            Sounds = platform.Sounds,
            Permissions = platform.Permissions,
            ScreenCapture = platform.ScreenCapture,
        });
        meetingWiring = new MeetingWiring(settings, recorder, platform.Shortcuts, mainThread, diarization, diarization);
        transcriber = new MeetingTranscriber(meetings, engines, mainThread, settings, diarization);
        processingControl = new MeetingProcessingControl(transcriber, diarization, ReportProcessingFailure);
        recordingControl = new MeetingRecordingControl(recorder, stopped =>
        {
            if (stopped.HasAudio)
            {
                processingControl.Transcribe(stopped.Id);
            }
        });
        meetingPlayback = new MeetingPlayback(MakeAudioPlayer(), meetings);
        inbox = new InboxImporter(paths, meetings, settings, mainThread, processingControl.TranscribeForInboxAsync, platform.Decoder);
        // A transport beside the importer: clips pulled off a cable land in the same folder.
        phoneClips = new PhoneClipCollector(paths, settings, mainThread);

        recorder.PropertyChanged += OnRecorderChanged;
        meetingWiring.Start();
        inbox.Start();
        phoneClips.Start();
    }

    void OnRecorderChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MeetingRecorder.State):
                UpdateTray();
                if (!recorder.IsMeetingRecording)
                {
                    flowBarModel.ClearMeetingNotice();
                }
                // The recording carries on without the screen, so without this the only sign that
                // the screen was not captured is a caption on the panel (as AppModel.startMeeting says).
                if (recorder.State is MeetingRecorderState.Recording
                    && recorder.ScreenRecordingStatus is { Kind: ScreenRecordingStatusKind.Unavailable, Message: { } screenProblem })
                {
                    ShowMeetingsAlert(Loc.Instance.Get("meetings.error.screenRecording"), screenProblem);
                }
                break;
            // The tray icon shows a microphone switched off or lost during a call.
            case nameof(MeetingRecorder.IsMicrophoneOn):
            case nameof(MeetingRecorder.MicrophoneStatus):
                UpdateTray();
                break;
            case nameof(MeetingRecorder.LastMarkedMoment) when recorder.LastMarkedMoment is { } moment:
                flowBarModel.ShowMarkedMoment(moment.Time);
                ClearMeetingNoticeLater();
                break;
            case nameof(MeetingRecorder.LastMicrophoneSwitch) when recorder.LastMicrophoneSwitch is { } change:
                flowBarModel.ShowMicrophoneSwitch(change.IsOn);
                ClearMeetingNoticeLater();
                break;
            case nameof(MeetingRecorder.LastFailure) when recorder.LastFailure is { } message:
                ReportRecordingFailure(message);
                break;
            case nameof(MeetingRecorder.AutoStoppedMeeting) when recorder.AutoStoppedMeeting is { HasAudio: true } meeting:
                // A recording that ended by itself is transcribed like one the owner stopped.
                processingControl.Transcribe(meeting.Id);
                break;
        }
    }

    void ClearMeetingNoticeLater()
    {
        meetingNoticeTimer?.Dispose();
        meetingNoticeTimer = DispatcherTimer.RunOnce(flowBarModel.ClearMeetingNotice, MeetingNoticeDuration);
    }

    void ReportRecordingFailure(string message)
    {
        Logger.Error("The meeting recording stopped by itself: " + message);
        platform.Sounds.Play(SoundCue.Cancel);
        ShowMeetingsAlert(Loc.Instance.Get("meetings.error.recording"), message);
    }

    void ReportProcessingFailure(string message) => ShowMeetingsAlert(Loc.Instance.Get("meetings.error.transcribe"), message);

    void ShowMeetingsAlert(string title, string message)
    {
        OpenMainWindow();
        mainModel?.Select(Screen.Meetings);
        mainModel?.ShowAlert(title, message);
    }

    void StopMeetings()
    {
        recorder.PropertyChanged -= OnRecorderChanged;
        recorder.StopImmediately();
        meetingWiring.Stop();
        inbox.Dispose();
        phoneClips.Dispose();
        meetingPlayback.Dispose();
        meetingNoticeTimer?.Dispose();
    }

    // MARK: - Tray

    public void NewMeeting() => recordingControl.Start();
    public void StopMeeting() => recordingControl.Stop();
    public void MarkMoment() => recorder.MarkMoment();
    public void ToggleMeetingMicrophone() => recorder.ToggleMicrophone();

    // MARK: - Screens

    ViewModelBase MakeMeetingsScreen(ISheetHost sheets) => new MeetingsViewModel(new MeetingsContext(
        meetings, recordingControl, processingControl, meetingPlayback, settings, platform.Shell, models,
        Copy, PickSaveFileAsync, PickImportFileAsync, ImportFileAsync, section => OpenSettings(section),
        platform.ScreenCapture.CapturableApps), sheets);

    ViewModelBase MakeMeetingsSettings(ISheetHost sheets) => new MeetingsSettingsViewModel(settings, models, diarization, diarization,
        sheets, platform.Shell, paths,
        _ =>
        {
            meetingWiring.ApplySettings();
            return meetingWiring.RefusedShortcut is null;
        },
        _ =>
        {
            meetingWiring.ApplySettings();
            return meetingWiring.RefusedMicrophoneShortcut is null;
        },
        meetingWiring.DeleteDiarizationModelAsync);

    async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        if (mainWindow?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        });
        return file?.TryGetLocalPath();
    }

    async Task<string?> PickImportFileAsync()
    {
        if (mainWindow?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Instance.Get("meetings.import.fileType"))
                {
                    Patterns = InboxImporter.AudioExtensions.Select(extension => "*." + extension.TrimStart('.')).ToList(),
                },
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Imports a picked file as a meeting and transcribes it; throws SlovoException.</summary>
    async Task ImportFileAsync(string path)
    {
        var meeting = await inbox.ImportPickedFileAsync(path);
        processingControl.Transcribe(meeting.Id);
        (mainModel?.CurrentScreen as MeetingsViewModel)?.Select(meeting.Id);
    }
}
