using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Engines.Audio;
using Slovo.Engines.Diarization;

namespace Slovo.Engines.Tests.Diarization;

/// <summary>
/// The real speaker models on the two-voice fixture (Dmitri, Kathleen, Dmitri). They run
/// when <c>SLOVO_DIARIZATION_MODELS_DIR</c> points at a folder laid out like
/// <c>Models/diarization-onnx</c> and skip otherwise; the tests never download anything.
/// </summary>
public class RealDiarizationTests
{
    /// <summary>The parts of two-voices.wav: start, end and who speaks.</summary>
    static readonly (double Start, double End, string Voice)[] Parts = [(0.00, 8.76, "Dmitri"), (9.56, 15.46, "Kathleen"), (16.26, 21.53, "Dmitri")];

    static float[] TwoVoices() => AudioFileReader.ReadMono16k(TestSupport.Fixture("two-voices.wav"), TestContext.Current.CancellationToken);

    /// <summary>The cluster that covers most of a part.</summary>
    static int ClusterOf((double Start, double End, string Voice) part, IReadOnlyList<SpeakerTurn> turns) =>
        turns.GroupBy(turn => turn.Cluster)
            .Select(group => (Cluster: group.Key, Overlap: group.Sum(turn => Math.Max(0, Math.Min(turn.End, part.End) - Math.Max(turn.Start, part.Start)))))
            .OrderByDescending(pair => pair.Overlap)
            .First().Cluster;

    static void AssertSeparatesTheTwoVoices(IReadOnlyList<SpeakerTurn> turns)
    {
        Assert.NotEmpty(turns);
        Assert.Equal(turns.OrderBy(turn => turn.Start).ThenBy(turn => turn.End).ThenBy(turn => turn.Cluster), turns);
        Assert.Equal(0, turns[0].Cluster);
        var clusters = Parts.Select(part => ClusterOf(part, turns)).ToArray();
        Assert.Equal(clusters[0], clusters[2]);
        Assert.NotEqual(clusters[0], clusters[1]);
        // Speech is found where the voices speak.
        var speech = turns.Sum(turn => turn.End - turn.Start);
        Assert.InRange(speech, 14, 21.6);
    }

    [Fact]
    public async Task SeparatesTwoVoicesAutomatically()
    {
        var folder = TestSupport.RequireDiarizationModels();
        using var diarizer = SherpaSpeakerDiarizer.Load(folder);
        var reports = new List<double>();
        var progress = new SynchronousProgress(reports.Add);

        var samples = TwoVoices();
        var turns = await diarizer.DiarizeAsync(samples, SpeakerCountHint.Automatic, progress, TestContext.Current.CancellationToken);

        AssertSeparatesTheTwoVoices(turns);
        Assert.Equal(2, turns.Select(turn => turn.Cluster).Distinct().Count());
        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1]);
        Assert.Equal(reports.Order(), reports);
        var normalized = SpeakerTurns.Normalize(turns, samples.Length / 16_000d, SpeakerCountHint.Automatic);
        AssertSeparatesTheTwoVoices(normalized);
    }

    [Fact]
    public async Task HonoursAnExactCountAndAMaximum()
    {
        var folder = TestSupport.RequireDiarizationModels();
        using var diarizer = SherpaSpeakerDiarizer.Load(folder);
        var samples = TwoVoices();

        var exactlyTwo = await diarizer.DiarizeAsync(samples, new SpeakerCountHint(Exactly: 2), null, TestContext.Current.CancellationToken);
        AssertSeparatesTheTwoVoices(exactlyTwo);

        var one = await diarizer.DiarizeAsync(samples, new SpeakerCountHint(Exactly: 1), null, TestContext.Current.CancellationToken);
        Assert.Single(one.Select(turn => turn.Cluster).Distinct());

        // Thresholds that split every voice, capped at two, merge back into the two voices.
        using var eager = SherpaSpeakerDiarizer.Load(folder, SherpaDiarizationTuning.Standard with { SplitThreshold = 0.02, ClusteringThreshold = 0.05 });
        var split = await eager.DiarizeAsync(samples, SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken);
        Assert.True(split.Select(turn => turn.Cluster).Distinct().Count() > 2, $"{split.Select(turn => turn.Cluster).Distinct().Count()} voices");
        var capped = await eager.DiarizeAsync(samples, new SpeakerCountHint(Maximum: 2), null, TestContext.Current.CancellationToken);
        AssertSeparatesTheTwoVoices(capped);
        Assert.Equal(2, capped.Select(turn => turn.Cluster).Distinct().Count());
    }

    [Fact]
    public async Task SilenceAndEmptyInputHaveNoSpeakers()
    {
        var folder = TestSupport.RequireDiarizationModels();
        using var diarizer = SherpaSpeakerDiarizer.Load(folder);

        Assert.Empty(await diarizer.DiarizeAsync([], SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken));
        Assert.Empty(await diarizer.DiarizeAsync(new float[16_000 * 3], SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken));
        Assert.Empty(await diarizer.DiarizeAsync(new float[16_000 * 25], SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingEndsTheWaitAndTheDiarizerStaysUsable()
    {
        var folder = TestSupport.RequireDiarizationModels();
        using var diarizer = SherpaSpeakerDiarizer.Load(folder);
        var samples = TwoVoices();
        // Repeat the fixture to keep the native run busy during cancellation.
        var longer = Enumerable.Repeat(samples, 7).SelectMany(part => part).ToArray();
        using var cancellation = new CancellationTokenSource();

        var run = diarizer.DiarizeAsync(longer, SpeakerCountHint.Automatic, null, cancellation.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var cancelledAt = System.Diagnostics.Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(cancelledAt.Elapsed < TimeSpan.FromSeconds(1), $"The wait ended {cancelledAt.Elapsed} after the cancellation");

        // The next run waits for the abandoned one and then works.
        var turns = await diarizer.DiarizeAsync(samples, SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken);
        AssertSeparatesTheTwoVoices(turns);
    }

    [Fact]
    public async Task TheServiceLoadsFromTheModelsFolderAndReleases()
    {
        var folder = TestSupport.RequireDiarizationModels();
        var root = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar))!;
        using var temporary = new TemporaryFolder();
        // The service reads Models/diarization-onnx; link the test models there.
        Directory.CreateSymbolicLink(Path.Combine(temporary.Path, DiarizationModelFiles.FolderName), Path.GetFullPath(folder));
        var service = new SherpaDiarizationService(temporary.Path);
        Assert.True(service.Status.IsUsable, $"{service.Status}");
        Assert.NotEqual("", root);

        var diarizer = await service.DiarizerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EngineLoadStateKind.Ready, service.LoadState.Kind);
        AssertSeparatesTheTwoVoices(await diarizer.DiarizeAsync(TwoVoices(), SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken));
        Assert.Equal(SherpaDiarizationTuning.Standard.Description, service.Configuration);
        Assert.Contains("pyannote-segmentation-3.0", service.ModelName);

        service.Release();
        Assert.Equal(EngineLoadStateKind.Unloaded, service.LoadState.Kind);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            diarizer.DiarizeAsync(TwoVoices(), SpeakerCountHint.Automatic, null, TestContext.Current.CancellationToken));
    }
}

/// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
sealed class SynchronousProgress(Action<double> report) : IProgress<double>
{
    readonly Lock gate = new();

    public void Report(double value)
    {
        lock (gate)
        {
            report(value);
        }
    }
}
