using Slovo.Core.Audio;
using Slovo.Core.Logging;
using Slovo.Core.Model;

namespace Slovo.Core.Meetings;

/// <summary>
/// Appends 16 kHz mono samples to a meeting track on a serial queue of its own (port of the
/// macOS <c>MeetingTrackWriter</c>), so capture callbacks never wait for the disk and nothing
/// accumulates in memory. The file itself is a <see cref="CafWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// All tracks of a meeting share one origin (<see cref="MeetingTimeline"/>). A buffer that
/// carries its capture time is placed at that time: when it arrives later than the samples
/// written so far account for (the device paused, capture was reopened after a device
/// change, loopback delivered nothing while nothing played), the gap is filled with silence;
/// when it overlaps what was written by more than the tolerance (a device clock running
/// fast, a live fill that ran ahead of a late buffer), the overlapping head is dropped. So a
/// track never drifts from the shared timeline by more than <see cref="MeetingTimeline.GapTolerance"/>,
/// and transcript times match the recording in both tracks.
/// </para>
/// <para>
/// Threading: every member may be called from any thread. Writes run on the queue in the
/// order they were queued; <see cref="FrameCount"/>, <see cref="HasSound"/>,
/// <see cref="PadSilence"/> and <see cref="Finish"/> wait for the writes queued before them.
/// </para>
/// </remarks>
internal sealed class MeetingTrackWriter
{
    static readonly Logger Logger = Log.For("MeetingTrackWriter");
    static readonly long ToleranceFrames = MeetingTimeline.Frames(MeetingTimeline.GapTolerance);
    static readonly long MaximumGapFrames = MeetingTimeline.Frames(MeetingTimeline.MaximumGap);
    static readonly long MaximumPadFrames = MeetingTimeline.Frames(MeetingTimeline.MaximumSleepPad);

    readonly SerialQueue queue = new();
    readonly Lock muteGate = new();
    bool muted;

    // Queue only.
    readonly CafWriter file;
    /// <summary>
    /// Silence added for intervals the uptime clock did not count, chiefly a system sleep. A
    /// position is <see cref="MeetingTimeline.Frame"/> plus this, rather than the origin moved
    /// back, so a pad longer than the machine's awake uptime cannot wrap the origin.
    /// </summary>
    long paddedFrames;
    long droppedFrames;
    bool writeFailed;
    bool closed;

    /// <summary>Creates the file with an open-ended data chunk. Throws SlovoException.</summary>
    public MeetingTrackWriter(string path, double origin)
    {
        file = CafWriter.Create(path);
        Path = path;
        Origin = origin;
    }

    public string Path { get; }

    /// <summary>The uptime the track's first frame stands for.</summary>
    public double Origin { get; }

    /// <summary>
    /// The first write that failed, so the recording can end instead of ticking on over a track
    /// that stopped growing. Called once, on the writer's queue; set before the first append.
    /// </summary>
    public Action<Exception>? OnWriteFailure { get; set; }

    /// <summary>Runs before every write on the queue; tests throw from it to reach the failure path, which a real disk only takes when it fills.</summary>
    internal Action<long>? WriteHook { get; set; }

    /// <summary>
    /// Whether appended samples are written as silence. Each buffer keeps its length and its
    /// capture time, so a muted stretch holds its place on the timeline and the capture behind
    /// the writer never has to stop. Read when a buffer is appended, so a change applies from
    /// the next buffer delivered.
    /// </summary>
    public bool IsMuted
    {
        get
        {
            lock (muteGate)
            {
                return muted;
            }
        }
        set
        {
            lock (muteGate)
            {
                muted = value;
            }
        }
    }

    /// <summary>
    /// Queues samples for writing and takes the array over. <paramref name="time"/> is the
    /// uptime of the first sample, when the source knows it; without it the samples follow
    /// the ones before.
    /// </summary>
    public void Append(float[] samples, double? time = null)
    {
        if (samples.Length == 0)
        {
            return;
        }
        if (IsMuted)
        {
            var length = samples.Length;
            queue.Post(() => Write(null, length, time));
        }
        else
        {
            queue.Post(() => Write(samples, samples.Length, time));
        }
    }

    /// <summary>Frames written so far, silence included, after the writes queued before.</summary>
    public long FrameCount => queue.Invoke(() => file.FrameCount);

    /// <summary>Whether the file holds any sound at all, after the writes queued before.</summary>
    public bool HasSound => queue.Invoke(() => file.HasSound);

    /// <summary>Samples of early buffers left out to keep the track on the timeline.</summary>
    public long DroppedFrames => queue.Invoke(() => droppedFrames);

    /// <summary>Waits until everything queued so far is written.</summary>
    public void Drain() => queue.Invoke(() => 0);

    /// <summary>Waits for queued writes and closes the file; later appends are ignored. Returns the frames written.</summary>
    public long Finish() => queue.Invoke(() =>
    {
        if (!closed)
        {
            closed = true;
            try
            {
                file.Close();
            }
            catch (SlovoException error)
            {
                Logger.Error($"Closing {System.IO.Path.GetFileName(Path)} failed: {error.Message}");
            }
        }
        return file.FrameCount;
    });

