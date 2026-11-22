using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Slovo.Core.Json;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;

namespace Slovo.Core.Mcp;

/// <summary>A meeting as the MCP server read it, with what identifies that version of the file.</summary>
/// <param name="RecordModified">Modification date of the file the record came from.</param>
/// <param name="RecordSize">Size in bytes of the file the record came from.</param>
public sealed record ArchivedMeeting(MeetingRecord Record, DateTimeOffset RecordModified, long RecordSize);

public interface IMeetingArchiveReading
{
    /// <summary>Newest first; skips folders without a readable record or transcript.</summary>
    IReadOnlyList<ArchivedMeeting> Meetings();

    ArchivedMeeting? Meeting(Guid id);

    /// <summary>
    /// The creation date of the folder named by this id when it exists but neither its record
    /// nor its transcript can be read; null otherwise. Lets a tool say "damaged" instead of
    /// "not found" for a meeting the user can still see in Slovo.
    /// </summary>
    DateTimeOffset? UnreadableMeetingDate(Guid id) => null;
}

/// <summary>
/// Reads <c>Meetings/&lt;UUID&gt;/</c> without ever changing anything: no repairs, renames or
/// writes, unlike the meeting store, which the running app owns (port of the macOS
/// <c>MeetingArchive</c>). A damaged <c>meeting.json</c> falls back to parsing
/// <c>transcript.md</c> in memory.
///
/// Symbolic links and other reparse points are never followed, so every read stays inside the
/// data folder. Files are opened read-only and shared for reading, writing and deleting, so
/// the app can replace them meanwhile; it saves atomically (a temporary file, then a rename),
/// so a read sees either the old or the new file.
/// </summary>
public sealed class MeetingArchive(AppPaths paths) : IMeetingArchiveReading
{
    /// <summary>Larger record files are skipped.</summary>
    public const int MaximumFileBytes = 32 << 20;

    internal const string RecordFileName = "meeting.json";
    internal const string TranscriptFileName = "transcript.md";

    static readonly Logger Logger = Log.For("MCP");

    readonly Lock gate = new();
    /// <summary>
    /// Parsed files by path, valid while the file's identity is unchanged; failures are cached
    /// too, so a damaged file is not decoded again on every request.
    /// </summary>
    Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    public AppPaths Paths { get; } = paths;

