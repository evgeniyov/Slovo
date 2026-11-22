using System.Globalization;
using System.Text.RegularExpressions;
using Slovo.Core.Dictation;
using Slovo.Core.Input;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Dictation;

/// <summary>
/// Port of FixCoreDictationTests.swift: taps of the dictation key, shortcuts made with it,
/// a microphone that fails to open, results that are only a filler, and where a recording
/// lives while it is transcribed. Key events go through the real
/// <see cref="HotkeyStateMachine"/>; the dictation key is the Windows default, Ctrl+Win.
/// </summary>
public sealed partial class FixCoreDictationTests : IDisposable
{
    readonly DictationHarness h = new();

    public FixCoreDictationTests() => h.Engine.Text = "да я перезвоню";

    public void Dispose() => h.Dispose();

    DictationController Controller => h.Controller;

    /// <summary>
    /// Feeds key events through the state machine into the controller, the way the keyboard
    /// hook does, and records the state after each step.
    /// </summary>
    sealed class KeyFeed(DictationHarness harness)
    {
        readonly HotkeyStateMachine machine = new(harness.Settings.HotkeyConfiguration);

        public List<DictationState> States { get; } = [];

        public HotkeyConfiguration Configuration => machine.Configuration;

        /// <summary>Ctrl, then Win: the key counts as held once both are down.</summary>
        public void Press(double at) => Send(Key(RawKeyEventKind.KeyDown, VirtualKey.LeftCtrl, at), Key(RawKeyEventKind.KeyDown, VirtualKey.LeftWin, at));

        public void Release(double at) => Send(Key(RawKeyEventKind.KeyUp, VirtualKey.LeftWin, at), Key(RawKeyEventKind.KeyUp, VirtualKey.LeftCtrl, at));

        public void Other(int virtualKey, double at) => Send(Key(RawKeyEventKind.KeyDown, virtualKey, at));

        public void Deadline(double at) => Send(new HotkeyInput.Deadline(at));

        public void HoldCheck()
        {
            harness.Controller.HoldCheckDue();
            States.Add(harness.Controller.State);
        }

        static HotkeyInput Key(RawKeyEventKind kind, int virtualKey, double at) => new HotkeyInput.Key(new RawKeyEvent(kind, virtualKey, at));

        void Send(params HotkeyInput[] inputs)
        {
            foreach (var input in inputs)
            {
                var triggers = machine.Handle(input);
                harness.Hotkeys.IsKeyDown = machine.IsKeyDown;
                foreach (var trigger in triggers)
                {
                    harness.Controller.Handle(trigger);
                }
            }
            States.Add(harness.Controller.State);
        }
    }

    // MARK: - Taps

    /// <summary>A plain tap of the key, which people do by accident all the time.</summary>
    [Fact]
    public Task TapPlaysNoSoundAndShowsNothing() => h.Run(() =>
    {
        var keys = new KeyFeed(h);
        keys.Press(0);
        keys.Release(0.1);
        keys.HoldCheck();
        keys.Deadline(0.5);
        keys.HoldCheck();

        Assert.All(keys.States, state => Assert.Equal(DictationState.IdleState, state));
        Assert.Equal(5, keys.States.Count);
        Assert.Empty(h.Sounds.Played); // a tap played a sound
        // Capture starts at key down so the first syllable of a real dictation is kept.
        Assert.Single(h.Recorder.StartedDeviceUids);
        Assert.Equal(1, h.Recorder.CancelCount);
        Assert.Equal(0, h.Recorder.StopCount);
        Assert.False(h.Recorder.IsRecording); // the microphone is off again
        Assert.Empty(h.Engines.Requested);
    });

