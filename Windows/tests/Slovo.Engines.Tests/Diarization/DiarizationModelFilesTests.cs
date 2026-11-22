using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Engines.Diarization;

namespace Slovo.Engines.Tests.Diarization;

/// <summary>Fixtures for the diarization tests: fake model files of the release sizes in temporary folders.</summary>
static class DiarizationFixtures
{
    /// <summary>Writes both models (sparse where the file system can), optionally one of them the wrong size. Returns the bytes.</summary>
    public static long InstallModel(string modelFolder, bool skipEmbedding = false, bool truncateSegmentation = false)
    {
        var segmentation = DiarizationModelFiles.SegmentationPath(modelFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(segmentation)!);
        using (var stream = new FileStream(segmentation, FileMode.Create))
        {
            stream.SetLength(DiarizationModelFiles.Segmentation.Size - (truncateSegmentation ? 1 : 0));
        }
        long bytes = DiarizationModelFiles.Segmentation.Size - (truncateSegmentation ? 1 : 0);
        if (!skipEmbedding)
        {
            using var stream = new FileStream(DiarizationModelFiles.EmbeddingPath(modelFolder), FileMode.Create);
            stream.SetLength(DiarizationModelFiles.Embedding.Size);
            bytes += DiarizationModelFiles.Embedding.Size;
        }
        return bytes;
    }
}

public sealed class DiarizationModelFilesTests : IDisposable
{
    readonly TemporaryFolder temporary = new("SlovoDiarizationFiles");

    string Folder => Path.Combine(temporary.Path, DiarizationModelFiles.FolderName);

    public void Dispose() => temporary.Dispose();

    static ModelStatus Damaged => ModelStatus.Damaged(CoreStrings.Get("diarization.error.modelDamaged"));

    [Fact]
    public void NoFolderIsNotDownloaded()
    {
        Assert.Equal(ModelStatus.NotDownloaded(33_488_994), DiarizationModelFiles.Status(Folder));
        Assert.False(DiarizationModelFiles.IsComplete(Folder));
    }

    [Fact]
    public void EmptyFoldersAndHiddenFilesAreNotDownloaded()
    {
        Directory.CreateDirectory(Path.Combine(Folder, DiarizationModelFiles.SegmentationFolderName));
        File.WriteAllBytes(Path.Combine(Folder, ".DS_Store"), []);
        Assert.Equal(ModelStatus.NotDownloaded(DiarizationModelFiles.DownloadBytes), DiarizationModelFiles.Status(Folder));
    }

    [Fact]
    public void ACompleteModelIsReadyWithItsSize()
    {
        var bytes = DiarizationFixtures.InstallModel(Folder);
        Assert.True(DiarizationModelFiles.IsComplete(Folder));
        Assert.Equal(ModelStatus.Ready(bytes), DiarizationModelFiles.Status(Folder));
        Assert.Equal(Path.Combine(temporary.Path, "diarization-onnx"), DiarizationModelFiles.ModelFolder(temporary.Path));
    }

    [Fact]
    public void AMissingOrTruncatedModelIsDamaged()
    {
        DiarizationFixtures.InstallModel(Folder, skipEmbedding: true);
        Assert.Equal(Damaged, DiarizationModelFiles.Status(Folder));

        DiarizationFixtures.InstallModel(Folder, truncateSegmentation: true);
        Assert.Equal(Damaged, DiarizationModelFiles.Status(Folder));
    }

    [Fact]
    public void TheReleaseAssetsAreTheOnesChecked()
    {
        Assert.Equal("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
            DiarizationModelFiles.SegmentationArchiveUrl.AbsoluteUri);
        Assert.Equal("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx",
            DiarizationModelFiles.EmbeddingUrl.AbsoluteUri);
    }

    [Fact]
    public void LoadingMissingOrDamagedModelsFailsWithoutTouchingNativeCode()
    {
        var missing = Assert.Throws<SlovoException>(() => SherpaSpeakerDiarizer.Load(Folder));
        Assert.Equal(CoreStrings.Get("diarization.error.modelMissing"), missing.Message);

        DiarizationFixtures.InstallModel(Folder, skipEmbedding: true);
        var damaged = Assert.Throws<SlovoException>(() => SherpaSpeakerDiarizer.Load(Folder));
        Assert.Equal(CoreStrings.Get("diarization.error.modelDamaged"), damaged.Message);
    }
}
