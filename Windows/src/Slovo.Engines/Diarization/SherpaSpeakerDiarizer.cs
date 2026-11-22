using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SherpaOnnx;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Engines.Native;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Diarization;

/// <summary>
/// sherpa-onnx's offline speaker diarization (pyannote segmentation 3.0 and a WeSpeaker
/// embedding model) behind <see cref="ISpeakerDiarizer"/>, the Windows counterpart of
/// FluidSpeakerDiarizer.swift.
///
/// Two passes: sherpa-onnx finds who speaks when and splits the voices finely, then
/// <see cref="SpeakerClusterMerger"/> joins them into people from centroid embeddings of
/// each voice's longest turns, honouring the speaker count hint.
///
/// The models load once and never download. Runs are serialized: a run of an hour holds
/// its embeddings in memory, and a second one gains nothing. A run cannot be interrupted
/// inside sherpa-onnx, so cancelling ends the caller's wait at once while the native work
/// finishes in the background and its result is dropped; the next run waits for it.
/// </summary>
public sealed class SherpaSpeakerDiarizer : ISpeakerDiarizer, IDisposable
{
    static readonly Logger Logger = Log.For("Diarization");
    const int SampleRate = 16_000;

    /// <summary>The first pass fills this share of the progress; centroids take the rest.</summary>
    const double FirstPassShare = 0.9;
    /// <summary>A voice's centroid for <see cref="SpeakerClusterMerger"/> comes from at most this many of its longest turns.</summary>
    const int CentroidTurns = 4;
    const double CentroidTurnSeconds = 8;
    const double CentroidMinimumTurnSeconds = 0.5;

    readonly string modelFolder;
    readonly JobQueue runs = new();
    readonly Lock gate = new();
    IntPtr handle;
    /// <summary>Loaded on the first run that finds more than one voice. Used only inside a run.</summary>
    IntPtr extractor;
    bool isRunning;
    bool disposed;

    SherpaSpeakerDiarizer(IntPtr handle, string modelFolder, SherpaDiarizationTuning tuning)
    {
        this.handle = handle;
        this.modelFolder = modelFolder;
        Tuning = tuning;
    }

    public SherpaDiarizationTuning Tuning { get; }

