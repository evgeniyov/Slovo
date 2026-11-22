using System.Text;
using System.Text.Json;
using Slovo.Core.Audio;
using Slovo.Core.Json;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Storage;

namespace Slovo.Core.Meetings;

/// <summary>
/// <c>Meetings/&lt;id&gt;/</c>: <c>meeting.json</c>, <c>mic.caf</c>, <c>system.caf</c>,
/// <c>screen.mov</c> (macOS only), <c>transcript.md</c> (port of the macOS <c>MeetingStore</c>;
/// a folder written by either app opens in the other).
///
/// Each meeting keeps its own JSON file, so a damaged file costs one meeting's metadata
/// instead of the whole list, and a meeting's audio stays next to it. Used from the UI
/// thread; changes are saved 500 ms after the last one (debounced through
/// <see cref="IMainThread"/>), or at once by <see cref="Flush"/>.
/// </summary>
public sealed class MeetingStore : Observable, IMeetingStore
{
    public const string RecordFileName = MeetingRecordFile.FileName;
    public const string TranscriptFileName = "transcript.md";
    public static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromMilliseconds(500);

    static readonly Logger Logger = Log.For("MeetingStore");
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    readonly IMainThread mainThread;
    readonly TimeProvider time;
    readonly HashSet<Guid> dirty = [];
    /// <summary>The meeting.json of each meeting as <see cref="RefreshFromDisk"/> last read it.</summary>
    readonly Dictionary<Guid, RecordStamp> readRecords = [];
    /// <summary>
    /// The highlight ids of each meeting the last time this store and its file agreed on
    /// them: after a load, a save, or <see cref="RefreshFromDisk"/>. Saves and refreshes
    /// compare the file against this before touching the other side's highlights, so an MCP
    /// mark or unmark and an unrelated app-side edit both survive.
    /// </summary>
    readonly Dictionary<Guid, HashSet<Guid>> knownHighlightIds = [];
    IReadOnlyList<MeetingRecord> meetings = [];
    ITimer? pendingSave;
    long saveGeneration;

    /// <summary>Reads every <c>meeting.json</c> under the meetings folder, repairing and recovering what it can.</summary>
    public MeetingStore(AppPaths paths, IMainThread mainThread, TimeProvider? timeProvider = null, TimeSpan? saveDelay = null)
    {
        Paths = paths;
        this.mainThread = mainThread;
        time = timeProvider ?? TimeProvider.System;
        SaveDelay = saveDelay ?? DefaultSaveDelay;
        var loaded = LoadAll();
        foreach (var record in loaded)
        {
            knownHighlightIds[record.Id] = record.Highlights.Select(h => h.Id).ToHashSet();
        }
        meetings = Sorted(loaded);
    }

    public AppPaths Paths { get; }

    public TimeSpan SaveDelay { get; }

    /// <summary>
    /// The language <c>transcript.md</c> is written in; null, the default, means the language
    /// Slovo shows (the saved interface language). Tests pin one.
    /// </summary>
    public Func<AppLanguage?> TranscriptLanguage { get; set; } = () => null;

    /// <summary>The time zone dates in <c>transcript.md</c> are written in; null means the computer's.</summary>
    public TimeZoneInfo? TranscriptTimeZone { get; set; }

    /// <summary>How long a save waits for the meeting's lock before it saves without it; tests shorten it.</summary>
    internal TimeSpan LockTimeout { get; set; } = MeetingFileLock.DefaultTimeout;

    /// <summary>Newest first; ties keep a stable order by id.</summary>
    public IReadOnlyList<MeetingRecord> Meetings => meetings;

    public MeetingRecord? Meeting(Guid id) => meetings.FirstOrDefault(m => m.Id == id);

    /// <summary>Creates the meeting folder and its meeting.json with status Recording. Throws SlovoException.</summary>
    public MeetingRecord Create(string title)
    {
        var now = StorableNow();
        var trimmed = TranscriptMerger.NormalizedText(title);
        var record = new MeetingRecord
        {
            Title = trimmed.Length == 0 ? MeetingTitles.DefaultTitle(now) : trimmed,
            CreatedAt = now,
            Status = MeetingStatus.Recording,
        };
        try
        {
            Directory.CreateDirectory(Folder(record.Id));
            Write(record);
        }
        catch (Exception error) when (IsFileError(error))
        {
            RemoveItem(Folder(record.Id));
            throw MeetingErrors.CreateFailed(error);
        }
        SetMeetings(Sorted([.. meetings, record]));
        return record;
    }

