using System.Collections.Concurrent;
using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// <c>transcript.md</c>: the first format's document for meetings without speakers or moments,
/// the second format in both languages, the recovery parser, and <see cref="MeetingStore"/>
/// keeping the file in step with transcribed meetings (port of the macOS
/// <c>MeetingsDocumentTests</c>, plus the document tests of <c>FoundationMeetingMigrationTests</c>).
///
/// The macOS tests write dates in the Mac's time zone; here every document is written in a
/// fixed zone, so the expected date text does not depend on the computer running the tests.
/// </summary>
public sealed class MeetingDocumentTests : IDisposable
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");

    readonly string temporary;
    readonly AppPaths paths;

    public MeetingDocumentTests()
    {
        temporary = Path.Combine(Path.GetTempPath(), "SlovoMeetingsDocumentTests-" + Guid.NewGuid().ToString("N"));
        paths = new AppPaths(temporary);
        paths.CreateFolders();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(temporary, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // MARK: - First format

    [Fact]
    public void FirstFormatRecordGivesTheFirstFormatDocumentByteForByte()
    {
        var record = new MeetingRecord
        {
            Title = "Коворк 15 сентября",
            CreatedAt = CreatedAt,
            Duration = 754.5,
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments = FirstFormatSegments,
            FormatVersion = 1,
        };
        var withStoredFallback = record with { Speakers = [MeetingSpeaker.Fallback(MeetingTrack.System)] };

        foreach (var language in new[] { AppLanguage.En, AppLanguage.Ru })
        {
            var expected = TranscriptMerger.Markdown(record.Title, record.CreatedAt, FirstFormatSegments, language, Zone);
            Assert.Equal(expected, TranscriptMerger.Markdown(record, language, timeZone: Zone));
            Assert.Equal(expected, TranscriptMerger.Markdown(withStoredFallback, language, timeZone: Zone));
        }
    }

    [Fact]
    public void ClipSegmentsWithoutSpeakersKeepTheFirstFormatLayout()
    {
        var record = new MeetingRecord
        {
            Title = "Созвон",
            CreatedAt = CreatedAt,
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = true,
            Segments =
            [
                Segment(0, 2, Speaker.Me, "a"),
                Segment(3, 5, Speaker.Me, "b"),
                Segment(6, 8, Speaker.Others, "c"),
            ],
        };
        var expected = TranscriptMerger.Markdown("Созвон", CreatedAt,
        [
            Segment(0, 5, Speaker.Me, "a b"),
            Segment(6, 8, Speaker.Others, "c"),
        ], AppLanguage.En, Zone);
        Assert.Equal(expected, TranscriptMerger.Markdown(record, AppLanguage.En, timeZone: Zone));

        var renamed = MeetingEdits.RenameSpeaker("others", "Команда", record);
        Assert.Contains("\n## Transcript\n", TranscriptMerger.Markdown(renamed, AppLanguage.En, timeZone: Zone), StringComparison.Ordinal);
        var marked = record with { Highlights = [new MeetingHighlight { Time = 1, Source = HighlightSource.Recording }] };
        Assert.Contains("\n## Important moments\n", TranscriptMerger.Markdown(marked, AppLanguage.En, timeZone: Zone), StringComparison.Ordinal);
    }

    // MARK: - Second format

    [Fact]
    public void SecondFormatInRussian()
    {
        var expected = $"""
            # Коворк 15 сентября

            {DateText(AppLanguage.Ru)} · 1:04:12

            Участники: Я, Аня, Спикер 3

            ## Важные моменты

            - [12:40] Аня: «Давайте к пятнице соберём прототип и покажем его команде.» — дедлайн

            ## Расшифровка

            [00:04] Я: Всем привет, начнём с планов на неделю.

            [12:38] ★ Аня: Давайте к пятнице соберём прототип и покажем его команде.

            [13:20] Спикер 3: Согласен.

            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, TranscriptMerger.Markdown(CoworkRecord(), AppLanguage.Ru, timeZone: Zone));
    }

    [Fact]
    public void SecondFormatInEnglishListsMomentsWithoutALineToo()
    {
        var cowork = CoworkRecord();
        var record = cowork with
        {
            Highlights =
            [
                .. cowork.Highlights,
                new MeetingHighlight { Time = 3000, Source = HighlightSource.Recording, CreatedAt = AppleDate.FromReferenceSeconds(779_003_000) },
            ],
        };
        var expected = $"""
            # Коворк 15 сентября

            {DateText(AppLanguage.En)} · 1:04:12

            Participants: Me, Аня, Speaker 3

            ## Important moments

            - [12:40] Аня: «Давайте к пятнице соберём прототип и покажем его команде.» — дедлайн
            - [50:00]

            ## Transcript

            [00:04] Me: Всем привет, начнём с планов на неделю.

            [12:38] ★ Аня: Давайте к пятнице соберём прототип и покажем его команде.

            [13:20] Speaker 3: Согласен.

            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, TranscriptMerger.Markdown(record, AppLanguage.En, timeZone: Zone));
    }

    [Fact]
    public void SpeakerFilterNarrowsParticipantsMomentsAndLines()
    {
        var cowork = CoworkRecord();
        var record = cowork with { Highlights = [.. cowork.Highlights, new MeetingHighlight { Time = 3000, Source = HighlightSource.Recording }] };

        var anya = TranscriptMerger.Markdown(record, AppLanguage.Ru, new HashSet<string> { "s2" }, Zone);
        Assert.Contains("\nУчастники: Аня\n", anya, StringComparison.Ordinal);
        Assert.Contains("- [12:40] Аня: «", anya, StringComparison.Ordinal);
        Assert.DoesNotContain("- [50:00]", anya, StringComparison.Ordinal);
        Assert.EndsWith("## Расшифровка\n\n[12:38] ★ Аня: Давайте к пятнице соберём прототип и покажем его команде.\n", anya,
                        StringComparison.Ordinal);

        var third = TranscriptMerger.Markdown(record, AppLanguage.Ru, new HashSet<string> { "s3" }, Zone);
        Assert.DoesNotContain("## Важные моменты", third, StringComparison.Ordinal);
        Assert.EndsWith("[13:20] Спикер 3: Согласен.\n", third, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySecondFormatDocumentSaysNoSpeech()
    {
        var record = new MeetingRecord
        {
            Title = "Тишина",
            CreatedAt = CreatedAt,
            Status = MeetingStatus.Transcribed,
            Speakers = [new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, DisplayName = "Аня", ColorIndex = 0 }],
        };
        var document = TranscriptMerger.Markdown(record, AppLanguage.En, timeZone: Zone);
        Assert.EndsWith("\n\n## Transcript\n\nNo speech was recognized.\n", document, StringComparison.Ordinal);
        Assert.DoesNotContain("Participants", document, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Copy moments" (the macOS <c>AppModel.highlightsText</c>): the document's list lines, a
    /// moment without a line keeps its time and note, and a filter keeps the chosen speakers' only.
    /// </summary>
    [Fact]
    public void HighlightsTextListsMomentsAsTheDocumentDoes()
    {
        var cowork = CoworkRecord();
        var record = cowork with
        {
            Highlights =
            [
                .. cowork.Highlights,
                new MeetingHighlight { Time = 3000, Note = "  без строки \n", Source = HighlightSource.Recording },
                new MeetingHighlight { Time = 801, Note = "   ", Source = HighlightSource.Transcript },
            ],
        };

        Assert.Equal(
            "- [12:40] Аня: «Давайте к пятнице соберём прототип и покажем его команде.» — дедлайн\n" +
            "- [13:21] Спикер 3: «Согласен.»\n" +
            "- [50:00] — без строки",
            TranscriptMerger.HighlightsText(record, AppLanguage.Ru));
        Assert.Equal("- [13:21] Speaker 3: «Согласен.»", TranscriptMerger.HighlightsText(record, AppLanguage.En, new HashSet<string> { "s3" }));
        Assert.Equal("", TranscriptMerger.HighlightsText(record, AppLanguage.En, new HashSet<string> { "s1" }));
        // An empty filter is no filter.
        Assert.Equal(TranscriptMerger.HighlightsText(record, AppLanguage.En), TranscriptMerger.HighlightsText(record, AppLanguage.En, new HashSet<string>()));
        Assert.Equal("", TranscriptMerger.HighlightsText(cowork with { Highlights = [] }, AppLanguage.En));
    }

    [Fact]
    public void AnchoredLineIsTheLineThatListsTheMomentElseNone()
    {
        var record = CoworkRecord();
        var lines = TranscriptMerger.Lines(record);
        Assert.Equal("s2", TranscriptMerger.AnchoredLine(record.Highlights[0], lines)?.SpeakerId);
        Assert.Equal("s3", TranscriptMerger.AnchoredLine(new MeetingHighlight { Time = 810 }, lines)?.SpeakerId);
        Assert.Null(TranscriptMerger.AnchoredLine(new MeetingHighlight { Time = 3000 }, lines));
    }

    [Fact]
    public void ExcerptIsCutAtOneHundredTwentyCharacters()
    {
        Assert.Equal(new string('ы', 120) + "…", TranscriptMerger.Excerpt(new string('ы', 130)));
        Assert.Equal(new string('ы', 120), TranscriptMerger.Excerpt(new string('ы', 120)));
    }

    // MARK: - Recovery parser

    [Fact]
    public void RecoveryParserRoundTripsSecondFormatDocuments()
    {
        var original = CoworkRecord();
        foreach (var language in new[] { AppLanguage.En, AppLanguage.Ru })
        {
            var document = TranscriptMerger.Markdown(original, language, timeZone: Zone);
            var parsed = MeetingStore.ParseTranscript(document);

            Assert.Equal("Коворк 15 сентября", parsed.Title);
            Assert.Equal([4.0, 758, 800], parsed.Segments.Select(s => s.Start));
            Assert.Equal(original.Segments.Select(s => s.Text), parsed.Segments.Select(s => s.Text));
            Assert.Equal([Speaker.Me, Speaker.Me, Speaker.Me], parsed.Segments.Select(s => s.Speaker));
            Assert.Equal([null, "s4", "s3"], parsed.Segments.Select(s => s.SpeakerId));
            Assert.Equal(
            [
                new MeetingSpeaker { Id = "s4", Track = MeetingTrack.Microphone, Number = 4, DisplayName = "Аня", ColorIndex = 3 },
                new MeetingSpeaker { Id = "s3", Track = MeetingTrack.Microphone, Number = 3, ColorIndex = 2 },
            ], parsed.Speakers);
            Assert.Equal([758.0], parsed.Highlights.Select(h => h.Time));
            Assert.Equal([HighlightSource.Transcript], parsed.Highlights.Select(h => h.Source));
            // The same document gives the same moment ids.
            Assert.Equal(parsed.Highlights.Select(h => h.Id), MeetingStore.ParseTranscript(document).Highlights.Select(h => h.Id));

            var recovered = new MeetingRecord
            {
                Title = parsed.Title ?? "",
                CreatedAt = original.CreatedAt,
                Duration = original.Duration,
                Status = MeetingStatus.Transcribed,
                Segments = parsed.Segments,
                Speakers = parsed.Speakers,
                Highlights = parsed.Highlights,
            };
            var rendered = TranscriptMerger.Markdown(recovered, language, timeZone: Zone);
            var heading = "## " + MeetingLanguage.Table(language).Get("meetings.transcript.heading") + "\n\n";
            Assert.Equal(document.Split(heading)[^1], rendered.Split(heading)[^1]);
        }
    }

    [Fact]
    public void RecoveryParserSkipsEverythingButTranscriptLines()
    {
        var text = $"""
            # Заметки

            Participants: Me, Олег
            - [00:10] Олег: «пункт списка»
            ## Transcript
            [00:05] Олег: Первая строка
            [1:00:07] ★ Speaker 2: Вторая: с двоеточием
            [aa:bb] Me: не время
            [00:09] {new string('д', 41)}: слишком длинное имя
            [00:12]Others: без пробела
            просто текст
            [00:20] Olga Speaker: ещё
            """.ReplaceLineEndings("\n");

        var parsed = MeetingStore.ParseTranscript(text);

        Assert.Equal("Заметки", parsed.Title);
        Assert.Equal(["Первая строка", "Вторая: с двоеточием", "без пробела", "ещё"], parsed.Segments.Select(s => s.Text));
        Assert.Equal([5.0, 3607, 12, 20], parsed.Segments.Select(s => s.Start));
        Assert.Equal([Speaker.Me, Speaker.Me, Speaker.Others, Speaker.Me], parsed.Segments.Select(s => s.Speaker));
        Assert.Equal(["s3", "s2", null, "s4"], parsed.Segments.Select(s => s.SpeakerId));
        Assert.Equal(["s3", "s2", "s4"], parsed.Speakers.Select(s => s.Id));
        Assert.Equal(["Олег", null, "Olga Speaker"], parsed.Speakers.Select(s => s.DisplayName));
        Assert.Equal([3607.0], parsed.Highlights.Select(h => h.Time));
    }

    /// <summary>
    /// The MCP server's archive recovers meetings off the main thread (FoundationMeetingMigrationTests;
    /// on macOS the call compiles only while the parser stays nonisolated). Here the parse runs on a
    /// thread-pool thread.
    /// </summary>
    [Fact]
    public async Task TranscriptRecoveryParserReadsAVersionOneDocumentOffTheMainActor()
    {
        foreach (var language in new[] { AppLanguage.En, AppLanguage.Ru })
        {
            var document = TranscriptMerger.Markdown("Коворк 15 сентября", AppleDate.FromReferenceSeconds(779_000_000),
                                                     FirstFormatSegments, language, Zone);
            var parsed = await Task.Run(() => MeetingStore.ParseTranscript(document), TestContext.Current.CancellationToken);
            Assert.Equal("Коворк 15 сентября", parsed.Title);
            Assert.Equal([Speaker.Me, Speaker.Others, Speaker.Me], parsed.Segments.Select(s => s.Speaker));
            Assert.Equal(FirstFormatSegments.Select(s => s.Text), parsed.Segments.Select(s => s.Text));
            Assert.Equal([0.0, 13, 21], parsed.Segments.Select(s => s.Start));
        }
    }

    /// <summary>From FoundationMeetingMigrationTests.</summary>
    [Fact]
    public void SpeakerLabelsFollowTheNamingRules()
    {
        Assert.Equal("Я", TranscriptMerger.SpeakerLabel(MeetingSpeaker.Fallback(MeetingTrack.Microphone), AppLanguage.Ru));
        Assert.Equal("Собеседники", TranscriptMerger.SpeakerLabel(MeetingSpeaker.Fallback(MeetingTrack.System), AppLanguage.Ru));
        Assert.Equal("Speaker 3", TranscriptMerger.SpeakerLabel(
            new MeetingSpeaker { Id = "s3", Track = MeetingTrack.Microphone, Number = 3, ColorIndex = 2 }, AppLanguage.En));
        Assert.Equal("Me", TranscriptMerger.SpeakerLabel(
            new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, ColorIndex = 1, IsMe = true }, AppLanguage.En));
        Assert.Equal("Аня", TranscriptMerger.SpeakerLabel(
            new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, DisplayName = "Аня", ColorIndex = 1, IsMe = true },
            AppLanguage.En));
    }

    // MARK: - Store

    [Fact]
    public void StoreWritesTheTranscriptOnlyForTranscribedMeetings()
    {
        var store = new MeetingStore(paths, ImmediateMainThread.Shared) { TranscriptLanguage = () => AppLanguage.Ru, TranscriptTimeZone = Zone };
        var meeting = store.Create("Планёрка");
        var path = store.TranscriptPath(meeting.Id);
        store.Flush();
        Assert.False(File.Exists(path));

        meeting = meeting with { Status = MeetingStatus.Recorded };
        store.Update(meeting);
        store.Flush();
        Assert.False(File.Exists(path));

        meeting = meeting with
        {
            Status = MeetingStatus.Transcribed,
            Segments = [Segment(1, 4, Speaker.Me, "Итоги пришлю вечером", "s1")],
            Speakers = [new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0 }],
        };
        store.Update(meeting);
        store.Flush();
        var saved = store.Meeting(meeting.Id);
        Assert.NotNull(saved);
        Assert.Equal(TranscriptMerger.Markdown(saved, AppLanguage.Ru, timeZone: Zone), File.ReadAllText(path, Encoding.UTF8));

        store.Update(MeetingEdits.RenameSpeaker("s1", "Аня", saved));
        store.Flush();
        Assert.Contains("[00:01] Аня: Итоги пришлю вечером", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);

        var reprocessing = store.Meeting(meeting.Id);
        Assert.NotNull(reprocessing);
        reprocessing = reprocessing with { Status = MeetingStatus.Transcribing };
        store.Update(MeetingEdits.RenameSpeaker("s1", "Борис", reprocessing));
        store.Flush();
        var kept = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("Аня", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("Борис", kept, StringComparison.Ordinal);
    }

    /// <summary>
    /// The save runs 500 ms after the change, posted to the main thread; the test pumps that
    /// thread while it waits, as the macOS test's main actor runs the save between its sleeps.
    /// </summary>
    [Fact]
    public async Task DebouncedSaveWritesTheTranscriptAndUnchangedDocumentsAreLeftAlone()
    {
        var mainThread = new PumpedMainThread();
        var store = new MeetingStore(paths, mainThread) { TranscriptLanguage = () => AppLanguage.En, TranscriptTimeZone = Zone };
        var meeting = store.Create("Draft") with
        {
            Status = MeetingStatus.Transcribed,
            Segments = [Segment(0, 2, Speaker.Me, "Hello")],
        };
        store.Update(meeting);
        var path = store.TranscriptPath(meeting.Id);

        for (var attempt = 0; attempt < 60 && !File.Exists(path); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            mainThread.RunPending();
        }
        Assert.Contains("[00:00] Me: Hello", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);

        var past = AppleDate.FromReferenceSeconds(700_000_000);
        File.SetLastWriteTimeUtc(path, past.UtcDateTime);
        store.DeleteAudio(meeting.Id);
        store.Flush();
        Assert.False(store.Meeting(meeting.Id)?.HasAudio);
        Assert.Equal(past.UtcDateTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void DamagedRecordComesBackFromASecondFormatTranscriptWithNamesAndMoments()
    {
        var id = Guid.NewGuid();
        var folder = paths.MeetingFolder(id);
        Directory.CreateDirectory(folder);
        var document = Encoding.UTF8.GetBytes(TranscriptMerger.Markdown(CoworkRecord(), AppLanguage.Ru, timeZone: Zone));
        var transcriptPath = Path.Combine(folder, "transcript.md");
        File.WriteAllBytes(transcriptPath, document);
        File.WriteAllBytes(Path.Combine(folder, "meeting.json"), "{ broken"u8.ToArray());

        var store = new MeetingStore(paths, ImmediateMainThread.Shared) { TranscriptLanguage = () => AppLanguage.Ru, TranscriptTimeZone = Zone };

        var recovered = store.Meeting(id);
        Assert.NotNull(recovered);
        Assert.Equal("Коворк 15 сентября", recovered.Title);
        Assert.Equal(MeetingStatus.Transcribed, recovered.Status);
        Assert.False(recovered.HasAudio);
        Assert.Equal(1, recovered.FormatVersion);
        Assert.Equal(["Аня", null], recovered.Speakers.Select(s => s.DisplayName));
        Assert.Single(recovered.Highlights);
        var labels = TranscriptMerger.Lines(recovered).Select(line => TranscriptMerger.Label(line, recovered, "ru"));
        Assert.Equal(["Я", "Аня", "Спикер 3"], labels);
        Assert.Equal([0, 1, 0], TranscriptMerger.Lines(recovered).Select(line => line.HighlightIds.Count));
        // The transcript it was recovered from is kept.
        Assert.Equal(document, File.ReadAllBytes(transcriptPath));
        Assert.Equal(recovered, new MeetingStore(paths, ImmediateMainThread.Shared).Meeting(id));
    }

    // MARK: - Fixtures

    static readonly DateTimeOffset CreatedAt = AppleDate.FromReferenceSeconds(779_000_000);

    static readonly TranscriptSegment[] FirstFormatSegments =
    [
        Segment(0.5, 12.5, Speaker.Me, "Всем привет, начнём с планов на неделю."),
        Segment(13, 20, Speaker.Others, "Давайте к пятнице соберём прототип."),
        Segment(21, 31.25, Speaker.Me, "Договорились."),
    ];

    static TranscriptSegment Segment(double start, double end, Speaker speaker, string text, string? speakerId = null) =>
        new() { Start = start, End = end, Speaker = speaker, Text = text, SpeakerId = speakerId };

    /// <summary>
    /// An in-person meeting of 1:04:12 with «Я», a renamed speaker, a numbered one and a
    /// moment marked two seconds into Аня's line.
    /// </summary>
    static MeetingRecord CoworkRecord() => new()
    {
        Title = "Коворк 15 сентября",
        CreatedAt = CreatedAt,
        Duration = 3852,
        Status = MeetingStatus.Transcribed,
        HasSystemAudio = false,
        HasAudio = false,
        EngineId = EngineId.Parakeet,
        Segments =
        [
            Segment(4, 8, Speaker.Me, "Всем привет, начнём с планов на неделю.", "s1"),
            Segment(758, 765, Speaker.Me, "Давайте к пятнице соберём прототип и покажем его команде.", "s2"),
            Segment(800, 802, Speaker.Me, "Согласен.", "s3"),
        ],
        Speakers =
        [
            new MeetingSpeaker { Id = "s1", Track = MeetingTrack.Microphone, Number = 1, ColorIndex = 0, IsMe = true },
            new MeetingSpeaker { Id = "s2", Track = MeetingTrack.Microphone, Number = 2, DisplayName = "Аня", ColorIndex = 1 },
            new MeetingSpeaker { Id = "s3", Track = MeetingTrack.Microphone, Number = 3, ColorIndex = 2 },
        ],
        Highlights =
        [
            new MeetingHighlight
            {
                Time = 760,
                Note = "дедлайн",
                Source = HighlightSource.Recording,
                CreatedAt = AppleDate.FromReferenceSeconds(779_000_760),
            },
        ],
    };

    /// <summary>
    /// <c>DateFormatter</c> with <c>dateStyle</c> .long and <c>timeStyle</c> .short, as the macOS test
    /// builds it, in the fixed zone the documents are written in.
    /// </summary>
    static string DateText(AppLanguage language) => MeetingDates.LongDateShortTime(CreatedAt, language, Zone);

    /// <summary>Holds what the store posts to the main thread until the test runs it on its own thread.</summary>
    sealed class PumpedMainThread : IMainThread
    {
        readonly ConcurrentQueue<Action> queue = new();

        public bool IsMainThread => true;

        public void Post(Action action) => queue.Enqueue(action);

        public void RunPending()
        {
            while (queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