    /// <summary>
    /// Loads the models from <paramref name="modelFolder"/> (<c>Models/diarization-onnx</c>).
    /// Throws a localized <see cref="SlovoException"/> when they are missing, damaged or
    /// rejected. Never downloads.
    /// </summary>
    public static SherpaSpeakerDiarizer Load(string modelFolder, SherpaDiarizationTuning? tuning = null)
    {
        tuning ??= SherpaDiarizationTuning.Standard;
        // ONNX Runtime aborts the whole process on a truncated model, so sizes are checked first.
        switch (DiarizationModelFiles.Status(modelFolder).Kind)
        {
            case ModelStatusKind.Ready:
                break;
            case ModelStatusKind.Damaged:
                throw new SlovoException(CoreStrings.Get("diarization.error.modelDamaged"));
            default:
                throw new SlovoException(CoreStrings.Get("diarization.error.modelMissing"));
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var config = Config(modelFolder, tuning);
        var created = SherpaNative.SherpaOnnxCreateOfflineSpeakerDiarization(config.Pointer);
        if (created == IntPtr.Zero)
        {
            throw new SlovoException(CoreStrings.Format("diarization.error.loadFailed",
                CoreStrings.Format("diarization.error.sherpaRejected", modelFolder)));
        }
        var rate = SherpaNative.SherpaOnnxOfflineSpeakerDiarizationGetSampleRate(created);
        if (rate != SampleRate)
        {
            SherpaNative.SherpaOnnxDestroyOfflineSpeakerDiarization(created);
            throw new SlovoException(CoreStrings.Format("diarization.error.loadFailed",
                CoreStrings.Format("diarization.error.sherpaRejected", modelFolder)));
        }
        Logger.Info($"Loaded the speaker model in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F2} s");
        return new SherpaSpeakerDiarizer(created, modelFolder, tuning);
    }

    /// <summary>ONNX Runtime threads for segmentation and embeddings.</summary>
    static int Threads => SherpaTranscriptionEngine.Threads;

    /// <summary>
    /// The native configuration: the first pass always clusters by the tuning's split
    /// threshold. sherpa-onnx's exact-count mode is not used, because complete linkage
    /// spends clusters on outlying windows; the second pass applies the hint.
    /// </summary>
    internal static NativeConfig Config(string modelFolder, SherpaDiarizationTuning tuning)
    {
        var config = new NativeConfig(typeof(OfflineSpeakerDiarizationConfig));
        config.Set("Segmentation.Pyannote.Model", DiarizationModelFiles.SegmentationPath(modelFolder))
            .Set("Segmentation.Pyannote.WindowShiftRatio", (float)tuning.SegmentationStepRatio)
            .Set("Segmentation.NumThreads", Threads)
            .Set("Segmentation.Provider", "cpu")
            // sherpa-onnx writes its configuration and stage timings to stderr when asked.
            .Set("Segmentation.Debug", Environment.GetEnvironmentVariable("SLOVO_SHERPA_DEBUG") == "1" ? 1 : 0)
            .Set("Embedding.Model", DiarizationModelFiles.EmbeddingPath(modelFolder))
            .Set("Embedding.NumThreads", Threads)
            .Set("Embedding.Provider", "cpu")
            .Set("Clustering.NumClusters", -1)
            .Set("Clustering.Threshold", (float)tuning.SplitThreshold)
            // The C API reads a zero as "use the default" (0.3 s and 0.5 s), so "off" is a millisecond.
            .Set("MinDurationOn", (float)Math.Max(tuning.MinDurationOn, 0.001))
            .Set("MinDurationOff", (float)Math.Max(tuning.MinDurationOff, 0.001));
        return config;
    }

    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(float[] samples, SpeakerCountHint hint, IProgress<double>? progress,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(samples);
        cancellation.ThrowIfCancellationRequested();
        await runs.AcquireAsync(cancellation).ConfigureAwait(false);
        Task<IReadOnlyList<SpeakerTurn>> work;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                isRunning = true;
            }
            work = Task.Factory.StartNew(() =>
            {
                try
                {
                    return Run(samples, hint, progress, cancellation);
                }
                finally
                {
                    Finished();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            Finished();
            throw;
        }
        // A run the caller stopped waiting for still ends on its own; its failure is not news.
        _ = work.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        var turns = await work.WaitAsync(cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return turns;
    }

    /// <summary>
    /// Releases the native models, at once or when the running job ends. The diarizer
    /// cannot be used afterwards.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (!isRunning)
            {
                DestroyLocked();
            }
        }
    }

    void Finished()
    {
        lock (gate)
        {
            isRunning = false;
            if (disposed)
            {
                DestroyLocked();
            }
        }
        runs.Release();
    }

    void DestroyLocked()
    {
        if (handle != IntPtr.Zero)
        {
            SherpaNative.SherpaOnnxDestroyOfflineSpeakerDiarization(handle);
            handle = IntPtr.Zero;
        }
        if (extractor != IntPtr.Zero)
        {
            SherpaNative.SherpaOnnxDestroySpeakerEmbeddingExtractor(extractor);
            extractor = IntPtr.Zero;
        }
    }

    // Running.

    unsafe IReadOnlyList<SpeakerTurn> Run(float[] samples, SpeakerCountHint hint, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (samples.Length == 0)
        {
            // Silence is a valid track: it simply has no speakers.
            progress?.Report(1);
            return [];
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var reporter = new ProgressReporter(progress, cancellation);
        var reporterHandle = GCHandle.Alloc(reporter);
        IntPtr result;
        try
        {
            fixed (float* pointer = samples)
            {
                result = SherpaNative.SherpaOnnxOfflineSpeakerDiarizationProcessWithCallback(handle, pointer, samples.Length,
                    &OnProgress, GCHandle.ToIntPtr(reporterHandle));
            }
        }
        finally
        {
            reporterHandle.Free();
        }
        var turns = Turns(result);
        cancellation.ThrowIfCancellationRequested();
        var fine = turns.Select(turn => turn.Cluster).Distinct().Count();
        turns = Merged(turns, samples, hint, reporter, cancellation);
        reporter.ReportFraction(1);
        Logger.Info($"Separated {turns.Select(turn => turn.Cluster).Distinct().Count()} voices (from {fine}) in "
            + $"{samples.Length / (double)SampleRate:F0} s of audio in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
        return Renumbered(turns);
    }

    /// <summary>Reads and frees a native result: turns sorted by start, without empty spans.</summary>
    static unsafe List<SpeakerTurn> Turns(IntPtr result)
    {
        if (result == IntPtr.Zero)
        {
            return [];
        }
        try
        {
            var count = SherpaNative.SherpaOnnxOfflineSpeakerDiarizationResultGetNumSegments(result);
            if (count <= 0)
            {
                return [];
            }
            var segments = SherpaNative.SherpaOnnxOfflineSpeakerDiarizationResultSortByStartTime(result);
            if (segments == IntPtr.Zero)
            {
                return [];
            }
            try
            {
                var span = new ReadOnlySpan<SherpaNative.DiarizationSegment>((void*)segments, count);
                var turns = new List<SpeakerTurn>(count);
                foreach (var segment in span)
                {
                    double start = segment.Start, end = segment.End;
                    if (double.IsFinite(start) && double.IsFinite(end) && end > start && segment.Speaker >= 0)
                    {
                        turns.Add(new SpeakerTurn(start, end, segment.Speaker));
                    }
                }
                return turns.OrderBy(turn => turn.Start).ThenBy(turn => turn.End).ThenBy(turn => turn.Cluster).ToList();
            }
            finally
            {
                SherpaNative.SherpaOnnxOfflineSpeakerDiarizationDestroySegment(segments);
            }
        }
        finally
        {
            SherpaNative.SherpaOnnxOfflineSpeakerDiarizationDestroyResult(result);
        }
    }

    /// <summary>Cluster numbers in order of first appearance, from 0.</summary>
    internal static IReadOnlyList<SpeakerTurn> Renumbered(IEnumerable<SpeakerTurn> sorted)
    {
        var numbers = new Dictionary<int, int>();
        return sorted.Select(turn =>
        {
            if (!numbers.TryGetValue(turn.Cluster, out var number))
            {
                number = numbers.Count;
                numbers[turn.Cluster] = number;
            }
            return turn with { Cluster = number };
        }).ToList();
    }

    // The second pass.

    /// <summary>
    /// Joins the first pass's voices into people (<see cref="SpeakerClusterMerger"/>). A
    /// voice too short for an embedding joins the voice of the nearest turn that has one.
    /// </summary>
    List<SpeakerTurn> Merged(List<SpeakerTurn> turns, float[] samples, SpeakerCountHint hint, ProgressReporter reporter,
        CancellationToken cancellation)
    {
        var clusters = turns.Select(turn => turn.Cluster).Distinct().ToList();
        if (clusters.Count < 2)
        {
            return turns;
        }
        var voices = new Dictionary<int, SpeakerClusterMerger.Voice>();
        for (var index = 0; index < clusters.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var own = turns.Where(turn => turn.Cluster == clusters[index]).ToList();
            if (Centroid(own, samples) is { } centroid)
            {
                voices[clusters[index]] = new(centroid, own.Sum(turn => turn.End - turn.Start));
            }
            reporter.ReportFraction(FirstPassShare + (1 - FirstPassShare) * (index + 1) / clusters.Count);
        }
        if (voices.Count == 0)
        {
            return turns;
        }
        var mapping = SpeakerClusterMerger.Merge(voices, hint, Tuning.ClusteringThreshold);
        var anchors = turns.Where(turn => mapping.ContainsKey(turn.Cluster)).ToList();
        return turns.Select(turn =>
        {
            if (mapping.TryGetValue(turn.Cluster, out var target))
            {
                return turn with { Cluster = target };
            }
            var nearest = anchors.MinBy(anchor => Math.Max(0, Math.Max(anchor.Start - turn.End, turn.Start - anchor.End)));
            return turn with { Cluster = mapping[nearest.Cluster] };
        }).ToList();
    }

    /// <summary>The average normalized embedding of a voice's longest turns; null when none is long enough.</summary>
    unsafe float[]? Centroid(List<SpeakerTurn> turns, float[] samples)
    {
        var chosen = turns.Where(turn => turn.End - turn.Start >= CentroidMinimumTurnSeconds)
            .OrderByDescending(turn => turn.End - turn.Start)
            .Take(CentroidTurns)
            .ToList();
        if (chosen.Count == 0)
        {
            return null;
        }
        var model = Extractor();
        double[]? sum = null;
        foreach (var turn in chosen)
        {
            var start = Math.Clamp((int)(turn.Start * SampleRate), 0, samples.Length);
            var end = Math.Clamp((int)(Math.Min(turn.End, turn.Start + CentroidTurnSeconds) * SampleRate), start, samples.Length);
            if (end - start < CentroidMinimumTurnSeconds * SampleRate)
            {
                continue;
            }
            var stream = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorCreateStream(model);
            if (stream == IntPtr.Zero)
            {
                continue;
            }
            try
            {
                fixed (float* pointer = &samples[start])
                {
                    SherpaNative.SherpaOnnxOnlineStreamAcceptWaveform(stream, SampleRate, pointer, end - start);
                }
                SherpaNative.SherpaOnnxOnlineStreamInputFinished(stream);
                if (SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorIsReady(model, stream) == 0)
                {
                    continue;
                }
                var embedding = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(model, stream);
                if (embedding == null)
                {
                    continue;
                }
                try
                {
                    var dimension = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorDim(model);
                    var vector = SpeakerClusterMerger.Normalized(new ReadOnlySpan<float>(embedding, dimension).ToArray());
                    if (vector.Any(value => !double.IsFinite(value)))
                    {
                        continue;
                    }
                    sum ??= new double[dimension];
                    var weight = turn.End - turn.Start;
                    for (var k = 0; k < dimension; k++)
                    {
                        sum[k] += vector[k] * weight;
                    }
                }
                finally
                {
                    SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding(embedding);
                }
            }
            finally
            {
                SherpaNative.SherpaOnnxDestroyOnlineStream(stream);
            }
        }
        return sum is null ? null : SpeakerClusterMerger.Normalized(sum).Select(value => (float)value).ToArray();
    }

    IntPtr Extractor()
    {
        if (extractor != IntPtr.Zero)
        {
            return extractor;
        }
        using var config = new NativeConfig(typeof(SpeakerEmbeddingExtractorConfig));
        config.Set("Model", DiarizationModelFiles.EmbeddingPath(modelFolder))
            .Set("NumThreads", Threads)
            .Set("Provider", "cpu");
        var created = SherpaNative.SherpaOnnxCreateSpeakerEmbeddingExtractor(config.Pointer);
        if (created == IntPtr.Zero)
        {
            throw new SlovoException(CoreStrings.Format("diarization.error.loadFailed",
                CoreStrings.Format("diarization.error.sherpaRejected", modelFolder)));
        }
        extractor = created;
        return created;
    }

    // Progress.

    /// <summary>
    /// Forwards progress until the caller gives up: native progress (window embeddings done
    /// out of all) fills the first pass's share, centroids the rest. Never goes backwards.
    /// </summary>
    sealed class ProgressReporter(IProgress<double>? progress, CancellationToken cancellation)
    {
        double last;

        public void Report(int processed, int total)
        {
            if (total > 0)
            {
                ReportFraction(FirstPassShare * processed / total);
            }
        }

        public void ReportFraction(double fraction)
        {
            if (progress is null || cancellation.IsCancellationRequested || !double.IsFinite(fraction))
            {
                return;
            }
            fraction = Math.Clamp(fraction, 0, 1);
            // A report per embedding would flood the interface on a long meeting.
            if (fraction - last < 0.01)
            {
                return;
            }
            last = fraction;
            progress.Report(fraction);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnProgress(int processed, int total, IntPtr argument)
    {
        try
        {
            if (GCHandle.FromIntPtr(argument).Target is ProgressReporter reporter)
            {
                reporter.Report(processed, total);
            }
        }
        catch (Exception error)
        {
            // Nothing may unwind into native code.
            Logger.Error("Diarization progress failed", error);
        }
        return 0;
    }
}
