using Slovo.Core.Dictation;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Dictation;

/// <summary>
/// Port of DictationEscapeHatchTests.swift. A dictation that cannot finish never holds the
/// Flow bar: Esc, the × and the menu cancel it, a time limit abandons it, a lost microphone
/// keeps what it captured, and re-running an old dictation uses the language it was
/// dictated in.
/// </summary>
public sealed class DictationEscapeHatchTests : IDisposable
{
    readonly DictationHarness h = new();
    readonly ProcessingHotkeys hotkeys = new();
    readonly LanguageEngines engines = new();

    public DictationEscapeHatchTests()
    {
        h.Hotkeys = hotkeys;
        h.Engines = engines;
        h.Recorder.NextAudio = TestAudio.Speech(0.4);
        h.Build();
    }

    public void Dispose() => h.Dispose();

    DictationController Controller => h.Controller;

    static DictationState TimedOut =>
        new DictationState.Message(FlowMessage.For(DictationFailure.EngineFailed(CoreStrings.Get("dictation.error.timedOut"))));

    // MARK: - Cancelling while processing

    [Fact]
    public Task CancelAbandonsADictationBeingTranscribedAndTellsTheKeyListener() => h.Run(async () =>
    {
        var gate = h.NewGate();
        h.Engine.Gate = gate;
        Controller.StartHandsFree();
        Controller.Finish();
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        Assert.Equal([true], hotkeys.Processing); // Esc must cancel while the engine works
        Assert.True(await TestAudio.WaitUntil(() => gate.Arrived == 1));

        Controller.Cancel();
        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal([true, false], hotkeys.Processing);

        gate.Open();
        await Controller.WaitForProcessingAsync();
        Assert.Empty(h.Inserter.Inserted);
        Assert.Empty(h.History.Entries);
        Assert.Equal(DictationState.IdleState, Controller.State);
    });

    [Fact]
    public Task EscapeTriggerCancelsProcessing() => h.Run(() =>
    {
        h.Engine.Gate = h.NewGate();
        Controller.StartHandsFree();
        Controller.Finish();
        Controller.Handle(DictationTrigger.CancelRecording);
        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Equal([true, false], hotkeys.Processing);
    });

    [Fact]
    public Task CancelBeforeTheEngineIsAskedLoadsNothing() => h.Run(async () =>
    {
        Controller.StartHandsFree();
        Controller.Finish();
        Controller.Cancel();
        await Controller.WaitForProcessingAsync();

        Assert.Empty(engines.Requested);
        Assert.Empty(engines.Languages);
        Assert.Empty(Directory.EnumerateFiles(h.TemporaryFolder));
    });

    [Fact]
    public Task FinishedDictationTellsTheKeyListenerProcessingEnded() => h.Run(async () =>
    {
        Controller.StartHandsFree();
        Controller.Finish();
        await Controller.WaitForProcessingAsync();
        Assert.Equal(["привет мир"], h.Inserter.Inserted);
        Assert.Equal([true, false], hotkeys.Processing);
    });

    // MARK: - Time limits

    [Fact]
    public Task TranscriptionThatTakesTooLongIsAbandoned() => h.Run(async () =>
    {
        var gate = h.NewGate();
        h.Engine.Gate = gate;
        Controller.TranscriptionTimeLimit = 0.05;
        Controller.StartHandsFree();
        Controller.Finish();
        Assert.True(await TestAudio.WaitUntil(() => gate.Arrived == 1));

        // The limit is the transcription time plus the recording's own 0.4 s.
        h.Clock.Advance(0.4);
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        h.Clock.Advance(0.06);
        Assert.Equal(TimedOut, Controller.State);
        Assert.Equal([true, false], hotkeys.Processing);

        // A new dictation may start at once.
        Controller.StartHandsFree();
        Assert.True(Controller.State.IsRecording);
        Controller.Cancel();

        gate.Open();
        await Controller.WaitForProcessingAsync();
        Assert.Empty(h.Inserter.Inserted); // the late result of an abandoned dictation is dropped
        Assert.Empty(h.History.Entries);
    });

    [Fact]
    public Task ModelLoadThatTakesTooLongIsAbandoned() => h.Run(() =>
    {
        engines.LoadStates[EngineId.GigaamRnnt] = EngineLoadState.Loading;
        engines.LoadGate = h.NewGate();
        Controller.ModelLoadTimeLimit = 0.05;
        Controller.StartHandsFree();
        Controller.Finish();
        Assert.Equal(new DictationState.Processing(true), Controller.State);

        h.Clock.Advance(0.06);
        Assert.Equal(TimedOut, Controller.State);
    });

    [Fact]
    public Task LoadedEngineGetsTheTranscriptionLimitInsteadOfTheLoadLimit() => h.Run(async () =>
    {
        engines.LoadStates[EngineId.GigaamRnnt] = EngineLoadState.Loading;
        h.Engine.Gate = h.NewGate();
        Controller.ModelLoadTimeLimit = 0.2;
        Controller.TranscriptionTimeLimit = 30;
        Controller.StartHandsFree();
        Controller.Finish();

        Assert.True(await TestAudio.WaitUntil(() => Controller.State == new DictationState.Processing(false)));
        h.Clock.Advance(0.5);
        // The load limit no longer applies once the engine is ready.
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        h.Clock.Advance(30);
        Assert.Equal(TimedOut, Controller.State);
    });

