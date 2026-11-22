using System.Diagnostics;
using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Meetings;

/// <summary>
/// The app and the MCP server both read, change and replace one meeting.json. These tests
/// start one writer while the other is inside its read-modify-write and check that the second
/// waits for the first, so neither change is undone (port of MCPMeetingFileLockTests.swift),
/// plus the lock itself: two handles in one process, and on macOS or Linux a <c>flock</c>
/// taken by another process the way the macOS app takes it.
/// </summary>
public sealed class MeetingFileLockTests : IDisposable
{
    static readonly Guid CoworkId = Guid.Parse("5A1D0B6E-8C11-4E0A-9E43-2B7C9D1E0F31");
    const string CoworkTitle = "Коворк";
    static readonly MeetingNotes Notes = new("Итоги встречи.", ["Выпуск в пятницу"], [], "Claude", AppleDate.FromReferenceSeconds(779_700_000));

    readonly TemporaryMeetingsFolder folder = new();

    public MeetingFileLockTests()
    {
        folder.Write(new MeetingRecord
        {
            Id = CoworkId,
            Title = CoworkTitle,
            CreatedAt = AppleDate.FromReferenceSeconds(779_612_527),
            Status = MeetingStatus.Transcribed,
            HasAudio = false,
            Segments = [new TranscriptSegment { Start = 4, End = 9, Speaker = Speaker.Me, Text = "Всем привет." }],
        });
    }

    public void Dispose()
    {
        MeetingFileLock.AfterLockingForTests = null;
        folder.Dispose();
    }

    string MeetingFolder => folder.Paths.MeetingFolder(CoworkId);

    MeetingRecord StoredRecord() => MeetingRecordFile.Read(folder.Paths, CoworkId) ?? throw new InvalidOperationException("no record");

    /// <summary>Claude writes notes while the app is inside a save: the notes wait for the save to finish, then land on top of what it wrote.</summary>
    [Fact]
    public void ANotesWriteStartedInsideAStoreSaveWaitsForIt()
    {
        var store = new MeetingStore(folder.Paths, ImmediateMainThread.Shared);
        store.Update(store.Meeting(CoworkId)! with { Title = "Коворк, переименован" });

        var testThread = Environment.CurrentManagedThreadId;
        var writer = new MeetingNotesFile(folder.Paths);
        using var finished = new ManualResetEventSlim();
        var started = false;
        MeetingNotes? notesDuringSave = null;
        MeetingFileLock.AfterLockingForTests = locked =>
        {
            // Only the store's save, on the test thread, starts the other writer.
            if (Environment.CurrentManagedThreadId != testThread || started || !SameFolder(locked, MeetingFolder))
            {
                return;
            }
            started = true;
            new Thread(() =>
            {
                try
                {
                    writer.Write(Notes, CoworkId);
                }
                finally
                {
                    finished.Set();
                }
            }).Start();
            // Long enough for the notes writer to reach the lock; it must not get past it.
            Thread.Sleep(300);
            notesDuringSave = StoredRecord().ClaudeNotes;
        };

        store.Flush();
        Assert.True(started, "the store's save took the meeting's lock");
        Assert.Null(notesDuringSave);
        Assert.True(finished.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var stored = StoredRecord();
        Assert.Equal("Коворк, переименован", stored.Title);
        Assert.Equal(Notes.Summary, stored.ClaudeNotes?.Summary);
    }

    /// <summary>
    /// The app saves a rename while the MCP server is between reading the record and writing
    /// its notes back. Without a shared lock the server's write would restore the old title.
    /// </summary>
    [Fact]
    public void AStoreSaveStartedInsideANotesWriteWaitsForIt()
    {
        var store = new MeetingStore(folder.Paths, ImmediateMainThread.Shared);
        using var insideWrite = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        new Thread(() =>
        {
            try
            {
                MeetingRecordFile.Update(folder.Paths, CoworkId, record =>
                {
                    insideWrite.Set();
                    // The app saves now, while this writer holds the record it read.
                    Thread.Sleep(300);
                    return (record with { ClaudeNotes = Notes }, 0, true);
                });
            }
            finally
            {
                finished.Set();
            }
        }).Start();
        Assert.True(insideWrite.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        store.Update(store.Meeting(CoworkId)! with { Title = "Коворк, переименован" });
        store.Flush();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var stored = StoredRecord();
        Assert.Equal("Коворк, переименован", stored.Title);
        Assert.Equal(Notes.Summary, stored.ClaudeNotes?.Summary);
        Assert.Equal(Notes.Summary, store.Meeting(CoworkId)?.ClaudeNotes?.Summary);
    }

    [Fact]
    public void TheLockIsExclusiveAndReleasedAfterward()
    {
        MeetingFileLock.WithLock(MeetingFolder, () =>
        {
            var error = Assert.Throws<MeetingFileLockException>(() =>
                MeetingFileLock.WithLock(MeetingFolder, () => 0, TimeSpan.FromMilliseconds(50)));
            Assert.Equal(MeetingFileLockFailure.TimedOut, error.Failure);
            return 0;
        });
        Assert.Equal(1, MeetingFileLock.WithLock(MeetingFolder, () => 1, TimeSpan.FromMilliseconds(50)));
        Assert.True(File.Exists(Path.Combine(MeetingFolder, ".lock")), "the lock file is left in place");
    }

    [Fact]
    public void TwoHandlesOnTheLockFileExcludeEachOther()
    {
        var path = Path.Combine(MeetingFolder, MeetingFileLock.FileName);
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var clock = Stopwatch.StartNew();
            var error = Assert.Throws<MeetingFileLockException>(() => MeetingFileLock.Acquire(MeetingFolder, TimeSpan.FromMilliseconds(200)));
            Assert.Equal(MeetingFileLockFailure.TimedOut, error.Failure);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(150), "it waited for the other holder");
        }
        using var handle = MeetingFileLock.Acquire(MeetingFolder, TimeSpan.FromMilliseconds(200));
        Assert.NotNull(handle);
    }

