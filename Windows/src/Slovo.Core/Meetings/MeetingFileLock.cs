using System.Diagnostics;
using Slovo.Core.Logging;

namespace Slovo.Core.Meetings;

/// <summary>Why <see cref="MeetingFileLock"/> could not be taken.</summary>
public enum MeetingFileLockFailure
{
    /// <summary>The meeting folder is missing or the lock file cannot be opened.</summary>
    Unavailable,
    /// <summary>Another holder kept it for longer than the timeout.</summary>
    TimedOut,
}

public sealed class MeetingFileLockException(MeetingFileLockFailure failure, Exception? inner = null)
    : Exception($"The meeting's lock could not be taken: {failure}", inner)
{
    public MeetingFileLockFailure Failure { get; } = failure;
}

/// <summary>
/// The lock both processes take around a read-modify-write of one <c>meeting.json</c> (port of
/// the macOS <c>MeetingFileLock</c>).
///
/// The app (<see cref="MeetingStore"/>) and the MCP server (<see cref="MeetingNotesFile"/>,
/// <see cref="MeetingHighlightsFile"/>) each read the record, change their part of it and
/// replace the file. Without a lock shared between the two processes a save of one could land
/// between the read and the write of the other and quietly undo it: a rename, a star or
/// Claude's notes would be gone. The lock is <c>Meetings/&lt;id&gt;/.lock</c>, a hidden file
/// that holds nothing and is left in place, opened with <see cref="FileShare.None"/>: .NET
/// takes that as an exclusive share mode on Windows (LockFileEx semantics) and as an
/// exclusive <c>flock</c> on macOS and Linux, the very lock the macOS app takes, so the two
/// apps exclude each other too. The system releases it when the handle closes, so a process
/// that dies holding it never blocks the other one.
///
/// Only writers lock. Reads never wait: every save replaces the file atomically, so a reader
/// sees the old or the new file, never half of one.
/// </summary>
public static class MeetingFileLock
{
    /// <summary>The file inside a meeting folder that carries the lock.</summary>
    public const string FileName = ".lock";
    /// <summary>
    /// Holders keep the lock for one decode and one encode of a record, so waiting this long
    /// means the other side is stuck, not busy.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    static readonly Logger Logger = Log.For("MeetingFileLock");
    const int RetryMilliseconds = 5;

    /// <summary>
    /// Called with the meeting folder right after the lock is taken, before the holder reads
    /// anything; tests pause a holder here to start the other writer. Null outside tests.
    /// </summary>
    internal static Action<string>? AfterLockingForTests;

    /// <summary>
    /// Runs <paramref name="body"/> while holding the lock of the meeting in
    /// <paramref name="meetingFolder"/>; throws <see cref="MeetingFileLockException"/> without
    /// running it when the lock cannot be taken in time.
    /// </summary>
    public static T WithLock<T>(string meetingFolder, Func<T> body, TimeSpan? timeout = null)
    {
        using var handle = Acquire(meetingFolder, timeout ?? DefaultTimeout);
        AfterLockingForTests?.Invoke(meetingFolder);
        return body();
    }

    /// <summary>
    /// Runs <paramref name="body"/> while holding the lock when it can be had, and without it
    /// otherwise. For a save of the app, which must not be dropped because a lock file cannot
    /// be created.
    /// </summary>
    public static T WithLockIfPossible<T>(string meetingFolder, Func<T> body, TimeSpan? timeout = null)
    {
        FileStream? handle = null;
        try
        {
            handle = Acquire(meetingFolder, timeout ?? DefaultTimeout);
        }
        catch (MeetingFileLockException error)
        {
            Logger.Error($"Saving a meeting without its lock: {error.Failure}");
        }
        using (handle)
        {
            if (handle is not null)
            {
                AfterLockingForTests?.Invoke(meetingFolder);
            }
            return body();
        }
    }

    /// <summary>
    /// Opens the lock file, creating it when needed, and waits for the exclusive lock. The
    /// returned handle holds the lock until it is disposed.
    /// </summary>
    public static FileStream Acquire(string meetingFolder, TimeSpan timeout)
    {
        var path = Path.Combine(meetingFolder, FileName);
        var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeout.TotalSeconds) * Stopwatch.Frequency);
        while (true)
        {
            // Not following a link keeps the lock file inside the meeting folder.
            if (IsLink(path))
            {
                throw new MeetingFileLockException(MeetingFileLockFailure.Unavailable);
            }
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException
                                              or PathTooLongException or NotSupportedException or ArgumentException)
            {
                throw new MeetingFileLockException(MeetingFileLockFailure.Unavailable, error);
            }
            catch (IOException error)
            {
                // Held by the other side (a sharing violation on Windows, EWOULDBLOCK from
                // flock elsewhere): try again until the deadline.
                if (!Directory.Exists(meetingFolder))
                {
                    throw new MeetingFileLockException(MeetingFileLockFailure.Unavailable, error);
                }
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    throw new MeetingFileLockException(MeetingFileLockFailure.TimedOut, error);
                }
            }
            Thread.Sleep(RetryMilliseconds);
        }
    }

    static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
