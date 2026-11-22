using Slovo.Core.Audio;
using Slovo.Core.Dictation;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Core.Tests.Dictation;

namespace Slovo.Core.Tests.Meetings;

/// <summary>A capture stream the test feeds by hand, on whatever thread it calls from.</summary>
sealed class FakeAudioStream : IAudioStream
{
    Action<float[], double?>? onSamples;
    int startCount;
    int stopCount;
    volatile bool isRunning;
    volatile bool isDisposed;

    public string? DeviceName { get; set; } = "Test device";
    /// <summary>Thrown by <see cref="Start"/>.</summary>
    public Exception? StartError { get; set; }
    /// <summary>Runs inside <see cref="Start"/>, on the thread the recorder starts streams on.</summary>
    public Action? DuringStart { get; set; }
    public int StartCount => Volatile.Read(ref startCount);
    public int StopCount => Volatile.Read(ref stopCount);
    public bool IsRunning => isRunning;
    public bool IsDisposed => isDisposed;

    public event Action<string>? Failed;
    public event Action? Resumed;

    public void Start(Action<float[], double?> callback)
    {
        Interlocked.Increment(ref startCount);
        onSamples = callback;
        DuringStart?.Invoke();
        if (StartError is { } error)
        {
            throw error;
        }
        isRunning = true;
    }

    public void Stop()
    {
        Interlocked.Increment(ref stopCount);
        isRunning = false;
    }

    public void Dispose()
    {
        isRunning = false;
        isDisposed = true;
    }

    /// <summary>Hands samples to the recorder as a capture thread would; ignored unless running.</summary>
    public void Deliver(float[] samples, double? time)
    {
        if (isRunning)
        {
            onSamples?.Invoke(samples, time);
        }
    }

    /// <summary>Delivers even when the stream is not running (a callback racing a stop).</summary>
    public void DeliverAnyway(float[] samples, double? time) => onSamples?.Invoke(samples, time);

    public void Fail(string message) => Failed?.Invoke(message);

    public void Resume() => Resumed?.Invoke();
}

sealed class FakeAudioSources : IMeetingAudioSources
{
    public List<FakeAudioStream> Microphones { get; } = [];
    public List<string?> MicrophoneUids { get; } = [];
    /// <summary>Builds the next microphone stream.</summary>
    public Func<FakeAudioStream> NewMicrophone { get; set; } = () => new FakeAudioStream { DeviceName = "Headset" };
    /// <summary>The stream SystemAudio() returns; null when the system cannot capture it.</summary>
    public FakeAudioStream? System { get; set; } = new() { DeviceName = "Speakers" };
    public int SystemRequests { get; private set; }
    /// <summary>The stream ApplicationAudio() returns; null where one app's sound cannot be captured.</summary>
    public FakeAudioStream? Application { get; set; } = new() { DeviceName = "teams.exe" };
    public List<string> ApplicationRequests { get; } = [];

    public IAudioStream? ApplicationAudio(string bundleId)
    {
        ApplicationRequests.Add(bundleId);
        return Application;
    }

    /// <summary>The microphone stream opened last.</summary>
    public FakeAudioStream Microphone => Microphones[^1];

    IAudioStream IMeetingAudioSources.Microphone(string? deviceUid)
    {
        MicrophoneUids.Add(deviceUid);
        var stream = NewMicrophone();
        Microphones.Add(stream);
        return stream;
    }

    public IAudioStream? SystemAudio()
    {
        SystemRequests++;
        return System;
    }
}

/// <summary>
/// Uptime, timers and the wall clock of a computer that can sleep. <see cref="Advance"/> is
/// awake time: both clocks move and due timers fire in order. <see cref="Sleep"/> moves the wall
/// clock only (Windows' uptime clock standing still, like the Mac's) or both clocks at once
/// (an uptime clock that counts sleep); timers that fell due fire once, late, as after a wake.
/// Use it on the test's main thread.
/// </summary>
sealed class SleepingClock : IUptime, IScheduler
{
    readonly List<Entry> entries = [];
    long sequence;
    double slept;

    public double Now { get; private set; } = 1_000;

    public SleepingClock() => Wall = new WallClock(this);

    /// <summary>The wall clock, which counts sleep.</summary>
    public TimeProvider Wall { get; }

    public void Advance(double seconds)
    {
        var target = Now + seconds;
        while (entries.Where(e => !e.IsDone && e.Due <= target).OrderBy(e => e.Due).ThenBy(e => e.Sequence).FirstOrDefault() is { } next)
        {
            entries.Remove(next);
            Now = Math.Max(Now, next.Due);
            next.Fire();
        }
        Now = target;
        entries.RemoveAll(e => e.IsDone);
    }

    /// <summary>A sleep. With <paramref name="uptimeCounts"/> the uptime clock jumps along with the wall clock.</summary>
    public void Sleep(double seconds, bool uptimeCounts = false)
    {
        if (!uptimeCounts)
        {
            slept += seconds;
            return;
        }
        Now += seconds;
        foreach (var due in entries.Where(e => !e.IsDone && e.Due <= Now).OrderBy(e => e.Sequence).ToList())
        {
            entries.Remove(due);
            due.Fire();
        }
    }

    /// <summary>A time sync that sets the wall clock by this much.</summary>
    public void StepWallClock(double seconds) => slept += seconds;

    public IDisposable Schedule(double seconds, Action action)
    {
        var entry = new Entry(Now + Math.Max(0, seconds), sequence++, action);
        entries.Add(entry);
        return entry;
    }

