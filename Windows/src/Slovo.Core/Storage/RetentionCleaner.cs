using System.Globalization;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Storage;

/// <summary>What one retention pass removed.</summary>
public sealed record RetentionReport(int RemovedEntries = 0, int RemovedDictationAudio = 0, int RemovedMeetingAudio = 0)
{
    public static readonly RetentionReport Nothing = new();
}

/// <summary>
/// Applies Data and privacy retention settings at launch and every 6 hours (port of the macOS
/// <c>RetentionCleaner</c>).
///
/// Ages are wall-clock ages of stored dates (a dictation from 8 days ago), read from the
/// <see cref="TimeProvider"/>; days are those of the <see cref="DayCalendar"/>. Use it from
/// the UI thread, like the stores it cleans.
/// </summary>
public sealed class RetentionCleaner : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>
    /// Recordings no entry refers to stay at least this long, even with "never", in case
    /// something is still about to add their entry.
    /// </summary>
    public static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromMinutes(5);

    static readonly Logger Logger = Log.For("Retention");

    readonly IHistoryStore history;
    readonly IMeetingStore meetings;
    readonly AppSettings settings;
    readonly string? recordingsFolder;
    readonly IMainThread mainThread;
    readonly TimeProvider time;
    readonly DayCalendar? calendar;
    ITimer? timer;

    /// <param name="recordingsFolder"><c>Recordings/</c>, swept for files no entry refers to; defaults to the folder of a <see cref="HistoryStore"/>.</param>
    /// <param name="calendar">The days recordings folders are named after; the machine's by default.</param>
    public RetentionCleaner(IHistoryStore history, IMeetingStore meetings, AppSettings settings, IMainThread mainThread,
                            TimeProvider? time = null, string? recordingsFolder = null, DayCalendar? calendar = null)
    {
        this.history = history;
        this.meetings = meetings;
        this.settings = settings;
        this.mainThread = mainThread;
        this.time = time ?? TimeProvider.System;
        this.recordingsFolder = recordingsFolder ?? (history as HistoryStore)?.Paths.RecordingsFolder;
        this.calendar = calendar;
    }

    /// <summary>Runs one pass now.</summary>
    public RetentionReport Run(DateTimeOffset? now = null)
    {
        var moment = now ?? time.GetUtcNow();

        var expiredEntries = ExpiredEntryIds(history.Entries, settings.KeepHistory, moment);
        foreach (var id in expiredEntries)
        {
            history.Delete(id);
        }

        var expiredAudio = ExpiredAudioIds(history.Entries, settings.KeepDictationAudio, moment);
        foreach (var id in expiredAudio)
        {
            history.RemoveAudio(id);
        }
        var orphans = RemoveOrphanedRecordings(moment);

        var keepMeetingAudio = settings.KeepMeetingAudio;
        var expiredMeetingAudio = ExpiredMeetingAudioIds(meetings.Meetings, keepMeetingAudio, moment);
        foreach (var id in expiredMeetingAudio)
        {
            // "Delete after transcription" is about the audio a transcript replaces, so a
            // screen recording stays; an age limit removes it with the audio.
            if (keepMeetingAudio == RetentionPeriod.AfterTranscription)
            {
                meetings.RemoveAudioTracks(id);
            }
            else
            {
                meetings.DeleteAudio(id);
            }
        }

        var report = new RetentionReport(expiredEntries.Count, expiredAudio.Count + orphans, expiredMeetingAudio.Count);
        if (report != RetentionReport.Nothing)
        {
            Logger.Info($"Retention removed {report.RemovedEntries} entries, {report.RemovedDictationAudio} dictation recordings, {report.RemovedMeetingAudio} meeting recordings");
            try
            {
                history.Flush();
                meetings.Flush();
            }
            catch (SlovoException error)
            {
                Logger.Error(error.Message);
            }
        }
        return report;
    }

    /// <summary>Runs a pass now and then every <see cref="Interval"/> until <see cref="Stop"/>; the passes run on the UI thread.</summary>
    public void Start()
    {
        Stop();
        Run();
        timer = time.CreateTimer(_ => mainThread.Post(() =>
        {
            if (timer is not null)
            {
                Run();
            }
        }), null, Interval, Interval);
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Deletes <c>Recordings/&lt;day&gt;/*.wav</c> files no entry refers to once they are older
    /// than dictation audio may be kept, then day folders left empty. Such files appear when a
    /// damaged <c>history.json</c> was moved aside, or when the app quit before a new entry was
    /// saved. A dictation still being transcribed is never among them: its audio stays in the
    /// temporary folder until its entry is added. Returns how many went.
    /// </summary>
    int RemoveOrphanedRecordings(DateTimeOffset now)
    {
        if (recordingsFolder is null || settings.KeepDictationAudio.MaximumAge() is not { } maximumAge || !Directory.Exists(recordingsFolder))
        {
            return 0;
        }
        var minimumAge = Math.Max(maximumAge, OrphanGracePeriod.TotalSeconds);
        // Entries store "<day>/<file>.wav"; comparing those names instead of full paths keeps
        // two spellings of one folder from looking different.
        var referenced = new HashSet<string>(history.Entries.Select(entry => entry.AudioFileName).OfType<string>(),
                                             OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var removed = 0;
        string[] days;
        try
        {
            days = Directory.GetDirectories(recordingsFolder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Could not list the recordings", error);
            return 0;
        }
        foreach (var day in days)
        {
            var dayName = Path.GetFileName(day);
            var dayEnd = EndOfDay(dayName, calendar);
            string[] files;
            try
            {
                files = Directory.GetFiles(day);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".wav", StringComparison.OrdinalIgnoreCase)
                    || referenced.Contains($"{dayName}/{Path.GetFileName(file)}"))
                {
                    continue;
                }
                DateTimeOffset? modified;
                try
                {
                    modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    modified = null;
                }
                // The folder is named after the day of the dictation, which survives a copy that
                // refreshes the modification date; the older of the two counts.
                var recorded = new[] { modified, dayEnd }.OfType<DateTimeOffset>().DefaultIfEmpty().Min();
                if (recorded == default || (now - recorded).TotalSeconds < minimumAge)
                {
                    continue;
                }
                try
                {
                    File.Delete(file);
                    removed += 1;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Logger.Error($"Could not remove {Path.GetFileName(file)}", error);
                }
            }
            HistoryStore.RemoveFolderIfEmpty(day, recordingsFolder);
        }
        return removed;
    }

    /// <summary>The end of a <c>yyyy-MM-dd</c> recordings day: no recording in that folder is newer.</summary>
    public static DateTimeOffset? EndOfDay(string name, DayCalendar? calendar = null)
    {
        if (name.Length != 10 || name[4] != '-' || name[7] != '-'
            || !int.TryParse(name.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(name.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(name.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || month is < 1 or > 12 || day < 1 || year < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }
        return (calendar ?? DayCalendar.Current).EndOfDay(new DateOnly(year, month, day));
    }

    /// <summary>Ids of entries older than the period allows.</summary>
    public static IReadOnlyList<Guid> ExpiredEntryIds(IEnumerable<HistoryEntry> entries, RetentionPeriod keep, DateTimeOffset now)
    {
        if (keep.MaximumAge() is not { } maximumAge)
        {
            return [];
        }
        return entries.Where(entry => IsExpired(entry.Date, maximumAge, now)).Select(entry => entry.Id).ToList();
    }

    /// <summary>Ids of entries whose audio file should be removed.</summary>
    public static IReadOnlyList<Guid> ExpiredAudioIds(IEnumerable<HistoryEntry> entries, RetentionPeriod keep, DateTimeOffset now)
    {
        if (keep.MaximumAge() is not { } maximumAge)
        {
            return [];
        }
        return entries
            .Where(entry => entry.AudioFileName is not null && IsExpired(entry.Date, maximumAge, now))
            .Select(entry => entry.Id)
            .ToList();
    }

    /// <summary>
    /// Ids of meetings whose audio should be removed.
    ///
    /// Only the audio of a transcribed meeting is removed: a meeting that was never
    /// transcribed (still recording or transcribing, recorded and waiting, or failed) has
    /// nothing else, and removing its audio would leave an empty card. AfterTranscription
    /// removes it once a transcript exists, whatever its age. An age is counted from
    /// AudioStoredAt, the moment the audio became this computer's to keep, and only from
    /// CreatedAt when there is none: a clip recorded on a phone a week ago carries that week in
    /// CreatedAt, and counting it would delete the audio of a meeting collected minutes ago.
    /// </summary>
    public static IReadOnlyList<Guid> ExpiredMeetingAudioIds(IEnumerable<MeetingRecord> meetings, RetentionPeriod keep, DateTimeOffset now) =>
        meetings.Where(meeting =>
        {
            if (!meeting.HasAudio || meeting.Status != MeetingStatus.Transcribed)
            {
                return false;
            }
            if (keep == RetentionPeriod.AfterTranscription)
            {
                return true;
            }
            return keep.MaximumAge() is { } maximumAge && IsExpired(meeting.AudioStoredAt ?? meeting.CreatedAt, maximumAge, now);
        })
        .Select(meeting => meeting.Id)
        .ToList();

    /// <summary>An item is expired once it is at least the maximum age old; a zero age (Never) expires everything up to now.</summary>
    static bool IsExpired(DateTimeOffset date, double maximumAge, DateTimeOffset now) => (now - date).TotalSeconds >= maximumAge;
}