    [Fact]
    public Task HoldPastTheMinimumShowsTheRecordingAndDictates() => h.Run(async () =>
    {
        var keys = new KeyFeed(h);
        keys.Press(0);
        Assert.Equal(DictationState.IdleState, Controller.State);
        keys.HoldCheck();
        Assert.True(Controller.State.IsRecording);
        Assert.Equal([SoundCue.Start], h.Sounds.Played);
        keys.Release(1.2);
        await Controller.WaitForProcessingAsync();

        Assert.Equal([SoundCue.Start, SoundCue.Stop], h.Sounds.Played);
        Assert.Single(h.Inserter.Inserted);
        Assert.Equal(h.Inserter.Inserted, h.History.Entries.Select(e => e.Text));
    });

    [Fact]
    public Task DoubleTapShowsNothingUntilItIsPromoted() => h.Run(() =>
    {
        var keys = new KeyFeed(h);
        keys.Press(0);
        keys.Release(0.08);
        keys.HoldCheck();
        Assert.All(keys.States, state => Assert.Equal(DictationState.IdleState, state));
        Assert.Empty(h.Sounds.Played);

        keys.Press(0.3);
        var recording = Assert.IsType<DictationState.Recording>(Controller.State); // a double tap starts hands-free recording
        Assert.Equal(DictationMode.HandsFree, recording.Info.Mode);
        Assert.Equal([SoundCue.Start], h.Sounds.Played);
        Assert.Single(h.Recorder.StartedDeviceUids); // the capture of the first tap goes on
    });

    [Fact]
    public Task TapNobodyResolvesIsStoppedAtTheSecondCheck() => h.Run(() =>
    {
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.HoldCheckDue();
        Assert.Equal(0, h.Recorder.CancelCount); // the double-tap window is still open
        Controller.HoldCheckDue();

        Assert.Equal(1, h.Recorder.CancelCount);
        Assert.Equal(DictationState.IdleState, Controller.State);
        Assert.Empty(h.Sounds.Played);
        Assert.Equal([null], h.Hotkeys.SyncedModes); // the key state machine stops waiting for the tap
    });

    [Fact]
    public Task TapNobodyResolvesIsStoppedByTheSchedulerToo() => h.Run(() =>
    {
        Controller.HoldCheckDelay = null;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.Clock.Advance(0.25);
        Assert.Equal(0, h.Recorder.CancelCount);
        // The second check waits for the double-tap window and a little more.
        h.Clock.Advance(0.45);
        Assert.Equal(0, h.Recorder.CancelCount);
        h.Clock.Advance(0.1);
        Assert.Equal(1, h.Recorder.CancelCount);
        Assert.Equal([null], h.Hotkeys.SyncedModes);
    });