    public void Update(MeetingRecord meeting)
    {
        var index = IndexOf(meeting.Id);
        if (index < 0 || meetings[index].Equals(meeting))
        {
            return;
        }
        var reorder = meetings[index].CreatedAt != meeting.CreatedAt;
        var list = meetings.ToList();
        list[index] = meeting;
        SetMeetings(reorder ? Sorted(list) : list);
        MarkDirty(meeting.Id);
    }

    /// <summary>Removes the whole meeting folder.</summary>
    public void Delete(Guid id)
    {
        SetMeetings(meetings.Where(m => m.Id != id).ToList());
        dirty.Remove(id);
        readRecords.Remove(id);
        knownHighlightIds.Remove(id);
        RemoveItem(Folder(id));
    }

    /// <summary>Removes the audio tracks and the screen recording, and sets HasAudio and HasScreenRecording to false.</summary>
    public void DeleteAudio(Guid id)
    {
        RemoveItem(MicrophoneAudioPath(id));
        RemoveItem(SystemAudioPath(id));
        RemoveItem(ScreenRecordingPath(id));
        var index = IndexOf(id);
        if (index < 0)
        {
            return;
        }
        var meeting = meetings[index];
        if (!meeting.HasAudio && !meeting.HasScreenRecording)
        {
            return;
        }
        ReplaceAt(index, meeting with { HasAudio = false, HasScreenRecording = false });
        MarkDirty(id);
    }

    /// <summary>
    /// Removes <c>mic.caf</c> and <c>system.caf</c> and sets HasAudio to false, leaving
    /// <c>screen.mov</c> in place. "Keep meeting audio: delete after transcription" is about
    /// the audio a transcript replaces; a screen recording has nothing that replaces it, so it
    /// stays until the meeting is deleted or an age limit removes it with <see cref="DeleteAudio"/>.
    /// </summary>
    public void RemoveAudioTracks(Guid id) => MeetingStoreExtensions.RemoveAudioTracks(this, id);

    public string MeetingsFolder => Paths.MeetingsFolder;

    public string Folder(Guid id) => Paths.MeetingFolder(id);

    public string MicrophoneAudioPath(Guid id) => Path.Combine(Folder(id), MeetingAudioFormat.MicrophoneFileName);

    public string SystemAudioPath(Guid id) => Path.Combine(Folder(id), MeetingAudioFormat.SystemFileName);

    public string ScreenRecordingPath(Guid id) => Path.Combine(Folder(id), MeetingAudioFormat.ScreenFileName);

    public string TranscriptPath(Guid id) => Path.Combine(Folder(id), TranscriptFileName);

    public string RecordPath(Guid id) => Path.Combine(Folder(id), RecordFileName);

    /// <summary>
    /// Writes pending changes now instead of after the debounce, <c>transcript.md</c> of
    /// transcribed meetings included. Throws SlovoException (<c>meetings.error.saveFailed</c>)
    /// when a meeting could not be saved; it stays pending.
    /// </summary>
    public void Flush()
    {
        CancelPendingSave();
        SaveDirty();
    }

    // MARK: - Saving

    void MarkDirty(Guid id)
    {
        dirty.Add(id);
        CancelPendingSave();
        var generation = ++saveGeneration;
        pendingSave = time.CreateTimer(_ => mainThread.Post(() => SaveAfterDelay(generation)), null, SaveDelay, Timeout.InfiniteTimeSpan);
    }

    void SaveAfterDelay(long generation)
    {
        if (generation != saveGeneration)
        {
            return;
        }
        CancelPendingSave();
        try
        {
            SaveDirty();
        }
        catch (SlovoException error)
        {
            Logger.Error($"Saving meetings failed: {error.Message}");
        }
    }

    void CancelPendingSave()
    {
        pendingSave?.Dispose();
        pendingSave = null;
        saveGeneration++;
    }