    /// <summary>
    /// Fills an interval the uptime clock never counted, which a system sleep can be: without
    /// it the track would butt-join the two sides of the sleep and every later timestamp would
    /// be early by the slept time. What was added is counted, and every later buffer is placed
    /// that much further along. Capped by <see cref="MeetingTimeline.MaximumGap"/>. Returns the frames added.
    /// </summary>
    public long PadSilence(double seconds)
    {
        var capped = MeetingTimeline.PadSeconds(seconds);
        return capped > 0 ? queue.Invoke(() => Pad(capped)) : 0;
    }

    /// <summary>
    /// The same pad, queued behind the writes already waiting instead of waited for, so a long
    /// sleep never holds the caller while hours of silence reach the disk. Buffers appended
    /// afterwards land after it, at their true position. Capped by <see cref="MeetingTimeline.MaximumSleepPad"/>.
    /// </summary>
    public void EnqueueSilence(double seconds)
    {
        var capped = double.IsFinite(seconds) ? Math.Min(Math.Max(0, seconds), MeetingTimeline.MaximumSleepPad) : 0;
        if (capped > 0)
        {
            queue.Post(() => Pad(capped));
        }
    }

    /// <summary>
    /// Queues silence up to the position of <paramref name="time"/> when the track is behind it:
    /// loopback delivers nothing while nothing plays, and a missing microphone delivers nothing
    /// at all, but the track must still grow with the meeting and end where the other one does.
    /// Nothing is dropped when the track is already further along.
    /// </summary>
    public void EnqueueFillTo(double time) => queue.Post(() =>
    {
        if (closed || writeFailed)
        {
            return;
        }
        var gap = MeetingTimeline.Frame(time, Origin) + paddedFrames - file.FrameCount;
        if (gap > 0)
        {
            WriteSilence(Math.Min(gap, MaximumPadFrames));
        }
    });

    // Queue only.

    long Pad(double seconds)
    {
        if (closed || writeFailed)
        {
            return 0;
        }
        var before = file.FrameCount;
        WriteSilence(MeetingTimeline.Frames(seconds));
        // Only what reached the file counts: a pad cut short by a failed write must not push
        // later buffers past the end of the track.
        var added = file.FrameCount - before;
        paddedFrames += added;
        return added;
    }

    void Write(float[]? samples, int length, double? time)
    {
        if (closed || writeFailed)
        {
            return;
        }
        var skip = 0;
        if (time is { } capturedAt)
        {
            var gap = MeetingTimeline.Frame(capturedAt, Origin) + paddedFrames - file.FrameCount;
            if (gap > ToleranceFrames)
            {
                WriteSilence(Math.Min(gap, MaximumGapFrames));
                if (writeFailed)
                {
                    return;
                }
            }
            else if (gap < -ToleranceFrames)
            {
                skip = (int)Math.Min(-gap, length);
                droppedFrames += skip;
                if (skip == length)
                {
                    return;
                }
            }
        }
        if (samples is null)
        {
            WriteSilence(length - skip);
        }
        else
        {
            Run(length - skip, () => file.Append(samples.AsSpan(skip)));
        }
    }

    void WriteSilence(long frames)
    {
        if (frames > 0)
        {
            Run(frames, () => file.AppendSilence(frames));
        }
    }

    void Run(long frames, Action write)
    {
        if (writeFailed)
        {
            return;
        }
        try
        {
            WriteHook?.Invoke(frames);
            write();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A full disk or a file that went away: the track cannot grow any further. Writing
            // stops here, so the computed gap does not widen with every later buffer and send
            // the silence fill into ever more work for nothing.
            writeFailed = true;
            Logger.Error($"Writing {System.IO.Path.GetFileName(Path)} failed: {error.Message}");
            try
            {
                OnWriteFailure?.Invoke(error);
            }
            catch (Exception handlerError)
            {
                Logger.Error("The write failure handler failed", handlerError);
            }
        }
    }
}

/// <summary>
/// Runs actions one at a time, in the order they were posted, on the thread pool (the
/// counterpart of a serial DispatchQueue). <see cref="Invoke{T}"/> waits for the result and
/// runs inline when called from the queue itself.
/// </summary>
internal sealed class SerialQueue
{
    static readonly Logger Logger = Log.For("SerialQueue");

    [ThreadStatic] static SerialQueue? current;

    readonly Lock gate = new();
    readonly Queue<Action> pending = new();
    bool isRunning;

    public void Post(Action action)
    {
        lock (gate)
        {
            pending.Enqueue(action);
            if (isRunning)
            {
                return;
            }
            isRunning = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static queue => queue.Run(), this, preferLocal: false);
    }

    public T Invoke<T>(Func<T> work)
    {
        if (current == this)
        {
            return work();
        }
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    void Run()
    {
        var previous = current;
        current = this;
        try
        {
            while (true)
            {
                Action? action;
                lock (gate)
                {
                    if (!pending.TryDequeue(out action))
                    {
                        isRunning = false;
                        return;
                    }
                }
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    Logger.Error("A queued write failed", error);
                }
            }
        }
        finally
        {
            current = previous;
        }
    }
}
