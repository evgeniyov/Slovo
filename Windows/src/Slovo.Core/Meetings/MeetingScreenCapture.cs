using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Video;

namespace Slovo.Core.Meetings;

/// <summary>
/// Records the screen into a meeting's video file, on the timeline the audio tracks use —
/// either the whole main display, or one app's window (port of the macOS
/// <c>MeetingScreenCapture</c>; the platform supplies pictures and an H.264 encoder through
/// <see cref="IScreenCapture"/>).
/// </summary>
/// <remarks>
/// <para>
/// Screen video is a best-effort, optional third track: any failure here degrades only this
/// track and never touches the meeting's audio. A display or window that goes away gets
/// exactly one reconnect attempt before the track gives up for the rest of the meeting; a
/// screen lock pauses capture instead, and resumes on unlock; a wake from sleep tears the
/// capture down and rebuilds it, since a capture from before a sleep may not survive it.
/// </para>
/// <para>
/// About four pictures a second are taken (<see cref="FrameInterval"/>). When the picture has
/// not changed, the last one is encoded again, which costs the encoder next to nothing, so the
/// video keeps a steady rate and its fragments keep reaching the disk. Every picture is scaled
/// into one fixed canvas sized from the main display (<see cref="ScreenVideoGeometry"/>), so a
/// display swap or a resized window never changes the file's dimensions. A key frame is asked
/// for every <see cref="KeyFrameInterval"/> seconds, after which the file starts a fragment.
/// </para>
/// <para>
/// Threading: everything that touches the platform — opening, reading pictures, encoding —
/// runs on one thread of its own, as the platform wants its capture and encoder used from the
/// thread that made them. Public members may be called from any thread; <see cref="Start"/> and
/// <see cref="Stop"/> wait for that thread, never longer than <see cref="CallTimeout"/>. The
/// callbacks are raised on the capture thread.
/// </para>
/// </remarks>
public sealed class MeetingScreenCapture : IDisposable
{
    /// <summary>Four pictures a second (the macOS <c>frameInterval</c>).</summary>
    public const double FrameInterval = 0.25;
    /// <summary>How often a key frame, and with it a new fragment of the file, is asked for.</summary>
    public const double KeyFrameInterval = 10;
    /// <summary>
    /// How long one call into the capture thread may take before it counts as failed, so a
    /// platform call that never returns cannot hold a stop, and with it the meeting, forever.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    static readonly Logger Logger = Log.For("MeetingScreenCapture");

    readonly IScreenCapture capture;
    readonly IUptime uptime;
    readonly double origin;
    readonly bool ownThread;
    readonly object gate = new();
    readonly Queue<Action> jobs = new();
    readonly BgraFrameHandler drawFrame;
    Thread? thread;
    bool exiting;
    long sleptBits;
    bool subscribed;

    // Capture thread only.
    ScreenCaptureTarget target = ScreenCaptureTarget.WholeDisplay;
    IScreenCaptureStream? stream;
    IVideoEncoder? encoder;
    ScreenFrameConverter? converter;
    MeetingScreenWriter? writer;
    bool reconnectedOnce;
    bool stopped;
    bool ended;
    bool isScreenLocked;
    bool forceKeyFrame;
    double? lastTime;
    double? lastKeyTime;
    double nextTick;

    /// <param name="origin">The uptime the meeting's tracks count from.</param>
    /// <param name="ownThread">False in tests, which run the capture thread's work themselves (<see cref="Tick"/>).</param>
    public MeetingScreenCapture(IScreenCapture capture, IUptime uptime, double origin, bool ownThread = true)
    {
        this.capture = capture;
        this.uptime = uptime;
        this.origin = origin;
        this.ownThread = ownThread;
        drawFrame = (pixels, width, height, stride) =>
            converter?.Draw(pixels, width, height, stride, allowUpscale: target is ScreenCaptureTarget.Display);
    }

    /// <summary>
    /// Called once the track is done for the rest of this meeting: a reconnect after a display or
    /// window loss also failed. Never called for a lock, which pauses instead. The argument is an
    /// untranslated reason for the log. Set before <see cref="Start"/>.
    /// </summary>
    public Action<string>? OnLost { get; set; }

    /// <summary>True when a locked screen paused capture, false when unlocking resumed it. Set before <see cref="Start"/>.</summary>
    public Action<bool>? OnPauseChanged { get; set; }

    /// <summary>The encoder failed; nothing more is encoded. Set before <see cref="Start"/>.</summary>
    public Action<Exception>? OnVideoFailed { get; set; }