    [Fact]
    public Task TapLeavesAMessageOnScreen() => h.Run(() =>
    {
        h.Recorder.NextAudio = RecordedAudio.Empty;
        Controller.StartHandsFree();
        Controller.Finish();
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);

        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);
        Controller.Handle(DictationTrigger.DiscardRecording);
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);

        Controller.Handle(DictationTrigger.BeginPushToTalk);
        h.Hotkeys.IsKeyDown = true;
        Controller.HoldCheckDue();
        Assert.True(Controller.State.IsRecording);
    });

    [Fact]
    public Task RefusedTapShowsNothingButARefusedHoldExplainsWhy() => h.Run(() =>
    {
        h.Meetings.IsMeetingRecording = true;
        var keys = new KeyFeed(h);
        keys.Press(0);
        keys.Release(0.1);
        keys.HoldCheck();
        Assert.All(keys.States, state => Assert.Equal(DictationState.IdleState, state));
        Assert.Empty(h.Recorder.StartedDeviceUids);
        Assert.Equal([null], h.Hotkeys.SyncedModes);

        keys.Press(1);
        keys.HoldCheck();
        Assert.Equal(new DictationState.Message(FlowMessage.BlockedByMeeting), Controller.State);
        keys.Release(2);
        Assert.Equal(new DictationState.Message(FlowMessage.BlockedByMeeting), Controller.State);
    });

    // MARK: - Shortcuts with the dictation key

    /// <summary>Ctrl+Win+Right (next virtual desktop), with the key held a moment before the arrow.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShortcutWithTheDictationKeyIsNotADictation(bool confirmsHoldFirst) => h.Run(async () =>
    {
        const int right = 0x27;
        var keys = new KeyFeed(h);
        Assert.Equal(DictationKey.CtrlWin, keys.Configuration.Key);
        keys.Press(0);
        if (confirmsHoldFirst)
        {
            // Real time reaches the minimum hold before the arrow at 0.4 s.
            keys.HoldCheck();
        }
        keys.Other(right, 0.4);
        keys.Other(right, 0.6);
        keys.Release(0.9);
        await Controller.WaitForProcessingAsync();

        Assert.Empty(h.Inserter.Inserted); // Ctrl+Win+Right pasted a dictation into the focused app
        Assert.Empty(h.History.Entries);
        Assert.Empty(h.Engines.Requested);
        Assert.Equal(0, h.Recorder.StopCount);
        Assert.Equal(1, h.Recorder.CancelCount);
        Assert.Equal(DictationState.IdleState, Controller.State);
        // No stop sound: nothing was dictated.
        SoundCue[] expected = confirmsHoldFirst ? [SoundCue.Start] : [];
        Assert.Equal(expected, h.Sounds.Played);
    });

    // MARK: - Microphone failures

    [Fact]
    public Task MicrophoneThatFailsToOpenIsReportedWhenTheHoldIsConfirmed() => h.Run(() =>
    {
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        // The recorder opens the device in the background and gave up, with nothing captured.
        h.Recorder.IsRecording = false;
        h.Recorder.NextAudio = RecordedAudio.Empty;
        h.Hotkeys.IsKeyDown = true;
        Controller.HoldCheckDue();

        var message = CoreStrings.Get("dictation.error.microphoneFailed");
        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.RecordingFailed(message))), Controller.State);
        Assert.Empty(h.Sounds.Played);
        Assert.Equal([null], h.Hotkeys.SyncedModes);
        // The recorder is asked for what it kept, which after a failed open is nothing.
        Assert.Equal(1, h.Recorder.StopCount);
        Assert.Equal(0, h.Recorder.CancelCount);
    });

    [Fact]
    public Task MicrophoneThatFailsWhileShownIsReportedInsteadOfNothingHeard() => h.Run(() =>
    {
        Controller.StartHandsFree();
        h.Recorder.IsRecording = false;
        h.Recorder.NextAudio = RecordedAudio.Empty;
        Controller.CheckRecordingLimit();
        var message = CoreStrings.Get("dictation.error.microphoneFailed");
        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.RecordingFailed(message))), Controller.State);
    });

    [Fact]
    public Task MicrophoneThatFailedBeforeFinishIsReportedInsteadOfNothingHeard() => h.Run(async () =>
    {
        Controller.StartHandsFree();
        h.Recorder.IsRecording = false;
        h.Recorder.NextAudio = RecordedAudio.Empty;
        Controller.Finish();
        await Controller.WaitForProcessingAsync();
        var message = CoreStrings.Get("dictation.error.microphoneFailed");
        Assert.Equal(new DictationState.Message(FlowMessage.For(DictationFailure.RecordingFailed(message))), Controller.State);
        Assert.Empty(h.Engines.Requested);
        Assert.Equal([SoundCue.Start], h.Sounds.Played); // no stop sound for a recording that captured nothing
    });

    [Fact]
    public void MicrophoneFailureMessageIsTranslated()
    {
        var english = CoreStrings.TableFor("en").Get("dictation.error.microphoneFailed");
        var russian = CoreStrings.TableFor("ru").Get("dictation.error.microphoneFailed");
        Assert.NotEqual("dictation.error.microphoneFailed", english);
        Assert.NotEqual(english, russian);
        // The Flow bar shows longer messages as a generic line.
        Assert.True(new StringInfo(russian).LengthInTextElements <= 48, russian);
    }

    // MARK: - Filler-only results

    /// <summary>Stands in for the text pipeline's cleanup, which removes filler words.</summary>
    sealed partial class FillerPipeline : ITextProcessor
    {
        public TextPipelineResult Process(string raw, TextPipelineContext context) => new(Fillers().Replace(raw, "").Trim());

        [GeneratedRegex(@"(?<!\p{L})(э|ээ|эм|ммм|um|uh|erm|hmm)(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Fillers();
    }

    [Theory]
    [InlineData("Эм.")]
    [InlineData("Um.")]
    [InlineData("Hmm?")]
    [InlineData("Э...")]
    [InlineData("э, ээ, эм.")]
    public Task FillerOnlyDictationMustNotInsertPunctuation(string raw) => h.Run(async () =>
    {
        h.Pipeline = new FillerPipeline();
        h.Engine.Text = raw;
        h.Build();
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.FinishRecording);
        await Controller.WaitForProcessingAsync();

        Assert.Empty(h.Inserter.Inserted);
        Assert.Empty(h.History.Entries);
        Assert.Equal(new DictationState.Message(FlowMessage.NothingHeard), Controller.State);
        Assert.Empty(h.RecordedFiles());
    });

    [Fact]
    public Task RerunThatOnlyHearsAFillerKeepsTheEntry() => h.Run(async () =>
    {
        h.Pipeline = new FillerPipeline();
        h.Engine.Text = "созвонимся завтра";
        h.Build();
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.FinishRecording);
        await Controller.WaitForProcessingAsync();
        var entry = Assert.Single(h.History.Entries);

        h.Engines.Engines[EngineId.Parakeet] = new FakeEngine(EngineId.Parakeet.RawValue(), "Hmm?");
        var error = await Assert.ThrowsAsync<SlovoException>(() => Controller.RerunAsync(entry.Id, EngineId.Parakeet, TestContext.Current.CancellationToken));
        Assert.Equal(CoreStrings.Get("dictation.error.rerunEmpty"), error.Message);
        Assert.Equal(entry.Text, h.History.Entries[0].Text);
    });

    [Fact]
    public void ContentIsLettersDigitsOrSymbols()
    {
        Assert.False(DictationController.HasContent(""));
        Assert.False(DictationController.HasContent(". ? ... … — «»"));
        Assert.True(DictationController.HasContent("да"));
        Assert.True(DictationController.HasContent("42"));
        Assert.True(DictationController.HasContent("👍")); // a snippet may expand to an emoji
        Assert.True(DictationController.HasContent("€"));
    }

    // MARK: - Recordings in progress

    /// <summary>
    /// A recording stays out of Recordings/ until its entry exists, so retention can never
    /// delete the audio of a dictation that is still being transcribed.
    /// </summary>
    [Fact]
    public Task RecordingEntersRecordingsOnlyWithItsEntry() => h.Run(async () =>
    {
        h.Settings.KeepDictationAudio = RetentionPeriod.Hours24;
        h.Engine.Text = "проверка связи";
        var gate = h.NewGate();
        h.Engine.Gate = gate;
        Controller.Handle(DictationTrigger.BeginPushToTalk);
        Controller.Handle(DictationTrigger.FinishRecording);
        Assert.True(await TestAudio.WaitUntil(() => gate.Arrived == 1));
        Assert.Equal([16_000], h.Engine.SampleCounts); // the engine read the recording
        Assert.Empty(h.RecordedFiles());

        gate.Open();
        await Controller.WaitForProcessingAsync();
        var entry = Assert.Single(h.History.Entries);
        Assert.NotNull(entry.AudioFileName);
        Assert.Equal([entry.AudioFileName], h.RecordedFiles());
        Assert.NotNull(h.History.AudioPath(entry));
        Assert.False(h.TemporaryRecordingExists(entry.Id));
    });
}