    [Fact]
    public void AWaitingWriterGetsTheLockOnceItIsReleased()
    {
        using var holding = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var handle = MeetingFileLock.Acquire(MeetingFolder, TimeSpan.FromSeconds(5));
            holding.Set();
            Thread.Sleep(300);
        });
        holder.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var clock = Stopwatch.StartNew();
        using var handle = MeetingFileLock.Acquire(MeetingFolder, TimeSpan.FromSeconds(5));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(100));
        holder.Join();
    }

    /// <summary>
    /// The macOS app takes this lock with <c>flock(LOCK_EX)</c>. .NET's FileShare.None takes the
    /// same lock on macOS and Linux, so the two apps exclude each other when both run on one
    /// Mac (a folder shared between them, say). Another process stands in for the Swift app.
    /// </summary>
    [Fact]
    public void AFlockHeldByAnotherProcessIsRespected()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("flock is how macOS and Linux lock; Windows share modes are covered by the tests above.");
        }
        var python = new[] { "/usr/bin/python3", "/usr/local/bin/python3", "/opt/homebrew/bin/python3" }.FirstOrDefault(File.Exists);
        if (python is null)
        {
            Assert.Skip("python3 is not installed to play the other process.");
        }
        var path = Path.Combine(MeetingFolder, MeetingFileLock.FileName);
        var script = "import fcntl, os, sys, time\n"
                     + "fd = os.open(sys.argv[1], os.O_RDWR | os.O_CREAT, 0o600)\n"
                     + "fcntl.flock(fd, fcntl.LOCK_EX)\n"
                     + "print('locked', flush=True)\n"
                     + "time.sleep(1.0)\n";
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        Assert.Equal("locked", process.StandardOutput.ReadLine());

        var error = Assert.Throws<MeetingFileLockException>(() => MeetingFileLock.Acquire(MeetingFolder, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(MeetingFileLockFailure.TimedOut, error.Failure);

        using var handle = MeetingFileLock.Acquire(MeetingFolder, TimeSpan.FromSeconds(10));
        Assert.True(process.WaitForExit(10_000));
    }

    [Fact]
    public void AMissingMeetingIsUnreadableRatherThanALockFailure()
    {
        var missing = Guid.Parse("0BADF00D-0000-4000-8000-000000000000");
        var error = Assert.Throws<MeetingRecordFileException>(() => new MeetingNotesFile(folder.Paths).Write(Notes, missing));
        Assert.Equal(MeetingRecordFileFailure.Unreadable, error.Failure);
        Assert.False(Directory.Exists(folder.Paths.MeetingFolder(missing)), "taking the lock never creates a meeting folder");
    }

    /// <summary>A writer that cannot get the lock in time refuses rather than risking the race.</summary>
    [Fact]
    public void AWriterThatCannotGetTheLockFails()
    {
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => MeetingFileLock.WithLock(MeetingFolder, () =>
        {
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            return 0;
        }));
        holder.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var error = Assert.Throws<MeetingRecordFileException>(() =>
            MeetingRecordFile.Update(folder.Paths, CoworkId, record => (record with { Title = "Не должно попасть в файл" }, 0, true),
                                     TimeSpan.FromMilliseconds(100)));
        Assert.Equal(MeetingRecordFileFailure.Failed, error.Failure);

        release.Set();
        holder.Join();
        Assert.Equal(CoworkTitle, StoredRecord().Title);
    }

    /// <summary>The app's save does not wait forever for a stuck holder: it saves without the lock rather than lose the save.</summary>
    [Fact]
    public void AStoreSaveGoesAheadWithoutTheLockAfterTheTimeout()
    {
        var store = new MeetingStore(folder.Paths, ImmediateMainThread.Shared) { LockTimeout = TimeSpan.FromMilliseconds(300) };
        var path = Path.Combine(MeetingFolder, MeetingFileLock.FileName);
        using var stuck = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var ran = MeetingFileLock.WithLockIfPossible(MeetingFolder, () => "saved", TimeSpan.FromMilliseconds(50));
        Assert.Equal("saved", ran);

        store.Update(store.Meeting(CoworkId)! with { Title = "Коворк, переименован" });
        var clock = Stopwatch.StartNew();
        store.Flush();
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(250), "the save waited for the lock first");
        Assert.Equal("Коворк, переименован", StoredRecord().Title);
        Assert.Equal(MeetingFileLock.DefaultTimeout, new MeetingStore(folder.Paths, ImmediateMainThread.Shared).LockTimeout);
    }

    static bool SameFolder(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