    void SaveDirty()
    {
        Exception? firstError = null;
        foreach (var id in dirty.OrderBy(id => id.ToUpperString(), StringComparer.Ordinal).ToList())
        {
            var index = IndexOf(id);
            if (index < 0)
            {
                dirty.Remove(id);
                continue;
            }
            try
            {
                // The document is written from the record the file now holds, so saving a
                // rename or a star cannot drop Claude's notes from transcript.md while
                // meeting.json keeps them.
                var record = meetings[index];
                var stored = Write(record);
                // The meeting in memory follows the file for the same reason: without this a
                // rename made in a session that has not refreshed yet would leave the card
                // without Claude's notes, or without a star the MCP server just placed.
                if (!Equals(record.ClaudeNotes, stored.ClaudeNotes) || !record.Highlights.SequenceEqual(stored.Highlights))
                {
                    ReplaceAt(index, record with { ClaudeNotes = stored.ClaudeNotes, Highlights = stored.Highlights });
                }
                WriteTranscript(stored);
                dirty.Remove(id);
            }
            catch (Exception error) when (IsFileError(error))
            {
                firstError ??= MeetingErrors.SaveFailed(error);
            }
        }
        if (firstError is not null)
        {
            throw firstError;
        }
    }

    /// <summary>
    /// Writes to a temporary file and replaces the old one, so a crash never leaves half a
    /// file behind. Returns the record the file now holds, which is what every document
    /// derived from it has to be written from.
    ///
    /// What Claude wrote is taken from the file rather than from the record in memory: the
    /// MCP server writes those notes from its own process, and saving a rename here must not
    /// throw them away. The app changes them only through <see cref="SetClaudeNotes"/>, which
    /// puts them in the file first.
    ///
    /// Highlights are different: the app edits them too, so neither side's copy simply wins.
    /// <see cref="ReconciledHighlights"/> folds in whatever the MCP server marked or unmarked
    /// since the ids were last agreed, on top of this save's own highlights.
    /// </summary>
    MeetingRecord Write(MeetingRecord record)
    {
        var path = RecordPath(record.Id);
        var folder = Folder(record.Id);
        Directory.CreateDirectory(folder);
        // The MCP server changes this file from its own process under the same lock, so its
        // notes or marks cannot land between this read and this write and be undone by it.
        var stored = MeetingFileLock.WithLockIfPossible(folder, () =>
        {
            var onDisk = StoredRecord(record.Id);
            var result = record with
            {
                ClaudeNotes = onDisk?.ClaudeNotes,
                Highlights = ReconciledHighlights(record.Highlights, onDisk?.Highlights ?? [], knownHighlightIds.GetValueOrDefault(record.Id)),
            };
            AtomicFile.Write(path, MeetingRecordFile.Encode(result));
            return result;
        }, LockTimeout);
        knownHighlightIds[record.Id] = stored.Highlights.Select(h => h.Id).ToHashSet();
        return stored;
    }

