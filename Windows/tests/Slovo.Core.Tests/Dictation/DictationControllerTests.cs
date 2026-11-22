using Slovo.Core.Audio;
using Slovo.Core.Dictation;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Dictation;

/// <summary>Port of DictationControllerTests.swift.</summary>
public sealed class DictationControllerTests : IDisposable
{
    readonly DictationHarness h = new();

    DictationController Controller => h.Controller;

    public void Dispose() => h.Dispose();

    static RecordingInfo Info(DictationMode mode, DictationTarget? target = null) =>
        new(mode, 1_000, target ?? DictationTarget.Foreground);

    // MARK: - Recording

    [Fact]
    public Task BeginStartsPushToTalkRecording() => h.Run(() =>
    {
        h.Settings.MicrophoneUid = "{0.0.1}.{usb-mic}";
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        // Capture starts at once, but nothing is shown or heard until the hold is confirmed.
        Assert.Equal(["{0.0.1}.{usb-mic}"], h.Recorder.StartedDeviceUids);
        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Empty(h.Sounds.Played);

        h.Clock.Advance(0.25);
        h.ConfirmHold();
        Assert.Equal(new DictationState.Recording(Info(DictationMode.PushToTalk)), Controller.State);
        Assert.Single(h.Recorder.StartedDeviceUids);
        Assert.Equal([SoundCue.Start], h.Sounds.Played);
        Assert.Empty(h.Hotkeys.SyncedModes); // trigger-driven starts are not echoed back to the key state machine
    });

