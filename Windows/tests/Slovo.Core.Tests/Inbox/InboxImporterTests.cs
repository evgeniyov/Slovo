using System.Globalization;
using System.Text.Json.Nodes;
using Slovo.Core.Audio;
using Slovo.Core.Inbox;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Core.Tests.Dictation;
using Slovo.Core.Tests.Meetings;

namespace Slovo.Core.Tests.Inbox;

/// <summary>
/// The inbox importer against a temporary data folder, on a test UI thread: a clip that
/// arrives with the phone's sidecar and one that arrives alone, the moments marked on the
/// phone, a sidecar whose audio never comes, imports cut off by a crash, refused files, hidden
/// and half-copied files, decoding, and the poll and folder watch that drive the sweeps (port
/// of InboxImporterTests.swift and the claim parts of InboxPhoneClipsTests.swift).
/// </summary>
public sealed class InboxImporterTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    readonly TemporaryMeetingsFolder folder = new();
    readonly TestMainThread mainThread = new();
    readonly AppSettings settings = new(SettingsStore.InMemory()) { InboxEnabled = true };
    readonly List<(Guid Id, MeetingProcessingOptions Options)> handedOver = [];
    MeetingStore store;

    public InboxImporterTests()
    {
        store = NewStore();
    }

    public void Dispose() => folder.Dispose();

    MeetingStore NewStore() => new(folder.Paths, mainThread) { TranscriptLanguage = () => AppLanguage.Ru };

    Task Run(Func<Task> body) => mainThread.RunAsync(body);

    string InboxFolder => folder.Paths.InboxFolder;

    // MARK: - Clips

    [Fact]
    public Task AClipWithoutASidecarIsNamedAndSortedByTheSameInstant() => Run(async () =>
    {
        var dropped = Now.AddSeconds(-600);
        WriteClip("voice-message.caf", 2, dropped);

        await MakeImporter().SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        // The store normalizes whitespace, and a medium date can hold a narrow no-break space.
        Assert.Equal(TranscriptMerger.NormalizedText(MeetingTitles.DefaultTitle(dropped)), meeting.Title);
        // The file's own date names the meeting, so it is also where the meeting sits in the list.
        Assert.Equal(dropped, meeting.CreatedAt);
        // The file's date can be older than its arrival, so retention counts from the import.
        Assert.Equal(Now, meeting.AudioStoredAt);
        Assert.Null(meeting.Source);
        Assert.Empty(meeting.Highlights);
        Assert.Equal(2, meeting.Duration, 0.05);
        Assert.True(meeting.HasAudio);
        Assert.Equal(MeetingStatus.Transcribed, meeting.Status);
        Assert.Equal(["quiet words"], meeting.Segments.Select(s => s.Text));
        Assert.False(File.Exists(Inbox("voice-message.caf")));
        Assert.True(File.Exists(Imported("voice-message.caf")));
        // Audio alone is a note: one voice, no separation.
        Assert.Equal([(meeting.Id, new MeetingProcessingOptions(MeetingProcessingMode.Full, SeparateSpeakers: false))], handedOver);
        // The track is the same 16 kHz CAF a recording is.
        Assert.True(CafFile.IsCaf(store.MicrophoneAudioPath(meeting.Id)));
        Assert.Equal(2, CafFile.Duration(store.MicrophoneAudioPath(meeting.Id)), 0.01);
    });

    [Fact]
    public Task TheSidecarGivesTheMeetingItsTimeTitleSourceAndMoments() => Run(async () =>
    {
        var recordedAt = new DateTimeOffset(2026, 9, 16, 12, 54, 58, TimeSpan.FromHours(6));
        settings.SpeakerSeparation = true;
        settings.ExpectedSpeakers = 3;
        WriteClip("slovo-20260916-125458.caf", 6, Now.AddSeconds(-60));
        WriteSidecar("slovo-20260916-125458.json", """
            {"version": 1, "kind": "meeting", "recordedAt": "2026-09-16T12:54:58+06:00", "durationMs": 6000,
             "device": "Demo Phone", "title": "Созвон с Аней",
             "highlights": [{"atMs": 1500, "note": "дедлайн"}, {"atMs": 4200, "note": null},
                            {"atMs": 99000000, "note": null}, {"note": "no time"}]}
            """);

        await MakeImporter().SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        Assert.Equal("Созвон с Аней", meeting.Title);
        Assert.Equal(recordedAt, meeting.CreatedAt);
        Assert.Equal(new MeetingSource { Origin = MeetingOrigin.Phone, Device = "Demo Phone" }, meeting.Source);
        Assert.Equal([1.5, 4.2, meeting.Duration], meeting.Highlights.Select(h => h.Time));
        Assert.Equal(["дедлайн", null, null], meeting.Highlights.Select(h => h.Note));
        Assert.All(meeting.Highlights, h => Assert.Equal(HighlightSource.Recording, h.Source));
        // A moment was made at the moment it names, so it sorts with the marks made here.
        Assert.Equal(recordedAt.AddSeconds(1.5), meeting.Highlights[0].CreatedAt);
        Assert.True(File.Exists(Imported("slovo-20260916-125458.json")));
        Assert.True(File.Exists(Imported("slovo-20260916-125458.caf")));
        // A clip the phone calls a meeting is processed as the Meetings settings say.
        Assert.Equal([(meeting.Id, new MeetingProcessingOptions(MeetingProcessingMode.Full, true, 3))], handedOver);

        // What the phone said is saved with the meeting.
        store.Flush();
        var reloaded = NewStore().Meeting(meeting.Id)!;
        Assert.Equal(meeting.Source, reloaded.Source);
        Assert.Equal(meeting.Highlights.Select(h => h.Note), reloaded.Highlights.Select(h => h.Note));
    });

    [Fact]
    public Task AClipRecordedDaysAgoOnThePhoneKeepsItsDateButItsAudioIsNew() => Run(async () =>
    {
        var recordedAt = Now.AddDays(-8);
        WriteClip("slovo-20260908-090000.caf", 2, Now.AddSeconds(-60));
        var stamp = recordedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        WriteSidecar("slovo-20260908-090000.json", $$"""{"version": 1, "kind": "meeting", "recordedAt": "{{stamp}}"}""");

        await MakeImporter().SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        // The meeting is shown and sorted by when the phone recorded it; its audio is a minute
        // old here, and keeping it starts now.
        Assert.Equal(recordedAt, meeting.CreatedAt);
        Assert.Equal(Now, meeting.AudioStoredAt);
    });

    [Fact]
    public Task AnImportedMeetingReadsBackExactlyAsItIsHeld() => Run(async () =>
    {
        var now = Now.AddTicks(1_234_567);
        WriteClip("odd-times.caf", 2, now.AddSeconds(-60).AddTicks(7_654_321));
        WriteSidecar("odd-times.json", """
            {"version": 1, "kind": "note", "recordedAt": "2026-09-16T12:54:58.123456+06:00", "highlights": [{"atMs": 1234.567}]}
            """, now.AddSeconds(-60));

        await MakeImporter(time: new FixedTime(now)).SweepAsync(now);

        var meeting = Assert.Single(store.Meetings);
        store.Flush();
        Assert.Equal(meeting, NewStore().Meeting(meeting.Id));
    });

    [Fact]
    public Task AMalformedSidecarCostsTheMetadataAndNotTheClip() => Run(async () =>
    {
        var dropped = Now.AddSeconds(-600);
        WriteClip("slovo-20260916-131002.caf", 2, dropped);
        File.WriteAllText(Inbox("slovo-20260916-131002.json"), "{ \"kind\": \"meeting\", this is not JSON");

        await MakeImporter().SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        Assert.Equal(TranscriptMerger.NormalizedText(MeetingTitles.DefaultTitle(dropped)), meeting.Title);
        Assert.Null(meeting.Source);
        Assert.Empty(meeting.Highlights);
        // The unreadable sidecar leaves with its clip; left behind it would later look like
        // metadata waiting for audio.
        Assert.True(File.Exists(Imported("slovo-20260916-131002.json")));
    });

    [Fact]
    public Task ASidecarWhoseAudioNeverArrivesIsGivenUpOn() => Run(async () =>
    {
        WriteSidecar("slovo-20260916-140000.json", """{"version": 1, "kind": "note"}""", Now.AddSeconds(-InboxImporter.StrandedSidecarSeconds - 60));
        WriteSidecar("slovo-20260916-141500.json", """{"version": 1, "kind": "note"}""", Now);
        var importer = MakeImporter();

        await importer.SweepAsync(Now);

        Assert.Empty(store.Meetings);
        Assert.True(File.Exists(Imported("slovo-20260916-140000.json")));
        // The fresh one is still waiting for a clip that may be seconds away.
        Assert.True(File.Exists(Inbox("slovo-20260916-141500.json")));

        await importer.SweepAsync(Now);
        Assert.Empty(store.Meetings);
    });

    [Fact]
    public Task AClipTooShortToBeSpeechIsPutAwayWithoutAMeeting() => Run(async () =>
    {
        WriteClip("tap.caf", 0.2, Now.AddSeconds(-60));
        WriteSidecar("tap.json", """{"version": 1, "kind": "note"}""", Now.AddSeconds(-60));

        await MakeImporter().SweepAsync(Now);

        Assert.Empty(store.Meetings);
        Assert.True(File.Exists(Imported("tap.caf")));
        Assert.True(File.Exists(Imported("tap.json")));
    });

    [Fact]
    public Task OnlyAudioThatHasSettledIsTaken() => Run(async () =>
    {
        WriteClip("fresh.caf", 2, Now.AddSeconds(-1));
        WriteClip("older.caf", 2, Now.AddSeconds(-30));
        WriteClip("oldest.caf", 2, Now.AddSeconds(-90));
        File.WriteAllText(Inbox("notes.txt"), "not audio");
        File.SetLastWriteTimeUtc(Inbox("notes.txt"), Now.AddSeconds(-90).UtcDateTime);
        var importer = MakeImporter();

        Assert.Equal([Inbox("oldest.caf"), Inbox("older.caf")], importer.ReadyFiles(Now));
        Assert.Equal([Inbox("oldest.caf"), Inbox("older.caf"), Inbox("fresh.caf")], importer.ReadyFiles(Now.AddSeconds(InboxImporter.SettleSeconds)));
        await Task.CompletedTask;
    });

    // MARK: - Arriving files

    /// <summary>
    /// A copy that gives the file its source's old date while it is still growing: the file is
    /// taken only once its size and date have stayed the same for the settle time.
    /// </summary>
    [Fact]
    public Task AFileThatKeepsGrowingUnderAnOldDateWaitsUntilItsSizeSettles() => Run(async () =>
    {
        var importer = MakeImporter();
        WriteClip("copying.caf", 1, Now.AddSeconds(-1));
        await importer.SweepAsync(Now);
        Assert.Empty(store.Meetings);

        // More of the file arrives; the copy has already set the source's date.
        WriteClip("copying.caf", 3, Now.AddDays(-2));
        await importer.SweepAsync(Now.AddSeconds(5));
        Assert.Empty(store.Meetings);
        await importer.SweepAsync(Now.AddSeconds(7));
        Assert.Empty(store.Meetings);

        await importer.SweepAsync(Now.AddSeconds(8.5));
        var meeting = Assert.Single(store.Meetings);
        Assert.Equal(3, meeting.Duration, 0.05);
    });

    [Fact]
    public Task AFileAnotherProgramStillHoldsOpenIsLeftForALaterSweep() => Run(async () =>
    {
        WriteClip("held.caf", 2, Now.AddSeconds(-60));
        var importer = MakeImporter();
        using (new FileStream(Inbox("held.caf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await importer.SweepAsync(Now);
            Assert.Empty(store.Meetings);
            Assert.True(File.Exists(Inbox("held.caf")));
        }

        await importer.SweepAsync(Now.AddSeconds(1));
        Assert.Single(store.Meetings);
    });

    /// <summary>
    /// A transport writes under a hidden name and renames the file once it arrived whole, so a
    /// copy that stalls is never imported half copied; hidden files are never taken.
    /// </summary>
    [Fact]
    public Task HiddenAndPartialFilesAreNeverTaken() => Run(async () =>
    {
        WriteClip(".slovo-20260916-125458.m4a.partial", 2, Now.AddSeconds(-60));
        WriteClip(".partial-clip.caf", 2, Now.AddSeconds(-60));
        WriteClip(".DS_Store.caf", 2, Now.AddSeconds(-60));
        var importer = MakeImporter();

        await importer.SweepAsync(Now);
        Assert.Empty(store.Meetings);
        Assert.Empty(importer.ReadyFiles(Now));

        File.Move(Inbox(".partial-clip.caf"), Inbox("clip.caf"));
        await importer.SweepAsync(Now);
        var meeting = Assert.Single(store.Meetings);
        Assert.True(File.Exists(Imported("clip.caf")));
        Assert.True(File.Exists(Inbox(".slovo-20260916-125458.m4a.partial")));
        Assert.True(File.Exists(Inbox(".DS_Store.caf")));
        Assert.Equal(2, meeting.Duration, 0.05);
    });

    [Fact]
    public Task AFileMarkedHiddenIsNotTaken() => Run(async () =>
    {
        WriteClip("marked.caf", 2, Now.AddSeconds(-60));
        File.SetAttributes(Inbox("marked.caf"), File.GetAttributes(Inbox("marked.caf")) | FileAttributes.Hidden);
        var hidden = (File.GetAttributes(Inbox("marked.caf")) & FileAttributes.Hidden) != 0;
        var importer = MakeImporter();

        await importer.SweepAsync(Now);

        // Where the file system cannot mark a file hidden, the leading dot is the only rule.
        Assert.Equal(hidden ? 0 : 1, store.Meetings.Count);
    });

    [Fact]
    public Task AnImportLeavesNothingHiddenBehind() => Run(async () =>
    {
        WriteClip("voice-message.caf", 1, Now.AddSeconds(-60));
        await MakeImporter().SweepAsync(Now);
        Assert.Equal([InboxImporter.ImportedFolderName], Directory.EnumerateFileSystemEntries(InboxFolder).Select(Path.GetFileName));
    });

    [Fact]
    public Task NothingIsTakenWhileTheInboxIsOff() => Run(async () =>
    {
        settings.InboxEnabled = false;
        WriteClip("voice-message.caf", 2, Now.AddSeconds(-60));

        await MakeImporter().SweepAsync(Now);

        Assert.Empty(store.Meetings);
        Assert.True(File.Exists(Inbox("voice-message.caf")));
    });

    // MARK: - Decoding

    [Fact]
    public Task CompressedAudioGoesThroughThePlatformDecoder() => Run(async () =>
    {
        File.WriteAllText(Inbox("voice-message.m4a"), "an m4a the fake decoder reads");
        File.SetLastWriteTimeUtc(Inbox("voice-message.m4a"), Now.AddSeconds(-60).UtcDateTime);
        var decoder = new FakeDecoder();

        await MakeImporter(decoder).SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        Assert.Equal(2.5, meeting.Duration, 0.01);
        Assert.Equal([Inbox("voice-message.m4a")], decoder.Decoded);
        Assert.True(File.Exists(Imported("voice-message.m4a")));
    });

    [Fact]
    public Task WithoutADecoderCompressedAudioIsRefusedAndLeftInPlace() => Run(async () =>
    {
        File.WriteAllText(Inbox("voice-message.m4a"), "an m4a nothing here can read");
        File.SetLastWriteTimeUtc(Inbox("voice-message.m4a"), Now.AddSeconds(-60).UtcDateTime);
        var importer = MakeImporter();

        await importer.SweepAsync(Now);

        Assert.Empty(store.Meetings);
        Assert.True(File.Exists(Inbox("voice-message.m4a")));
        Assert.Empty(importer.ReadyFiles(Now));
    });

    [Fact]
    public Task AWavAtAnotherRateIsReadHereAndResampled() => Run(async () =>
    {
        // One second of tone at 48 kHz, as a recorder that keeps its own rate writes it.
        var samples = TranscriberAudio.Tone(1, 0.2f).SelectMany(s => new[] { s, s, s }).ToArray();
        WavFile.Write(new RecordedAudio(samples, 48_000), Inbox("studio.wav"));
        File.SetLastWriteTimeUtc(Inbox("studio.wav"), Now.AddSeconds(-60).UtcDateTime);
        var decoder = new FakeDecoder();

        await MakeImporter(decoder).SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        Assert.Equal(1, meeting.Duration, 0.01);
        Assert.Empty(decoder.Decoded);
    });

    // MARK: - Failures and interrupted imports

    [Fact]
    public Task AClipThatFailedIsTriedAgainOnceTheFileChanges() => Run(async () =>
    {
        const string name = "slovo-20260916-150000.caf";
        File.WriteAllText(Inbox(name), "not audio at all");
        File.SetLastWriteTimeUtc(Inbox(name), Now.AddSeconds(-600).UtcDateTime);
        var importer = MakeImporter();

        await importer.SweepAsync(Now);
        Assert.Empty(store.Meetings);
        Assert.True(File.Exists(Inbox(name))); // a broken clip stays where it was
        Assert.Empty(importer.ReadyFiles(Now)); // and is not tried again while it is unchanged

        // The copy completes, or the user drops a good file under the same name: a new size and
        // date make it a new file, taken without a relaunch.
        File.Delete(Inbox(name));
        WriteClip(name, 2, Now.AddSeconds(-60));
        await importer.SweepAsync(Now);
        Assert.Single(store.Meetings);
        Assert.True(File.Exists(Imported(name)));
    });

    [Fact]
    public Task ARefusedFileWithTheSameSizeButANewDateIsTriedAgain() => Run(async () =>
    {
        const string name = "broken.caf";
        File.WriteAllText(Inbox(name), "not audio at all");
        File.SetLastWriteTimeUtc(Inbox(name), Now.AddSeconds(-600).UtcDateTime);
        var importer = MakeImporter();
        await importer.SweepAsync(Now);
        Assert.Empty(importer.ReadyFiles(Now));

        File.SetLastWriteTimeUtc(Inbox(name), Now.AddSeconds(-300).UtcDateTime);
        Assert.Equal([Inbox(name)], importer.ReadyFiles(Now));
    });

    /// <summary>
    /// The app quit or crashed after the meeting was created and before the clip reached
    /// <c>Imported/</c>: the next sweep finishes that meeting rather than making a second one.
    /// </summary>
    [Fact]
    public Task AnImportCutOffAfterItsMeetingWasCreatedFinishesThatMeeting() => Run(async () =>
    {
        var created = store.Create("Созвон с Аней");
        const string name = "slovo-20260916-125458.caf";
        var claimed = InboxImporter.ClaimName(created.Id, name);
        WriteClip(claimed, 3, Now.AddSeconds(-60));
        WriteSidecar("slovo-20260916-125458.json", """
            {"version": 1, "kind": "meeting", "device": "Demo Phone", "highlights": [{"atMs": 1000, "note": "дедлайн"}]}
            """, Now.AddSeconds(-InboxImporter.StrandedSidecarSeconds - 60));

        await MakeImporter().SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings); // the clip became the meeting it was already becoming
        Assert.Equal(created.Id, meeting.Id);
        Assert.True(meeting.HasAudio);
        Assert.Equal(3, meeting.Duration, 0.05);
        Assert.Equal(MeetingStatus.Transcribed, meeting.Status);
        Assert.Equal(new MeetingSource { Origin = MeetingOrigin.Phone, Device = "Demo Phone" }, meeting.Source);
        Assert.Equal(["дедлайн"], meeting.Highlights.Select(h => h.Note));
        Assert.True(File.Exists(Imported(name))); // the clip moved on under its own name
        Assert.True(File.Exists(Imported("slovo-20260916-125458.json"))); // the sidecar went with it
        Assert.False(File.Exists(Inbox(claimed)));
    });

    /// <summary>
    /// A crash in earnest: the clip is claimed and its meeting saved, the app goes away before
    /// the clip moves on, and a new launch — a new store read from disk and a new importer —
    /// finishes the same meeting.
    /// </summary>
    [Fact]
    public Task AClaimSurvivesARelaunchAndFinishesTheSameMeeting() => Run(async () =>
    {
        WriteClip("voice-message.caf", 2, Now.AddSeconds(-60));
        // Imported/ cannot be made while a file holds its name, so the clip stays claimed.
        File.WriteAllText(Path.Combine(InboxFolder, InboxImporter.ImportedFolderName), "in the way");
        var first = MakeImporter();
        await first.SweepAsync(Now);

        var created = Assert.Single(store.Meetings);
        Assert.Equal(MeetingStatus.Recorded, created.Status);
        var claim = InboxImporter.ClaimName(created.Id, "voice-message.caf");
        Assert.True(File.Exists(Inbox(claim)));
        Assert.Empty(handedOver);
        // The same run does not try again every few seconds.
        Assert.Empty(first.ClaimedClips());
        await first.SweepAsync(Now.AddSeconds(5));
        Assert.Single(store.Meetings);
        store.Flush();

        // Relaunch.
        File.Delete(Path.Combine(InboxFolder, InboxImporter.ImportedFolderName));
        store = NewStore();
        await MakeImporter().SweepAsync(Now.AddSeconds(60));

        var meeting = Assert.Single(store.Meetings);
        Assert.Equal(created.Id, meeting.Id);
        Assert.Equal(MeetingStatus.Transcribed, meeting.Status);
        Assert.Equal(2, meeting.Duration, 0.05);
        Assert.True(File.Exists(Imported("voice-message.caf")));
        Assert.False(File.Exists(Inbox(claim)));
    });

    [Fact]
    public Task AClaimWhoseMeetingWasNeverSavedIsImportedAfresh() => Run(async () =>
    {
        const string name = "voice-message.caf";
        var orphan = Guid.Parse("0BADF00D-0000-4000-8000-000000000001");
        WriteClip(InboxImporter.ClaimName(orphan, name), 2, Now.AddSeconds(-60));

        await MakeImporter().SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        Assert.NotEqual(orphan, meeting.Id);
        Assert.True(File.Exists(Imported(name)));
    });

    [Fact]
    public Task AClaimedClipWhoseMeetingWentFurtherIsOnlyPutAway() => Run(async () =>
    {
        var created = store.Create("Уже расшифрована") with { Status = MeetingStatus.Transcribed, Duration = 42 };
        store.Update(created);
        const string name = "voice-message.caf";
        WriteClip(InboxImporter.ClaimName(created.Id, name), 2, Now.AddSeconds(-60));

        await MakeImporter().SweepAsync(Now);

        var meeting = Assert.Single(store.Meetings);
        Assert.Equal(42, meeting.Duration); // a meeting taken further is not refilled
        Assert.True(File.Exists(Imported(name)));
        Assert.Empty(handedOver);
    });

    [Fact]
    public void ClaimNamesRoundTripAndOthersAreNotClaims()
    {
        var id = Guid.Parse("3F2A9C1E-5B7D-4C1A-9E0F-2B6D8A4C7E11");
        var name = InboxImporter.ClaimName(id, "Запись 1.m4a");
        Assert.Equal(".importing-3F2A9C1E-5B7D-4C1A-9E0F-2B6D8A4C7E11-Запись 1.m4a", name);
        Assert.StartsWith(".", name); // a claimed clip is hidden from the sweep
        Assert.Equal((id, "Запись 1.m4a"), InboxImporter.ParseClaimName(name));
        foreach (var other in new[]
                 {
                     "Запись 1.m4a", ".importing-not-a-uuid-clip.m4a", $".importing-{id:D}-notes.txt", $".importing-{id:D}",
                     ".partial-clip.m4a",
                 })
        {
            Assert.Null(InboxImporter.ParseClaimName(other));
        }
    }

    [Fact]
    public void ProcessingOptionsFollowTheKindOfClip()
    {
        settings.SpeakerSeparation = true;
        settings.ExpectedSpeakers = 4;
        Assert.Equal(new MeetingProcessingOptions(MeetingProcessingMode.Full, true, 4),
                     InboxImporter.ProcessingOptions(InboxSidecar.SidecarKind.Meeting, settings));
        Assert.Equal(new MeetingProcessingOptions(MeetingProcessingMode.Full, SeparateSpeakers: false),
                     InboxImporter.ProcessingOptions(InboxSidecar.SidecarKind.Note, settings));

        settings.SpeakerSeparation = false;
        Assert.False(InboxImporter.ProcessingOptions(InboxSidecar.SidecarKind.Meeting, settings).SeparateSpeakers);
        Assert.False(InboxImporter.ProcessingOptions(InboxSidecar.SidecarKind.Note, settings).SeparateSpeakers);
    }

    [Fact]
    public Task AFailedTranscriptionDoesNotStopTheSweep() => Run(async () =>
    {
        WriteClip("a.caf", 2, Now.AddSeconds(-90));
        WriteClip("b.caf", 2, Now.AddSeconds(-60));
        var importer = MakeImporter(transcribe: (_, _) => throw new SlovoException("No engine"));

        await importer.SweepAsync(Now);

        Assert.Equal(2, store.Meetings.Count);
        Assert.All(store.Meetings, m => Assert.Equal(MeetingStatus.Recorded, m.Status));
        Assert.True(File.Exists(Imported("a.caf")));
        Assert.True(File.Exists(Imported("b.caf")));
    });

    // MARK: - Manual import

    [Fact]
    public Task APickedFileIsNamedAfterItselfAndLeftWhereItWas() => Run(async () =>
    {
        var modified = Now.AddHours(-1);
        var path = WriteManualFile("Обсуждение БФЛ.caf", 2, modified);
        settings.InboxEnabled = false; // a manual pick does not depend on the inbox

        var meeting = await MakeImporter().ImportPickedFileAsync(path);

        Assert.Single(store.Meetings);
        Assert.Equal("Обсуждение БФЛ", meeting.Title);
        Assert.Equal(modified, meeting.CreatedAt);
        Assert.Equal(2, meeting.Duration, 0.05);
        Assert.True(meeting.HasAudio);
        Assert.Equal(MeetingStatus.Recorded, meeting.Status);
        Assert.Empty(handedOver); // the caller starts transcription
        // A manual pick is the user's own file, sitting wherever they keep it: never moved or deleted.
        Assert.True(File.Exists(path));
    });

    [Fact]
    public Task APickedFileTooShortToBeSpeechIsRefused() => Run(async () =>
    {
        var path = WriteManualFile("tap.caf", 0.1, Now);

        var error = await Assert.ThrowsAsync<SlovoException>(() => MakeImporter().ImportPickedFileAsync(path));

        Assert.Equal(MeetingErrors.FileTooShort().Message, error.Message);
        Assert.Empty(store.Meetings);
    });

    [Fact]
    public void APickedFileWithoutANameGetsTheDateTitle()
    {
        Assert.Equal("Планёрка", InboxImporter.Title(Path.Combine("folder", "  Планёрка .m4a"), Now));
        Assert.Equal(MeetingTitles.DefaultTitle(Now), InboxImporter.Title(Path.Combine("folder", " .m4a"), Now));
    }

    // MARK: - Watching

    [Fact]
    public Task StartSweepsAtOnceThenPollsAfterEachSweepUntilStopped() => Run(async () =>
    {
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow();
        WriteClip("first.caf", 2, now.AddSeconds(-60));
        var importer = MakeImporter(time: clock);

        importer.Start();
        Assert.True(importer.IsRunning);
        await WaitUntil(() => store.Meetings.Count == 1 && store.Meetings[0].Status == MeetingStatus.Transcribed);

        WriteClip("second.caf", 2, now.AddSeconds(-60));
        await Task.Delay(100);
        Assert.Single(store.Meetings);
        clock.Advance(TimeSpan.FromSeconds(InboxImporter.PollSeconds));
        await WaitUntil(() => store.Meetings.Count == 2 && handedOver.Count == 2);
        Assert.Equal(2, store.Meetings.Count);

        importer.Stop();
        Assert.False(importer.IsRunning);
        WriteClip("third.caf", 2, now.AddSeconds(-60));
        clock.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(200);
        Assert.Equal(2, store.Meetings.Count);
        Assert.True(File.Exists(Inbox("third.caf")));
    });

    [Fact]
    public Task TurningTheInboxOnSweepsAtOnce() => Run(async () =>
    {
        settings.InboxEnabled = false;
        var clock = new ManualTimeProvider();
        WriteClip("voice-message.caf", 2, clock.GetUtcNow().AddSeconds(-60));
        using var importer = MakeImporter(time: clock);
        importer.Start();
        await Task.Delay(100);
        Assert.Empty(store.Meetings);

        settings.InboxEnabled = true;

        await WaitUntil(() => store.Meetings.Count == 1);
        Assert.Single(store.Meetings);
    });

    [Fact]
    public Task SweepsNeverOverlap() => Run(async () =>
    {
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow();
        var gate = new TaskCompletionSource();
        var active = 0;
        var mostActive = 0;
        WriteClip("a.caf", 2, now.AddSeconds(-90));
        using var importer = MakeImporter(time: clock, transcribe: async (_, _) =>
        {
            active++;
            mostActive = Math.Max(mostActive, active);
            await gate.Task;
            active--;
        });

        importer.Start();
        await WaitUntil(() => active == 1);
        WriteClip("b.caf", 2, now.AddSeconds(-60));
        importer.RequestSweep();
        clock.Advance(TimeSpan.FromSeconds(InboxImporter.PollSeconds));
        await Task.Delay(100);
        Assert.Single(store.Meetings);

        gate.SetResult();
        await WaitUntil(() => store.Meetings.Count == 2 && active == 0);
        Assert.Equal(2, store.Meetings.Count);
        Assert.Equal(1, mostActive);
    });

    [Fact]
    public async Task TheFolderWatcherReportsANewFile()
    {
        var changes = 0;
        using var watcher = new InboxFolderWatcher(InboxFolder, () => Interlocked.Increment(ref changes));
        Assert.True(watcher.IsWatching);

        await File.WriteAllTextAsync(Inbox("clip.m4a"), "arriving", TestContext.Current.CancellationToken);

        for (var attempt = 0; attempt < 100 && Volatile.Read(ref changes) == 0; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.True(Volatile.Read(ref changes) > 0);
    }

    [Fact]
    public void AFolderThatCannotBeWatchedLeavesThePollToIt()
    {
        var blocked = Path.Combine(folder.Base, "not-a-folder");
        File.WriteAllText(blocked, "a file where the inbox should be");

        using var watcher = new InboxFolderWatcher(blocked, () => { });

        Assert.False(watcher.IsWatching);
    }

    // MARK: - Helpers

    InboxImporter MakeImporter(IAudioDecoder? decoder = null, TimeProvider? time = null,
                               Func<Guid, MeetingProcessingOptions, Task>? transcribe = null)
    {
        var transcriber = new MeetingTranscriber(store, new TranscriberEngines(), mainThread);
        return new InboxImporter(folder.Paths, store, settings, mainThread, (id, options) =>
        {
            handedOver.Add((id, options));
            return transcribe is null ? transcriber.TranscribeAsync(id, options) : transcribe(id, options);
        }, decoder, time ?? new FixedTime(Now));
    }

    string Inbox(string name) => Path.Combine(InboxFolder, name);

    string Imported(string name) => Path.Combine(InboxFolder, InboxImporter.ImportedFolderName, name);

    /// <summary>A clip in the inbox: a quiet tone written as a meeting track, with the given date.</summary>
    void WriteClip(string name, double seconds, DateTimeOffset modified) => WriteTone(Inbox(name), seconds, modified);

    /// <summary>A file the user picked from anywhere on disk, outside the inbox.</summary>
    string WriteManualFile(string name, double seconds, DateTimeOffset modified)
    {
        var path = Path.Combine(folder.Base, name);
        WriteTone(path, seconds, modified);
        return path;
    }

    static void WriteTone(string path, double seconds, DateTimeOffset modified)
    {
        InboxImporter.WriteTrack(TranscriberAudio.Tone(seconds, 0.2f), path);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
    }

    void WriteSidecar(string name, string json, DateTimeOffset? modified = null)
    {
        Assert.NotNull(JsonNode.Parse(json));
        File.WriteAllText(Inbox(name), json);
        File.SetLastWriteTimeUtc(Inbox(name), (modified ?? Now).UtcDateTime);
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>A clock that stands still; its timers never fire.</summary>
    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Idle();

        sealed class Idle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Stands in for Media Foundation: "decodes" m4a and mp3 files as 2.5 s of tone.</summary>
    sealed class FakeDecoder : IAudioDecoder
    {
        public List<string> Decoded { get; } = [];

        public bool CanDecode(string path) => Path.GetExtension(path).ToLowerInvariant() is ".m4a" or ".mp3";

        public float[] DecodeToMono16k(string path, CancellationToken cancellation)
        {
            lock (Decoded)
            {
                Decoded.Add(path);
            }
            return TranscriberAudio.Tone(2.5, 0.2f);
        }
    }
}