    /// <summary>The record the meeting's file holds now; null when there is no readable file yet.</summary>
    MeetingRecord? StoredRecord(Guid id)
    {
        try
        {
            return MeetingRecordFile.Decode(File.ReadAllBytes(RecordPath(id)));
        }
        catch (Exception error) when (IsFileError(error) || error is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges <paramref name="ours"/> with what the file holds now (<paramref name="disk"/>),
    /// given the highlight ids both sides last agreed on (<paramref name="knownIds"/>; null, as
    /// for a meeting never read before, falls back to treating <paramref name="disk"/> itself
    /// as that baseline, which is exactly right for a file that does not exist yet).
    ///
    /// A highlight that appeared on disk since the baseline is added even where ours does not
    /// have it: the MCP server marked it from its own process. One that vanished from disk is
    /// removed even where ours still has it: the MCP server unmarked it. Anything neither side
    /// touched relative to the baseline is left exactly as ours has it, so an edit already
    /// sitting in ours (a star just added or removed by the app itself, a note just changed)
    /// survives.
    /// </summary>
    internal static IReadOnlyList<MeetingHighlight> ReconciledHighlights(IReadOnlyList<MeetingHighlight> ours, IReadOnlyList<MeetingHighlight> disk,
                                                                          IReadOnlySet<Guid>? knownIds)
    {
        var diskIds = disk.Select(h => h.Id).ToHashSet();
        var known = knownIds ?? diskIds;
        var removedFromDisk = known.Where(id => !diskIds.Contains(id)).ToHashSet();
        var addedOnDisk = disk.Where(h => !known.Contains(h.Id));
        var merged = ours.Where(h => !removedFromDisk.Contains(h.Id)).ToList();
        var alreadyPresent = merged.Select(h => h.Id).ToHashSet();
        merged.AddRange(addedOnDisk.Where(h => !alreadyPresent.Contains(h.Id)));
        return merged;
    }

    /// <summary>
    /// Replaces or removes what Claude wrote about a meeting. The file owns these notes, so the
    /// change reaches it first and the meeting in memory follows; the save that follows brings
    /// <c>transcript.md</c> in step.
    /// </summary>
    public void SetClaudeNotes(MeetingNotes? notes, Guid id)
    {
        if (IndexOf(id) < 0)
        {
            return;
        }
        MeetingNotes? stored;
        try
        {
            stored = new MeetingNotesFile(Paths).Write(notes, id);
        }
        catch (MeetingRecordFileException error)
        {
            Logger.Error($"Changing Claude's notes failed: {error.Failure}");
            return;
        }
        var index = IndexOf(id);
        ReplaceAt(index, meetings[index] with { ClaudeNotes = stored });
        MarkDirty(id);
    }

    /// <summary>
    /// Re-reads what Claude wrote and marked about the meetings already loaded (the macOS
    /// <c>refreshClaudeNotes</c>).
    ///
    /// The file owns the notes, and holds highlights the MCP server may have marked or
    /// unmarked, both from a process of its own that tells the running app nothing. Slovo
    /// stays running for days, so without this the meeting card would show them only after a
    /// relaunch or an unrelated save. <c>transcript.md</c> is derived from the record and is
    /// brought in step here; <c>meeting.json</c> is left alone, because it is the file this
    /// just came from. A meeting whose file cannot be read keeps what it has.
    ///
    /// This runs every time the app is activated, so it stays cheap: a meeting whose file has
    /// not changed since it was last read costs one file-system query. Only a file with a new
    /// size or modification or creation time is decoded again.
    /// </summary>
    public void RefreshFromDisk()
    {
        var list = meetings.ToList();
        var changed = false;
        for (var index = 0; index < list.Count; index++)
        {
            var id = list[index].Id;
            if (RecordStampOf(id) is not { } stamp || (readRecords.TryGetValue(id, out var previous) && previous == stamp))
            {
                continue;
            }
            // Remembered before decoding, so a file that cannot be decoded is not read again
            // either until it changes.
            readRecords[id] = stamp;
            if (StoredRecord(id) is not { } stored)
            {
                continue;
            }
            var highlights = ReconciledHighlights(list[index].Highlights, stored.Highlights, knownHighlightIds.GetValueOrDefault(id));
            // The baseline is what the file holds, not the merge: a star or an unstar the app
            // has not saved yet is not in the file, and taking it for agreed would make the next
            // save read its absence there as the server's unmark, and drop it.
            knownHighlightIds[id] = stored.Highlights.Select(h => h.Id).ToHashSet();
            if (Equals(list[index].ClaudeNotes, stored.ClaudeNotes) && list[index].Highlights.SequenceEqual(highlights))
            {
                continue;
            }
            list[index] = list[index] with { ClaudeNotes = stored.ClaudeNotes, Highlights = highlights };
            changed = true;
            try
            {
                WriteTranscript(list[index]);
            }
            catch (Exception error) when (IsFileError(error))
            {
                Logger.Error("Saving the transcript after Claude's notes or highlights changed failed", error);
            }
        }
        if (changed)
        {
            SetMeetings(list);
        }
    }

    /// <summary>
    /// What identifies the version of a meeting's record file: its size and its modification
    /// and creation times. Null when there is no plain file there or it is larger than a
    /// record can sensibly be, which keeps a huge file from being decoded on every activation.
    /// Symbolic links are not followed.
    /// </summary>
    RecordStamp? RecordStampOf(Guid id)
    {
        try
        {
            var info = new FileInfo(RecordPath(id));
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                || info.Length > MeetingRecordFile.MaximumFileBytes)
            {
                return null;
            }
            return new RecordStamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
        }
        catch (Exception error) when (IsFileError(error))
        {
            return null;
        }
    }

    readonly record struct RecordStamp(long Size, long ModifiedTicks, long CreatedTicks);

    /// <summary>
    /// Keeps <c>transcript.md</c> in step with every save of a transcribed meeting, so renames,
    /// merges, stars and title changes reach the file that Export, Explorer and a lost
    /// <c>meeting.json</c> rely on. Other statuses leave an existing file alone: a meeting
    /// being processed again keeps its previous transcript until the new one is saved.
    /// </summary>
    void WriteTranscript(MeetingRecord record)
    {
        if (record.Status != MeetingStatus.Transcribed)
        {
            return;
        }
        var path = TranscriptPath(record.Id);
        var data = Encoding.UTF8.GetBytes(TranscriptMerger.Markdown(record, TranscriptLanguage(), timeZone: TranscriptTimeZone));
        // An unchanged document is not rewritten, so saving an unrelated change does not
        // touch the file.
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
            {
                return;
            }
        }
        catch (Exception error) when (IsFileError(error))
        {
        }
        AtomicFile.Write(path, data);
    }