    [Fact]
    public Task PromoteToHandsFreeKeepsRecording() => h.Run(() =>
    {
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.Promote);

        Assert.Equal(new DictationState.Recording(Info(DictationMode.HandsFree)), Controller.State);
        Assert.Single(h.Recorder.StartedDeviceUids);
    });

    [Fact]
    public Task MeetingBlocksDictation() => h.Run(() =>
    {
        h.Meetings.IsMeetingRecording = true;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Assert.Equal(DictationState.IdleState, Controller.State); // a press that may still be a tap shows nothing yet
        h.ConfirmHold();

        Assert.Equal(new DictationState.Message(FlowMessage.BlockedByMeeting), Controller.State);
        Assert.Empty(h.Recorder.StartedDeviceUids);
        Assert.Equal([null], h.Hotkeys.SyncedModes);

        Controller.DismissMessage();
        Controller.StartHandsFree();
        Assert.Equal(new DictationState.Message(FlowMessage.BlockedByMeeting), Controller.State);
        // A refused Flow bar start was never announced to the key state machine.
        Assert.Equal([null], h.Hotkeys.SyncedModes);
    });

    [Fact]
    public Task DeniedMicrophoneShowsFailure() => h.Run(() =>
    {
        h.Permissions.Statuses[PermissionKind.Microphone] = PermissionStatus.Denied;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.ConfirmHold();

        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.MicrophoneDenied)), Controller.State);
        Assert.Empty(h.Recorder.StartedDeviceUids);
        Assert.Equal([null], h.Hotkeys.SyncedModes);
    });

    [Fact]
    public Task RecorderFailureShowsFailure() => h.Run(() =>
    {
        h.Recorder.StartError = new SlovoException("no input");
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.ConfirmHold();

        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.RecordingFailed("no input"))), Controller.State);
        Assert.Equal([null], h.Hotkeys.SyncedModes);
        Assert.Empty(h.Sounds.Played);
    });

    [Fact]
    public Task EscapeCancelsWithoutTranscribing() => h.Run(() =>
    {
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.ConfirmHold();
        Controller.Handle(DictationTrigger.CancelRecording);

        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal(1, h.Recorder.CancelCount);
        Assert.Equal(0, h.Recorder.StopCount);
        Assert.Empty(h.Engines.Requested);
        Assert.Equal([SoundCue.Start, SoundCue.Cancel], h.Sounds.Played);
        Assert.Empty(h.Hotkeys.SyncedModes);
    });

    [Fact]
    public Task FlowBarStartAndCancelSyncTheKeyStateMachine() => h.Run(() =>
    {
        Controller.StartHandsFree();
        Assert.Equal(new DictationState.Recording(Info(DictationMode.HandsFree)), Controller.State);
        Assert.Equal([DictationMode.HandsFree], h.Hotkeys.SyncedModes);

        Controller.Cancel();
        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal([DictationMode.HandsFree, null], h.Hotkeys.SyncedModes);
    });

    [Fact]
    public Task DiscardedTapIsSilent() => h.Run(() =>
    {
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.DiscardRecording);

        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal(1, h.Recorder.CancelCount);
        Assert.Empty(h.Sounds.Played);
        Assert.Empty(h.Engines.Requested);
    });

    [Fact]
    public Task LevelIsPublishedOnlyWhileRecording() => h.Run(() =>
    {
        Controller.Start();
        h.Recorder.RaiseLevel(0.4f);
        Assert.Equal(0, Controller.Level);

        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.Recorder.RaiseLevel(0.4f);
        Assert.Equal(0, Controller.Level); // a hold that is not confirmed yet is not shown
        h.ConfirmHold();
        h.Recorder.RaiseLevel(0.4f);
        Assert.Equal(0.4f, Controller.Level);

        Controller.Handle(DictationTrigger.CancelRecording);
        Assert.Equal(0, Controller.Level);
    });

    [Fact]
    public Task LevelFromAnotherThreadArrivesOnTheMainThread() => h.Run(async () =>
    {
        var changedOnMain = new List<bool>();
        Controller.Start();
        Controller.StartHandsFree();
        Controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DictationController.Level))
            {
                changedOnMain.Add(h.MainThread.IsMainThread);
            }
        };
        await Task.Run(() => h.Recorder.RaiseLevel(0.7f));
        Assert.True(await TestAudio.WaitUntil(() => Controller.Level == 0.7f));
        Assert.Equal([true], changedOnMain);
    });

    [Fact]
    public Task HandsFreeCountdownAndTimeLimit() => h.Run(async () =>
    {
        h.Settings.MaxHandsFreeSeconds = 60;
        Controller.StartHandsFree();

        h.Clock.Advance(20);
        Controller.CheckRecordingLimit();
        Assert.Null(Controller.HandsFreeRemaining);

        h.Clock.Advance(15);
        Controller.CheckRecordingLimit();
        Assert.Equal(25, Controller.HandsFreeRemaining);

        h.Clock.Advance(24.5);
        Controller.CheckRecordingLimit();
        Assert.Equal(1, Controller.HandsFreeRemaining);

        h.Clock.Advance(1);
        Controller.CheckRecordingLimit();
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        Assert.Null(Controller.HandsFreeRemaining);
        Assert.Equal([DictationMode.HandsFree, null], h.Hotkeys.SyncedModes);

        await Controller.WaitForProcessingAsync();
        Assert.Single(h.History.Entries);
        Assert.Equal(["привет мир"], h.Inserter.Inserted);
    });

    [Fact]
    public Task TheLimitTimerFinishesARecordingWithoutHelp() => h.Run(async () =>
    {
        h.Settings.MaxHandsFreeSeconds = 120;
        Controller.LimitCheckInterval = 0.25;
        Controller.StartHandsFree();

        h.Clock.Advance(100);
        Assert.Equal(20, Controller.HandsFreeRemaining);
        h.Clock.Advance(20);
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        await Controller.WaitForProcessingAsync();
        Assert.Equal(["привет мир"], h.Inserter.Inserted);
        Assert.Equal(0, h.Clock.PendingTimers);
    });

    [Fact]
    public Task PushToTalkHasNoCountdown() => h.Run(() =>
    {
        h.Settings.MaxHandsFreeSeconds = 60;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.ConfirmHold();
        h.Clock.Advance(50);
        Controller.CheckRecordingLimit();
        Assert.Null(Controller.HandsFreeRemaining);
        Assert.True(Controller.State.IsRecording);
    });

    [Fact]
    public Task HoldCheckRunsAtTheMinimumHoldWithoutHelp() => h.Run(() =>
    {
        Controller.HoldCheckDelay = null;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.Hotkeys.IsKeyDown = true;
        h.Clock.Advance(0.2);
        Assert.Equal(DictationState.IdleState, Controller.State);
        h.Clock.Advance(0.06);
        Assert.Equal(new DictationState.Recording(Info(DictationMode.PushToTalk)), Controller.State);
    });

    // MARK: - Transcription and insertion

    [Fact]
    public Task FinishTranscribesInsertsAndSavesHistory() => h.Run(async () =>
    {
        h.Categories.Mapping["slack.exe"] = AppCategory.Work;
        h.Settings.SetStyle(StylePreset.VeryCasual, AppCategory.Work);
        h.Dictionary.List = [DictionaryItem.Term("Kubernetes"), DictionaryItem.ReplacementRule("гитхаб", "GitHub")];
        h.Snippets.List = [new Snippet { Trigger = "мой адрес", Expansion = "Москва" }];
        h.FakePipeline.Transform = raw => new TextPipelineResult(raw + ".", ReplacementsApplied: 1, TermFixesApplied: 2);

        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.ConfirmHold();
        Controller.Handle(DictationTrigger.FinishRecording);
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        await Controller.WaitForProcessingAsync();

        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal(["привет мир."], h.Inserter.Inserted);
        Assert.Equal("привет мир.", Controller.LastTranscript);
        Assert.Equal([SoundCue.Start, SoundCue.Stop], h.Sounds.Played);
        Assert.Equal([EngineId.GigaamRnnt], h.Engines.MarkedUsed);
        Assert.Equal([16_000], h.Engine.SampleCounts); // the engine reads the recording from a real WAV file

        var context = h.FakePipeline.Contexts[^1];
        Assert.Equal(StylePreset.VeryCasual, context.Style);
        Assert.Equal(h.Dictionary.Items, context.Dictionary);
        Assert.Equal(h.Snippets.Snippets, context.Snippets);
        Assert.Equal(h.Settings.Cleanup, context.Cleanup);
        Assert.Equal(LanguageMode.Mixed, context.LanguageMode);

        var entry = Assert.Single(h.History.Entries);
        Assert.Equal("привет мир.", entry.Text);
        Assert.Equal("привет мир", entry.RawText);
        Assert.Equal(EngineId.GigaamRnnt, entry.EngineId);
        Assert.Equal(LanguageMode.Mixed, entry.LanguageMode);
        Assert.Equal("slack.exe", entry.AppBundleId);
        Assert.Equal("Slack", entry.AppName);
        Assert.Equal(AppCategory.Work, entry.Category);
        Assert.Equal(1, entry.Duration, 3);
        Assert.Equal(2, entry.WordCount);
        Assert.Equal(3, entry.FixesCount);
        Assert.Equal(WavFile.RelativePath(entry.Id, entry.Date), entry.AudioFileName);

        var audioPath = h.History.AudioPath(entry);
        Assert.NotNull(audioPath);
        Assert.StartsWith(h.Paths.RecordingsFolder, audioPath);
        Assert.Equal(16_000, WavFile.Read(audioPath).Samples.Length);
        Assert.Single(h.RecordedFiles());
        Assert.False(h.TemporaryRecordingExists(entry.Id));
    });

    [Fact]
    public Task EnglishModeUsesItsEngineAndMode() => h.Run(async () =>
    {
        h.Settings.LanguageMode = LanguageMode.English;
        h.Engines.DictationEngine = EngineId.Parakeet;
        h.Engines.Engines[EngineId.Parakeet] = new FakeEngine(EngineId.Parakeet.RawValue(), "hello");
        h.Engines.LoadStates[EngineId.Parakeet] = EngineLoadState.Ready;
        await h.Dictate();

        Assert.Equal([EngineId.Parakeet], h.Engines.Requested);
        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(EngineId.Parakeet, entry.EngineId);
        Assert.Equal(LanguageMode.English, entry.LanguageMode);
    });

    [Fact]
    public Task TooShortRecordingIsNothingHeard() => h.Run(async () =>
    {
        h.Recorder.NextAudio = TestAudio.Speech(0.25);
        await h.Dictate();

        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);
        Assert.Empty(h.Engines.Requested);
        Assert.Empty(h.History.Entries);
        Assert.Empty(h.RecordedFiles());
    });

    [Fact]
    public Task SilentRecordingIsNothingHeard() => h.Run(async () =>
    {
        h.Recorder.NextAudio = new RecordedAudio(Enumerable.Range(0, 16_000).Select(i => i % 2 == 0 ? 0.001f : -0.001f).ToArray());
        await h.Dictate();

        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);
        Assert.Empty(h.Engines.Requested);
    });

    [Fact]
    public Task EmptyTranscriptIsNothingHeardAndDropsAudio() => h.Run(async () =>
    {
        h.Engine.Text = "  ";
        await h.Dictate();

        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);
        Assert.Empty(h.Inserter.Inserted);
        Assert.Empty(h.History.Entries);
        Assert.Empty(h.RecordedFiles());
        Assert.Empty(Directory.EnumerateFiles(h.TemporaryFolder));
    });

    [Fact]
    public Task CopyFallbackShowsCopiedAndKeepsHistory() => h.Run(async () =>
    {
        // An elevated window in front: input would not reach it, so the text is copied.
        h.Inserter.Result = new InsertionResult.Copied(CopyReason.SecureInput);
        await h.Dictate();

        Assert.Equal(new DictationState.Message(FlowMessage.Copied), Controller.State);
        Assert.Single(h.History.Entries);
        Assert.Equal("привет мир", Controller.LastTranscript);
    });

    [Fact]
    public Task MissingModelShowsModelFailure() => h.Run(async () =>
    {
        h.Engines.Unavailable[EngineId.GigaamRnnt] = ModelStatus.NotDownloaded(1);
        await h.Dictate();

        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.ModelMissing(EngineId.GigaamRnnt))), Controller.State);
        Assert.Empty(h.History.Entries);
        Assert.Empty(h.RecordedFiles());
    });

    [Fact]
    public Task EngineErrorShowsEngineFailure() => h.Run(async () =>
    {
        h.Engine.Error = new SlovoException("decoder failed");
        await h.Dictate();

        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.EngineFailed("decoder failed"))), Controller.State);
        Assert.Empty(h.Inserter.Inserted);
        Assert.Empty(h.History.Entries);
        Assert.Empty(h.RecordedFiles());
    });

    [Fact]
    public Task LoadingModelIsShownUntilTheEngineIsReady() => h.Run(async () =>
    {
        h.Engines.LoadStates[EngineId.GigaamRnnt] = EngineLoadState.Unloaded;
        var gate = h.NewGate();
        h.Engine.Gate = gate;

        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.FinishRecording);
        Assert.Equal(new DictationState.Processing(true), Controller.State);

        Assert.True(await TestAudio.WaitUntil(() => gate.Arrived == 1));
        Assert.Equal(new DictationState.Processing(false), Controller.State);

        gate.Open();
        await Controller.WaitForProcessingAsync();
        Assert.Equal(DictationState.IdleState, Controller.State);
    });

    [Fact]
    public Task CancelWhileTranscribingInsertsNothing() => h.Run(async () =>
    {
        var gate = h.NewGate();
        h.Engine.Gate = gate;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.FinishRecording);
        Assert.True(await TestAudio.WaitUntil(() => gate.Arrived == 1));

        Controller.Cancel();
        Assert.Equal(DictationState.IdleState, Controller.State);
        gate.Open();
        await Controller.WaitForProcessingAsync();

        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Empty(h.Inserter.Inserted);
        Assert.Empty(h.History.Entries);
        Assert.Empty(h.RecordedFiles());
        Assert.Empty(Directory.EnumerateFiles(h.TemporaryFolder));
    });

    [Fact]
    public Task KeyPressWhileTranscribingIsRefused() => h.Run(async () =>
    {
        var gate = h.NewGate();
        h.Engine.Gate = gate;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.FinishRecording);
        Assert.True(await TestAudio.WaitUntil(() => gate.Arrived == 1));

        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        Assert.Single(h.Recorder.StartedDeviceUids);
        Assert.Equal([null], h.Hotkeys.SyncedModes);

        gate.Open();
        await Controller.WaitForProcessingAsync();
        Assert.Single(h.History.Entries);
    });

    [Fact]
    public Task MessagesExpire() => h.Run(async () =>
    {
        Controller.MessageLifetime = 1.5;
        h.Recorder.NextAudio = RecordedAudio.Empty;
        await h.Dictate();
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);

        h.Clock.Advance(1.49);
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);
        h.Clock.Advance(0.02);
        Assert.Equal(DictationState.IdleState, Controller.State);
    });

    [Fact]
    public void DefaultTimesMatchTheMacApp()
    {
        var fresh = new DictationController(new DictationController.Dependencies
        {
            Recorder = h.Recorder, Hotkeys = h.Hotkeys, Inserter = h.Inserter, ForegroundApp = h.ForegroundApp,
            Permissions = h.Permissions, Engines = h.Engines, Pipeline = h.Pipeline, History = h.History,
            Dictionary = h.Dictionary, Snippets = h.Snippets, Notes = h.Notes, Categories = h.Categories,
            Meetings = h.Meetings, Settings = h.Settings, Paths = h.Paths, MainThread = h.MainThread,
        });
        Assert.Equal(1.5, fresh.MessageLifetime);
        Assert.Equal(300, fresh.ModelLoadTimeLimit);
        Assert.Equal(60, fresh.TranscriptionTimeLimit);
        Assert.Equal(0.25, fresh.LimitCheckInterval);
        Assert.Null(fresh.HoldCheckDelay);
        Assert.Equal(30, DictationController.CountdownThreshold);
    }

    [Fact]
    public Task DismissMessage() => h.Run(async () =>
    {
        h.Recorder.NextAudio = RecordedAudio.Empty;
        await h.Dictate();
        Controller.DismissMessage();
        Assert.Equal(DictationState.IdleState, Controller.State);
    });

    [Fact]
    public Task NewDictationReplacesMessage() => h.Run(async () =>
    {
        h.Recorder.NextAudio = RecordedAudio.Empty;
        await h.Dictate();
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);

        Controller.Handle(DictationTrigger.BeginPushToTalk);
        // A press that may still be a tap leaves the message alone.
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);
        h.ConfirmHold();
        Assert.True(Controller.State.IsRecording);
    });

    // MARK: - Targets and retention

    [Fact]
    public Task NoteTargetAppendsToNote() => h.Run(async () =>
    {
        var noteId = Guid.NewGuid();
        Controller.StartHandsFree(new DictationTarget.Note(noteId));
        Controller.Finish();
        await Controller.WaitForProcessingAsync();

        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal([("привет мир", noteId)], h.Notes.Appended);
        Assert.Empty(h.Inserter.Inserted);
        Assert.Equal(h.Settings.Style(AppCategory.Other), h.FakePipeline.Contexts[^1].Style);
        var entry = Assert.Single(h.History.Entries);
        Assert.Null(entry.AppBundleId);
        Assert.Equal(AppCategory.Other, entry.Category);
        Assert.Equal([DictationMode.HandsFree, null], h.Hotkeys.SyncedModes);
    });

    [Fact]
    public Task PracticeTargetFillsPracticeResultOnly() => h.Run(async () =>
    {
        Controller.KeyTarget = DictationTarget.PracticeTarget;
        await h.Dictate();

        Assert.Equal("привет мир", Controller.PracticeResult);
        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Empty(h.Inserter.Inserted);
        Assert.Empty(h.History.Entries);
        Assert.Empty(h.RecordedFiles());
        Assert.Empty(Directory.EnumerateFiles(h.TemporaryFolder));
    });

    [Fact]
    public Task AudioIsNotKeptWhenRetentionIsNever() => h.Run(async () =>
    {
        h.Settings.KeepDictationAudio = RetentionPeriod.Never;
        await h.Dictate();

        var entry = Assert.Single(h.History.Entries);
        Assert.Null(entry.AudioFileName);
        Assert.Empty(h.RecordedFiles());
        Assert.Equal([16_000], h.Engine.SampleCounts);
        Assert.False(h.TemporaryRecordingExists(entry.Id));
    });

    [Fact]
    public Task SoundsFollowTheSetting() => h.Run(async () =>
    {
        h.Settings.SoundsEnabled = false;
        await h.Dictate();
        Controller.StartHandsFree();
        Controller.Cancel();

        Assert.Single(h.History.Entries);
        Assert.Empty(h.Sounds.Played);
    });

    // MARK: - Paste last and re-run

    static HistoryEntry Entry(string text, Guid? id = null, DateTimeOffset? date = null, string? audioFileName = null,
                              AppCategory category = AppCategory.Other, string? bundleId = null, string? appName = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Date = date ?? DateTimeOffset.UtcNow,
        Text = text,
        RawText = text,
        EngineId = EngineId.GigaamRnnt,
        LanguageMode = LanguageMode.Mixed,
        AppBundleId = bundleId,
        AppName = appName,
        Category = category,
        Duration = 1,
        WordCount = HistoryEntry.CountWords(text),
        AudioFileName = audioFileName,
    };

    [Fact]
    public Task PasteLastTranscriptUsesHistoryAfterRelaunch() => h.Run(async () =>
    {
        h.History.Items = [Entry("из истории")];
        h.Build();
        Assert.Equal("из истории", Controller.LastTranscript);

        await Controller.PasteLastTranscriptAsync();
        Assert.Equal(["из истории"], h.Inserter.Inserted);
        Assert.Equal(DictationState.IdleState, Controller.State);

        h.Inserter.Result = new InsertionResult.Copied(CopyReason.SecureInput);
        await Controller.PasteLastTranscriptAsync();
        Assert.Equal(new DictationState.Message(FlowMessage.Copied), Controller.State);
    });

    [Fact]
    public Task PasteLastTranscriptWithoutTextDoesNothing() => h.Run(async () =>
    {
        await Controller.PasteLastTranscriptAsync();
        Assert.Empty(h.Inserter.Inserted);
    });

    [Fact]
    public Task PasteLastWhileRecordingShowsNoMessage() => h.Run(async () =>
    {
        h.History.Items = [Entry("из истории")];
        h.Build();
        h.Inserter.Result = new InsertionResult.Copied(CopyReason.PasteFailed);
        Controller.StartHandsFree();

        await Controller.PasteLastTranscriptAsync();
        Assert.True(Controller.State.IsRecording);
    });

    [Fact]
    public Task RerunReplacesEntryText() => h.Run(async () =>
    {
        var id = Guid.NewGuid();
        var date = DateTimeOffset.UtcNow;
        var relativePath = WavFile.RelativePath(id, date);
        WavFile.Write(TestAudio.Speech(1), h.Paths.RecordingPath(relativePath));
        h.History.Items = [Entry("кибернетес", id, date, relativePath, AppCategory.Email, "outlook.exe", "Outlook")];
        h.Build();
        h.Settings.SetStyle(StylePreset.Casual, AppCategory.Email);
        h.Engines.Engines[EngineId.Parakeet] = new FakeEngine(EngineId.Parakeet.RawValue(), "Kubernetes is up");
        h.FakePipeline.Transform = raw => new TextPipelineResult(raw, TermFixesApplied: 1);

        await Controller.RerunAsync(id, EngineId.Parakeet, TestContext.Current.CancellationToken);

        var entry = Assert.Single(h.History.Entries);
        Assert.Equal("Kubernetes is up", entry.Text);
        Assert.Equal("Kubernetes is up", entry.RawText);
        Assert.Equal(EngineId.Parakeet, entry.EngineId);
        Assert.Equal(3, entry.WordCount);
        Assert.Equal(1, entry.FixesCount);
        Assert.Equal(relativePath, entry.AudioFileName);
        Assert.Equal(StylePreset.Casual, h.FakePipeline.Contexts[^1].Style);
        Assert.Equal([EngineId.Parakeet], h.Engines.MarkedUsed);
        Assert.Equal("Kubernetes is up", Controller.LastTranscript);
    });

    [Fact]
    public Task RerunOfAnOlderEntryLeavesLastTranscriptAlone() => h.Run(async () =>
    {
        var id = Guid.NewGuid();
        var date = DateTimeOffset.UtcNow.AddHours(-1);
        var relativePath = WavFile.RelativePath(id, date);
        WavFile.Write(TestAudio.Speech(1), h.Paths.RecordingPath(relativePath));
        h.History.Items = [Entry("новее"), Entry("старее", id, date, relativePath)];
        h.Build();
        h.Engines.Engines[EngineId.Parakeet] = new FakeEngine(EngineId.Parakeet.RawValue(), "older, rerun");

        await Controller.RerunAsync(id, EngineId.Parakeet, TestContext.Current.CancellationToken);

        Assert.Equal("older, rerun", h.History.Entries[1].Text);
        Assert.Equal("новее", Controller.LastTranscript);
    });

    [Fact]
    public Task RerunWithoutAudioThrows() => h.Run(async () =>
    {
        var id = Guid.NewGuid();
        h.History.Items = [Entry("текст", id)];

        var error = await Assert.ThrowsAsync<SlovoException>(() => Controller.RerunAsync(id, EngineId.Parakeet, TestContext.Current.CancellationToken));
        Assert.Equal(CoreStrings.Get("dictation.error.audioMissing"), error.Message);
        Assert.Empty(h.Engines.Requested);
    });

    [Fact]
    public Task RerunOfUnknownEntryThrows() => h.Run(async () =>
    {
        var error = await Assert.ThrowsAsync<SlovoException>(() => Controller.RerunAsync(Guid.NewGuid(), EngineId.Parakeet, TestContext.Current.CancellationToken));
        Assert.Equal(CoreStrings.Get("dictation.error.entryMissing"), error.Message);
    });

    // MARK: - Lifecycle

    [Fact]
    public Task StartWiresHotkeysAndStopReleasesThem() => h.Run(() =>
    {
        Controller.Start();
        Assert.Equal([h.Settings.HotkeyConfiguration], h.Hotkeys.StartedConfigurations);

        h.Hotkeys.Fire(DictationTrigger.BeginPushToTalk);
        h.ConfirmHold();
        Assert.True(Controller.State.IsRecording);

        Controller.Stop();
        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal(1, h.Recorder.CancelCount);
        Assert.Equal(1, h.Hotkeys.StopCount);
        Assert.False(h.Hotkeys.HasTriggerListeners);
        Assert.False(h.Recorder.HasLevelListeners);
    });

    [Fact]
    public Task KeySettingsReachTheRunningMonitor() => h.Run(() =>
    {
        Controller.Start();
        h.Settings.DictationKey = DictationKey.RightCtrl;
        h.Settings.HandsFreeEnabled = false;
        // The settings store raised its change events already; this covers the case it had not.
        Controller.SettingsDidChange();

        Assert.Equal(DictationKey.RightCtrl, h.Hotkeys.UpdatedConfigurations[^1].Key);
        Assert.False(h.Hotkeys.UpdatedConfigurations[^1].HandsFreeEnabled);

        var pushed = h.Hotkeys.UpdatedConfigurations.Count;
        Assert.True(pushed > 0);
        Controller.SettingsDidChange();
        h.Settings.SoundsEnabled = false;
        Assert.Equal(pushed, h.Hotkeys.UpdatedConfigurations.Count); // an unchanged configuration is not pushed again
    });

    [Fact]
    public Task KeySettingsChangedOnAnotherThreadArriveOnTheMainThread() => h.Run(async () =>
    {
        Controller.Start();
        await Task.Run(() => h.Settings.EscapeCancels = false);
        Assert.True(await TestAudio.WaitUntil(() => h.Hotkeys.UpdatedConfigurations.Count == 1));
        Assert.False(h.Hotkeys.UpdatedConfigurations[0].EscapeCancels);
    });

    [Fact]
    public Task RefreshHotkeysRetriesAMonitorThatCouldNotStart() => h.Run(() =>
    {
        h.Hotkeys.AllowsStart = false;
        Controller.Start();
        Assert.False(h.Hotkeys.IsRunning);

        h.Hotkeys.AllowsStart = true;
        Controller.RefreshHotkeys();
        Assert.True(h.Hotkeys.IsRunning);
        Assert.Equal(2, h.Hotkeys.StartedConfigurations.Count);
    });

    [Fact]
    public Task StateChangesAreRaisedOnTheMainThread() => h.Run(async () =>
    {
        var threads = new List<bool>();
        Controller.PropertyChanged += (_, _) => threads.Add(h.MainThread.IsMainThread);
        await h.Dictate();
        Assert.NotEmpty(threads);
        Assert.All(threads, Assert.True);
    });
}
