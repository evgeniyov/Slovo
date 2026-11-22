using System.ComponentModel;
using Slovo.Core.Localization;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Settings;
using Slovo.Core.Tests.Dictation;
using static Slovo.Core.Tests.Meetings.TranscriberAudio;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="MeetingTranscriber"/> with a fake engine and a fake diarizer on synthetic tone
/// tracks, run on a test UI thread: clips and lines, speakers, phases and progress, the
/// transcript file, cancellation, failures, re-runs, speakers-only runs, the speaker count
/// hint and retention (port of MeetingsTranscriberTests.swift).
/// </summary>
public sealed class TranscriberTests : IDisposable
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");

    readonly TemporaryMeetingsFolder folder = new();
    readonly TestMainThread mainThread = new();
    readonly MeetingStore store;

    public TranscriberTests()
    {
        store = NewStore();
    }

    public void Dispose() => folder.Dispose();

    MeetingStore NewStore() => new(folder.Paths, mainThread)
    {
        TranscriptLanguage = () => AppLanguage.Ru,
        TranscriptTimeZone = Zone,
    };

    Task Run(Func<Task> body) => mainThread.RunAsync(body);

    // MARK: - Without speaker separation

    [Fact]
    public Task TranscribesBothTracksAndWritesTheTranscript() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(
            Join(Silence(1), Tone(2, 0.2f), Silence(6), Tone(2, 0.2f), Silence(2)),
            Join(Silence(4), Tone(2, 0.5f), Silence(7)));
        var engines = new TranscriberEngines();
        var transcriber = new MeetingTranscriber(store, engines, mainThread);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.Equal(EngineId.Parakeet, result.EngineId);
        Assert.Equal(2, result.FormatVersion);
        Assert.Null(result.ErrorMessage);
        Assert.Equal([Speaker.Me, Speaker.Others, Speaker.Me], result.Segments.Select(s => s.Speaker));
        Assert.Equal(["quiet words", "loud words", "quiet words"], result.Segments.Select(s => s.Text));
        Assert.Equal(["00:00", "00:03", "00:08"], result.Segments.Select(s => TranscriptMerger.Timestamp(s.Start)));
        Assert.All(result.Segments, s => Assert.Null(s.SpeakerId));
        Assert.Empty(result.Speakers);
        Assert.Equal(DiarizationState.Off, result.Diarization?.State);
        Assert.Equal(3, engines.Engine.CallCount);
        Assert.Equal(3, engines.MarkedUsed);
        Assert.False(transcriber.IsTranscribing(meeting.Id));
        Assert.Empty(transcriber.Progress);
        Assert.Equal(result, store.Meeting(meeting.Id));
        Assert.True(result.HasAudio);

        var transcript = File.ReadAllText(store.TranscriptPath(meeting.Id));
        Assert.StartsWith("# Standup\n", transcript);
        Assert.Contains("[00:00] Я: quiet words\n\n[00:03] Собеседники: loud words\n\n[00:08] Я: quiet words", transcript);
    });

    [Fact]
    public Task SegmentsStayOnePerClipAndJoinOnlyIntoDisplayLines() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(Join(Silence(1), Tone(2, 0.2f), Silence(3), Tone(2, 0.2f)), Silence(8));
        var transcriber = new MeetingTranscriber(store, new TranscriberEngines(), mainThread);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(["quiet words", "quiet words"], result.Segments.Select(s => s.Text));
        var lines = TranscriptMerger.Lines(result);
        Assert.Equal(["quiet words quiet words"], lines.Select(l => l.Text));
        Assert.Equal([0, 1], lines[0].SegmentIndices);
        Assert.Equal("me", lines[0].SpeakerId);
    });

    [Fact]
    public Task ProgressCountsSegmentsOfBothTracks() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(Join(Tone(2, 0.2f), Silence(2), Tone(2, 0.2f)), Join(Silence(3), Tone(2, 0.5f)));
        var engines = new TranscriberEngines();
        engines.Engine.Delay = TimeSpan.FromMilliseconds(100);
        var transcriber = new MeetingTranscriber(store, engines, mainThread);
        var seen = Observe(transcriber, meeting.Id);

        await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(new TranscriptionProgress(0, 0, MeetingProcessingPhase.Preparing, 0), seen[0]);
        Assert.Equal(new TranscriptionProgress(3, 3), seen[^1]);
        Assert.Contains(new TranscriptionProgress(1, 3), seen);
    });

    [Fact]
    public Task CancelStopsTranscriptionAndMarksTheMeeting() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(Join(Tone(2, 0.2f), Silence(3), Tone(2, 0.2f)), null);
        var engines = new TranscriberEngines();
        engines.Engine.Delay = TimeSpan.FromSeconds(30);
        var transcriber = new MeetingTranscriber(store, engines, mainThread);

        var task = transcriber.TranscribeAsync(meeting.Id);
        await WaitUntil(() => engines.Engine.CallCount > 0);
        Assert.True(transcriber.IsTranscribing(meeting.Id));
        Assert.Equal(MeetingStatus.Transcribing, store.Meeting(meeting.Id)?.Status);
        await Assert.ThrowsAsync<SlovoException>(() => transcriber.TranscribeAsync(meeting.Id));

        var started = DateTime.UtcNow;
        transcriber.Cancel(meeting.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(1, engines.Engine.CallCount);
        Assert.False(transcriber.IsTranscribing(meeting.Id));
        var failed = store.Meeting(meeting.Id)!;
        Assert.Equal(MeetingStatus.Failed, failed.Status);
        Assert.Equal(CoreStrings.Get("meetings.transcription.cancelled"), failed.ErrorMessage);
        Assert.False(File.Exists(store.TranscriptPath(meeting.Id)));

        // A cancelled meeting can be transcribed again.
        engines.Engine.Delay = null;
        var retried = await transcriber.TranscribeAsync(meeting.Id);
        Assert.Equal(MeetingStatus.Transcribed, retried.Status);
    });

    [Fact]
    public Task EngineFailureMarksTheMeetingFailed() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(Tone(2, 0.2f), null);
        var engines = new TranscriberEngines { LoadError = new SlovoException("Parakeet v3 is not downloaded") };
        var transcriber = new MeetingTranscriber(store, engines, mainThread);

        var error = await Assert.ThrowsAsync<SlovoException>(() => transcriber.TranscribeAsync(meeting.Id));

        Assert.Equal("Parakeet v3 is not downloaded", error.Message);
        var failed = store.Meeting(meeting.Id)!;
        Assert.Equal(MeetingStatus.Failed, failed.Status);
        Assert.Equal("Parakeet v3 is not downloaded", failed.ErrorMessage);
        Assert.False(transcriber.IsTranscribing(meeting.Id));
    });

    [Fact]
    public Task AnEngineWhoseModelIsMissingFailsTheMeetingWithItsLocalizedMessage() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(Tone(2, 0.2f), null);
        var unavailable = new EngineUnavailableException(EngineId.Parakeet, ModelStatus.NotDownloaded(1));
        var engines = new TranscriberEngines { LoadError = unavailable };
        var transcriber = new MeetingTranscriber(store, engines, mainThread);

        await Assert.ThrowsAsync<EngineUnavailableException>(() => transcriber.TranscribeAsync(meeting.Id));

        var failed = store.Meeting(meeting.Id)!;
        Assert.Equal(MeetingStatus.Failed, failed.Status);
        Assert.Equal(CoreStrings.Format("dictation.error.modelMissing", "Parakeet v3"), failed.ErrorMessage);
        Assert.Equal(0, engines.Engine.CallCount);
    });

    [Fact]
    public Task SilentMeetingGetsAnEmptyTranscript() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(Silence(5), Silence(5));
        var engines = new TranscriberEngines();
        var transcriber = new MeetingTranscriber(store, engines, mainThread);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.Empty(result.Segments);
        Assert.Equal(0, engines.Engine.CallCount);
        var transcript = File.ReadAllText(store.TranscriptPath(meeting.Id));
        Assert.EndsWith(CoreStrings.TableFor("ru").Get("meetings.transcript.empty") + "\n", transcript);
    });

    [Fact]
    public Task RefusesMeetingsThatCannotBeTranscribed() => Run(async () =>
    {
        var transcriber = new MeetingTranscriber(store, new TranscriberEngines(), mainThread);

        await AssertThrows(MeetingErrors.NotFound(), () => transcriber.TranscribeAsync(Guid.NewGuid()));

        var recording = store.Create("Live");
        await AssertThrows(MeetingErrors.StillRecording(), () => transcriber.TranscribeAsync(recording.Id));

        var noAudio = MakeRecordedMeeting(Tone(1, 0.2f), null);
        store.DeleteAudio(noAudio.Id);
        noAudio = store.Meeting(noAudio.Id)!;
        await AssertThrows(MeetingErrors.NoAudio(), () => transcriber.TranscribeAsync(noAudio.Id));
        Assert.Equal(MeetingStatus.Recorded, store.Meeting(noAudio.Id)?.Status);
        Assert.False(transcriber.CanProcess(noAudio, MeetingProcessingMode.Full));
        Assert.False(transcriber.CanProcess(recording, MeetingProcessingMode.Full));
    });

    [Fact]
    public Task DeleteAfterTranscriptionRemovesAudioOnceTheTranscriptIsSaved() => Run(async () =>
    {
        var settings = new AppSettings(SettingsStore.InMemory()) { KeepMeetingAudio = RetentionPeriod.AfterTranscription };
        var meeting = MakeRecordedMeeting(Tone(2, 0.2f), Tone(2, 0.5f));
        var transcriber = new MeetingTranscriber(store, new TranscriberEngines(), mainThread, settings);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.False(result.HasAudio);
        Assert.False(File.Exists(store.MicrophoneAudioPath(meeting.Id)));
        Assert.False(File.Exists(store.SystemAudioPath(meeting.Id)));
        Assert.True(File.Exists(store.TranscriptPath(meeting.Id)));
        Assert.False(NewStore().Meeting(meeting.Id)!.HasAudio);
    });

    [Fact]
    public Task KeptAudioStaysAfterTranscription() => Run(async () =>
    {
        var settings = new AppSettings(SettingsStore.InMemory()) { KeepMeetingAudio = RetentionPeriod.Days30 };
        var meeting = MakeRecordedMeeting(Tone(2, 0.2f), null);
        var result = await new MeetingTranscriber(store, new TranscriberEngines(), mainThread, settings).TranscribeAsync(meeting.Id);

        Assert.True(result.HasAudio);
        Assert.True(File.Exists(store.MicrophoneAudioPath(meeting.Id)));
    });

    [Fact]
    public Task HeavyWorkRunsOffTheMainThread() => Run(async () =>
    {
        var main = Environment.CurrentManagedThreadId;
        var meeting = MakeRecordedMeeting(TwoVoices(), CallSystem());
        var engines = new TranscriberEngines();
        var transcriber = new MeetingTranscriber(store, engines, mainThread);
        var changedOn = new List<int>();
        transcriber.PropertyChanged += (_, _) => changedOn.Add(Environment.CurrentManagedThreadId);

        await transcriber.TranscribeAsync(meeting.Id);

        Assert.NotEmpty(engines.Engine.Threads);
        Assert.DoesNotContain(main, engines.Engine.Threads);
        Assert.NotEmpty(changedOn);
        Assert.All(changedOn, thread => Assert.Equal(main, thread));
        Assert.Equal(main, Environment.CurrentManagedThreadId);
    });

    // MARK: - Speaker separation

    [Fact]
    public Task SeparatedSpeakersGetNumberedLinesInTheTranscript() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var engines = new TranscriberEngines();
        var clock = new ManualTimeProvider();
        var transcriber = MakeTranscriber(engines, diarization, time: clock);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.Equal(2, result.FormatVersion);
        Assert.Equal(
            [
                new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0 },
                new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, ColorIndex = 1 },
            ],
            result.Speakers);
        Assert.Equal(["s1", "s2"], result.Segments.Select(s => s.SpeakerId));
        Assert.Equal([Speaker.Me, Speaker.Me], result.Segments.Select(s => s.Speaker));
        Assert.Equal(0.75, result.Segments[0].Start, 1e-6);
        Assert.Equal(7.25, result.Segments[0].End, 1e-6);
        Assert.Equal(8.75, result.Segments[^1].Start, 1e-6);
        Assert.Equal(15.25, result.Segments[^1].End, 1e-6);

        Assert.Equal(["[00:00] Спикер 1: quiet words", "[00:08] Спикер 2: quiet words"], LineTexts(result));

        var record = result.Diarization!;
        Assert.Equal(DiarizationState.Applied, record.State);
        Assert.Equal(new Dictionary<string, int> { ["microphone"] = 2 }, record.SpeakersPerTrack);
        Assert.Equal(TranscriberDiarization.FakeModelName, record.ModelName);
        Assert.Equal(TranscriberDiarization.FakeConfiguration, record.Configuration);
        Assert.Null(record.RequestedSpeakers);
        Assert.Equal(clock.GetUtcNow(), record.ProcessedAt);

        Assert.Equal(new Dictionary<string, SpeakerCountHint> { ["mic.caf"] = SpeakerCountHint.Automatic }, diarization.Diarizer.Hints);
        Assert.Equal(1, diarization.DiarizerRequests);
        Assert.Equal(1, diarization.Releases);
        Assert.Equal(2, engines.Engine.CallCount);

        var transcript = File.ReadAllText(store.TranscriptPath(meeting.Id));
        Assert.Contains("\nУчастники: Спикер 1, Спикер 2\n", transcript);
        Assert.Contains("[00:00] Спикер 1: quiet words\n\n[00:08] Спикер 2: quiet words\n", transcript);
        // The outcome is what the record on disk says too.
        Assert.Equal(result, NewStore().Meeting(meeting.Id));
    });

    [Fact]
    public Task CallGivesSystemSpeakersTheNextNumbersAndMarksTheOnlyMicrophoneVoiceAsMe() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), CallSystem());
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new()
        {
            ["mic.caf"] = [new SpeakerTurn(0.9, 15.1, 0)],
            ["system.caf"] = [new SpeakerTurn(2.9, 9.1, 0)],
        };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(
            [
                new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0, IsMe = true },
                new MeetingSpeaker { Id = "s2", Track = MeetingTrack.System, Number = 2, ColorIndex = 1 },
            ],
            result.Speakers);
        Assert.Equal(["s1", "s2"], result.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["quiet words", "loud words"], result.Segments.Select(s => s.Text));
        Assert.Equal(new Dictionary<string, int> { ["microphone"] = 1, ["system"] = 1 }, result.Diarization?.SpeakersPerTrack);
        Assert.Equal(["Я", "Спикер 2"], Labels(result));
    });

    /// <summary>
    /// A call recorded with the microphone off the whole time has a microphone track of
    /// silence. It gives no lines and no «Я», and the diarizer is never shown it.
    /// </summary>
    [Fact]
    public Task ACallWithTheMicrophoneOffGivesNoMicrophoneLinesAndNoMe() => Run(async () =>
    {
        var meeting = store.Create("Созвон");
        Write(TwoVoices(), store.MicrophoneAudioPath(meeting.Id), muted: true);
        Write(CallSystem(), store.SystemAudioPath(meeting.Id));
        store.Update(meeting with { Status = MeetingStatus.Recorded, HasSystemAudio = true, Duration = 16 });

        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new()
        {
            ["mic.caf"] = [new SpeakerTurn(0.9, 15.1, 0)],
            ["system.caf"] = [new SpeakerTurn(2.9, 9.1, 0)],
        };
        var engines = new TranscriberEngines();
        var separated = await MakeTranscriber(engines, diarization).TranscribeAsync(meeting.Id);

        Assert.Equal(1, diarization.Diarizer.CallCount);
        Assert.Equal(["system.caf"], diarization.Diarizer.Hints.Keys);
        Assert.Equal([Speaker.Others], separated.Segments.Select(s => s.Speaker));
        Assert.Equal([new MeetingSpeaker { Id = "s1", Track = MeetingTrack.System, Number = 1, ColorIndex = 0 }], separated.Speakers);
        Assert.DoesNotContain(separated.EffectiveSpeakers, s => TranscriptMerger.IsMe(s, separated));
        Assert.Equal(["Спикер 1"], Labels(separated));
        Assert.Equal(1, engines.Engine.CallCount); // no clip is cut from the silent track

        // Without speaker separation the silent track is just as absent, and the voice the
        // first run found on system audio keeps its number.
        var plain = await MakeTranscriber(new TranscriberEngines(), new TranscriberDiarization(), separate: false).TranscribeAsync(meeting.Id);
        Assert.Equal([Speaker.Others], plain.Segments.Select(s => s.Speaker));
        Assert.Equal(["s1"], plain.EffectiveSpeakers.Select(s => s.Id));
        Assert.DoesNotContain(plain.EffectiveSpeakers, s => TranscriptMerger.IsMe(s, plain));
        Assert.DoesNotContain("Я:", File.ReadAllText(store.TranscriptPath(meeting.Id)));
    });

    [Fact]
    public Task PhasesFollowTheRunAndProgressReachesOne() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), CallSystem());
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns, ["system.caf"] = [new SpeakerTurn(2.9, 9.1, 0)] };
        // The pause after the diarizer's half-way report lets that report reach the UI thread
        // while the track is still being separated.
        diarization.Diarizer.Delay = TimeSpan.FromMilliseconds(100);
        var engines = new TranscriberEngines();
        engines.Engine.Delay = TimeSpan.FromMilliseconds(30);
        var transcriber = MakeTranscriber(engines, diarization);
        var seen = Observe(transcriber, meeting.Id);

        await transcriber.TranscribeAsync(meeting.Id);

        var kinds = new List<string>();
        foreach (var kind in seen.Select(p => Kind(p.Phase)))
        {
            if (kinds.Count == 0 || kinds[^1] != kind)
            {
                kinds.Add(kind);
            }
        }
        Assert.Equal(["preparing", "loading", "separating-microphone", "separating-system", "transcribing"], kinds);
        Assert.Equal(1, seen[^1].OverallFraction, 1e-9);
        Assert.Equal(3, seen[^1].CompletedSegments);
        for (var index = 1; index < seen.Count; index++)
        {
            Assert.True(seen[index - 1].OverallFraction <= seen[index].OverallFraction + 1e-9, $"{seen[index - 1]} → {seen[index]}");
        }
        // Separation takes the first fifth of the bar in a full run.
        Assert.Equal(0.2, seen.First(p => p.Phase == MeetingProcessingPhase.Transcribing).OverallFraction, 1e-9);
        // Half of the first of two tracks is a quarter of separation, a twentieth of the bar.
        Assert.Contains(seen, p => p.Phase == MeetingProcessingPhase.SeparatingSpeakers(MeetingTrack.Microphone, 0.5)
                                   && Math.Abs(p.OverallFraction - 0.05) < 1e-9);
    });

    [Fact]
    public Task CancelDuringSeparationLeavesNoTranscript() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        diarization.Diarizer.Delay = TimeSpan.FromSeconds(30);
        var engines = new TranscriberEngines();
        var transcriber = MakeTranscriber(engines, diarization);

        var task = transcriber.TranscribeAsync(meeting.Id);
        await WaitUntil(() => diarization.Diarizer.CallCount > 0);
        transcriber.Cancel(meeting.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var failed = store.Meeting(meeting.Id)!;
        Assert.Equal(MeetingStatus.Failed, failed.Status);
        Assert.Equal(CoreStrings.Get("meetings.transcription.cancelled"), failed.ErrorMessage);
        Assert.Empty(failed.Segments);
        Assert.Equal(0, engines.Requests);
        Assert.Equal(1, diarization.Releases);
        Assert.False(transcriber.IsTranscribing(meeting.Id));
        Assert.False(File.Exists(store.TranscriptPath(meeting.Id)));
    });

    [Fact]
    public Task CancelWhileTheSpeakerModelLoadsEndsTheRunAtOnce() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization { LoadGate = new TaskCompletionSource() };
        var engines = new TranscriberEngines();
        var transcriber = MakeTranscriber(engines, diarization);

        var task = transcriber.TranscribeAsync(meeting.Id);
        await WaitUntil(() => diarization.DiarizerRequests == 1);
        Assert.Equal(MeetingProcessingPhase.LoadingSpeakerModel, transcriber.Progress[meeting.Id].Phase);
        var cancelledAt = DateTime.UtcNow;
        transcriber.Cancel(meeting.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.True(DateTime.UtcNow - cancelledAt < TimeSpan.FromSeconds(2), "the run waited for the model");
        Assert.Equal(MeetingStatus.Failed, store.Meeting(meeting.Id)?.Status);
        Assert.Equal(0, engines.Requests);
        Assert.Equal(1, diarization.Releases);
    });

    [Fact]
    public Task CancelDuringSeparationOfAReRunRestoresThePreviousTranscript() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);
        var previous = await transcriber.TranscribeAsync(meeting.Id);
        var previousDocument = File.ReadAllBytes(store.TranscriptPath(meeting.Id));

        diarization.Diarizer.Delay = TimeSpan.FromSeconds(30);
        var task = transcriber.TranscribeAsync(meeting.Id);
        await WaitUntil(() => diarization.Diarizer.CallCount > 1);
        Assert.Equal(MeetingStatus.Transcribing, store.Meeting(meeting.Id)?.Status);
        transcriber.Cancel(meeting.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(previous, store.Meeting(meeting.Id));
        Assert.Equal(previousDocument, File.ReadAllBytes(store.TranscriptPath(meeting.Id)));
        store.Flush();
        Assert.Equal(previous, NewStore().Meeting(meeting.Id));
    });

    [Fact]
    public Task WithoutTheModelSpeakersStayMeAndOthers() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), CallSystem());
        var diarization = new TranscriberDiarization { ModelStatus = ModelStatus.NotDownloaded(21_776_918) };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization, expectedSpeakers: 3);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.Empty(result.Speakers);
        Assert.Equal(["me", "others"], result.EffectiveSpeakers.Select(s => s.Id));
        Assert.All(result.Segments, s => Assert.Null(s.SpeakerId));
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal(DiarizationState.ModelMissing, result.Diarization?.State);
        Assert.Equal(3, result.Diarization?.RequestedSpeakers);
        Assert.Null(result.Diarization?.ModelName);
        Assert.Equal(0, diarization.DiarizerRequests);
        Assert.Equal(0, diarization.Releases);
        Assert.False(transcriber.CanProcess(result, MeetingProcessingMode.SpeakersOnly));
        Assert.True(transcriber.CanProcess(result, MeetingProcessingMode.Full));
    });

    [Fact]
    public Task SeparationOffOrUnsupportedIsRecorded() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };

        var off = await MakeTranscriber(new TranscriberEngines(), diarization, separate: false).TranscribeAsync(meeting.Id);
        Assert.Equal(DiarizationState.Off, off.Diarization?.State);
        Assert.Empty(off.Speakers);

        diarization.IsSupportedOnThisSystem = false;
        var unsupported = await MakeTranscriber(new TranscriberEngines(), diarization).TranscribeAsync(meeting.Id);
        Assert.Equal(DiarizationState.UnsupportedSystem, unsupported.Diarization?.State);
        Assert.Equal(CoreStrings.Get("meetings.diarization.unsupportedSystem"), unsupported.Diarization?.Message);
        Assert.Empty(unsupported.Speakers);
        Assert.Equal(0, diarization.DiarizerRequests);

        // Without settings or a provider there is no separation at all.
        var bare = await new MeetingTranscriber(store, new TranscriberEngines(), mainThread).TranscribeAsync(meeting.Id);
        Assert.Equal(DiarizationState.Off, bare.Diarization?.State);
    });

    /// <summary>
    /// A full re-run that does not separate (turned off, no model, or a failure) keeps the
    /// voices an earlier run found, with the names and «Я» the user gave them.
    /// </summary>
    [Fact]
    public Task AFullReRunWithoutSeparationKeepsTheNamedSpeakers() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var first = await MakeTranscriber(new TranscriberEngines(), diarization).TranscribeAsync(meeting.Id);
        Assert.Equal(["s1", "s2"], first.Segments.Select(s => s.SpeakerId));
        store.Update(MeetingEdits.MarkAsMe("s1", MeetingEdits.RenameSpeaker("s2", "Аня", first)));

        var plain = await MakeTranscriber(new TranscriberEngines(), diarization, separate: false).TranscribeAsync(meeting.Id);

        Assert.Equal(DiarizationState.Off, plain.Diarization?.State);
        Assert.Equal(["s1", "s2"], plain.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["s1", "s2"], plain.Speakers.Select(s => s.Id));
        Assert.Equal([null, "Аня"], plain.Speakers.Select(s => s.DisplayName));
        Assert.Equal([true, false], plain.Speakers.Select(s => s.IsMe));
        Assert.Equal(["[00:00] Я: quiet words", "[00:08] Аня: quiet words"], LineTexts(plain));

        // A run whose separation fails keeps them the same way.
        diarization.Diarizer.Error = new SlovoException("Segmentation crashed");
        var failed = await MakeTranscriber(new TranscriberEngines(), diarization).TranscribeAsync(meeting.Id);
        Assert.Equal(DiarizationState.Failed, failed.Diarization?.State);
        Assert.Equal(["s1", "s2"], failed.Segments.Select(s => s.SpeakerId));
        Assert.Equal([null, "Аня"], failed.Speakers.Select(s => s.DisplayName));
        Assert.Equal(2, failed.HighestSpeakerNumber);
    });

    [Fact]
    public Task DiarizerErrorStillCompletesWithFallbackSpeakers() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), CallSystem());
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Error = new SlovoException("Segmentation crashed");
        var engines = new TranscriberEngines();
        var transcriber = MakeTranscriber(engines, diarization);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.Empty(result.Speakers);
        Assert.Equal(["me", "others"], result.EffectiveSpeakers.Select(s => s.Id));
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal(DiarizationState.Failed, result.Diarization?.State);
        Assert.Equal(CoreStrings.Format("meetings.diarization.failed", "Segmentation crashed"), result.Diarization?.Message);
        // Separation stops at the first failure.
        Assert.Equal(1, diarization.Diarizer.CallCount);
        Assert.Equal(1, diarization.Releases);
        Assert.Equal(3, engines.Engine.CallCount);

        diarization.Diarizer.Error = null;
        diarization.LoadError = new SlovoException("The speaker model is damaged");
        var loadFailed = await transcriber.TranscribeAsync(meeting.Id);
        Assert.Equal(MeetingStatus.Transcribed, loadFailed.Status);
        Assert.Equal(DiarizationState.Failed, loadFailed.Diarization?.State);
        Assert.Equal(CoreStrings.Format("meetings.diarization.failed", "The speaker model is damaged"), loadFailed.Diarization?.Message);
    });

    [Fact]
    public Task FailedFullReRunRestoresThePreviousTranscript() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var engines = new TranscriberEngines();
        var transcriber = MakeTranscriber(engines, diarization);
        var previous = await transcriber.TranscribeAsync(meeting.Id);
        var previousDocument = File.ReadAllBytes(store.TranscriptPath(meeting.Id));

        engines.LoadError = new SlovoException("GigaAM is not downloaded");
        var error = await Assert.ThrowsAsync<SlovoException>(() =>
            transcriber.TranscribeAsync(meeting.Id, new MeetingProcessingOptions(ExpectedSpeakers: 3, EngineId: EngineId.GigaamCtc)));

        Assert.Equal("GigaAM is not downloaded", error.Message);
        Assert.Equal([EngineId.Parakeet, EngineId.GigaamCtc], engines.RequestedIds);
        Assert.Equal(previous, store.Meeting(meeting.Id));
        Assert.Equal(previousDocument, File.ReadAllBytes(store.TranscriptPath(meeting.Id)));
        Assert.False(transcriber.IsTranscribing(meeting.Id));
    });

    [Fact]
    public Task RenameTitleAndMomentMadeDuringProcessingSurvive() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var engines = new TranscriberEngines();
        var transcriber = MakeTranscriber(engines, diarization);
        await transcriber.TranscribeAsync(meeting.Id);

        engines.Engine.Delay = TimeSpan.FromMilliseconds(300);
        var task = transcriber.TranscribeAsync(meeting.Id);
        await WaitUntil(() => engines.Engine.CallCount > 2);
        var processing = store.Meeting(meeting.Id)!;
        var moment = new MeetingHighlight { Time = 9, Note = "дедлайн", Source = HighlightSource.Recording };
        var edited = MeetingEdits.RenameSpeaker("s2", "Аня", processing) with { Title = "Коворк" };
        store.Update(edited with { Highlights = [.. edited.Highlights, moment] });

        var result = await task;

        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.Equal("Коворк", result.Title);
        Assert.Equal([moment], result.Highlights);
        Assert.Equal(["s1", "s2"], result.Speakers.Select(s => s.Id));
        Assert.Equal("Аня", result.SpeakerWithId("s2")?.DisplayName);
        var transcript = File.ReadAllText(store.TranscriptPath(meeting.Id));
        Assert.StartsWith("# Коворк\n", transcript);
        Assert.Contains("[00:08] ★ Аня: quiet words", transcript);
    });

    [Fact]
    public Task ReRunWithMorePeopleKeepsMatchedNamesAndNumbersNewVoicesAbove() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);
        var first = await transcriber.TranscribeAsync(meeting.Id);
        store.Update(MeetingEdits.RenameSpeaker("s2", "Аня", first));

        diarization.Diarizer.Turns = new() { ["mic.caf"] = ThreeVoiceTurns };
        var result = await transcriber.TranscribeAsync(meeting.Id, new MeetingProcessingOptions(ExpectedSpeakers: 3));

        Assert.Equal(["s1", "s2", "s3"], result.Speakers.Select(s => s.Id));
        Assert.Equal([null, "Аня", null], result.Speakers.Select(s => s.DisplayName));
        Assert.Equal([1, 2, 3], result.Speakers.Select(s => s.Number));
        Assert.Equal(["s1", "s2", "s3"], result.Segments.Select(s => s.SpeakerId));
        Assert.Equal(3, result.Diarization?.RequestedSpeakers);
        Assert.Equal(new SpeakerCountHint(Exactly: 3), diarization.Diarizer.Hints["mic.caf"]);
    });

    [Fact]
    public Task SpeakersOnlyReassignsExistingTextWithoutTheEngine() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var engines = new TranscriberEngines();
        var transcriber = MakeTranscriber(engines, diarization);
        var first = await transcriber.TranscribeAsync(meeting.Id);
        Assert.True(transcriber.CanProcess(first, MeetingProcessingMode.SpeakersOnly));
        var requests = engines.Requests;
        var calls = engines.Engine.CallCount;

        diarization.Diarizer.Turns = new() { ["mic.caf"] = [new SpeakerTurn(0.9, 15.1, 0)] };
        var seen = Observe(transcriber, meeting.Id);
        var result = await transcriber.TranscribeAsync(meeting.Id, new MeetingProcessingOptions(MeetingProcessingMode.SpeakersOnly));

        Assert.Equal(requests, engines.Requests);
        Assert.Equal(calls, engines.Engine.CallCount);
        Assert.Equal(MeetingStatus.Transcribed, result.Status);
        Assert.Equal(first.Segments.Select(s => s.Text), result.Segments.Select(s => s.Text));
        Assert.Equal(first.Segments.Select(s => s.Start), result.Segments.Select(s => s.Start));
        Assert.Equal(first.Segments.Select(s => s.End), result.Segments.Select(s => s.End));
        Assert.Equal(["s1", "s1"], result.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["s1"], result.Speakers.Select(s => s.Id));
        Assert.Equal(first.EngineId, result.EngineId);
        Assert.Equal(DiarizationState.Applied, result.Diarization?.State);
        Assert.Equal(new Dictionary<string, int> { ["microphone"] = 1 }, result.Diarization?.SpeakersPerTrack);
        Assert.DoesNotContain(seen, p => p.Phase == MeetingProcessingPhase.Transcribing);
        Assert.Equal(1, seen[^1].OverallFraction, 1e-9);
        Assert.Equal(2, diarization.Releases);
    });

    [Fact]
    public Task SpeakersOnlyKeepsTheSpeakersOfATrackThatGivesNoTurns() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), CallSystem());
        var diarization = new TranscriberDiarization();
        IReadOnlyList<SpeakerTurn> systemTurns = [new SpeakerTurn(2.9, 9.1, 0)];
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns, ["system.caf"] = systemTurns };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);
        var first = await transcriber.TranscribeAsync(meeting.Id);
        Assert.Equal(["s1", "s2", "s3"], first.Speakers.Select(s => s.Id));
        var edited = MeetingEdits.RenameSpeaker("s3", "Олег", MeetingEdits.RenameSpeaker("s2", "Аня", first));
        store.Update(edited);

        // The diarizer finds no speech on the microphone this time.
        diarization.Diarizer.Turns = new() { ["mic.caf"] = [], ["system.caf"] = systemTurns };
        var oneTrack = await transcriber.TranscribeAsync(meeting.Id, new MeetingProcessingOptions(MeetingProcessingMode.SpeakersOnly));

        Assert.Equal(edited.Speakers, oneTrack.Speakers);
        Assert.Equal(edited.Segments, oneTrack.Segments);
        Assert.Equal(DiarizationState.Applied, oneTrack.Diarization?.State);
        Assert.Equal(new Dictionary<string, int> { ["microphone"] = 2, ["system"] = 1 }, oneTrack.Diarization?.SpeakersPerTrack);

        // Nothing on either track leaves every speaker as it was.
        diarization.Diarizer.Turns = [];
        var noTrack = await transcriber.TranscribeAsync(meeting.Id, new MeetingProcessingOptions(MeetingProcessingMode.SpeakersOnly));

        Assert.Equal(edited.Speakers, noTrack.Speakers);
        Assert.Equal(edited.Segments, noTrack.Segments);
        Assert.Equal(new Dictionary<string, int> { ["microphone"] = 2, ["system"] = 1 }, noTrack.Diarization?.SpeakersPerTrack);
        var transcript = File.ReadAllText(store.TranscriptPath(meeting.Id));
        Assert.Contains("Аня", transcript);
        Assert.Contains("Олег", transcript);
    });

    [Fact]
    public Task NumbersRemovedByMergesStayUsedAcrossReRuns() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = ThreeVoiceTurns };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);
        var options = new MeetingProcessingOptions(ExpectedSpeakers: 3);

        var first = await transcriber.TranscribeAsync(meeting.Id, options);
        Assert.Equal(["s1", "s2", "s3"], first.Speakers.Select(s => s.Id));
        Assert.Equal(3, first.HighestSpeakerNumber);
        store.Update(MeetingEdits.MergeSpeaker("s3", "s2", first));

        // The two halves of the second voice tie for «Спикер 2», so which one gets it is left open.
        var second = await transcriber.TranscribeAsync(meeting.Id, options);
        Assert.Equal(new HashSet<string> { "s1", "s2", "s4" }, second.Speakers.Select(s => s.Id).ToHashSet());
        store.Update(MeetingEdits.MergeSpeaker("s4", "s1", second));

        var third = await transcriber.TranscribeAsync(meeting.Id, options);
        Assert.Equal(new HashSet<string> { "s1", "s2", "s5" }, third.Speakers.Select(s => s.Id).ToHashSet());
        Assert.Equal(5, third.HighestSpeakerNumber);
        store.Flush();
        Assert.Equal(5, NewStore().Meeting(meeting.Id)?.HighestSpeakerNumber);
    });

    [Fact]
    public Task SpeakersOnlyIsRefusedWhenItCannotRun() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);
        var recorded = store.Meeting(meeting.Id)!;
        var speakersOnly = new MeetingProcessingOptions(MeetingProcessingMode.SpeakersOnly);

        // No segments yet.
        Assert.False(transcriber.CanProcess(recorded, MeetingProcessingMode.SpeakersOnly));
        await AssertThrows(MeetingErrors.SpeakersOnlyUnavailable(), () => transcriber.TranscribeAsync(meeting.Id, speakersOnly));
        Assert.Equal(MeetingStatus.Recorded, store.Meeting(meeting.Id)?.Status);

        var firstFormat = recorded with
        {
            Status = MeetingStatus.Transcribed,
            FormatVersion = 1,
            Segments = [new TranscriptSegment { Start = 1, End = 15, Speaker = Speaker.Me, Text = "Длинный общий кусок" }],
        };
        store.Update(firstFormat);
        Assert.False(transcriber.CanProcess(firstFormat, MeetingProcessingMode.SpeakersOnly));

        var current = firstFormat with { FormatVersion = 2 };
        store.Update(current);
        Assert.True(transcriber.CanProcess(current, MeetingProcessingMode.SpeakersOnly));
        diarization.ModelStatus = ModelStatus.Damaged("weights missing");
        Assert.False(transcriber.CanProcess(current, MeetingProcessingMode.SpeakersOnly));
        diarization.ModelStatus = ModelStatus.Ready(1);
        diarization.IsSupportedOnThisSystem = false;
        Assert.False(transcriber.CanProcess(current, MeetingProcessingMode.SpeakersOnly));
        Assert.False(new MeetingTranscriber(store, new TranscriberEngines(), mainThread).CanProcess(current, MeetingProcessingMode.SpeakersOnly));
    });

    [Fact]
    public Task SpeakersOnlyFailureRestoresTheRecordAndThrows() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), null);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);
        var previous = await transcriber.TranscribeAsync(meeting.Id);

        diarization.Diarizer.Error = new SlovoException("Embedding failed");
        var error = await Assert.ThrowsAsync<SlovoException>(() =>
            transcriber.TranscribeAsync(meeting.Id, new MeetingProcessingOptions(MeetingProcessingMode.SpeakersOnly)));

        Assert.Equal("Embedding failed", error.Message);
        Assert.Equal(previous, store.Meeting(meeting.Id));
    });

    [Fact]
    public Task SpeakerCountHintFollowsTheKindOfMeeting() => Run(async () =>
    {
        Assert.Equal(SpeakerCountHint.Automatic, MeetingTranscriber.Hint(MeetingTrack.Microphone, null, hasSystemTrack: true));
        Assert.Equal(new SpeakerCountHint(Exactly: 4), MeetingTranscriber.Hint(MeetingTrack.Microphone, 4, hasSystemTrack: false));
        Assert.Equal(new SpeakerCountHint(Maximum: 3), MeetingTranscriber.Hint(MeetingTrack.Microphone, 3, hasSystemTrack: true));
        Assert.Equal(new SpeakerCountHint(Maximum: 2), MeetingTranscriber.Hint(MeetingTrack.System, 3, hasSystemTrack: true));
        Assert.Equal(new SpeakerCountHint(Maximum: 1), MeetingTranscriber.Hint(MeetingTrack.System, 2, hasSystemTrack: true));
        Assert.Null(MeetingTranscriber.RequestedSpeakers(0));
        Assert.Null(MeetingTranscriber.RequestedSpeakers(9));
        Assert.Equal(5, MeetingTranscriber.RequestedSpeakers(5));

        var call = MakeRecordedMeeting(TwoVoices(), CallSystem());
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns, ["system.caf"] = [new SpeakerTurn(2.9, 9.1, 0)] };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization, expectedSpeakers: 3);

        await transcriber.TranscribeAsync(call.Id);
        Assert.Equal(Hints(new SpeakerCountHint(Maximum: 3), new SpeakerCountHint(Maximum: 2)), diarization.Diarizer.Hints);

        await transcriber.TranscribeAsync(call.Id, new MeetingProcessingOptions(ExpectedSpeakers: 2));
        Assert.Equal(Hints(new SpeakerCountHint(Maximum: 2), new SpeakerCountHint(Maximum: 1)), diarization.Diarizer.Hints);

        await transcriber.TranscribeAsync(call.Id, new MeetingProcessingOptions(ExpectedSpeakers: 0));
        Assert.Equal(Hints(SpeakerCountHint.Automatic, SpeakerCountHint.Automatic), diarization.Diarizer.Hints);

        var inPerson = MakeRecordedMeeting(TwoVoices(), null);
        var recorded = await MakeTranscriber(new TranscriberEngines(), diarization, expectedSpeakers: 4).TranscribeAsync(inPerson.Id);
        Assert.Equal(new SpeakerCountHint(Exactly: 4), diarization.Diarizer.Hints["mic.caf"]);
        Assert.Equal(4, recorded.Diarization?.RequestedSpeakers);
    });

    [Fact]
    public void OptionsFromSettingsFollowTheMeetingsSettings()
    {
        var settings = new AppSettings(SettingsStore.InMemory()) { SpeakerSeparation = true, ExpectedSpeakers = 4 };
        Assert.Equal(new MeetingProcessingOptions(MeetingProcessingMode.Full, true, 4), MeetingProcessingOptions.FromSettings(settings));
        settings.SpeakerSeparation = false;
        settings.ExpectedSpeakers = 0;
        Assert.Equal(new MeetingProcessingOptions(MeetingProcessingMode.Full, false, null), MeetingProcessingOptions.FromSettings(settings));
    }

    [Fact]
    public Task TrackWithTooLittleSpeechKeepsItsFallbackSpeaker() => Run(async () =>
    {
        var meeting = MakeRecordedMeeting(TwoVoices(), Join(Silence(3), Tone(3, 0.5f), Silence(10)));
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns, ["system.caf"] = [new SpeakerTurn(2.9, 6.1, 0)] };
        var transcriber = MakeTranscriber(new TranscriberEngines(), diarization);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(["mic.caf"], diarization.Diarizer.Hints.Keys);
        Assert.Equal(["s1", "s2", "others"], result.EffectiveSpeakers.Select(s => s.Id));
        Assert.Equal(new Dictionary<string, int> { ["microphone"] = 2 }, result.Diarization?.SpeakersPerTrack);
        Assert.Null(result.Segments.First(s => s.Speaker == Speaker.Others).SpeakerId);
    });

    [Fact]
    public Task AudioIsRemovedOnlyAfterTheTranscriptIsSaved() => Run(async () =>
    {
        var spy = new SpyMeetingStore(store);
        var settings = new AppSettings(SettingsStore.InMemory()) { KeepMeetingAudio = RetentionPeriod.AfterTranscription, SpeakerSeparation = true };
        var meeting = MakeRecordedMeeting(TwoVoices(), CallSystem()) with { HasScreenRecording = true };
        store.Update(meeting);
        File.WriteAllBytes(store.ScreenRecordingPath(meeting.Id), [0]);
        var diarization = new TranscriberDiarization();
        diarization.Diarizer.Turns = new() { ["mic.caf"] = TwoVoiceTurns, ["system.caf"] = [new SpeakerTurn(2.9, 9.1, 0)] };
        var engines = new TranscriberEngines();
        engines.Engine.RequiredFile = store.MicrophoneAudioPath(meeting.Id);
        var transcriber = new MeetingTranscriber(spy, engines, mainThread, settings, diarization);

        var result = await transcriber.TranscribeAsync(meeting.Id);

        Assert.Equal(3, engines.Engine.CallCount);
        Assert.Equal(0, engines.Engine.CallsWithoutFile); // audio was not removed before every clip was transcribed
        Assert.False(result.HasAudio);
        var transcribed = spy.Events.LastIndexOf("update transcribed");
        var deletion = spy.Events.FindIndex(e => e.StartsWith("removeAudio", StringComparison.Ordinal));
        Assert.True(transcribed >= 0 && deletion > transcribed, string.Join(", ", spy.Events));
        Assert.Contains("flush", spy.Events.GetRange(transcribed, deletion - transcribed));
        Assert.Equal("removeAudio transcript=true saved=transcribed", spy.Events[deletion]);
        Assert.DoesNotContain("deleteAudio", spy.Events); // the screen recording is not audio
        Assert.True(File.Exists(store.TranscriptPath(meeting.Id)));
        Assert.False(File.Exists(store.MicrophoneAudioPath(meeting.Id)));
        Assert.False(File.Exists(store.SystemAudioPath(meeting.Id)));
        Assert.True(File.Exists(store.ScreenRecordingPath(meeting.Id)));
        Assert.True(result.HasScreenRecording);
    });

    // MARK: - Helpers

    static readonly IReadOnlyList<SpeakerTurn> TwoVoiceTurns = [new(0.9, 7.1, 0), new(8.9, 15.1, 1)];

    static readonly IReadOnlyList<SpeakerTurn> ThreeVoiceTurns = [new(0.9, 7.1, 0), new(8.9, 12, 1), new(12, 15.1, 2)];

    static Dictionary<string, SpeakerCountHint> Hints(SpeakerCountHint microphone, SpeakerCountHint system) =>
        new() { ["mic.caf"] = microphone, ["system.caf"] = system };

    MeetingTranscriber MakeTranscriber(TranscriberEngines engines, TranscriberDiarization diarization, bool separate = true,
                                       int expectedSpeakers = 0, TimeProvider? time = null)
    {
        var settings = new AppSettings(SettingsStore.InMemory()) { SpeakerSeparation = separate, ExpectedSpeakers = expectedSpeakers };
        return new MeetingTranscriber(store, engines, mainThread, settings, diarization, time: time);
    }

    MeetingRecord MakeRecordedMeeting(float[] microphone, float[]? system)
    {
        var meeting = store.Create("Standup");
        Write(microphone, store.MicrophoneAudioPath(meeting.Id));
        if (system is not null)
        {
            Write(system, store.SystemAudioPath(meeting.Id));
        }
        meeting = meeting with
        {
            Status = MeetingStatus.Recorded,
            HasSystemAudio = system is not null,
            Duration = (double)microphone.Length / 16_000,
        };
        store.Update(meeting);
        return meeting;
    }

    /// <summary>Every distinct progress value of one meeting, in order.</summary>
    static List<TranscriptionProgress> Observe(MeetingTranscriber transcriber, Guid meetingId)
    {
        var seen = new List<TranscriptionProgress>();
        transcriber.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MeetingTranscriber.Progress)
                && transcriber.Progress.TryGetValue(meetingId, out var value)
                && (seen.Count == 0 || !seen[^1].Equals(value)))
            {
                seen.Add(value);
            }
        };
        return seen;
    }

    static List<string> LineTexts(MeetingRecord meeting) =>
        TranscriptMerger.Lines(meeting).Select(line => TranscriptMerger.Text(line, meeting, "ru")).ToList();

    static List<string> Labels(MeetingRecord meeting) =>
        TranscriptMerger.Lines(meeting).Select(line => TranscriptMerger.Label(line, meeting, "ru")).ToList();

    static string Kind(MeetingProcessingPhase phase) => phase.Kind switch
    {
        MeetingProcessingPhaseKind.Preparing => "preparing",
        MeetingProcessingPhaseKind.LoadingSpeakerModel => "loading",
        MeetingProcessingPhaseKind.SeparatingSpeakers => "separating-" + EnumText.Raw(phase.Track),
        _ => "transcribing",
    };

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }

    static async Task AssertThrows(SlovoException expected, Func<Task> body)
    {
        var error = await Assert.ThrowsAsync<SlovoException>(body);
        Assert.Equal(expected.Message, error.Message);
    }
}