    static void RemoveItem(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path) || Directory.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (IsFileError(error))
        {
            Logger.Error($"Removing {Path.GetFileName(path)} failed", error);
        }
    }

    // MARK: - Loading

    List<MeetingRecord> LoadAll()
    {
        var result = new List<MeetingRecord>();
        IEnumerable<string> folders;
        try
        {
            folders = Directory.Exists(MeetingsFolder) ? Directory.GetDirectories(MeetingsFolder) : [];
        }
        catch (Exception error) when (IsFileError(error))
        {
            Logger.Error("Listing the meetings failed", error);
            return result;
        }
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith('.') || !Guid.TryParseExact(name, "D", out var id))
            {
                continue;
            }
            try
            {
                var attributes = File.GetAttributes(folder);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0)
                {
                    continue;
                }
            }
            catch (Exception error) when (IsFileError(error))
            {
                continue;
            }
            if (LoadMeeting(id) is { } record)
            {
                result.Add(record);
            }
        }
        return result;
    }

    MeetingRecord? LoadMeeting(Guid id)
    {
        var path = RecordPath(id);
        if (File.Exists(path))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception error) when (IsFileError(error))
            {
                // Not damaged, only unreadable right now (locked by a sync tool or an
                // antivirus scan): moving it aside could lose it, so the meeting waits for
                // the next launch instead.
                Logger.Error($"{Path.GetFileName(Folder(id))}/{RecordFileName} could not be read", error);
                return null;
            }
            try
            {
                // The folder name is what every other path is built from.
                return RepairInterrupted(MeetingRecordFile.Decode(data) with { Id = id });
            }
            catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                var moved = JsonFileCorruption.MoveAside(path);
                Logger.Error($"{Path.GetFileName(Folder(id))}/{RecordFileName} is damaged ({error.Message}); moved to {(moved is null ? "nowhere" : Path.GetFileName(moved))}");
            }
        }
        return Recover(id);
    }

    /// <summary>A meeting still marked as recording or transcribing was cut off when the app quit; nothing can be running at launch.</summary>
    MeetingRecord RepairInterrupted(MeetingRecord record)
    {
        MeetingRecord repaired;
        switch (record.Status)
        {
            case MeetingStatus.Recording:
            {
                var microphone = MicrophoneAudioPath(record.Id);
                var system = SystemAudioPath(record.Id);
                var hasMicrophone = File.Exists(microphone);
                var hasSystem = File.Exists(system);
                repaired = record with
                {
                    Status = MeetingStatus.Recorded,
                    HasAudio = hasMicrophone || hasSystem,
                    HasSystemAudio = record.HasSystemAudio && hasSystem,
                    HasScreenRecording = record.HasScreenRecording && File.Exists(ScreenRecordingPath(record.Id)),
                    Duration = Math.Max(record.Duration, Math.Max(CafFile.Duration(microphone), CafFile.Duration(system))),
                };
                break;
            }
            case MeetingStatus.Transcribing:
                // A run replaces the transcript only when it succeeds, so segments or an engine
                // mean an earlier transcript that a cut-off re-run left in place.
                if (record.Segments.Count > 0 || record.EngineId is not null)
                {
                    repaired = record with { Status = MeetingStatus.Transcribed };
                }
                else if (record.HasAudio)
                {
                    repaired = record with { Status = MeetingStatus.Recorded };
                }
                else
                {
                    repaired = record with { Status = MeetingStatus.Failed, ErrorMessage = MeetingErrors.InterruptedMessage() };
                }
                break;
            default:
                return record;
        }
        PersistRepaired(repaired);
        return repaired;
    }

    /// <summary>The tracks a meeting whose record was lost still has.</summary>
    sealed record SurvivingAudio(bool HasSystemAudio, double Duration, DateTimeOffset? Created);

    /// <summary>
    /// Rebuilds a record when <c>meeting.json</c> is missing or damaged but audio or a
    /// transcript survived, so the meeting can still be read, transcribed or deleted.
    ///
    /// A transcript comes back whenever <c>transcript.md</c> is there, audio or not: rebuilt
    /// from the audio alone the meeting would read as never transcribed, and transcribing it
    /// again would overwrite the one file that still holds its lines, names and moments.
    /// </summary>
    MeetingRecord? Recover(Guid id)
    {
        var microphone = MicrophoneAudioPath(id);
        var system = SystemAudioPath(id);
        var hasMicrophone = File.Exists(microphone);
        var hasSystem = File.Exists(system);
        var hasScreenRecording = File.Exists(ScreenRecordingPath(id));
        SurvivingAudio? audio = null;
        if (hasMicrophone || hasSystem)
        {
            var file = hasMicrophone ? microphone : system;
            audio = new SurvivingAudio(hasSystem, Math.Max(CafFile.Duration(microphone), CafFile.Duration(system)), CreationDate(file, folder: false));
        }
        if (RecoverFromTranscript(id, audio, hasScreenRecording) is { } recovered)
        {
            return recovered;
        }
        if (audio is null)
        {
            return null;
        }
        var created = audio.Created ?? StorableNow();
        var record = new MeetingRecord
        {
            Id = id,
            Title = MeetingTitles.DefaultTitle(created),
            CreatedAt = created,
            Duration = audio.Duration,
            Status = MeetingStatus.Recorded,
            HasSystemAudio = audio.HasSystemAudio,
            HasAudio = true,
            HasScreenRecording = hasScreenRecording,
        };
        PersistRepaired(record);
        return record;
    }

    /// <summary>
    /// A transcribed meeting whose record was lost: it comes back from <c>transcript.md</c>, its
    /// <c># title</c>, <c>[mm:ss] ★ Speaker: text</c> lines, speaker names and starred lines,
    /// with whatever audio survived next to it. Null when there is no transcript, or when it
    /// cannot be read but audio can still be transcribed again; without audio an unreadable
    /// transcript still gives a failed record, so the folder stays visible and can be deleted
    /// from the list.
    /// </summary>
    MeetingRecord? RecoverFromTranscript(Guid id, SurvivingAudio? audio, bool hasScreenRecording)
    {
        var path = TranscriptPath(id);
        if (!File.Exists(path))
        {
            return null;
        }
        // The store rewrites transcript.md on every save, so its own date can be recent for an
        // old meeting; the folder keeps the day the meeting began. The earlier of the two is
        // the one the MCP archive uses too, so a recovered meeting keeps its age for retention
        // and for Claude's age limit.
        var transcriptCreated = CreationDate(path, folder: false);
        var folderCreated = CreationDate(Folder(id), folder: true);
        var candidates = new[] { transcriptCreated, folderCreated }.OfType<DateTimeOffset>().ToList();
        var created = audio?.Created ?? (candidates.Count > 0 ? candidates.Min() : StorableNow());
        string text;
        try
        {
            text = File.ReadAllText(path, StrictUtf8);
        }
        catch (Exception error) when (IsFileError(error) || error is DecoderFallbackException)
        {
            if (audio is not null)
            {
                return null;
            }
            var failed = new MeetingRecord
            {
                Id = id,
                Title = MeetingTitles.DefaultTitle(created),
                CreatedAt = created,
                Status = MeetingStatus.Failed,
                HasSystemAudio = false,
                HasAudio = false,
                HasScreenRecording = hasScreenRecording,
                ErrorMessage = MeetingErrors.RecordDamagedMessage(),
            };
            PersistRepaired(failed);
            return failed;
        }
        var parsed = ParseTranscript(text);
        var title = parsed.Title is { Length: > 0 } found ? found : MeetingTitles.DefaultTitle(created);
        // Lines of a document hold merged speech like segments of the first format did, so the
        // record is format 1 even when names and moments came back.
        var record = new MeetingRecord
        {
            Id = id,
            Title = title,
            CreatedAt = created,
            Duration = Math.Max(audio?.Duration ?? 0, parsed.Segments.Count > 0 ? parsed.Segments[^1].End : 0),
            Status = MeetingStatus.Transcribed,
            HasSystemAudio = audio?.HasSystemAudio ?? false,
            HasAudio = audio is not null,
            HasScreenRecording = hasScreenRecording,
            Segments = parsed.Segments,
            Speakers = parsed.Speakers,
            Highlights = parsed.Highlights,
            FormatVersion = 1,
        };
        // Only meeting.json is written here: the transcript is the source being recovered
        // from and is kept exactly as found.
        PersistRepaired(record);
        return record;
    }

    /// <summary>
    /// Reads what <see cref="TranscriptMerger.Markdown(MeetingRecord, AppLanguage?, IReadOnlySet{string}?, TimeZoneInfo?)"/>
    /// wrote, with speaker names in either language. Free of file access and thread-safe, so
    /// the MCP server's read-only archive can recover a meeting from its transcript in memory.
    /// </summary>
    public static ParsedTranscript ParseTranscript(string text) => TranscriptDocumentParser.Parse(text);

    void PersistRepaired(MeetingRecord record)
    {
        try
        {
            Write(record);
        }
        catch (Exception error) when (IsFileError(error))
        {
            dirty.Add(record.Id);
            Logger.Error("Saving a repaired meeting failed", error);
        }
    }

    static DateTimeOffset? CreationDate(string path, bool folder)
    {
        try
        {
            if (folder ? !Directory.Exists(path) : !File.Exists(path))
            {
                return null;
            }
            var created = folder ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            return created.Year <= 1601 ? null : Storable(new DateTimeOffset(created, TimeSpan.Zero));
        }
        catch (Exception error) when (IsFileError(error))
        {
            return null;
        }
    }

    // MARK: - Helpers

    /// <summary>Now, rounded the way meeting.json stores it, so a record reads back equal to itself.</summary>
    static DateTimeOffset StorableNow() => Storable(DateTimeOffset.UtcNow);

    static DateTimeOffset Storable(DateTimeOffset date) => AppleDate.FromReferenceSeconds(AppleDate.ToReferenceSeconds(date));

    static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or SlovoException or System.Security.SecurityException;

    int IndexOf(Guid id)
    {
        for (var index = 0; index < meetings.Count; index++)
        {
            if (meetings[index].Id == id)
            {
                return index;
            }
        }
        return -1;
    }

    void ReplaceAt(int index, MeetingRecord record)
    {
        var list = meetings.ToList();
        list[index] = record;
        SetMeetings(list);
    }

    void SetMeetings(IReadOnlyList<MeetingRecord> list)
    {
        meetings = list;
        OnPropertyChanged(nameof(Meetings));
    }

    /// <summary>Newest first; ties keep a stable order by id.</summary>
    static List<MeetingRecord> Sorted(IEnumerable<MeetingRecord> records) =>
        records.OrderByDescending(m => m.CreatedAt).ThenBy(m => m.Id.ToUpperString(), StringComparer.Ordinal).ToList();
}

/// <summary>Helpers every meeting store has, whatever implements it.</summary>
public static class MeetingStoreExtensions
{
    /// <summary>
    /// Removes <c>mic.caf</c> and <c>system.caf</c> and sets HasAudio to false, leaving
    /// <c>screen.mov</c> in place (the macOS <c>MeetingStoring.removeAudioTracks</c>).
    /// </summary>
    public static void RemoveAudioTracks(this IMeetingStore store, Guid id)
    {
        foreach (var path in new[] { store.MicrophoneAudioPath(id), store.SystemAudioPath(id) })
        {
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.For("MeetingStore").Error($"Removing {Path.GetFileName(path)} failed", error);
            }
        }
        if (store.Meeting(id) is not { HasAudio: true } meeting)
        {
            return;
        }
        store.Update(meeting with { HasAudio = false });
    }
}
