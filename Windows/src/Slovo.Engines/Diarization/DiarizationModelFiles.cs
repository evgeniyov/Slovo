using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Diarization;

/// <summary>
/// The speaker model files, checked on disk without loading them (the counterpart of
/// DiarizationModelFiles.swift).
///
/// Windows separates speakers with sherpa-onnx: pyannote segmentation 3.0 finds who speaks
/// when inside 10-second windows, and WeSpeaker's ResNet34-LM (trained on VoxCeleb, whose
/// speakers talk in many languages) turns each voice into an embedding that clustering
/// groups across the recording. This is the pairing of pyannote's own 3.x pipeline.
/// Both live in <c>Models/diarization-onnx</c>:
/// <code>
/// diarization-onnx/
///   sherpa-onnx-pyannote-segmentation-3-0/model.onnx   (from the release archive)
///   wespeaker_en_voxceleb_resnet34_LM.onnx
/// </code>
/// </summary>
public static class DiarizationModelFiles
{
    public const string FolderName = "diarization-onnx";

    const string ReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";

    public const string SegmentationFolderName = "sherpa-onnx-pyannote-segmentation-3-0";

    /// <summary>The segmentation model inside its release folder (the fp32 export; the int8 one is less accurate).</summary>
    public static readonly ModelFile Segmentation = new("model.onnx", 5_992_913);

    public static readonly ModelFile Embedding = new("wespeaker_en_voxceleb_resnet34_LM.onnx", 26_530_550);

    public static readonly Uri SegmentationArchiveUrl =
        new(ReleaseUrl + "speaker-segmentation-models/" + SegmentationFolderName + ".tar.bz2");

    public const long SegmentationArchiveBytes = 6_958_444;

    /// <summary>The release tag really is spelled "recongition".</summary>
    public static readonly Uri EmbeddingUrl = new(ReleaseUrl + "speaker-recongition-models/" + Embedding.Name);

    /// <summary>What a download fetches: the segmentation archive and the embedding model.</summary>
    public const long DownloadBytes = SegmentationArchiveBytes + 26_530_550;

    /// <summary>Recorded in <c>MeetingDiarization.model</c>.</summary>
    public const string ModelIdentifier = "pyannote-segmentation-3.0+wespeaker-voxceleb-resnet34-lm@sherpa-onnx";

    public static string ModelFolder(string modelsRoot) => Path.Combine(modelsRoot, FolderName);

    public static string SegmentationPath(string modelFolder) =>
        Path.Combine(modelFolder, SegmentationFolderName, Segmentation.Name);

    public static string EmbeddingPath(string modelFolder) => Path.Combine(modelFolder, Embedding.Name);

    /// <summary>
    /// Both models have their release size. ONNX Runtime aborts the whole process on a
    /// truncated model instead of returning an error, so size is what decides.
    /// </summary>
    public static bool IsComplete(string modelFolder) =>
        ModelFiles.RegularFileSize(SegmentationPath(modelFolder)) == Segmentation.Size
        && ModelFiles.RegularFileSize(EmbeddingPath(modelFolder)) == Embedding.Size;

    public static ModelStatus Status(string modelFolder)
    {
        if (IsComplete(modelFolder))
        {
            return ModelStatus.Ready(ModelFiles.FolderSize(modelFolder));
        }
        // Empty folders left by a cancelled download count as nothing downloaded.
        if (ModelFiles.ContainsFiles(modelFolder, ignoreDotFiles: true))
        {
            return ModelStatus.Damaged(CoreStrings.Get("diarization.error.modelDamaged"));
        }
        return ModelStatus.NotDownloaded(DownloadBytes);
    }
}
