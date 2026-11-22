using System.ComponentModel;
using Slovo.Core.Audio;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Tests.Meetings;

/// <summary>Synthetic meeting tracks: 200 Hz tones and silence at 16 kHz, as the Swift tests make them.</summary>
static class TranscriberAudio
{
    public static float[] Tone(double seconds, float amplitude)
    {
        var samples = new float[(int)(seconds * MeetingAudioFormat.SampleRate)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = amplitude * (float)Math.Sin(2 * Math.PI * 200 * index / MeetingAudioFormat.SampleRate);
        }
        return samples;
    }

    public static float[] Silence(double seconds) => new float[(int)(seconds * MeetingAudioFormat.SampleRate)];

    public static float[] Join(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Two six-second voices on the microphone: 1–7 s and 9–15 s of a 16 s track.</summary>
    public static float[] TwoVoices() => Join(Silence(1), Tone(6, 0.2f), Silence(2), Tone(6, 0.2f), Silence(1));

    /// <summary>One remote voice, 3–9 s of a 16 s system track.</summary>
    public static float[] CallSystem() => Join(Silence(3), Tone(6, 0.5f), Silence(7));

    public static void Write(float[] samples, string path, bool muted = false)
    {
        using var writer = CafWriter.Create(path);
        writer.IsMuted = muted;
        writer.Append(samples);
        writer.Close();
    }

    /// <summary>"system.caf" for the loud remote voice (amplitude 0.5), "mic.caf" otherwise.</summary>
    public static string TrackName(float[] samples) => Peak(samples) > 0.3f ? "system.caf" : "mic.caf";

    public static float Peak(float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }
}

/// <summary>Hands out <see cref="Engine"/>, counting requests and marks, or fails to load.</summary>
sealed class TranscriberEngines : IEngineProvider
{
    int requests;
    int markedUsed;

    public TranscriberEngine Engine { get; } = new();
    public Exception? LoadError { get; set; }
    public int Requests => Volatile.Read(ref requests);
    public int MarkedUsed => Volatile.Read(ref markedUsed);
    public List<EngineId> RequestedIds { get; } = [];

    public event Action<EngineId>? LoadStateChanged { add { } remove { } }

    public EngineId EngineFor(EnginePurpose purpose) => purpose.IsMeetings ? EngineId.Parakeet : EngineId.GigaamRnnt;

    public EngineLoadState LoadState(EngineId id) => EngineLoadState.Ready;

    public Task<ITranscriptionEngine> EngineAsync(EngineId id, CancellationToken cancellation = default)
    {
        Interlocked.Increment(ref requests);
        lock (RequestedIds)
        {
            RequestedIds.Add(id);
        }
        return LoadError is { } error ? Task.FromException<ITranscriptionEngine>(error) : Task.FromResult<ITranscriptionEngine>(Engine);
    }

    public void PreloadForCurrentSettings()
    {
    }

    public void MarkUsed(EngineId id) => Interlocked.Increment(ref markedUsed);

    public void Unload(EngineId id)
    {
    }

    public Task WaitForLoadAsync(EngineId id) => Task.CompletedTask;
}

/// <summary>
/// Answers "loud words" for clips of the system track (amplitude 0.5) and "quiet words" for
/// the microphone track (0.2), after an optional cancellable delay.
/// </summary>
sealed class TranscriberEngine : ITranscriptionEngine
{
    readonly Lock gate = new();
    int calls;
    int callsWithoutFile;
    TimeSpan? delay;
    string? requiredFile;
    readonly List<int> threads = [];

    public string Id => "fake";

    public TimeSpan? Delay
    {
        get { lock (gate) return delay; }
        set { lock (gate) delay = value; }
    }

    /// <summary>A file that must still exist whenever a clip is transcribed.</summary>
    public string? RequiredFile
    {
        get { lock (gate) return requiredFile; }
        set { lock (gate) requiredFile = value; }
    }

    public int CallCount { get { lock (gate) return calls; } }

    public int CallsWithoutFile { get { lock (gate) return callsWithoutFile; } }

    /// <summary>Managed thread ids the calls started on.</summary>
    public IReadOnlyList<int> Threads { get { lock (gate) return threads.ToList(); } }

    public Task PrepareAsync(CancellationToken cancellation = default) => Task.CompletedTask;

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation = default)
    {
        string? required;
        TimeSpan? wait;
        lock (gate)
        {
            calls++;
            threads.Add(Environment.CurrentManagedThreadId);
            required = requiredFile;
            wait = delay;
        }
        if (required is not null && !File.Exists(required))
        {
            lock (gate)
            {
                callsWithoutFile++;
            }
        }
        if (wait is { } duration)
        {
            await Task.Delay(duration, cancellation);
        }
        return TranscriberAudio.Peak(samples) > 0.3f ? "loud words" : "quiet words";
    }

    public Task<string> TranscribeFileAsync(string path, CancellationToken cancellation = default) =>
        throw new NotSupportedException("The meeting pipeline hands the engine samples.");
}

