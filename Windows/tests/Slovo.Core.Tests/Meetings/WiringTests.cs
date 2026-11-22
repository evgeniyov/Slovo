using System.ComponentModel;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// Port of the shortcut and speaker model parts of IntegrationMeetingWiringTests.swift over the
/// real recorder and store: the mark-moment and microphone shortcuts exist only while a meeting
/// records and follow their settings, and the speaker model is released and a load in flight
/// finishes before its files are deleted.
/// </summary>
public sealed class WiringTests : IDisposable
{
    readonly RecorderHarness h = new();
    readonly MultiShortcuts shortcuts = new();

    public void Dispose() => h.Dispose();

    MeetingRecorder Recorder => h.Recorder;

    MeetingWiring MakeWiring(IDiarizationProvider? diarization = null, IDiarizationModelManager? models = null) =>
        new(h.Settings, Recorder, shortcuts, h.MainThread, diarization, models);

    static readonly HotkeyShortcut Mark = HotkeyShortcut.MarkMomentDefault;
    static readonly HotkeyShortcut Microphone = HotkeyShortcut.MicrophoneToggleDefault;

    [Fact]
    public Task ShortcutsAreRegisteredOnlyWhileAMeetingRecords() => h.Run(async () =>
    {
        var wiring = MakeWiring();
        wiring.Start();
        Assert.Empty(shortcuts.Registrations);
        Assert.False(wiring.IsShortcutRegistered);
        Assert.False(wiring.IsMicrophoneShortcutRegistered);

        var meeting = await Recorder.StartAsync();
        Assert.Equal(new[] { Mark, Microphone }, shortcuts.Registrations);
        Assert.True(shortcuts.IsActive(Mark));
        Assert.True(shortcuts.IsActive(Microphone));
        Assert.True(wiring.IsShortcutRegistered);
        Assert.True(wiring.IsMicrophoneShortcutRegistered);

        // Pressing the shortcut marks a moment in the meeting being recorded.
        h.Clock.Advance(3);
        shortcuts.Press(Mark);
        var marked = Recorder.LastMarkedMoment;
        Assert.NotNull(marked);
        Assert.Equal(HighlightSource.Recording, marked.Source);
        Assert.Equal(new[] { marked }, h.Store.Meeting(meeting.Id)!.Highlights);

        // A new choice in Settings replaces the registration during the recording.
        var changed = HotkeyShortcut.MarkMomentChoices[1];
        h.Settings.HighlightShortcut = changed;
        Assert.Equal(changed, shortcuts.Registrations[^1]);
        Assert.True(shortcuts.IsActive(changed));
        Assert.False(shortcuts.IsActive(Mark));
        Assert.True(shortcuts.IsActive(Microphone), "the other shortcut is left as it was");
        Assert.Equal(3, shortcuts.Registrations.Count);

        Assert.NotNull(await Recorder.StopAsync());
        Assert.Empty(shortcuts.Active);
        Assert.False(wiring.IsShortcutRegistered);
        Assert.False(wiring.IsMicrophoneShortcutRegistered);

        // Between recordings a settings change registers nothing.
        var later = HotkeyShortcut.MarkMomentChoices[2];
        h.Settings.HighlightShortcut = later;
        Assert.False(wiring.ApplySettings());
        Assert.Empty(shortcuts.Active);
        Assert.Equal(3, shortcuts.Registrations.Count);

        // The next recording takes the setting as it is then.
        await Recorder.StartAsync();
        Assert.True(shortcuts.IsActive(later));
        Recorder.StopImmediately();
        Assert.Empty(shortcuts.Active);

        // Once the wiring stops, a recording no longer registers anything.
        wiring.Stop();
        await Recorder.StartAsync();
        Assert.Empty(shortcuts.Active);
        Assert.Equal(5, shortcuts.Registrations.Count);
    });

