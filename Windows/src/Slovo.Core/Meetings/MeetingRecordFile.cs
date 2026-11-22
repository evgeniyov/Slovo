using System.Text.Json;
using Slovo.Core.Json;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Storage;

namespace Slovo.Core.Meetings;

/// <summary>Why <see cref="MeetingRecordFile.Update{T}"/> changed nothing.</summary>
public enum MeetingRecordFileFailure
{
    /// <summary><c>meeting.json</c> is missing, is not a regular file, or cannot be decoded.</summary>
    Unreadable,
    /// <summary>The lock could not be taken or the file could not be replaced.</summary>
    Failed,
}

public sealed class MeetingRecordFileException(MeetingRecordFileFailure failure, Exception? inner = null)
    : Exception($"meeting.json could not be changed: {failure}", inner)
{
    public MeetingRecordFileFailure Failure { get; } = failure;
}

/// <summary>
/// Reads and replaces one <c>meeting.json</c> outside <see cref="MeetingStore"/>: the writes
/// the MCP server may make, shared by every writer so its safety checks live in one place
/// (port of the macOS <c>MeetingRecordFile</c>).
///
/// Symbolic links are not followed on read, so a read cannot be aimed outside the data
/// folder; the write is atomic, the way <see cref="MeetingStore"/> saves, so a reader never
/// sees a half-written file. A change goes through <see cref="Update{T}"/>, which holds the
/// meeting's <see cref="MeetingFileLock"/> from the read to the write, the same lock the
/// app's saves take.
/// </summary>
public static class MeetingRecordFile
{
    public const string FileName = "meeting.json";
    /// <summary>Larger record files are skipped (the macOS <c>MeetingArchive.maximumFileBytes</c>).</summary>
    public const long MaximumFileBytes = 32 << 20;

    static readonly Logger Logger = Log.For("MCP");

    public static string PathFor(AppPaths paths, Guid id) => Path.Combine(paths.MeetingFolder(id), FileName);

    /// <summary>
    /// The record fresh from disk, with its id set from the folder name, or null when the file
    /// is missing, is not a regular file, is too large, or cannot be decoded.
    /// </summary>
    public static MeetingRecord? Read(AppPaths paths, Guid id) => Read(PathFor(paths, id), id);

    internal static MeetingRecord? Read(string path, Guid id)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                || info.Length > MaximumFileBytes)
            {
                return null;
            }
            var record = Decode(File.ReadAllBytes(path));
            // The folder name is what the app builds every other path from.
            return record with { Id = id };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException
                                          or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Decodes <c>meeting.json</c> with the tolerant rules of the macOS decoder. Throws JsonException.</summary>
    public static MeetingRecord Decode(ReadOnlySpan<byte> data) =>
        JsonSerializer.Deserialize<MeetingRecord>(data, SlovoJson.Options) ?? throw new JsonException("meeting.json holds null.");

    /// <summary>The bytes <see cref="MeetingStore"/> writes: pretty-printed with sorted keys.</summary>
    public static byte[] Encode(MeetingRecord record) => SlovoJson.SerializeToUtf8(record);

    /// <summary>Replaces the record's file atomically. Throws IOException or UnauthorizedAccessException.</summary>
    public static void Write(MeetingRecord record, AppPaths paths) => AtomicFile.Write(PathFor(paths, record.Id), Encode(record));

    /// <summary>
    /// Reads the record, lets <paramref name="change"/> edit it and replaces the file when it
    /// reports a change, all under the meeting's lock, so a save of the app cannot land in
    /// between and be undone, nor undo this. Throws <see cref="MeetingRecordFileException"/>.
    /// </summary>
    public static T Update<T>(AppPaths paths, Guid id, Func<MeetingRecord, (MeetingRecord Record, T Result, bool Changed)> change,
                              TimeSpan? timeout = null)
    {
        (T Result, MeetingRecordFileFailure? Failure) outcome;
        try
        {
            outcome = MeetingFileLock.WithLock<(T, MeetingRecordFileFailure?)>(paths.MeetingFolder(id), () =>
            {
                if (Read(paths, id) is not { } record)
                {
                    return (default!, MeetingRecordFileFailure.Unreadable);
                }
                var (edited, result, changed) = change(record);
                if (!changed)
                {
                    return (result, null);
                }
                try
                {
                    Write(edited with { Id = id }, paths);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    Logger.Error("Replacing meeting.json failed", error);
                    return (result, MeetingRecordFileFailure.Failed);
                }
                return (result, null);
            }, timeout);
        }
        catch (MeetingFileLockException error)
        {
            // No lock: a missing meeting reads as one, anything else is a failure to write.
            Logger.Error($"The meeting's lock could not be taken: {error.Failure}");
            throw new MeetingRecordFileException(Read(paths, id) is null ? MeetingRecordFileFailure.Unreadable : MeetingRecordFileFailure.Failed, error);
        }
        if (outcome.Failure is { } failure)
        {
            throw new MeetingRecordFileException(failure);
        }
        return outcome.Result;
    }
}