/// <summary>A speaker model that is ready, supported and hands out <see cref="Diarizer"/>, unless told otherwise.</summary>
sealed class TranscriberDiarization : IDiarizationProvider
{
    public const string FakeModelName = "fake-segmentation+fake-embedding@test";
    public const string FakeConfiguration = "step=0.1 minSeg=0 threshold=0.5 zeroVote=on";

    int diarizerRequests;
    int releases;

    public TranscriberDiarizer Diarizer { get; } = new();
    public ModelStatus ModelStatus { get; set; } = ModelStatus.Ready(1_000);
    public bool IsSupportedOnThisSystem { get; set; } = true;
    public Exception? LoadError { get; set; }
    /// <summary>When set, loading waits for it (or for the caller's cancellation).</summary>
    public TaskCompletionSource? LoadGate { get; set; }
    public int DiarizerRequests => Volatile.Read(ref diarizerRequests);
    public int Releases => Volatile.Read(ref releases);
    public string ModelName => FakeModelName;
    public string Configuration => FakeConfiguration;

    public async Task<ISpeakerDiarizer> DiarizerAsync(CancellationToken cancellation)
    {
        Interlocked.Increment(ref diarizerRequests);
        if (LoadError is { } error)
        {
            throw error;
        }
        if (LoadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellation);
        }
        return Diarizer;
    }

    public void Release() => Interlocked.Increment(ref releases);

    public Task WaitForLoadAsync() => Task.CompletedTask;
}

/// <summary>
/// Returns the turns set for a track ("mic.caf", "system.caf", told apart by loudness) and
/// records the hint it was given, reporting progress half way and at the end.
/// </summary>
sealed class TranscriberDiarizer : ISpeakerDiarizer
{
    readonly Lock gate = new();
    Dictionary<string, IReadOnlyList<SpeakerTurn>> turns = [];
    Dictionary<string, SpeakerCountHint> hints = [];
    Exception? error;
    TimeSpan? delay;
    int calls;

    public Dictionary<string, IReadOnlyList<SpeakerTurn>> Turns
    {
        get { lock (gate) return new(turns); }
        set { lock (gate) turns = new(value); }
    }

    public Exception? Error
    {
        get { lock (gate) return error; }
        set { lock (gate) error = value; }
    }

    public TimeSpan? Delay
    {
        get { lock (gate) return delay; }
        set { lock (gate) delay = value; }
    }

    /// <summary>Hints of the latest run; a run starts over when the microphone track comes again.</summary>
    public Dictionary<string, SpeakerCountHint> Hints { get { lock (gate) return new(hints); } }

    public int CallCount { get { lock (gate) return calls; } }

    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(float[] samples, SpeakerCountHint hint, IProgress<double>? progress,
                                                              CancellationToken cancellation)
    {
        var name = TranscriberAudio.TrackName(samples);
        IReadOnlyList<SpeakerTurn> result;
        Exception? failure;
        TimeSpan? wait;
        lock (gate)
        {
            calls++;
            if (name == "mic.caf")
            {
                hints = [];
            }
            hints[name] = hint;
            result = turns.GetValueOrDefault(name) ?? [];
            failure = error;
            wait = delay;
        }
        progress?.Report(0.5);
        if (wait is { } duration)
        {
            await Task.Delay(duration, cancellation);
        }
        if (failure is not null)
        {
            throw failure;
        }
        progress?.Report(1);
        return result;
    }
}

/// <summary>Forwards to a real store and records the order of saves and audio removal.</summary>
sealed class SpyMeetingStore(MeetingStore store) : IMeetingStore
{
    public List<string> Events { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => store.PropertyChanged += value;
        remove => store.PropertyChanged -= value;
    }

    public IReadOnlyList<MeetingRecord> Meetings => store.Meetings;

    public MeetingRecord? Meeting(Guid id) => store.Meeting(id);

    public MeetingRecord Create(string title) => store.Create(title);

    public void Update(MeetingRecord meeting)
    {
        if (!meeting.HasAudio && store.Meeting(meeting.Id)?.HasAudio == true)
        {
            var transcript = File.Exists(store.TranscriptPath(meeting.Id));
            var saved = MeetingRecordFile.Read(store.Paths, meeting.Id)?.Status;
            Events.Add($"removeAudio transcript={(transcript ? "true" : "false")} saved={(saved is { } status ? EnumText.Raw(status) : "none")}");
        }
        else
        {
            Events.Add("update " + EnumText.Raw(meeting.Status));
        }
        store.Update(meeting);
    }

    public void Delete(Guid id) => store.Delete(id);

    public void DeleteAudio(Guid id)
    {
        Events.Add("deleteAudio");
        store.DeleteAudio(id);
    }

    public void SetClaudeNotes(MeetingNotes? notes, Guid id) => store.SetClaudeNotes(notes, id);

    public void RefreshFromDisk() => store.RefreshFromDisk();

    public string MeetingsFolder => store.MeetingsFolder;

    public string Folder(Guid id) => store.Folder(id);

    public string MicrophoneAudioPath(Guid id) => store.MicrophoneAudioPath(id);

    public string SystemAudioPath(Guid id) => store.SystemAudioPath(id);

    public string TranscriptPath(Guid id) => store.TranscriptPath(id);

    public void Flush()
    {
        Events.Add("flush");
        store.Flush();
    }
}