    [Fact]
    public Task TheShortcutsAreGoneWhileTheFilesCloseAndAfterARecordingEndsByItself() => h.Run(async () =>
    {
        var wiring = MakeWiring();
        wiring.Start();
        var t0 = h.Now;
        h.Settings.RecordSystemAudio = false;
        await Recorder.StartAsync();
        Assert.True(wiring.IsShortcutRegistered);
        var states = new List<(MeetingRecorderState State, bool Registered)>();
        Recorder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MeetingRecorder.State))
            {
                states.Add((Recorder.State, shortcuts.IsActive(Mark)));
            }
        };

        h.Writers.Microphone.WriteHook = _ => throw new IOException("the disk is full");
        h.Microphone.Deliver(RecorderHarness.Constant(1, 0.2f), t0);
        Assert.True(await RecorderHarness.WaitUntil(() => Recorder.LastFailure is not null));

        Assert.Empty(shortcuts.Active);
        Assert.False(wiring.IsShortcutRegistered);
        Assert.Equal(MeetingRecorderState.StoppingState, states[0].State);
        Assert.False(states[0].Registered, "nothing is left to mark once the files close");
        wiring.Stop();
    });

    /// <summary>A wiring started while a meeting already records registers at once.</summary>
    [Fact]
    public Task StartingTheWiringDuringARecordingRegistersAtOnce() => h.Run(async () =>
    {
        await Recorder.StartAsync();
        var wiring = MakeWiring();
        wiring.Start();
        Assert.True(wiring.IsShortcutRegistered);
        Assert.True(shortcuts.IsActive(Mark));
        wiring.Start();
        Assert.Equal(2, shortcuts.Registrations.Count);
        wiring.Stop();
        Assert.Empty(shortcuts.Active);
        Assert.True(Recorder.IsMeetingRecording, "stopping the wiring leaves the recording alone");
    });

    [Fact]
    public Task ARefusedShortcutIsReportedUntilARegistrationSucceeds() => h.Run(async () =>
    {
        shortcuts.Refused.Add(Mark);
        var wiring = MakeWiring();
        var reported = new List<HotkeyShortcut?>();
        wiring.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MeetingWiring.RefusedShortcut))
            {
                reported.Add(wiring.RefusedShortcut);
            }
        };
        wiring.Start();

        await Recorder.StartAsync();
        Assert.False(wiring.IsShortcutRegistered);
        Assert.True(wiring.IsMicrophoneShortcutRegistered, "the other shortcut is not affected");
        Assert.Equal(Mark, wiring.RefusedShortcut);
        Assert.Null(wiring.RefusedMicrophoneShortcut);
        Assert.Equal(new HotkeyShortcut?[] { Mark }, reported);

        // Unrelated settings do not retry a refused shortcut.
        h.Settings.SoundsEnabled = false;
        Assert.False(wiring.ApplySettings());
        Assert.Equal(1, shortcuts.Registrations.Count(s => s == Mark));

        // The warning stays after the recording, while that shortcut is still chosen.
        Recorder.StopImmediately();
        Assert.Equal(Mark, wiring.RefusedShortcut);

        var accepted = HotkeyShortcut.MarkMomentChoices[1];
        h.Settings.HighlightShortcut = accepted;
        await Recorder.StartAsync();
        Assert.True(wiring.IsShortcutRegistered);
        Assert.True(shortcuts.IsActive(accepted));
        Assert.Null(wiring.RefusedShortcut);
        Assert.Equal(new HotkeyShortcut?[] { Mark, null }, reported);
        wiring.Stop();
    });

    [Fact]
    public Task TheMicrophoneShortcutSwitchesOnlyTheMicrophoneOfTheRecording() => h.Run(async () =>
    {
        Assert.Equal(HotkeyShortcut.MicrophoneToggleDefault, h.Settings.MicrophoneShortcut);
        foreach (var taken in HotkeyShortcut.MarkMomentChoices.Append(HotkeyShortcut.PasteLastDefault))
        {
            Assert.DoesNotContain(taken, HotkeyShortcut.MicrophoneToggleChoices);
        }

        var wiring = MakeWiring();
        wiring.Start();
        await Recorder.StartAsync();

        // Each press switches the microphone of the meeting being recorded, and only that.
        shortcuts.Press(Microphone);
        Assert.False(Recorder.IsMicrophoneOn);
        Assert.Null(Recorder.LastMarkedMoment);
        shortcuts.Press(Microphone);
        Assert.True(Recorder.IsMicrophoneOn);
        shortcuts.Press(Mark);
        Assert.NotNull(Recorder.LastMarkedMoment);
        Assert.True(Recorder.IsMicrophoneOn, "marking a moment leaves the microphone alone");
        Assert.True(h.Settings.RecordMicrophone, "the shortcut changes the recording, not the setting");

        var changed = HotkeyShortcut.MicrophoneToggleChoices[1];
        h.Settings.MicrophoneShortcut = changed;
        Assert.True(shortcuts.IsActive(changed));
        Assert.True(shortcuts.IsActive(Mark), "the other shortcut is left as it was");
        Assert.Equal(1, shortcuts.Registrations.Count(s => s == Mark));

        Recorder.StopImmediately();
        Assert.Empty(shortcuts.Active);

        // A refusal is reported for this shortcut alone and leaves marking working.
        var refused = HotkeyShortcut.MicrophoneToggleChoices[2];
        shortcuts.Refused.Add(refused);
        h.Settings.MicrophoneShortcut = refused;
        Assert.False(wiring.ApplySettings());
        Assert.Null(wiring.RefusedMicrophoneShortcut);
        await Recorder.StartAsync();
        Assert.False(wiring.IsMicrophoneShortcutRegistered);
        Assert.Equal(refused, wiring.RefusedMicrophoneShortcut);
        Assert.Null(wiring.RefusedShortcut);
        Assert.True(wiring.IsShortcutRegistered);
        Recorder.StopImmediately();
        Assert.Equal(refused, wiring.RefusedMicrophoneShortcut);
        wiring.Stop();
    });

    [Fact]
    public Task SettingsChangedOnAnotherThreadAreAppliedOnTheMainThread() => h.Run(async () =>
    {
        var wiring = MakeWiring();
        wiring.Start();
        await Recorder.StartAsync();
        var changed = HotkeyShortcut.MarkMomentChoices[2];
        await Task.Run(() => h.Settings.HighlightShortcut = changed);
        Assert.True(await RecorderHarness.WaitUntil(() => shortcuts.IsActive(changed)));
        wiring.Stop();
    });

    // MARK: - Speaker model deletion

    [Fact]
    public Task DeletingTheSpeakerModelReleasesWaitsThenDeletes() => h.Run(async () =>
    {
        var events = new List<string>();
        var model = new FakeSpeakerModel(events);
        var wiring = MakeWiring(model, model);

        await wiring.DeleteDiarizationModelAsync();
        Assert.Equal(new[] { "release", "wait", "delete" }, events);

        model.DeleteError = new SlovoException("busy");
        var error = await Assert.ThrowsAsync<SlovoException>(wiring.DeleteDiarizationModelAsync);
        Assert.Equal("busy", error.Message);
        Assert.Equal(new[] { "release", "wait", "delete", "release", "wait", "delete" }, events);

        await Assert.ThrowsAsync<InvalidOperationException>(MakeWiring().DeleteDiarizationModelAsync);
    });

    [Fact]
    public Task TheModelFilesStayUntilALoadInFlightEnds() => h.Run(async () =>
    {
        var events = new List<string>();
        var load = new TaskCompletionSource();
        var model = new FakeSpeakerModel(events) { Load = load.Task };
        var wiring = MakeWiring(model, model);

        var deleting = wiring.DeleteDiarizationModelAsync();
        await Task.Delay(20);
        Assert.Equal(new[] { "release", "wait" }, events);
        Assert.False(deleting.IsCompleted, "files must stay while the load reads them");

        load.SetResult();
        await deleting;
        Assert.Equal(new[] { "release", "wait", "delete" }, events);
    });

    sealed class FakeSpeakerModel(List<string> events) : IDiarizationProvider, IDiarizationModelManager
    {
        public Exception? DeleteError { get; set; }
        public Task Load { get; set; } = Task.CompletedTask;
        public ModelStatus Status { get; private set; } = ModelStatus.Ready(1);
        public string? LastError => null;
        public EngineLoadState LoadState => EngineLoadState.Unloaded;
        public ModelStatus ModelStatus => Status;
        public bool IsSupportedOnThisSystem => true;
        public string ModelName => "fake";
        public string Configuration => "fake";
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public Task<ISpeakerDiarizer> DiarizerAsync(CancellationToken cancellation) =>
            throw new NotSupportedException("Not used by these tests");

        public void Release() => events.Add("release");

        public Task WaitForLoadAsync()
        {
            events.Add("wait");
            return Load;
        }

        public void Refresh()
        {
        }

        public void StartDownload()
        {
        }

        public void CancelDownload()
        {
        }

        public void Delete()
        {
            events.Add("delete");
            if (DeleteError is { } error)
            {
                throw error;
            }
            Status = ModelStatus.NotDownloaded(0);
        }
    }
}