/// <summary>Writes what Claude concluded into one meeting.</summary>
public interface IMeetingNotesWriter
{
    /// <summary>
    /// Replaces the meeting's notes, or removes them when <paramref name="notes"/> is null,
    /// and returns what the file holds afterwards. Throws <see cref="MeetingRecordFileException"/>.
    /// </summary>
    MeetingNotes? Write(MeetingNotes? notes, Guid id);
}

/// <summary>
/// The <c>claudeNotes</c> of one <c>meeting.json</c>, the one thing besides highlights the MCP
/// server may change in Slovo's data folder (port of the macOS <c>MeetingNotesFile</c>; the
/// app's <see cref="MeetingStore.SetClaudeNotes"/> writes through it too).
///
/// The server runs in its own process while the app may be running too, and the app owns the
/// file. So the record is read again here immediately before it is replaced, under the
/// meeting's <see cref="MeetingFileLock"/>, and only the notes are changed: every other field
/// keeps whatever the app last saved. The app does the mirror image of this under the same
/// lock and keeps whatever notes the file holds whenever it saves, so neither process drops
/// the other's work. <c>transcript.md</c> is left alone: the app rewrites it on the meeting's
/// next save.
/// </summary>
public sealed class MeetingNotesFile(AppPaths paths) : IMeetingNotesWriter
{
    readonly Lock gate = new();

    public AppPaths Paths { get; } = paths;

    public MeetingNotes? Write(MeetingNotes? notes, Guid id)
    {
        lock (gate)
        {
            var written = notes is { IsEmpty: false } ? notes : null;
            return MeetingRecordFile.Update(Paths, id, record =>
            {
                // What the notes say is weighed, never when they were written: the server
                // stamps every call with the current time, so comparing that too would rewrite
                // the file for notes saying exactly what it already says. Notes that say the
                // same thing keep the date they were first written.
                if (SaysTheSame(record.ClaudeNotes, written))
                {
                    return (record, record.ClaudeNotes, false);
                }
                return (record with { ClaudeNotes = written }, written, true);
            });
        }
    }

    /// <summary>True when both sides hold the same notes, whenever each of them was written.</summary>
    static bool SaysTheSame(MeetingNotes? left, MeetingNotes? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return left.Summary == right.Summary && left.Decisions.SequenceEqual(right.Decisions)
            && left.Tasks.SequenceEqual(right.Tasks) && left.Author == right.Author;
    }
}

/// <summary>The edited record and the highlight a mark touched; Created is false when nothing changed.</summary>
public sealed record HighlightMarkResult(MeetingRecord Record, MeetingHighlight Highlight, bool Created);

/// <summary>The edited record an unmark produced, and whether a highlight with that id was there to remove.</summary>
public sealed record HighlightUnmarkResult(MeetingRecord Record, bool Removed);

/// <summary>Marks or unmarks an important moment on one meeting from outside the app (Claude, through the MCP server).</summary>
public interface IMeetingHighlightsWriter
{
    /// <summary>Throws <see cref="MeetingRecordFileException"/>.</summary>
    HighlightMarkResult Mark(double time, string? note, Guid id, DateTimeOffset now);
    /// <summary>Throws <see cref="MeetingRecordFileException"/>.</summary>
    HighlightUnmarkResult Unmark(Guid highlightId, Guid id);
}

/// <summary>
/// The <c>highlights</c> of one <c>meeting.json</c>, changed from the MCP server's process
/// (port of the macOS <c>MeetingHighlightsFile</c>). Same reasoning as
/// <see cref="MeetingNotesFile"/>: the record is read again immediately before it is replaced,
/// under the meeting's lock, and only <c>highlights</c> is touched. The app reconciles these
/// marks with its own on its next save or refresh.
/// </summary>
public sealed class MeetingHighlightsFile(AppPaths paths) : IMeetingHighlightsWriter
{
    readonly Lock gate = new();

    public AppPaths Paths { get; } = paths;

    public HighlightMarkResult Mark(double time, string? note, Guid id, DateTimeOffset now)
    {
        lock (gate)
        {
            return MeetingRecordFile.Update(Paths, id, record =>
            {
                var mark = MeetingEdits.MarkHighlight(time, note, record, now);
                return (mark.Record, new HighlightMarkResult(mark.Record, mark.Highlight, mark.Created), mark.Created);
            });
        }
    }

    public HighlightUnmarkResult Unmark(Guid highlightId, Guid id)
    {
        lock (gate)
        {
            return MeetingRecordFile.Update(Paths, id, record =>
            {
                var unmark = MeetingEdits.UnmarkHighlight(highlightId, record);
                return (unmark.Record, new HighlightUnmarkResult(unmark.Record, unmark.Removed), unmark.Removed);
            });
        }
    }
}