    /// <summary>
    /// Silence the audio tracks were padded with for sleeps the uptime clock did not count
    /// (the meeting recorder's own figure), so pictures after a sleep land where the audio does.
    /// </summary>
    public double SleptSeconds
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref sleptBits));
        set => Interlocked.Exchange(ref sleptBits, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>The video's size, known once <see cref="Start"/> returned.</summary>
    public (int Width, int Height) CanvasSize { get; private set; }

    /// <summary>
    /// Sizes the canvas from the main display, makes the encoder and, through
    /// <paramref name="makeVideoWriter"/>, the file, and starts capturing the target. Throws
    /// SlovoException, having undone whatever it started.
    /// </summary>
    public void Start(ScreenCaptureTarget target, Func<int, int, MeetingScreenWriter> makeVideoWriter)
    {
        if (capture.UnavailableReason is { } reason)
        {
            throw new SlovoException(reason);
        }
        var display = capture.MainDisplaySize() ?? throw MeetingErrors.ScreenRecordingUnavailable();
        var (width, height) = ScreenVideoGeometry.OutputSize(display.Width, display.Height);
        CanvasSize = (width, height);
        StartThread();
        bool started;
        try
        {
            started = Invoke(() =>
            {
                this.target = target;
                try
                {
                    encoder = capture.CreateEncoder(width, height);
                    converter = new ScreenFrameConverter(width, height);
                    writer = makeVideoWriter(width, height);
                    OpenStream();
                }
                catch
                {
                    ReleaseCapture();
                    writer = null;
                    converter = null;
                    throw;
                }
                nextTick = uptime.Now;
                return true;
            });
        }
        catch
        {
            Stop();
            throw;
        }
        if (!started)
        {
            Stop();
            throw MeetingErrors.ScreenRecordingUnavailable();
        }
        lock (gate)
        {
            capture.SessionChanged += OnSessionChanged;
            subscribed = true;
        }
    }

    /// <summary>
    /// Tears everything down for good; safe to call more than once. The file itself is
    /// finished by whoever owns the writer (the meeting recorder), not here.
    /// </summary>
    public void Stop()
    {
        lock (gate)
        {
            if (subscribed)
            {
                capture.SessionChanged -= OnSessionChanged;
                subscribed = false;
            }
        }
        Invoke(() =>
        {
            stopped = true;
            ReleaseCapture();
            writer = null;
            converter = null;
            return true;
        });
        lock (gate)
        {
            exiting = true;
            Monitor.PulseAll(gate);
        }
        if (thread is { } running && running != Thread.CurrentThread && !running.Join(CallTimeout))
        {
            Logger.Error("The screen capture thread did not end in time");
        }
    }

    /// <summary>
    /// Tears the capture down and rebuilds it against the same target, keeping the encoder and
    /// the file — for a wake from sleep. A locked screen waits for the unlock instead.
    /// </summary>
    public void Restart() => Post(RestartOnThread);

    public void Dispose() => Stop();

    // MARK: - Capture thread

    /// <summary>
    /// Takes and encodes one picture if one is due; the capture thread calls it about four
    /// times a second, tests call it directly.
    /// </summary>
    internal void Tick()
    {
        if (stopped || ended || stream is null || encoder is null || converter is null || writer is null)
        {
            return;
        }
        ScreenFrameResult result;
        string? reason = null;
        try
        {
            result = stream.ReadFrame(drawFrame);
            if (result == ScreenFrameResult.Lost)
            {
                reason = stream.LostReason;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result = ScreenFrameResult.Lost;
            reason = error.Message;
        }
        if (result == ScreenFrameResult.Lost)
        {
            StreamEnded(reason ?? "the capture ended");
            return;
        }
        if (!converter.HasPicture)
        {
            return;
        }
        var time = uptime.Now - origin + SleptSeconds;
        if (lastTime is { } previous && time - previous < FrameInterval / 2)
        {
            return;
        }
        var keyFrame = forceKeyFrame || lastKeyTime is not { } lastKey || time - lastKey >= KeyFrameInterval;
        IReadOnlyList<EncodedVideoFrame> frames;
        try
        {
            frames = encoder.Encode(converter.Frame, time, keyFrame);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Error($"Encoding the screen failed: {error.Message}");
            ended = true;
            ReleaseCapture();
            OnVideoFailed?.Invoke(error);
            return;
        }
        if (keyFrame)
        {
            lastKeyTime = time;
            forceKeyFrame = false;
        }
        lastTime = time;
        foreach (var frame in frames)
        {
            writer.Append(frame);
        }
    }

    /// <summary>What the platform's session events do, on the capture thread.</summary>
    internal void Handle(ScreenSessionEvent change)
    {
        switch (change)
        {
            case ScreenSessionEvent.Locked:
                ScreenLocked();
                break;
            case ScreenSessionEvent.Unlocked:
                ScreenUnlocked();
                break;
            case ScreenSessionEvent.Resumed:
                RestartOnThread();
                break;
        }
    }

    /// <summary>Whether a capture is open right now; for tests.</summary>
    internal bool IsCapturing => stream is not null;

    void OnSessionChanged(ScreenSessionEvent change) => Post(() => Handle(change));

    void OpenStream()
    {
        stream = capture.Open(target);
        forceKeyFrame = true;
    }

    void CloseStream()
    {
        var open = stream;
        stream = null;
        if (open is null)
        {
            return;
        }
        try
        {
            open.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Error("Closing the screen capture failed", error);
        }
    }

    void ReleaseCapture()
    {
        CloseStream();
        var used = encoder;
        encoder = null;
        try
        {
            used?.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Error("Releasing the screen encoder failed", error);
        }
    }

    /// <summary>The capture ended by itself: one reconnect, then the track is lost.</summary>
    void StreamEnded(string reason)
    {
        CloseStream();
        if (stopped || ended)
        {
            return;
        }
        Logger.Error($"Screen capture stopped: {reason}");
        if (reconnectedOnce)
        {
            ReportLost(reason);
            return;
        }
        reconnectedOnce = true;
        if (isScreenLocked)
        {
            return;
        }
        try
        {
            OpenStream();
            Logger.Info("Screen capture reconnected");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReportLost(error.Message);
        }
    }

    void ReportLost(string reason)
    {
        ended = true;
        ReleaseCapture();
        OnLost?.Invoke(reason);
    }

    void ScreenLocked()
    {
        if (stopped || ended)
        {
            return;
        }
        isScreenLocked = true;
        var wasRunning = stream is not null;
        CloseStream();
        if (wasRunning)
        {
            OnPauseChanged?.Invoke(true);
        }
    }

    void ScreenUnlocked()
    {
        isScreenLocked = false;
        if (stopped || ended || writer is null || stream is not null)
        {
            return;
        }
        try
        {
            OpenStream();
            nextTick = uptime.Now;
            OnPauseChanged?.Invoke(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReportLost(error.Message);
        }
    }

    void RestartOnThread()
    {
        if (stopped || ended || writer is null)
        {
            return;
        }
        CloseStream();
        if (isScreenLocked)
        {
            return;
        }
        try
        {
            OpenStream();
            nextTick = uptime.Now;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logger.Error($"Restarting screen capture failed: {error.Message}");
            ReportLost(error.Message);
        }
    }

    // MARK: - The thread

    void StartThread()
    {
        if (!ownThread || thread is not null)
        {
            return;
        }
        thread = new Thread(Run) { IsBackground = true, Name = "Slovo screen capture" };
        thread.Start();
    }

    /// <summary>Runs work on the capture thread (inline in tests).</summary>
    void Post(Action job)
    {
        if (!ownThread)
        {
            job();
            return;
        }
        lock (gate)
        {
            if (exiting)
            {
                return;
            }
            jobs.Enqueue(job);
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>
    /// Runs a function on the capture thread and waits for it; false when it did not finish
    /// within <see cref="CallTimeout"/> or the thread is gone. Exceptions come back to the caller.
    /// </summary>
    bool Invoke(Func<bool> work)
    {
        if (!ownThread || Thread.CurrentThread == thread)
        {
            return work();
        }
        Exception? failure = null;
        var result = false;
        using var done = new ManualResetEventSlim();
        lock (gate)
        {
            if (exiting)
            {
                return false;
            }
            jobs.Enqueue(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    done.Set();
                }
            });
            Monitor.PulseAll(gate);
        }
        if (!done.Wait(CallTimeout))
        {
            Logger.Error("A screen capture call did not finish in time");
            return false;
        }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }

    void Run()
    {
        while (true)
        {
            Action? job = null;
            var tick = false;
            lock (gate)
            {
                while (jobs.Count == 0 && !exiting)
                {
                    if (stream is not null && !stopped && !ended)
                    {
                        var wait = nextTick - uptime.Now;
                        if (wait <= 0)
                        {
                            tick = true;
                            break;
                        }
                        Monitor.Wait(gate, TimeSpan.FromSeconds(Math.Min(wait, FrameInterval)));
                    }
                    else
                    {
                        Monitor.Wait(gate);
                    }
                }
                if (jobs.Count > 0)
                {
                    job = jobs.Dequeue();
                    tick = false;
                }
                else if (exiting)
                {
                    return;
                }
            }
            try
            {
                if (job is not null)
                {
                    job();
                }
                else if (tick)
                {
                    var now = uptime.Now;
                    // A thread that fell behind (a sleep, a busy disk) skips ahead instead of catching up.
                    nextTick = Math.Max(nextTick + FrameInterval, now + FrameInterval / 2);
                    Tick();
                }
            }
            catch (Exception error)
            {
                Logger.Error("The screen capture thread hit an error", error);
            }
        }
    }
}
