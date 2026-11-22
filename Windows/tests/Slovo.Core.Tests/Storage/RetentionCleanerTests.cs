using Slovo.Core.Audio;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Core.Storage;

namespace Slovo.Core.Tests.Storage;

/// <summary>Retention of InputRetentionInsightsTests and FixCoreStorageTests, with an injected clock and time zone.</summary>
public sealed class RetentionCleanerTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000); // 2026-09-10 00:26:40 UTC
    const double Day = 86_400;

    readonly TemporaryFolder folder = new();
    readonly ManualTimeProvider clock = new(Now);
    readonly AppSettings settings = new(SettingsStore.InMemory());
    readonly DayCalendar calendar = new(TimeZoneInfo.Local, DayOfWeek.Monday);

    public void Dispose() => folder.Dispose();

    HistoryStore History() => new(folder.Paths, ImmediateMainThread.Shared, clock);

    RetentionCleaner Cleaner(IHistoryStore history, IMeetingStore? meetings = null) =>
        new(history, meetings ?? new FakeMeetingStore([]), settings, ImmediateMainThread.Shared, clock, calendar: calendar);

    static HistoryEntry Entry(double age = 0, string? audio = null) => new()
    {
        Date = Now.AddSeconds(-age), Text = "text", RawText = "text", EngineId = EngineId.Parakeet, LanguageMode = LanguageMode.English,
        AppBundleId = "notepad.exe", AppName = "Notepad", Duration = 2, WordCount = 5, AudioFileName = audio,
    };

    static MeetingRecord Meeting(double age, MeetingStatus status, bool hasAudio = true, double? stored = null) => new()
    {
        Title = "Sync", CreatedAt = Now.AddSeconds(-age), Duration = 600, Status = status, HasAudio = hasAudio,
        AudioStoredAt = stored is { } seconds ? Now.AddSeconds(-seconds) : null,
    };

    // MARK: - Helpers

    [Fact]
    public void ExpiredEntries()
    {
        var fresh = Entry(3600);
        var week = Entry(7 * Day);
        var old = Entry(40 * Day);
        HistoryEntry[] entries = [fresh, week, old];
        Assert.Empty(RetentionCleaner.ExpiredEntryIds(entries, RetentionPeriod.Forever, Now));
        Assert.Equal([old.Id], RetentionCleaner.ExpiredEntryIds(entries, RetentionPeriod.Days30, Now));
        Assert.Equal([week.Id, old.Id], RetentionCleaner.ExpiredEntryIds(entries, RetentionPeriod.Days7, Now));
        Assert.Equal([week.Id, old.Id], RetentionCleaner.ExpiredEntryIds(entries, RetentionPeriod.Hours24, Now));
        Assert.Empty(RetentionCleaner.ExpiredEntryIds([Entry(23 * 3600)], RetentionPeriod.Hours24, Now));
    }

    [Fact]
    public void ExpiredAudioOnlyCountsEntriesWithAudio()
    {
        var fresh = Entry(60, "a.wav");
        var old = Entry(8 * Day, "b.wav");
        var oldWithoutAudio = Entry(8 * Day);
        HistoryEntry[] entries = [fresh, old, oldWithoutAudio];
        Assert.Equal([old.Id], RetentionCleaner.ExpiredAudioIds(entries, RetentionPeriod.Days7, Now));
        Assert.Equal([fresh.Id, old.Id], RetentionCleaner.ExpiredAudioIds(entries, RetentionPeriod.Never, Now));
        Assert.Empty(RetentionCleaner.ExpiredAudioIds(entries, RetentionPeriod.Days30, Now));
        Assert.Empty(RetentionCleaner.ExpiredAudioIds(entries, RetentionPeriod.Forever, Now));
    }

    [Fact]
    public void OnlyTranscribedMeetingsLoseTheirAudio()
    {
        var transcribedOld = Meeting(40 * Day, MeetingStatus.Transcribed);
        var transcribedNew = Meeting(Day, MeetingStatus.Transcribed);
        var recordedOld = Meeting(40 * Day, MeetingStatus.Recorded);
        var recording = Meeting(40 * Day, MeetingStatus.Recording);
        var transcribing = Meeting(40 * Day, MeetingStatus.Transcribing);
        var failedOld = Meeting(40 * Day, MeetingStatus.Failed);
        var noAudio = Meeting(40 * Day, MeetingStatus.Transcribed, hasAudio: false);
        MeetingRecord[] all = [transcribedOld, transcribedNew, recordedOld, recording, transcribing, failedOld, noAudio];

        Assert.Empty(RetentionCleaner.ExpiredMeetingAudioIds(all, RetentionPeriod.Forever, Now));
        // A meeting never transcribed keeps its audio whatever its age: it has nothing else.
        Assert.Equal([transcribedOld.Id], RetentionCleaner.ExpiredMeetingAudioIds(all, RetentionPeriod.Days30, Now));
        Assert.Empty(RetentionCleaner.ExpiredMeetingAudioIds([recordedOld, failedOld, recording, transcribing], RetentionPeriod.Days7, Now));
        // "After transcription" never removes the audio of a meeting without a transcript.
        Assert.Equal([transcribedOld.Id, transcribedNew.Id], RetentionCleaner.ExpiredMeetingAudioIds(all, RetentionPeriod.AfterTranscription, Now));
    }

    [Fact]
    public void AfterTranscriptionKeepsTheScreenRecordingAndAnAgeLimitRemovesItWithTheAudio()
    {
        var screen = Meeting(40 * Day, MeetingStatus.Transcribed) with { HasScreenRecording = true };
        var meetings = new FakeMeetingStore([screen]);
        settings.KeepMeetingAudio = RetentionPeriod.AfterTranscription;
        Cleaner(History(), meetings).Run();
        // "Delete after transcription" is about the audio a transcript replaces.
        Assert.Empty(meetings.DeletedAudio);
        Assert.False(meetings.Meeting(screen.Id)!.HasAudio);
        Assert.True(meetings.Meeting(screen.Id)!.HasScreenRecording);

        var older = Meeting(40 * Day, MeetingStatus.Transcribed) with { HasScreenRecording = true };
        var aged = new FakeMeetingStore([older]);
        settings.KeepMeetingAudio = RetentionPeriod.Days30;
        Cleaner(History(), aged).Run();
        Assert.Equal([older.Id], aged.DeletedAudio);
        Assert.False(aged.Meeting(older.Id)!.HasScreenRecording);
    }

    [Fact]
    public void MeetingAudioAgesFromWhenItBecameThisComputersToKeep()
    {
        // Recorded on a phone eight days ago and collected a minute ago.
        var collected = Meeting(8 * Day, MeetingStatus.Transcribed, stored: 60);
        var recordedHere = Meeting(8 * Day, MeetingStatus.Transcribed);
        var collectedLongAgo = Meeting(40 * Day, MeetingStatus.Transcribed, stored: 8 * Day);
        MeetingRecord[] all = [collected, recordedHere, collectedLongAgo];

        Assert.Equal([recordedHere.Id, collectedLongAgo.Id], RetentionCleaner.ExpiredMeetingAudioIds(all, RetentionPeriod.Days7, Now));
        Assert.Empty(RetentionCleaner.ExpiredMeetingAudioIds(all, RetentionPeriod.Days30, Now));
        Assert.Equal(all.Select(m => m.Id), RetentionCleaner.ExpiredMeetingAudioIds(all, RetentionPeriod.AfterTranscription, Now));
    }

    // MARK: - A pass

    [Fact]
    public void RunAppliesSettingsToStores()
    {
        var history = History();
        settings.KeepHistory = RetentionPeriod.Days30;
        settings.KeepDictationAudio = RetentionPeriod.Days7;
        settings.KeepMeetingAudio = RetentionPeriod.AfterTranscription;

        var ancient = Entry(45 * Day, "2026-07-27/ancient.wav");
        var lastWeek = Entry(10 * Day, "2026-08-31/week.wav");
        var today = Entry(3600, "2026-09-09/today.wav");
        foreach (var item in new[] { ancient, lastWeek, today })
        {
            var path = folder.Paths.RecordingPath(item.AudioFileName!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetLastWriteTimeUtc(path, item.Date.UtcDateTime);
            history.Add(item);
        }
        var transcribed = Meeting(Day, MeetingStatus.Transcribed);
        var stillRecording = Meeting(Day, MeetingStatus.Recording);
        var meetings = new FakeMeetingStore([transcribed, stillRecording]);

        var cleaner = Cleaner(history, meetings);
        var report = cleaner.Run();
        Assert.Equal(new RetentionReport(1, 1, 1), report);
        Assert.Equal([today.Id, lastWeek.Id], history.Entries.Select(e => e.Id));
        Assert.Null(history.Entries.Single(e => e.Id == lastWeek.Id).AudioFileName);
        Assert.Equal(today.AudioFileName, history.Entries.Single(e => e.Id == today.Id).AudioFileName);
        Assert.False(File.Exists(folder.Paths.RecordingPath("2026-07-27/ancient.wav")));
        Assert.False(File.Exists(folder.Paths.RecordingPath("2026-08-31/week.wav")));
        Assert.True(File.Exists(folder.Paths.RecordingPath("2026-09-09/today.wav")));
        // "After transcription" removes the tracks and leaves a screen recording in place.
        Assert.Empty(meetings.DeletedAudio);
        Assert.False(meetings.Meeting(transcribed.Id)!.HasAudio);
        Assert.True(meetings.Meeting(stillRecording.Id)!.HasAudio);
        Assert.Equal(2, History().Entries.Count);
        Assert.Equal(1, meetings.Flushes);

        Assert.Equal(RetentionReport.Nothing, cleaner.Run());
    }

    [Fact]
    public void StartRunsNowAndEverySixHours()
    {
        var history = History();
        settings.KeepHistory = RetentionPeriod.Hours24;
        history.Add(Entry(2 * Day));
        var cleaner = Cleaner(history);
        cleaner.Start();
        Assert.Empty(history.Entries);

        history.Add(Entry(20 * 3600));
        clock.Advance(TimeSpan.FromHours(5));
        Assert.Single(history.Entries);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(history.Entries);

        cleaner.Stop();
        history.Add(Entry(30 * 3600));
        clock.Advance(TimeSpan.FromHours(12));
        Assert.Single(history.Entries);
        Assert.Equal(0, clock.ActiveTimers);
    }

    // MARK: - Recordings without an entry (FixCoreStorageTests)

    /// <summary>Writes a recording as dictation leaves it: in the folder of its day, modified then.</summary>
    (string Path, string RelativePath) WriteRecording(double daysAgo, DateTimeOffset? now = null)
    {
        var date = (now ?? Now).AddSeconds(-daysAgo * Day);
        var relative = WavFile.RelativePath(Guid.NewGuid(), date);
        var path = folder.Paths.RecordingPath(relative);
        WavFile.Write(new RecordedAudio(new float[8_000]), path);
        File.SetLastWriteTimeUtc(path, date.UtcDateTime);
        return (path, relative);
    }

    [Fact]
    public void AudioOfAMovedAsideHistoryIsStillSubjectToRetention()
    {
        var history = History();
        var id = Guid.NewGuid();
        var date = Now.AddDays(-40);
        var relative = WavFile.RelativePath(id, date);
        var wav = folder.Paths.RecordingPath(relative);
        WavFile.Write(new RecordedAudio(new float[16_000]), wav);
        history.Add(Entry(40 * Day, relative) with { Id = id });
        history.Flush();
        // history.json gets damaged: the next launch moves it aside and starts empty.
        File.WriteAllText(folder.Paths.HistoryFile, "{ broken");
        var reopened = History();
        Assert.NotNull(reopened.LoadWarning);

        settings.KeepDictationAudio = RetentionPeriod.Never;
        settings.KeepHistory = RetentionPeriod.Days7;
        var report = Cleaner(reopened).Run();
        // The file was written just now, but its day folder says it is 40 days old.
        Assert.False(File.Exists(wav));
        Assert.Equal(1, report.RemovedDictationAudio);
        Assert.False(Directory.Exists(Path.GetDirectoryName(wav)), "the emptied day folder goes too");
    }

    [Fact]
    public void UnreferencedRecordingsFollowTheAudioPeriod()
    {
        var history = History();
        var recent = WriteRecording(3);
        var old = WriteRecording(8);
        // A referenced recording of the same age is handled through its entry.
        var kept = WriteRecording(8.2);
        history.Add(Entry(60, kept.RelativePath));

        settings.KeepDictationAudio = RetentionPeriod.Days7;
        var report = Cleaner(history).Run();
        Assert.Equal(1, report.RemovedDictationAudio);
        Assert.True(File.Exists(recent.Path));
        Assert.False(File.Exists(old.Path));
        Assert.True(File.Exists(kept.Path), "an entry still refers to it");
    }

    [Fact]
    public void KeepForeverKeepsUnreferencedRecordings()
    {
        var old = WriteRecording(400);
        settings.KeepDictationAudio = RetentionPeriod.Forever;
        Assert.Equal(0, Cleaner(History()).Run().RemovedDictationAudio);
        Assert.True(File.Exists(old.Path));
    }

    /// <summary>With "never", a recording nobody refers to still gets a few minutes, in case its entry is about to be added.</summary>
    [Fact]
    public void NeverGivesAFreshRecordingAGracePeriod()
    {
        var fresh = WriteRecording(0);
        settings.KeepDictationAudio = RetentionPeriod.Never;
        var history = History();

        Assert.Equal(0, Cleaner(history).Run(Now.AddSeconds(60)).RemovedDictationAudio);
        Assert.True(File.Exists(fresh.Path));

        var later = Now + RetentionCleaner.OrphanGracePeriod + TimeSpan.FromSeconds(1);
        Assert.Equal(1, Cleaner(history).Run(later).RemovedDictationAudio);
        Assert.False(File.Exists(fresh.Path));
    }

    [Fact]
    public void OtherFilesInRecordingsAreLeftAlone()
    {
        var old = WriteRecording(30);
        var note = Path.Combine(Path.GetDirectoryName(old.Path)!, "notes.txt");
        File.WriteAllText(note, "keep");
        settings.KeepDictationAudio = RetentionPeriod.Days7;

        Assert.Equal(1, Cleaner(History()).Run().RemovedDictationAudio);
        Assert.True(File.Exists(note));
        Assert.True(Directory.Exists(Path.GetDirectoryName(old.Path)), "a day folder with other files stays");
    }

    [Fact]
    public void DayFolderNames()
    {
        var moscow = new DayCalendar(TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"), DayOfWeek.Monday);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.FromHours(3)), RetentionCleaner.EndOfDay("2026-09-15", moscow));
        foreach (var name in new[] { "2026-9-15", "2026-13-01", "2026-09-32", "2026-02-30", "notes", "2026-09-15-x", "", "+026-09-15" })
        {
            Assert.Null(RetentionCleaner.EndOfDay(name, moscow));
        }
    }

    // MARK: - Daylight saving time

    [Fact]
    public void AgesAreRealDurationsAcrossADaylightSavingChange()
    {
        // Berlin moves its clocks back on 2026-10-25: that calendar day has 25 hours.
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var now = new DateTimeOffset(2026, 10, 26, 0, 30, 0, TimeSpan.FromHours(1));
        var sameClockYesterday = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromHours(1), berlin.GetUtcOffset(now));
        Assert.Equal(TimeSpan.FromHours(2), berlin.GetUtcOffset(sameClockYesterday));
        Assert.Equal(TimeSpan.FromHours(25), now - sameClockYesterday);
        // "24 hours" is 24 hours of real time, not "since this time yesterday".
        HistoryEntry[] entries =
        [
            Entry() with { Date = now.AddHours(-23.99) },
            Entry() with { Date = now.AddHours(-24) },
            Entry() with { Date = sameClockYesterday.AddHours(0.5) },
        ];
        Assert.Equal([entries[1].Id, entries[2].Id], RetentionCleaner.ExpiredEntryIds(entries, RetentionPeriod.Hours24, now));
    }

    [Fact]
    public void DayFoldersEndAtTheNextLocalMidnightAcrossDaylightSavingChanges()
    {
        // Santiago skips midnight on 2026-09-06: that day starts at 01:00 and has 23 hours.
        var santiago = new DayCalendar(TimeZoneInfo.FindSystemTimeZoneById("America/Santiago"), DayOfWeek.Monday);
        var endOfFifth = RetentionCleaner.EndOfDay("2026-09-05", santiago)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.FromHours(-3)), endOfFifth);
        var endOfSixth = RetentionCleaner.EndOfDay("2026-09-06", santiago)!.Value;
        Assert.Equal(TimeSpan.FromHours(23), endOfSixth - endOfFifth);

        // Berlin's day of the October change has 25 hours.
        var berlin = new DayCalendar(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), DayOfWeek.Monday);
        Assert.Equal(TimeSpan.FromHours(25), RetentionCleaner.EndOfDay("2026-10-25", berlin)!.Value - RetentionCleaner.EndOfDay("2026-10-24", berlin)!.Value);
    }

    [Fact]
    public void AnOrphanFromTheDayBeforeAChangeExpiresOnTime()
    {
        // A recording from Berlin's 25-hour day, copied later (a fresh modification time):
        // its folder's end counts, 7 days of real time after it.
        var berlin = new DayCalendar(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), DayOfWeek.Monday);
        var relative = $"2026-10-25/{Guid.NewGuid().ToString("D").ToUpperInvariant()}.wav";
        var path = folder.Paths.RecordingPath(relative);
        WavFile.Write(new RecordedAudio(new float[100]), path);
        var dayEnd = RetentionCleaner.EndOfDay("2026-10-25", berlin)!.Value;
        File.SetLastWriteTimeUtc(path, dayEnd.AddDays(30).UtcDateTime);
        settings.KeepDictationAudio = RetentionPeriod.Days7;
        var cleaner = new RetentionCleaner(History(), new FakeMeetingStore([]), settings, ImmediateMainThread.Shared, clock, calendar: berlin);

        Assert.Equal(0, cleaner.Run(dayEnd.AddDays(7).AddSeconds(-1)).RemovedDictationAudio);
        Assert.True(File.Exists(path));
        Assert.Equal(1, cleaner.Run(dayEnd.AddDays(7)).RemovedDictationAudio);
        Assert.False(File.Exists(path));
    }
}