    sealed class WallClock(SleepingClock clock) : TimeProvider
    {
        static readonly DateTimeOffset Epoch = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromSeconds(clock.Now + clock.slept);
    }

    sealed class Entry(double due, long sequence, Action action) : IDisposable
    {
        public double Due => due;
        public long Sequence => sequence;
        public bool IsDone { get; private set; }

        public void Fire()
        {
            if (IsDone)
            {
                return;
            }
            IsDone = true;
            action();
        }

        public void Dispose() => IsDone = true;
    }
}

/// <summary>
/// A meeting recorder over a real <see cref="MeetingStore"/> in a temporary folder, fake capture
/// streams and a <see cref="SleepingClock"/>, used from a test main thread.
/// </summary>
sealed class RecorderHarness : IDisposable
{
    public readonly TemporaryMeetingsFolder Folder = new();
    public readonly TestMainThread MainThread = new();
    public readonly SleepingClock Clock = new();
    public readonly AppSettings Settings = new(SettingsStore.InMemory());
    public readonly FakeAudioSources Sources = new();
    public readonly FakeSounds Sounds = new();
    public readonly FakePermissions Permissions = new();
    public readonly FakeScreenCapture Screen = new();
    public readonly MeetingStore Store;
    public readonly MeetingRecorder Recorder;
    public long? FreeBytes = 50L * 1024 * 1024 * 1024;

    public RecorderHarness()
    {
        Store = new MeetingStore(Folder.Paths, MainThread, new ManualTimeProvider());
        Recorder = new MeetingRecorder(new MeetingRecorder.Dependencies
        {
            Store = Store,
            Settings = Settings,
            Sources = Sources,
            MainThread = MainThread,
            Uptime = Clock,
            Scheduler = Clock,
            Clock = Clock.Wall,
            Sounds = Sounds,
            Permissions = Permissions,
            AvailableSpace = () => FreeBytes,
            ScreenCapture = Screen,
        })
        {
            // The tests take the screen capture's pictures themselves (Tick).
            ScreenCaptureOwnThread = false,
        };
    }

    /// <summary>Runs the body on the test main thread, then stops whatever still records.</summary>
    public Task Run(Func<Task> body) => MainThread.RunAsync(async () =>
    {
        try
        {
            await body();
        }
        finally
        {
            Recorder.StopImmediately();
        }
    });

    public Task Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    public double Now => Clock.Now;

    public FakeAudioStream Microphone => Sources.Microphone;

    public FakeAudioStream System => Sources.System!;

    public (MeetingTrackWriter Microphone, MeetingTrackWriter? System) Writers =>
        Recorder.WritersForTests ?? throw new InvalidOperationException("Nothing is recording");

    /// <summary>Waits until everything delivered so far is on disk.</summary>
    public void Drain()
    {
        if (Recorder.WritersForTests is { } writers)
        {
            writers.Microphone.Drain();
            writers.System?.Drain();
        }
    }

    public string MicrophonePath(Guid id) => Store.MicrophoneAudioPath(id);

    public string SystemPath(Guid id) => Store.SystemAudioPath(id);

    public static float[] Constant(double seconds, float value) =>
        Enumerable.Repeat(value, (int)Math.Round(seconds * MeetingAudioFormat.SampleRate)).ToArray();

    /// <summary>What a constant microphone sample becomes on disk: boosted, then stored as 16 bits.</summary>
    public static float Boosted(float value) => CafWriter.ToInt16(MeetingAudioMath.MicrophoneBoost([value])[0]) / 32_768f;

    public static float Stored(float value) => CafWriter.ToInt16(value) / 32_768f;

    public static float[] Read(string path) => CafFile.ReadAll(path).Samples;

    public static float Rms(float[] samples, int from, int to) => LevelMeter.Rms(samples.AsSpan(from, to - from));

    /// <summary>Polls on the main thread until the condition holds, for work that hops threads.</summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, int milliseconds = 5_000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }
            await Task.Delay(2);
        }
        return true;
    }

    public void Dispose()
    {
        Recorder.StopImmediately();
        Folder.Dispose();
    }
}

/// <summary>Shortcuts that can hold several registrations at once, like RegisterHotKey.</summary>
sealed class MultiShortcuts : IGlobalShortcuts
{
    readonly List<Registration> active = [];

    /// <summary>Shortcuts the system refuses.</summary>
    public HashSet<HotkeyShortcut> Refused { get; } = [];
    public List<HotkeyShortcut> Registrations { get; } = [];
    public IReadOnlyList<HotkeyShortcut> Active => active.Select(r => r.Shortcut).ToList();

    public IDisposable? Register(HotkeyShortcut shortcut, Action action)
    {
        Registrations.Add(shortcut);
        if (Refused.Contains(shortcut) || active.Any(r => r.Shortcut == shortcut))
        {
            return null;
        }
        var registration = new Registration(this, shortcut, action);
        active.Add(registration);
        return registration;
    }

    public bool IsActive(HotkeyShortcut shortcut) => active.Any(r => r.Shortcut == shortcut);

    public void Press(HotkeyShortcut shortcut) => active.FirstOrDefault(r => r.Shortcut == shortcut)?.Action();

    sealed class Registration(MultiShortcuts owner, HotkeyShortcut shortcut, Action action) : IDisposable
    {
        public HotkeyShortcut Shortcut => shortcut;
        public Action Action => action;

        public void Dispose() => owner.active.Remove(this);
    }
}