    public IReadOnlyList<ArchivedMeeting> Meetings()
    {
        lock (gate)
        {
            var usedPaths = new HashSet<string>(StringComparer.Ordinal);
            var loaded = new List<ArchivedMeeting>();
            foreach (var (id, folder) in MeetingFolders())
            {
                if (Load(id, folder, usedPaths) is { } meeting)
                {
                    loaded.Add(meeting);
                }
            }
            // Forget meetings that were deleted, so a long session does not keep them in memory.
            cache = cache.Where(pair => usedPaths.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
            loaded.Sort(Compare);
            return loaded;
        }
    }

    public ArchivedMeeting? Meeting(Guid id)
    {
        lock (gate)
        {
            var folder = MeetingFolders().FirstOrDefault(entry => entry.Id == id);
            return folder.Path is null ? null : Load(id, folder.Path, []);
        }
    }

    public DateTimeOffset? UnreadableMeetingDate(Guid id)
    {
        lock (gate)
        {
            var folder = MeetingFolders().FirstOrDefault(entry => entry.Id == id);
            if (folder.Path is null || Load(id, folder.Path, []) is not null)
            {
                return null;
            }
            return FolderCreated(folder.Path);
        }
    }

    /// <summary>Newest first; equal dates keep a stable order by id, as the app's list does.</summary>
    public static int Compare(ArchivedMeeting left, ArchivedMeeting right)
    {
        if (left.Record.CreatedAt == right.Record.CreatedAt)
        {
            return string.CompareOrdinal(left.Record.Id.ToUpperString(), right.Record.Id.ToUpperString());
        }
        return right.Record.CreatedAt.CompareTo(left.Record.CreatedAt);
    }

    // MARK: - Folders and files

    /// <summary>What identifies one version of a file: its size and its dates.</summary>
    internal readonly record struct FileStamp(long Size, long ModifiedTicks, long CreatedTicks)
    {
        public DateTimeOffset Modified => new(ModifiedTicks, TimeSpan.Zero);
        public DateTimeOffset Created => new(CreatedTicks, TimeSpan.Zero);
    }

    sealed record CacheEntry(FileStamp Stamp, ArchivedMeeting? Meeting);

    /// <summary>Real directories named by a meeting id; symbolic links and junctions are not followed.</summary>
    List<(Guid Id, string Path)> MeetingFolders()
    {
        string[] names;
        try
        {
            names = Directory.GetFileSystemEntries(Paths.MeetingsFolder).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var seen = new HashSet<Guid>();
        var folders = new List<(Guid, string)>();
        foreach (var name in names.Order(SwiftStringComparer.Instance))
        {
            if (!Guid.TryParseExact(name, "D", out var id) || seen.Contains(id))
            {
                continue;
            }
            var path = Path.Combine(Paths.MeetingsFolder, name);
            if (!IsRealDirectory(path))
            {
                continue;
            }
            seen.Add(id);
            folders.Add((id, path));
        }
        return folders;
    }

    internal static bool IsRealDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A regular file, not a directory, symbolic link or other reparse point.</summary>
    internal static bool IsRegularFile(string path, out FileInfo info)
    {
        info = new FileInfo(path);
        try
        {
            if (!info.Exists)
            {
                return false;
            }
            var attributes = info.Attributes;
            return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)
                   && !attributes.HasFlag(FileAttributes.Device);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static DateTimeOffset? FolderCreated(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetCreationTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    ArchivedMeeting? Load(Guid id, string folder, HashSet<string> usedPaths)
    {
        var shortId = id.ToString("D")[..8];
        var recordPath = Path.Combine(folder, RecordFileName);
        var fromRecord = ParsedFile(recordPath, shortId, usedPaths, (data, stamp) =>
        {
            if (DecodeRecord(data) is not { } record)
            {
                Logger.Error("Meeting " + shortId + ": meeting.json cannot be decoded");
                return null;
            }
            // The folder name is what the app builds every other path from.
            return new ArchivedMeeting(record with { Id = id }, stamp.Modified, stamp.Size);
        });
        if (fromRecord is not null)
        {
            return fromRecord;
        }

        var transcriptPath = Path.Combine(folder, TranscriptFileName);
        // The folder is made when the meeting starts and never replaced. transcript.md is
        // rewritten, as a new file, on every save of the app, so its own creation date would
        // make an old meeting look new and slip past the age limit Claude is held to.
        var folderCreated = FolderCreated(folder);
        return ParsedFile(transcriptPath, shortId, usedPaths, (data, stamp) =>
        {
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
            var parsed = MeetingStore.ParseTranscript(text);
            var title = parsed.Title is null ? null : McpUnicode.TrimHorizontalWhitespace(parsed.Title);
            if (string.IsNullOrEmpty(title) && parsed.Segments.Count == 0)
            {
                return null;
            }
            var created = folderCreated is { } folderDate && folderDate < stamp.Created ? folderDate : stamp.Created;
            var record = new MeetingRecord
            {
                Id = id,
                // The macOS archive names an untitled document in the system language.
                Title = string.IsNullOrEmpty(title) ? MeetingTitles.DefaultTitle(created, AppLanguage.System) : title,
                CreatedAt = created,
                Duration = parsed.Segments.Count == 0 ? 0 : parsed.Segments.Max(segment => segment.End),
                Status = MeetingStatus.Transcribed,
                HasSystemAudio = parsed.Segments.Any(segment => segment.Speaker == Speaker.Others),
                HasAudio = false,
                Segments = parsed.Segments,
                Speakers = parsed.Speakers,
                Highlights = parsed.Highlights,
            };
            return new ArchivedMeeting(record, stamp.Modified, stamp.Size);
        });
    }

    /// <summary>A record decoded with the tolerant rules the app uses; null when the file is not one.</summary>
    internal static MeetingRecord? DecodeRecord(byte[] data)
    {
        try
        {
            return MeetingRecordFile.Decode(data);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The parsed contents of a regular file, from the cache when the file is unchanged.</summary>
    ArchivedMeeting? ParsedFile(string path, string shortId, HashSet<string> usedPaths, Func<byte[], FileStamp, ArchivedMeeting?> parse)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }
        usedPaths.Add(path);
        if (!IsRegularFile(path, out var info))
        {
            return null;
        }
        FileStamp stamp;
        try
        {
            stamp = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (cache.TryGetValue(path, out var entry) && entry.Stamp == stamp)
        {
            return entry.Meeting;
        }
        if (ReadFile(path, shortId) is not { } read)
        {
            cache[path] = new CacheEntry(stamp, null);
            return null;
        }
        var meeting = parse(read.Data, read.Stamp);
        cache[path] = new CacheEntry(read.Stamp, meeting);
        return meeting;
    }

    /// <summary>
    /// Reads a whole file through one read-only handle, so the contents and the identity
    /// describe the same file even when the app replaces it meanwhile.
    /// </summary>
    internal static (byte[] Data, FileStamp Stamp)? ReadFile(string path, string shortId)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var stamp = new FileStamp(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle).Ticks,
                File.GetCreationTimeUtc(handle).Ticks);
            if (stamp.Size > MaximumFileBytes)
            {
                Logger.Error("Meeting " + shortId + ": a file larger than 32 MiB was skipped");
                return null;
            }
            var data = ReadAll(handle, stamp.Size);
            if (data is null)
            {
                Logger.Error("Meeting " + shortId + ": a file larger than 32 MiB was skipped");
                return null;
            }
            return (data, stamp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Everything in the file, or null once it grows past the limit while being read.</summary>
    static byte[]? ReadAll(SafeFileHandle handle, long expected)
    {
        using var output = new MemoryStream((int)Math.Min(expected, MaximumFileBytes));
        var chunk = new byte[64 * 1024];
        long offset = 0;
        while (true)
        {
            var count = RandomAccess.Read(handle, chunk, offset);
            if (count == 0)
            {
                break;
            }
            output.Write(chunk, 0, count);
            offset += count;
            if (output.Length > MaximumFileBytes)
            {
                return null;
            }
        }
        return output.ToArray();
    }
}
