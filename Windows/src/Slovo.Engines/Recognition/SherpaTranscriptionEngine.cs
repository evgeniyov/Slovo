using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Engines.Audio;

namespace Slovo.Engines.Recognition;

/// <summary>
/// GigaAM v3 (RNNT or CTC, with punctuation) or Parakeet TDT v3, run through sherpa-onnx
/// (the port of GigaAMEngine.swift; on Windows Parakeet runs the same way).
///
/// Both GigaAM variants are the end-to-end models: they write punctuation and spell
/// English terms in Latin script. Long recordings are cut at pauses into pieces of at
/// most 20 seconds (<see cref="GigaAMChunker"/>), and cancellation is honoured between
/// pieces. One decode runs at a time: a queue serializes every call, because the native
/// recognizer is not thread-safe. Nothing is ever downloaded here.
/// </summary>
public sealed class SherpaTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    static readonly Logger Logger = Log.For("Engines");

    /// <summary>
    /// ONNX Runtime threads per decode. On an M4 (4 performance cores) 4 was fastest for
    /// both GigaAM variants: more spills onto efficiency cores, 2 is about 20% slower and
    /// 1 about twice as slow. Machines with fewer cores get one thread per core.
    /// </summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount, 1, 4);

    readonly SherpaModelSpec spec;
    readonly string folder;
    readonly JobQueue queue = new();
    /// <summary>Accessed only while holding <see cref="queue"/>.</summary>
    SherpaRecognizer? recognizer;
    /// <summary>Set by Dispose; the recognizer is released as soon as the running job ends.</summary>
    volatile bool released;

    /// <param name="modelsRoot">The Models folder; the engine reads <see cref="SherpaModelSpec.Folder"/> inside it.</param>
    public SherpaTranscriptionEngine(EngineId id, string modelsRoot)
    {
        spec = ModelCatalog.For(id) ?? throw new ArgumentException($"{id} has no sherpa-onnx model", nameof(id));
        folder = spec.FolderPath(modelsRoot);
        Id = id.RawValue();
    }

    public string Id { get; }

    public EngineId EngineId => spec.Engine;

    /// <summary>The folder the model files are read from.</summary>
    public string ModelFolder => folder;

    public Task PrepareAsync(CancellationToken cancellation = default) =>
        OnQueueAsync(() =>
        {
            LoadedRecognizer();
            return true;
        }, cancellation);

    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return OnQueueAsync(() => Transcribe(samples, cancellation), cancellation);
    }

    public Task<string> TranscribeFileAsync(string path, CancellationToken cancellation = default) =>
        OnQueueAsync(() => Transcribe(AudioFileReader.ReadMono16k(path, cancellation), cancellation), cancellation);

    /// <summary>
    /// Releases the loaded model as soon as a running decode finishes. Callers still
    /// holding the engine can keep using it: the next call loads the model again.
    /// </summary>
    public void Dispose()
    {
        released = true;
        _ = queue.RunAsync(() =>
        {
            ReleaseIfAsked();
            return Task.FromResult(true);
        });
    }

    string Transcribe(float[] samples, CancellationToken cancellation)
    {
        var loaded = LoadedRecognizer();
        var pieces = new List<string>();
        foreach (var range in GigaAMChunker.Ranges(samples, SherpaRecognizer.SampleRate, spec.MaxChunkSeconds))
        {
            cancellation.ThrowIfCancellationRequested();
            pieces.Add(loaded.Recognize(samples.AsSpan(range.Start, range.End - range.Start)));
        }
        return JoinPieces(pieces);
    }

    /// <summary>
    /// Joins chunks with one space and also collapses the double spaces the CTC variant
    /// sometimes emits between words.
    /// </summary>
    internal static string JoinPieces(IEnumerable<string> pieces) =>
        string.Join(' ', string.Join(' ', pieces).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Runs work on a thread of its own, one job at a time. Decoding holds a thread for
    /// seconds, so it stays off the thread pool.
    /// </summary>
    Task<T> OnQueueAsync<T>(Func<T> work, CancellationToken cancellation) =>
        queue.RunAsync(async () =>
        {
            try
            {
                return await Task.Factory.StartNew(work, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    .ConfigureAwait(false);
            }
            finally
            {
                ReleaseIfAsked();
            }
        }, cancellation);

    void ReleaseIfAsked()
    {
        if (released && recognizer is { } current)
        {
            recognizer = null;
            current.Dispose();
            Logger.Info($"Released the {Id} recognizer");
        }
    }

    /// <summary>Creates the recognizer on first use. Call only while holding the queue.</summary>
    SherpaRecognizer LoadedRecognizer()
    {
        released = false;
        if (recognizer is { } existing)
        {
            return existing;
        }
        // ONNX Runtime and sherpa-onnx abort the whole process on a truncated or damaged
        // file instead of returning an error, so every file must match the release size.
        var (missing, damaged) = ModelFiles.Check(folder, spec.Files);
        if (missing.Count > 0 || damaged.Count > 0)
        {
            throw new SlovoException(FilesProblem(missing, damaged));
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var created = SherpaRecognizer.Create(spec, folder, Threads);
        Logger.Info($"Loaded {Id} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F2} s (sherpa-onnx {Native.SherpaNative.Version})");
        recognizer = created;
        return created;
    }

    string FilesProblem(IReadOnlyList<string> missing, IReadOnlyList<string> damaged)
    {
        if (spec.Engine == EngineId.Parakeet)
        {
            return missing.Count == spec.Files.Count
                ? CoreStrings.Format("engines.parakeet.notDownloaded", folder)
                : CoreStrings.Format("engines.parakeet.damagedFiles", string.Join(", ", missing.Concat(damaged)), folder);
        }
        var variant = spec.Kind == SherpaModelKind.NemoTransducer ? "transducer" : "ctc";
        var problems = new List<string>();
        if (missing.Count > 0)
        {
            problems.Add(CoreStrings.Format("engines.gigaam.missingFiles", variant, string.Join(", ", missing)));
        }
        if (damaged.Count > 0)
        {
            problems.Add(CoreStrings.Format("engines.gigaam.damagedFiles", variant, string.Join(", ", damaged)));
        }
        problems.Add(CoreStrings.Format("engines.gigaam.expectedFolder", folder));
        return string.Join(' ', problems);
    }
}
