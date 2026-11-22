using System.ComponentModel;
using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <see cref="MeetingStore"/> against a temporary folder: round trips, ordering, audio
/// removal, debounced saves, damaged files, meetings cut off by a quit, recovery from
/// transcript.md, and the notes and highlights the MCP server writes from its own process
/// (ports of MeetingsStoreTests.swift, the store parts of FoundationMeetingMigrationTests,
/// MCPWriteNotesTests and MCPHighlightsTests).
/// </summary>
public sealed class MeetingStoreTests : IDisposable
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");

    readonly TemporaryMeetingsFolder folder = new();

    public void Dispose() => folder.Dispose();

    MeetingStore NewStore(IMainThread? mainThread = null, TimeProvider? time = null) =>
        new(folder.Paths, mainThread ?? ImmediateMainThread.Shared, time)
        {
            TranscriptLanguage = () => AppLanguage.Ru,
            TranscriptTimeZone = Zone,
        };

    [Fact]
    public void EmptyFolderLoadsNothing()
    {
        Assert.Empty(NewStore().Meetings);
        var missing = new AppPaths(Path.Combine(folder.Base, "nothing-here"));
        Assert.Empty(new MeetingStore(missing, ImmediateMainThread.Shared).Meetings);
    }

    [Fact]
    public void CreateUpdateAndReload()
    {
        var store = NewStore();
        var meeting = store.Create("  Design review ");
        Assert.Equal("Design review", meeting.Title);
        Assert.Equal(MeetingStatus.Recording, meeting.Status);
        Assert.True(File.Exists(store.RecordPath(meeting.Id)));
        Assert.Equal([meeting], store.Meetings);

        meeting = meeting with
        {
            Status = MeetingStatus.Transcribed,
            Duration = 1_234.5,
            HasSystemAudio = true,
            EngineId = EngineId.Parakeet,
            Segments = [new TranscriptSegment { Start = 1, End = 2, Speaker = Speaker.Others, Text = "Hello" }],
        };
        store.Update(meeting);
        Assert.Equal(meeting, store.Meeting(meeting.Id));
        store.Flush();

        Assert.Equal([meeting], NewStore().Meetings);
    }

    [Fact]
    public void TheFolderIsNamedByTheUpperCaseIdAndHoldsTheMacLayout()
    {
        var store = NewStore();
        var id = Guid.Parse("e621e1f8-c36c-495a-93fc-0c247a3e6e5f");
        Assert.Equal(Path.Combine(folder.Paths.MeetingsFolder, "E621E1F8-C36C-495A-93FC-0C247A3E6E5F"), store.Folder(id));
        Assert.Equal("meeting.json", Path.GetFileName(store.RecordPath(id)));
        Assert.Equal("mic.caf", Path.GetFileName(store.MicrophoneAudioPath(id)));
        Assert.Equal("system.caf", Path.GetFileName(store.SystemAudioPath(id)));
        Assert.Equal("screen.mov", Path.GetFileName(store.ScreenRecordingPath(id)));
        Assert.Equal("transcript.md", Path.GetFileName(store.TranscriptPath(id)));
        Assert.Equal(store.Folder(id), Path.GetDirectoryName(store.MicrophoneAudioPath(id)));
    }

    [Fact]
    public void EmptyTitleGetsTheDefaultTitle()
    {
        var meeting = NewStore().Create("   ");
        Assert.NotEmpty(meeting.Title);
        Assert.Equal(MeetingTitles.DefaultTitle(meeting.CreatedAt), meeting.Title);
    }

    [Fact]
    public void MeetingsAreNewestFirst()
    {
        var store = NewStore();
        var old = store.Create("Old");
        var middle = store.Create("Middle");
        var newest = store.Create("New");
        store.Update(middle with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        store.Update(old with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-2) });
        Assert.Equal(["New", "Middle", "Old"], store.Meetings.Select(m => m.Title));
        store.Flush();
        Assert.Equal([newest.Id, middle.Id, old.Id], NewStore().Meetings.Select(m => m.Id));
    }

    [Fact]
    public void UpdateOfUnknownMeetingIsIgnored()
    {
        var store = NewStore();
        store.Update(new MeetingRecord { Title = "Ghost" });
        Assert.Empty(store.Meetings);
        store.Flush();
        Assert.Empty(Directory.GetFileSystemEntries(folder.Paths.MeetingsFolder));
    }

    [Fact]
    public void ChangesRaisePropertyChangedForMeetings()
    {
        var store = NewStore();
        var changes = new List<string?>();
        store.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var meeting = store.Create("Draft");
        var before = store.Meetings;
        store.Update(meeting with { Title = "Renamed" });
        Assert.Equal([nameof(MeetingStore.Meetings), nameof(MeetingStore.Meetings)], changes);
        Assert.NotSame(before, store.Meetings);
        store.Update(store.Meetings[0]);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void DebouncedSaveWritesAfterTheDelayOnTheMainThread()
    {
        var time = new ManualTimeProvider();
        var main = new QueuedMainThread();
        var store = NewStore(main, time);
        var meeting = store.Create("Draft");
        store.Update(meeting with { Title = "Draft 2" });
        store.Update(meeting with { Title = "Renamed" });
        Assert.Equal(1, time.ActiveTimers);

        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(0, main.Pending);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, main.Pending);
        Assert.Equal("Draft", NewStore().Meeting(meeting.Id)?.Title);

        main.RunAll();
        Assert.Equal("Renamed", NewStore().Meeting(meeting.Id)?.Title);
    }

    [Fact]
    public void FlushCancelsThePendingSave()
    {
        var time = new ManualTimeProvider();
        var main = new QueuedMainThread();
        var store = NewStore(main, time);
        var meeting = store.Create("Draft");
        store.Update(meeting with { Title = "Renamed" });
        store.Flush();
        Assert.Equal("Renamed", NewStore().Meeting(meeting.Id)?.Title);
        Assert.Equal(0, time.ActiveTimers);
        time.Advance(TimeSpan.FromSeconds(1));
        main.RunAll();
    }

    [Fact]
    public async Task DebouncedSaveWritesWithoutFlush()
    {
        var store = new MeetingStore(folder.Paths, ImmediateMainThread.Shared);
        var meeting = store.Create("Draft");
        store.Update(meeting with { Title = "Renamed" });

        MeetingRecord? reloaded = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            reloaded = NewStore().Meeting(meeting.Id);
            if (reloaded?.Title == "Renamed")
            {
                break;
            }
        }
        Assert.Equal("Renamed", reloaded?.Title);
    }

    [Fact]
    public void DeleteRemovesTheFolder()
    {
        var store = NewStore();
        var keep = store.Create("Keep");
        var drop = store.Create("Drop");
        File.WriteAllText(store.MicrophoneAudioPath(drop.Id), "audio");

        store.Delete(drop.Id);

        Assert.Equal([keep.Id], store.Meetings.Select(m => m.Id));
        Assert.False(Directory.Exists(store.Folder(drop.Id)));
        Assert.Equal([keep.Id], NewStore().Meetings.Select(m => m.Id));
    }

    [Fact]
    public void DeleteAudioRemovesTracksAndClearsTheFlag()
    {
        var store = NewStore();
        var meeting = store.Create("Call");
        store.Update(meeting with { Status = MeetingStatus.Transcribed, HasScreenRecording = true });
        MeetingAudioSamples.WriteTone(store.MicrophoneAudioPath(meeting.Id), 1);
        MeetingAudioSamples.WriteTone(store.SystemAudioPath(meeting.Id), 1);
        File.WriteAllText(store.ScreenRecordingPath(meeting.Id), "movie");
        File.WriteAllText(store.TranscriptPath(meeting.Id), "# Call");

        store.DeleteAudio(meeting.Id);
        store.Flush();

        Assert.False(store.Meeting(meeting.Id)?.HasAudio);
        Assert.False(store.Meeting(meeting.Id)?.HasScreenRecording);
        Assert.False(File.Exists(store.MicrophoneAudioPath(meeting.Id)));
        Assert.False(File.Exists(store.SystemAudioPath(meeting.Id)));
        Assert.False(File.Exists(store.ScreenRecordingPath(meeting.Id)));
        Assert.True(File.Exists(store.TranscriptPath(meeting.Id)));
        Assert.False(NewStore().Meeting(meeting.Id)?.HasAudio);
    }

    [Fact]
    public void RemoveAudioTracksKeepsTheScreenRecording()
    {
        var store = NewStore();
        var meeting = store.Create("Call");
        store.Update(meeting with { HasScreenRecording = true });
        MeetingAudioSamples.WriteTone(store.MicrophoneAudioPath(meeting.Id), 1);
        MeetingAudioSamples.WriteTone(store.SystemAudioPath(meeting.Id), 1);
        File.WriteAllText(store.ScreenRecordingPath(meeting.Id), "movie");

        ((IMeetingStore)store).RemoveAudioTracks(meeting.Id);
        store.Flush();

        Assert.False(File.Exists(store.MicrophoneAudioPath(meeting.Id)));
        Assert.False(File.Exists(store.SystemAudioPath(meeting.Id)));
        Assert.True(File.Exists(store.ScreenRecordingPath(meeting.Id)));
        var reloaded = NewStore().Meeting(meeting.Id);
        Assert.False(reloaded?.HasAudio);
        Assert.True(reloaded?.HasScreenRecording);
    }

    [Fact]
    public void DamagedRecordIsMovedAsideAndRebuiltFromSurvivingAudio()
    {
        var id = Guid.NewGuid();
        var meetingFolder = folder.Paths.MeetingFolder(id);
        Directory.CreateDirectory(meetingFolder);
        File.WriteAllText(Path.Combine(meetingFolder, "meeting.json"), "{ not json");
        MeetingAudioSamples.WriteTone(Path.Combine(meetingFolder, "mic.caf"), 2);

        var store = NewStore();

        var recovered = store.Meeting(id);
        Assert.NotNull(recovered);
        Assert.Equal(MeetingStatus.Recorded, recovered.Status);
        Assert.True(recovered.HasAudio);
        Assert.False(recovered.HasSystemAudio);
        Assert.Equal(2, recovered.Duration, 0.01);
        var names = Directory.GetFiles(meetingFolder).Select(Path.GetFileName).ToList();
        Assert.Contains(names, n => n!.StartsWith("meeting.corrupt-", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));
        Assert.Contains("meeting.json", names);
        Assert.Equal(recovered, NewStore().Meeting(id));
    }

    [Fact]
    public void DamagedRecordWithoutAudioIsSkipped()
    {
        var store = NewStore();
        var good = store.Create("Good");
        var id = Guid.NewGuid();
        var meetingFolder = folder.Paths.MeetingFolder(id);
        Directory.CreateDirectory(meetingFolder);
        File.WriteAllBytes(Path.Combine(meetingFolder, "meeting.json"), [0xFF, 0x00, 0x13]);
        // Folders that are not meetings are left alone.
        Directory.CreateDirectory(Path.Combine(folder.Paths.MeetingsFolder, "Notes"));

        var reloaded = NewStore();

        Assert.Equal([good.Id], reloaded.Meetings.Select(m => m.Id));
        var names = Directory.GetFileSystemEntries(meetingFolder).Select(Path.GetFileName).ToList();
        Assert.Single(names);
        Assert.StartsWith("meeting.corrupt-", names[0]);
    }

    [Fact]
    public void MeetingsCutOffByAQuitAreRepairedAtLaunch()
    {
        var store = NewStore();
        var recording = store.Create("Was recording");
        MeetingAudioSamples.WriteTone(store.MicrophoneAudioPath(recording.Id), 3);

        var transcribing = store.Create("Was transcribing");
        store.Update(transcribing with { Status = MeetingStatus.Transcribing });

        var withoutAudio = store.Create("Audio gone");
        store.Update(withoutAudio with { Status = MeetingStatus.Transcribing, HasAudio = false });
        store.Flush();

        var relaunched = NewStore();

        var repaired = relaunched.Meeting(recording.Id);
        Assert.NotNull(repaired);
        Assert.Equal(MeetingStatus.Recorded, repaired.Status);
        Assert.Equal(3, repaired.Duration, 0.01);
        Assert.True(repaired.HasAudio);
        Assert.Equal(MeetingStatus.Recorded, relaunched.Meeting(transcribing.Id)?.Status);
        var failed = relaunched.Meeting(withoutAudio.Id);
        Assert.NotNull(failed);
        Assert.Equal(MeetingStatus.Failed, failed.Status);
        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(MeetingStatus.Recorded, NewStore().Meeting(recording.Id)?.Status);
    }

    [Fact]
    public void AReRunCutOffByAQuitKeepsTheEarlierTranscript()
    {
        var store = NewStore();
        store.TranscriptLanguage = () => AppLanguage.En;
        var meeting = store.Create("Коворк");
        MeetingAudioSamples.WriteTone(store.MicrophoneAudioPath(meeting.Id), 1);
        meeting = meeting with
        {
            Status = MeetingStatus.Transcribed,
            EngineId = EngineId.Parakeet,
            Segments = [new TranscriptSegment { Start = 1, End = 5, Speaker = Speaker.Me, Text = "Привет", SpeakerId = "s1" }],
            Speakers = [new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, DisplayName = "Аня", ColorIndex = 0 }],
            Diarization = new MeetingDiarization { State = DiarizationState.Applied, SpeakersPerTrack = new() { ["microphone"] = 1 } },
        };
        store.Update(meeting);
        store.Flush();
        var document = File.ReadAllBytes(store.TranscriptPath(meeting.Id));

        // What "Process again" keeps on disk until the new transcript is ready.
        meeting = meeting with { Status = MeetingStatus.Transcribing };
        store.Update(meeting);
        // A transcribed meeting that was silent has no segments, only its engine.
        var silent = store.Create("Тишина");
        store.Update(silent with { Status = MeetingStatus.Transcribing, EngineId = EngineId.GigaamCtc, HasAudio = false });
        store.Flush();

        var relaunched = NewStore();

        var expected = meeting with { Status = MeetingStatus.Transcribed };
        Assert.Equal(expected, relaunched.Meeting(meeting.Id));
        Assert.Equal(document, File.ReadAllBytes(store.TranscriptPath(meeting.Id)));
        Assert.Equal(MeetingStatus.Transcribed, relaunched.Meeting(silent.Id)?.Status);
        Assert.Null(relaunched.Meeting(silent.Id)?.ErrorMessage);
        Assert.Equal(expected, NewStore().Meeting(meeting.Id));
    }

    /// <summary>
    /// Rebuilt from the audio alone, a transcribed meeting would read as never transcribed, and
    /// transcribing it again would overwrite the document that still holds its lines, names and
    /// moments; so the transcript comes back even when the audio survived.
    /// </summary>
    [Fact]
    public void DamagedRecordWithAudioAndATranscriptComesBackTranscribed()
    {
        var id = Guid.NewGuid();
        var meetingFolder = folder.Paths.MeetingFolder(id);
        Directory.CreateDirectory(meetingFolder);
        File.WriteAllText(Path.Combine(meetingFolder, "meeting.json"), "{ not json");
        MeetingAudioSamples.WriteTone(Path.Combine(meetingFolder, "mic.caf"), 30);
        MeetingAudioSamples.WriteTone(Path.Combine(meetingFolder, "system.caf"), 30);
        const string document = """
            # Коворк

            15 сентября 2026 г., 11:02 · 00:30

            Участники: Я, Аня

            [00:04] Я: Всем привет.

            [00:12] ★ Аня: Давайте к пятнице соберём прототип.

            """;
        File.WriteAllText(Path.Combine(meetingFolder, "transcript.md"), document);

        var store = NewStore();

        var recovered = store.Meeting(id);
        Assert.NotNull(recovered);
        Assert.Equal(MeetingStatus.Transcribed, recovered.Status);
        Assert.Equal("Коворк", recovered.Title);
        Assert.True(recovered.HasAudio);
        Assert.True(recovered.HasSystemAudio);
        Assert.Equal(30, recovered.Duration, 0.01);
        Assert.Equal(1, recovered.FormatVersion);
        Assert.Equal(["Всем привет.", "Давайте к пятнице соберём прототип."], recovered.Segments.Select(s => s.Text));
        Assert.Equal(["Аня"], recovered.Speakers.Select(s => s.DisplayName));
        Assert.Equal([12.0], recovered.Highlights.Select(h => h.Time));
        Assert.Equal(document, File.ReadAllText(Path.Combine(meetingFolder, "transcript.md")));
        Assert.Equal(recovered, NewStore().Meeting(id));
    }

    [Fact]
    public void ALostRecordComesBackFromTheTranscriptAloneDatedByTheEarlierOfFolderAndDocument()
    {
        var id = Guid.NewGuid();
        var meetingFolder = folder.Paths.MeetingFolder(id);
        Directory.CreateDirectory(meetingFolder);
        var transcript = Path.Combine(meetingFolder, "transcript.md");
        File.WriteAllText(transcript, "# Планёрка\n\n[00:05] Me: Hello.\n\n[01:10] Others: Hi.\n");
        var folderCreated = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var documentCreated = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        Directory.SetCreationTimeUtc(meetingFolder, folderCreated);
        File.SetCreationTimeUtc(transcript, documentCreated);
        var folderDateStuck = Math.Abs((Directory.GetCreationTimeUtc(meetingFolder) - folderCreated).TotalSeconds) < 1;

        var recovered = NewStore().Meeting(id);

        Assert.NotNull(recovered);
        Assert.Equal("Планёрка", recovered.Title);
        Assert.Equal(MeetingStatus.Transcribed, recovered.Status);
        Assert.False(recovered.HasAudio);
        Assert.Equal(70, recovered.Duration);
        Assert.Equal([Speaker.Me, Speaker.Others], recovered.Segments.Select(s => s.Speaker));
        if (folderDateStuck)
        {
            // Some file systems cannot set a creation date; the check needs one that can.
            Assert.Equal(new DateTimeOffset(folderCreated), recovered.CreatedAt);
        }
        Assert.True(File.Exists(Path.Combine(meetingFolder, "meeting.json")));
    }

    [Fact]
    public void AnUnreadableTranscriptWithoutAudioStillListsTheMeetingAsFailed()
    {
        var id = Guid.NewGuid();
        var meetingFolder = folder.Paths.MeetingFolder(id);
        Directory.CreateDirectory(meetingFolder);
        File.WriteAllBytes(Path.Combine(meetingFolder, "transcript.md"), [0x23, 0x20, 0xFF, 0xFE, 0x0A]);

        var recovered = NewStore().Meeting(id);

        Assert.NotNull(recovered);
        Assert.Equal(MeetingStatus.Failed, recovered.Status);
        Assert.False(recovered.HasAudio);
        Assert.Equal(MeetingErrors.RecordDamagedMessage(), recovered.ErrorMessage);
    }

    [Fact]
    public void ARecordingFolderWithAudioOnlyComesBackAsRecorded()
    {
        var id = Guid.NewGuid();
        var meetingFolder = folder.Paths.MeetingFolder(id);
        Directory.CreateDirectory(meetingFolder);
        MeetingAudioSamples.WriteTone(Path.Combine(meetingFolder, "system.caf"), 1.5);

        var recovered = NewStore().Meeting(id);

        Assert.NotNull(recovered);
        Assert.Equal(MeetingStatus.Recorded, recovered.Status);
        Assert.True(recovered.HasSystemAudio);
        Assert.Equal(1.5, recovered.Duration, 0.01);
        Assert.Equal(MeetingTitles.DefaultTitle(recovered.CreatedAt), recovered.Title);
    }

    [Fact]
    public void AVersionOneFolderLoadsWithoutBeingRewritten()
    {
        var id = Guid.Parse("7C1F2A4E-9B3D-4E5F-8A6B-1C2D3E4F5A6B");
        var meetingFolder = folder.Paths.MeetingFolder(id);
        Directory.CreateDirectory(meetingFolder);
        var record = Path.Combine(meetingFolder, "meeting.json");
        var transcript = Path.Combine(meetingFolder, "transcript.md");
        const string versionOne = """
            {
              "createdAt" : 779000000,
              "duration" : 754.5,
              "engineID" : "parakeet-v3",
              "hasAudio" : true,
              "hasSystemAudio" : true,
              "id" : "7C1F2A4E-9B3D-4E5F-8A6B-1C2D3E4F5A6B",
              "segments" : [
                { "end" : 12.5, "speaker" : "me", "start" : 0.5, "text" : "Всем привет, начнём с планов на неделю." },
                { "end" : 20, "speaker" : "others", "start" : 13, "text" : "Давайте к пятнице соберём прототип." },
                { "end" : 31.25, "speaker" : "me", "start" : 21, "text" : "Договорились." }
              ],
              "status" : "transcribed",
              "title" : "Коворк 15 сентября"
            }
            """;
        var original = Encoding.UTF8.GetBytes(versionOne);
        File.WriteAllBytes(record, original);
        File.WriteAllText(transcript, "# Коворк 15 сентября\n");
        var past = new DateTime(2023, 3, 7, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(record, past);
        File.SetLastWriteTimeUtc(transcript, past);

        var store = NewStore();
        store.Flush();

        var meeting = store.Meeting(id);
        Assert.NotNull(meeting);
        Assert.Equal(MeetingStatus.Transcribed, meeting.Status);
        Assert.Equal(1, meeting.FormatVersion);
        Assert.Equal(["me", "others"], meeting.EffectiveSpeakers.Select(s => s.Id));
        Assert.Equal(original, File.ReadAllBytes(record));
        Assert.Equal(past, File.GetLastWriteTimeUtc(record));
        Assert.Equal(past, File.GetLastWriteTimeUtc(transcript));
        Assert.Equal(["meeting.json", "transcript.md"], Directory.GetFileSystemEntries(meetingFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // MARK: - transcript.md

    [Fact]
    public void SavingATranscribedMeetingWritesItsDocumentAndOtherStatusesLeaveItAlone()
    {
        var store = NewStore();
        var meeting = store.Create("Коворк");
        var path = store.TranscriptPath(meeting.Id);
        store.Update(meeting with { Title = "Коворк 2" });
        store.Flush();
        Assert.False(File.Exists(path));

        var transcribed = store.Meeting(meeting.Id)! with
        {
            Status = MeetingStatus.Transcribed,
            Segments = [new TranscriptSegment { Start = 4, End = 9, Speaker = Speaker.Me, Text = "Всем привет." }],
        };
        store.Update(transcribed);
        store.Flush();
        Assert.Equal(TranscriptMerger.Markdown(transcribed, AppLanguage.Ru, timeZone: Zone), File.ReadAllText(path));

        // An unchanged document is not written again.
        var past = new DateTime(2023, 3, 7, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, past);
        store.Update(transcribed with { Duration = 10 });
        store.Flush();
        Assert.Equal(past, File.GetLastWriteTimeUtc(path));

        store.Update(transcribed with { Status = MeetingStatus.Transcribing, Title = "Другое" });
        store.Flush();
        Assert.StartsWith("# Коворк 2\n", File.ReadAllText(path));
    }

    // MARK: - Claude's notes and highlights from the MCP process

    static MeetingRecord CoworkRecord() => new()
    {
        Id = Guid.Parse("5A1D0B6E-8C11-4E0A-9E43-2B7C9D1E0F31"),
        Title = "Коворк",
        CreatedAt = AppleDate.FromReferenceSeconds(779_612_527),
        Duration = 3852.4,
        Status = MeetingStatus.Transcribed,
        HasAudio = false,
        EngineId = EngineId.Parakeet,
        Segments =
        [
            new TranscriptSegment { Start = 4, End = 9, Speaker = Speaker.Me, Text = "Всем привет, начнём с планов на неделю.", SpeakerId = "s1" },
            new TranscriptSegment { Start = 758, End = 765, Speaker = Speaker.Me, Text = "Давайте к пятнице соберём прототип.", SpeakerId = "s2" },
        ],
        Speakers =
        [
            new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0, IsMe = true },
            new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, DisplayName = "Аня", ColorIndex = 1 },
        ],
        Highlights =
        [
            new MeetingHighlight { Id = Guid.Parse("9B1C0000-0000-4000-8000-000000000001"), Time = 760.2, Note = "дедлайн", Source = HighlightSource.Recording, CreatedAt = AppleDate.FromReferenceSeconds(779_613_000) },
            new MeetingHighlight { Id = Guid.Parse("9B1C0000-0000-4000-8000-000000000002"), Time = 5, Source = HighlightSource.Transcript, CreatedAt = AppleDate.FromReferenceSeconds(779_614_000) },
        ],
    };

    static readonly MeetingNotes Notes = new("Итоги встречи.", ["Выпуск в пятницу"], ["Аня собирает прототип."], "Claude", AppleDate.FromReferenceSeconds(779_700_000));

    MeetingRecord StoredRecord(Guid id) => MeetingRecordFile.Read(folder.Paths, id) ?? throw new InvalidOperationException("no record");

    [Fact]
    public void ASaveKeepsNotesTheServerWroteAndShowsThemAtOnce()
    {
        var record = folder.Write(CoworkRecord());
        var store = NewStore();
        new MeetingNotesFile(folder.Paths).Write(Notes, record.Id);
        Assert.Null(store.Meeting(record.Id)?.ClaudeNotes);

        store.Update(store.Meeting(record.Id)! with { Title = "Коворк, переименован" });
        store.Flush();

        Assert.Equal(Notes.Summary, store.Meeting(record.Id)?.ClaudeNotes?.Summary);
        var document = File.ReadAllText(store.TranscriptPath(record.Id));
        Assert.StartsWith("# Коворк, переименован", document);
        Assert.Contains("## Итоги Claude", document);
        Assert.Contains("- Аня собирает прототип.", document);
        Assert.Equal(Notes.Summary, StoredRecord(record.Id).ClaudeNotes?.Summary);
    }

    [Fact]
    public void TheAppPicksUpNotesWrittenWhileItWasRunning()
    {
        var record = folder.Write(CoworkRecord());
        var store = NewStore();
        new MeetingNotesFile(folder.Paths).Write(Notes, record.Id);

        store.RefreshFromDisk();

        Assert.Equal(Notes.Summary, store.Meeting(record.Id)?.ClaudeNotes?.Summary);
        var document = File.ReadAllText(store.TranscriptPath(record.Id));
        Assert.Contains("## Итоги Claude", document);
        Assert.Contains("Итоги встречи.", document);
    }

    [Fact]
    public void ARefreshReadsOnlyTheFilesThatChanged()
    {
        var record = folder.Write(CoworkRecord());
        var store = NewStore();
        var writer = new MeetingNotesFile(folder.Paths);
        writer.Write(Notes, record.Id);
        store.RefreshFromDisk();
        var path = store.TranscriptPath(record.Id);
        Assert.True(File.Exists(path));

        // Nothing has touched the record since, so the refresh stops at the file's stamp.
        File.Delete(path);
        store.RefreshFromDisk();
        Assert.False(File.Exists(path));

        writer.Write(new MeetingNotes("Итоги встречи, дополненные."), record.Id);
        store.RefreshFromDisk();
        Assert.Contains("Итоги встречи, дополненные.", File.ReadAllText(path));
    }

    [Fact]
    public void TheOwnerCanDeleteTheNotesInOneAction()
    {
        var record = folder.Write(CoworkRecord() with { ClaudeNotes = Notes });
        var store = NewStore();
        store.SetClaudeNotes(null, record.Id);
        Assert.Null(store.Meeting(record.Id)?.ClaudeNotes);
        Assert.Null(StoredRecord(record.Id).ClaudeNotes);
        store.Flush();

        Assert.Null(StoredRecord(record.Id).ClaudeNotes);
        Assert.Null(NewStore().Meeting(record.Id)?.ClaudeNotes);
        Assert.DoesNotContain("## Итоги Claude", File.ReadAllText(store.TranscriptPath(record.Id)));
    }

    [Fact]
    public void WritingTheSameNotesAgainLeavesTheFileAlone()
    {
        var record = folder.Write(CoworkRecord());
        var writer = new MeetingNotesFile(folder.Paths);
        writer.Write(Notes, record.Id);
        var path = MeetingRecordFile.PathFor(folder.Paths, record.Id);
        var before = File.ReadAllBytes(path);
        var past = new DateTime(2023, 3, 7, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, past);

        var stored = writer.Write(new MeetingNotes(Notes.Summary, Notes.Decisions, Notes.Tasks, Notes.Author, Notes.WrittenAt.AddMinutes(10)), record.Id);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(past, File.GetLastWriteTimeUtc(path));
        Assert.Equal(Notes.WrittenAt, stored?.WrittenAt);
    }

    [Fact]
    public void AnAppSaveKeepsAHighlightTheServerMarked()
    {
        var record = folder.Write(CoworkRecord());
        var store = NewStore();
        new MeetingHighlightsFile(folder.Paths).Mark(600, "важно", record.Id, DateTimeOffset.UtcNow);

        store.Update(store.Meeting(record.Id)! with { Title = "Коворк, переименован" });
        store.Flush();

        Assert.Equal(3, store.Meeting(record.Id)?.Highlights.Count);
        var reloaded = NewStore().Meeting(record.Id);
        Assert.Equal("Коворк, переименован", reloaded?.Title);
        Assert.Equal(3, reloaded?.Highlights.Count);
    }

    [Fact]
    public void AnAppSaveDoesNotResurrectAnUnmarkedHighlight()
    {
        var record = folder.Write(CoworkRecord());
        var store = NewStore();
        Assert.Equal(2, store.Meeting(record.Id)?.Highlights.Count);

        Assert.True(new MeetingHighlightsFile(folder.Paths).Unmark(record.Highlights[0].Id, record.Id).Removed);

        store.Update(store.Meeting(record.Id)! with { Title = "Коворк, переименован" });
        store.Flush();

        Assert.Equal(1, store.Meeting(record.Id)?.Highlights.Count);
        Assert.Equal(1, NewStore().Meeting(record.Id)?.Highlights.Count);
    }

    [Fact]
    public void RefreshPicksUpAHighlightMarkedWhileTheStoreWasRunning()
    {
        var record = folder.Write(CoworkRecord());
        var store = NewStore();
        new MeetingHighlightsFile(folder.Paths).Mark(600, null, record.Id, DateTimeOffset.UtcNow);

        store.RefreshFromDisk();
        Assert.Equal(3, store.Meeting(record.Id)?.Highlights.Count);

        // A save right after the refresh must not add the highlight a second time.
        store.Update(store.Meeting(record.Id)! with { Title = "Коворк, переименован" });
        store.Flush();
        Assert.Equal(3, store.Meeting(record.Id)?.Highlights.Count);
        Assert.Contains("★", File.ReadAllText(store.TranscriptPath(record.Id)));
    }

    /// <summary>
    /// A star the app has not saved yet, when the app becomes active and re-reads the file, is
    /// the app's own change: the next save keeps it, and keeps an unstar too.
    /// </summary>
    [Fact]
    public void RefreshKeepsAStarOrAnUnstarNotSavedYet()
    {
        var store = NewStore();
        var meeting = store.Create("Коворк");
        var kept = new MeetingHighlight { Time = 5, Source = HighlightSource.Transcript };
        var removed = new MeetingHighlight { Time = 9, Source = HighlightSource.Transcript };
        meeting = meeting with { Highlights = [kept, removed] };
        store.Update(meeting);
        store.Flush();

        var added = new MeetingHighlight { Time = 12, Source = HighlightSource.Transcript };
        meeting = meeting with { Highlights = [kept, added] };
        store.Update(meeting);
        store.RefreshFromDisk();
        Assert.Equal([kept.Id, added.Id], store.Meeting(meeting.Id)?.Highlights.Select(h => h.Id));
        store.Flush();

        Assert.Equal([kept.Id, added.Id], store.Meeting(meeting.Id)?.Highlights.Select(h => h.Id));
        Assert.Equal([kept.Id, added.Id], NewStore().Meeting(meeting.Id)?.Highlights.Select(h => h.Id));
    }

    [Fact]
    public void ReconciliationAddsWhatTheDiskGainedAndDropsWhatItLost()
    {
        MeetingHighlight Highlight(double time) => new() { Time = time, Source = HighlightSource.Transcript };
        var a = Highlight(1);
        var b = Highlight(2);
        var c = Highlight(3);
        var d = Highlight(4);
        var known = new HashSet<Guid> { a.Id, b.Id };

        // Ours added c; the disk lost b and gained d.
        var merged = MeetingStore.ReconciledHighlights([a, b, c], [a, d], known);

        Assert.Equal([a.Id, c.Id, d.Id], merged.Select(h => h.Id));
        // Without a baseline the disk is the baseline.
        Assert.Equal([a.Id, c.Id], MeetingStore.ReconciledHighlights([a, c], [a, b], null).Select(h => h.Id));
    }
}
