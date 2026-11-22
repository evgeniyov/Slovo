using System.Runtime.InteropServices;
using SherpaOnnx;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Engines.Native;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Tests.Recognition;

/// <summary>What the engine does before any native code runs: file checks, messages, text joining.</summary>
public sealed class SherpaTranscriptionEngineTests : IDisposable
{
    readonly TemporaryFolder temporary = new("SlovoEngineTests");

    public void Dispose() => temporary.Dispose();

    [Fact]
    public void PiecesAreJoinedWithSingleSpaces()
    {
        Assert.Equal("Привет. Как дела?", SherpaTranscriptionEngine.JoinPieces(["Привет.", "", "  Как  дела? "]));
        Assert.Equal("", SherpaTranscriptionEngine.JoinPieces([]));
    }

    [Fact]
    public async Task ATruncatedGigaAMFileIsNamedAndNothingIsLoaded()
    {
        TestSupport.InstallFakeModel(ModelCatalog.GigaamCtc, temporary.Path, resize: "tokens.txt", delta: 5);
        using var engine = new SherpaTranscriptionEngine(EngineId.GigaamCtc, temporary.Path);

        var error = await Assert.ThrowsAsync<SlovoException>(() => engine.PrepareAsync(TestContext.Current.CancellationToken));

        Assert.Equal(CoreStrings.Format("engines.gigaam.damagedFiles", "ctc", "tokens.txt") + " "
            + CoreStrings.Format("engines.gigaam.expectedFolder", ModelCatalog.GigaamCtc.FolderPath(temporary.Path)), error.Message);
    }

    [Fact]
    public async Task AnIncompleteParakeetIsReportedAsDamaged()
    {
        var folder = ModelCatalog.Parakeet.FolderPath(temporary.Path);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "tokens.txt"), "x");
        using var engine = new SherpaTranscriptionEngine(EngineId.Parakeet, temporary.Path);

        var error = await Assert.ThrowsAsync<SlovoException>(() => engine.TranscribeAsync(new float[16_000], TestContext.Current.CancellationToken));

        Assert.Contains("encoder.int8.onnx", error.Message);
        Assert.Contains("tokens.txt", error.Message);
        Assert.Contains(folder, error.Message);
        Assert.Equal("parakeet-v3", engine.Id);
        Assert.Equal(folder, engine.ModelFolder);
    }

    [Fact]
    public async Task ACancelledCallNeverStarts()
    {
        using var engine = new SherpaTranscriptionEngine(EngineId.GigaamRnnt, temporary.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranscribeAsync(new float[10], cancellation.Token));
    }

    [Fact]
    public void AppleDictationHasNoModelHere()
    {
        Assert.Throws<ArgumentException>(() => new SherpaTranscriptionEngine(EngineId.AppleDictation, temporary.Path));
        Assert.Null(ModelCatalog.For(EngineId.AppleDictation));
    }

    [Fact]
    public void TheCatalogMatchesTheSharedFolders()
    {
        foreach (var spec in ModelCatalog.All)
        {
            Assert.Equal(spec.Engine.ModelFolder(), spec.Folder);
            Assert.Equal(spec.ReleaseFolderName + ".tar.bz2", spec.ArchiveName);
            Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/", spec.ArchiveUrl.AbsoluteUri);
            Assert.Equal("tokens.txt", spec.Tokens);
        }
        Assert.Equal("encoder.int8.onnx", ModelCatalog.GigaamRnnt.Encoder);
        Assert.Equal("joiner.onnx", ModelCatalog.GigaamRnnt.Joiner);
        Assert.Equal("model.int8.onnx", ModelCatalog.GigaamCtc.CtcModel);
        Assert.Equal("decoder.int8.onnx", ModelCatalog.Parakeet.Decoder);
        Assert.Equal("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2", ModelCatalog.Parakeet.ArchiveName);
        Assert.Throws<InvalidOperationException>(() => ModelCatalog.GigaamCtc.Encoder);
    }

    [Fact]
    public void NativeConfigsUseTheWrapperLayoutAndUtf8()
    {
        using var config = new NativeConfig(typeof(OfflineRecognizerConfig));
        var (offset, type) = config.Field("ModelConfig.Transducer.Encoder");
        Assert.Equal(typeof(string), type);
        Assert.InRange(offset, 0, Marshal.SizeOf<OfflineRecognizerConfig>() - IntPtr.Size);

        config.Set("ModelConfig.Transducer.Encoder", "C:\\Users\\Тест\\модель.onnx").Set("ModelConfig.NumThreads", 3).Set("HotwordsScore", 1.5f);
        Assert.Equal("C:\\Users\\Тест\\модель.onnx", Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(config.Pointer, offset)));
        Assert.Equal(3, Marshal.ReadInt32(config.Pointer, config.Field("ModelConfig.NumThreads").Offset));
        Assert.Equal(1.5f, BitConverter.Int32BitsToSingle(Marshal.ReadInt32(config.Pointer, config.Field("HotwordsScore").Offset)));
        // Everything else stays zero, which sherpa-onnx reads as its defaults.
        Assert.Equal(IntPtr.Zero, Marshal.ReadIntPtr(config.Pointer, config.Field("ModelConfig.Tokens").Offset));

        Assert.Throws<InvalidOperationException>(() => config.Set("ModelConfig.NumThreads", "four"));
        Assert.Throws<InvalidOperationException>(() => config.Set("ModelConfig.NoSuchField", 1));

        using var diarization = new NativeConfig(typeof(OfflineSpeakerDiarizationConfig));
        Assert.Equal(typeof(float), diarization.Field("Clustering.Threshold").Type);
        Assert.Equal(typeof(float), diarization.Field("Segmentation.Pyannote.WindowShiftRatio").Type);
    }
}
