using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Engines.Audio;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Tests;

/// <summary>
/// The real GigaAM models on the speech fixtures. They run when the models are installed
/// (<c>SLOVO_MODELS_DIR</c> or the macOS app's folder, only read) and skip otherwise.
/// </summary>
public class RealModelTests
{
    static string Expected(string fixture) => File.ReadAllText(TestSupport.Fixture(fixture)).Trim();

    /// <summary>Lower case letters and digits only, so punctuation and case differences do not matter.</summary>
    static string Words(string text) =>
        string.Join(' ', new string(text.ToLowerInvariant().Replace('ё', 'е').Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData(EngineId.GigaamRnnt)]
    [InlineData(EngineId.GigaamCtc)]
    public async Task GigaAMTranscribesTheRussianFixture(EngineId id)
    {
        var root = TestSupport.RequireRealModel(id);
        using var engine = new SherpaTranscriptionEngine(id, root);
        await engine.PrepareAsync(TestContext.Current.CancellationToken);

        var text = await engine.TranscribeFileAsync(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken);

        Assert.Equal(Words(Expected("ru-hello.txt")), Words(text));
        // Both models restore sentence casing. Terminal punctuation can vary.
        Assert.True(char.IsUpper(text[0]), text);
    }

    [Fact]
    public async Task GigaAMReadsPathsWithCyrillicLetters()
    {
        var root = TestSupport.RequireRealModel(EngineId.GigaamCtc);
        using var temporary = new TemporaryFolder();
        var spec = ModelCatalog.GigaamCtc;
        var models = Path.Combine(temporary.Path, "Модели Слово");
        var folder = spec.FolderPath(models);
        Directory.CreateDirectory(folder);
        foreach (var file in spec.Files)
        {
            File.CreateSymbolicLink(Path.Combine(folder, file.Name), Path.Combine(spec.FolderPath(root), file.Name));
        }
        using var engine = new SherpaTranscriptionEngine(EngineId.GigaamCtc, models);

        var text = await engine.TranscribeFileAsync(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken);

        Assert.Equal(Words(Expected("ru-hello.txt")), Words(text));
    }

    [Fact]
    public async Task AReleasedEngineLoadsAgainWhenStillUsed()
    {
        var root = TestSupport.RequireRealModel(EngineId.GigaamCtc);
        var engine = new SherpaTranscriptionEngine(EngineId.GigaamCtc, root);
        var transcription = engine.TranscribeFileAsync(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken);
        // Released while it decodes: the decode finishes with the loaded model.
        engine.Dispose();
        Assert.Equal(Words(Expected("ru-hello.txt")), Words(await transcription));

        Assert.Equal(Words(Expected("ru-hello.txt")),
            Words(await engine.TranscribeFileAsync(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken)));
        engine.Dispose();
    }

    [Fact]
    public async Task TheEngineManagerLoadsTheRealEngine()
    {
        var root = TestSupport.RequireRealModel(EngineId.GigaamRnnt);
        var settings = new Core.Settings.AppSettings(Core.Settings.SettingsStore.InMemory());
        var models = new Slovo.Engines.Models.ModelManager(root);
        using var manager = new EngineManager(settings, models, EngineManager.Factory(root));

        var engine = await manager.EngineAsync(EngineId.GigaamRnnt, TestContext.Current.CancellationToken);

        Assert.Equal(EngineLoadStateKind.Ready, manager.LoadState(EngineId.GigaamRnnt).Kind);
        Assert.Equal(Words(Expected("ru-hello.txt")),
            Words(await engine.TranscribeFileAsync(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ShortAndEmptyInputIsSafe()
    {
        var root = TestSupport.RequireRealModel(EngineId.GigaamRnnt);
        using var engine = new SherpaTranscriptionEngine(EngineId.GigaamRnnt, root);

        Assert.Equal("", await engine.TranscribeAsync([], TestContext.Current.CancellationToken));
        // A tenth of a second of silence is under one feature frame's worth once padded away.
        var silence = await engine.TranscribeAsync(new float[1_600], TestContext.Current.CancellationToken);
        Assert.True(silence.Length < 5, silence);
        var single = await engine.TranscribeAsync([0.1f], TestContext.Current.CancellationToken);
        Assert.NotNull(single);
    }

    [Fact]
    public async Task LongAudioIsCutAtPausesAndStillReadsEveryPart()
    {
        var root = TestSupport.RequireRealModel(EngineId.GigaamCtc);
        using var engine = new SherpaTranscriptionEngine(EngineId.GigaamCtc, root);
        var hello = AudioFileReader.ReadMono16k(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken);
        // Nine repetitions with pauses: over 50 s, so the chunker cuts at least once.
        var pause = new float[16_000];
        var long_ = Enumerable.Range(0, 9).SelectMany(_ => hello.Concat(pause)).ToArray();
        Assert.True(GigaAMChunker.Ranges(long_).Count >= 2);

        var text = await engine.TranscribeAsync(long_, TestContext.Current.CancellationToken);

        var expected = Words(Expected("ru-hello.txt"));
        var occurrences = (Words(text).Length - Words(text).Replace(expected, "").Length) / expected.Length;
        Assert.Equal(9, occurrences);
    }

    [Fact]
    public async Task CancellationStopsBetweenChunks()
    {
        var root = TestSupport.RequireRealModel(EngineId.GigaamCtc);
        using var engine = new SherpaTranscriptionEngine(EngineId.GigaamCtc, root);
        await engine.PrepareAsync(TestContext.Current.CancellationToken);
        var noise = new float[16_000 * 600];
        var random = new Random(1);
        for (var index = 0; index < noise.Length; index++)
        {
            noise[index] = (float)(random.NextDouble() - 0.5) * 0.01f;
        }
        using var cancellation = new CancellationTokenSource();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var transcription = engine.TranscribeAsync(noise, cancellation.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"Ten minutes of audio were decoded after the cancellation ({started.Elapsed})");
        // The engine still works afterwards.
        Assert.Equal(Words(Expected("ru-hello.txt")),
            Words(await engine.TranscribeFileAsync(TestSupport.Fixture("ru-hello.wav"), TestContext.Current.CancellationToken)));
    }
}