    // MARK: - Microphone lost mid-recording

    [Fact]
    public Task MicrophoneLostMidRecordingStillTranscribesWhatWasCaptured() => h.Run(async () =>
    {
        Controller.StartHandsFree();
        // The recorder lost its device for good and keeps what it recorded before.
        h.Recorder.IsRecording = false;
        h.Recorder.NextAudio = TestAudio.Speech(1);
        Controller.CheckRecordingLimit();
        Assert.Equal(new DictationState.Processing(false), Controller.State);
        await Controller.WaitForProcessingAsync();

        Assert.Equal(["привет мир"], h.Inserter.Inserted);
        Assert.Equal(1, h.Recorder.StopCount);
        Assert.Equal([SoundCue.Start], h.Sounds.Played); // no stop sound: the recording ended by itself
        Assert.Null(hotkeys.SyncedModes[^1]);
    });

    [Fact]
    public Task MicrophoneLostWithNothingCapturedIsAnError() => h.Run(() =>
    {
        Controller.StartHandsFree();
        h.Recorder.IsRecording = false;
        h.Recorder.NextAudio = RecordedAudio.Empty;
        Controller.CheckRecordingLimit();
        var message = CoreStrings.Get("dictation.error.microphoneFailed");
        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.RecordingFailed(message))), Controller.State);
        Assert.Empty(engines.Requested);
    });

    // MARK: - Languages

    [Fact]
    public Task DictationAndRerunUseTheLanguageOfTheDictation() => h.Run(async () =>
    {
        h.Settings.LanguageMode = LanguageMode.English;
        engines.DictationEngine = EngineId.Parakeet;
        engines.Engines[EngineId.Parakeet] = new FakeEngine(EngineId.Parakeet.RawValue(), "hello");
        engines.LoadStates[EngineId.Parakeet] = EngineLoadState.Ready;
        Controller.StartHandsFree();
        Controller.Finish();
        await Controller.WaitForProcessingAsync();
        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(LanguageMode.English, entry.LanguageMode);
        Assert.NotNull(entry.AudioFileName);

        h.Settings.LanguageMode = LanguageMode.Russian;
        await Controller.RerunAsync(entry.Id, EngineId.Parakeet, TestContext.Current.CancellationToken);
        Assert.Equal([LanguageMode.English, LanguageMode.English], engines.Languages);
        Assert.Equal(LanguageMode.English, h.FakePipeline.Contexts[^1].LanguageMode);
    });

    // MARK: - Leftover temporary recordings

    [Fact]
    public void LeftoverTemporaryRecordingsAreRemoved()
    {
        var folder = Path.Combine(h.Temporary, "sweep");
        Directory.CreateDirectory(folder);
        var now = DateTime.UtcNow;
        var old = now.AddSeconds(-7_200);
        string Make(string name, DateTime modified)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetLastWriteTimeUtc(path, modified);
            return path;
        }
        var leftover = Make($"Slovo-{Guid.NewGuid().ToString("D").ToUpperInvariant()}.wav", old);
        var running = Make($"Slovo-{Guid.NewGuid().ToString("D").ToUpperInvariant()}.wav", now);
        var unrelated = Make("Slovo-notes.wav", old);
        var other = Make("recording.wav", old);
        var meeting = Path.Combine(folder, $"Slovo-Meeting-{Guid.NewGuid():D}");
        Directory.CreateDirectory(meeting);

        Assert.Equal(1, DictationController.RemoveLeftoverRecordings(folder, now));
        Assert.False(File.Exists(leftover));
        foreach (var kept in new[] { running, unrelated, other })
        {
            Assert.True(File.Exists(kept), kept);
        }
        Assert.True(Directory.Exists(meeting));
        Assert.Equal(0, DictationController.RemoveLeftoverRecordings(Path.Combine(folder, "missing"), now));
    }

    [Fact]
    public Task StartSweepsLeftoverRecordings() => h.Run(async () =>
    {
        var leftover = Path.Combine(h.TemporaryFolder, $"Slovo-{Guid.NewGuid().ToString("D").ToUpperInvariant()}.wav");
        File.WriteAllBytes(leftover, [1, 2, 3]);
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));
        Controller.Start();
        Assert.True(await TestAudio.WaitUntil(() => !File.Exists(leftover)));
    });

    [Fact]
    public void TemporaryRecordingNamesAreExact()
    {
        Assert.True(DictationController.IsTemporaryRecording($"Slovo-{Guid.NewGuid().ToString("D").ToUpperInvariant()}.wav"));
        Assert.False(DictationController.IsTemporaryRecording($"Slovo-{Guid.NewGuid().ToString("D").ToUpperInvariant()}.caf"));
        Assert.False(DictationController.IsTemporaryRecording($"Slovo-Meeting-{Guid.NewGuid():D}"));
        Assert.False(DictationController.IsTemporaryRecording("Slovo-.wav"));
        Assert.False(DictationController.IsTemporaryRecording("Slovo.wav"));
    }
}
