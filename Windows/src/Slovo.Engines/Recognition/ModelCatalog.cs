using Slovo.Core.Model;

namespace Slovo.Engines.Recognition;

/// <summary>A file a model needs, with its size in bytes in the release archive.</summary>
public sealed record ModelFile(string Name, long Size);

/// <summary>How sherpa-onnx runs a model.</summary>
public enum SherpaModelKind
{
    /// <summary>A NeMo transducer (encoder, decoder, joiner): GigaAM RNNT and Parakeet TDT.</summary>
    NemoTransducer,
    /// <summary>A NeMo CTC model: GigaAM CTC.</summary>
    NemoCtc,
}

/// <summary>
/// Everything about one engine's model: where it lives under the models root, which
/// files it needs (ONNX Runtime aborts the whole process on a truncated model instead of
/// returning an error, so every file must match the release size), where its archive is
/// and how big it is.
/// </summary>
/// <param name="Folder">The model folder relative to the models root (<c>EngineId.ModelFolder()</c>).</param>
/// <param name="Files">Transducers list encoder, decoder, joiner, tokens; CTC models the model, then tokens.</param>
/// <param name="DownloadBytes">Approximate size shown next to "Not downloaded": the extracted release folder.</param>
/// <param name="FeatureDim">Mel bins the model expects; sherpa-onnx also reads them from the model metadata.</param>
/// <param name="MaxChunkSeconds">The longest piece of audio one decode gets; longer recordings are cut at pauses.</param>
public sealed record SherpaModelSpec(
    EngineId Engine,
    SherpaModelKind Kind,
    string Folder,
    IReadOnlyList<ModelFile> Files,
    long DownloadBytes,
    int FeatureDim,
    double MaxChunkSeconds)
{
    /// <summary>The release folder name, which is also the archive's name and its top folder.</summary>
    public string ReleaseFolderName => Folder[(Folder.LastIndexOf('/') + 1)..];

    /// <summary>The <c>.tar.bz2</c> in the sherpa-onnx <c>asr-models</c> release.</summary>
    public string ArchiveName => ReleaseFolderName + ".tar.bz2";

    public Uri ArchiveUrl => new(ModelCatalog.ReleaseUrl + ArchiveName);

    /// <summary>The model folder under a models root.</summary>
    public string FolderPath(string modelsRoot) =>
        Path.Combine(modelsRoot, Folder.Replace('/', Path.DirectorySeparatorChar));

    public string Encoder => Kind == SherpaModelKind.NemoTransducer ? Files[0].Name : throw new InvalidOperationException();
    public string Decoder => Kind == SherpaModelKind.NemoTransducer ? Files[1].Name : throw new InvalidOperationException();
    public string Joiner => Kind == SherpaModelKind.NemoTransducer ? Files[2].Name : throw new InvalidOperationException();
    public string CtcModel => Kind == SherpaModelKind.NemoCtc ? Files[0].Name : throw new InvalidOperationException();
    public string Tokens => Files[^1].Name;
}

/// <summary>
/// The models Windows runs (the counterpart of <c>GigaAMEngine.Variant.files</c>,
/// <c>ModelManager.downloadBytes</c> and <c>LiveModelDownloader</c> on macOS).
/// </summary>
public static class ModelCatalog
{
    /// <summary>GigaAM and Parakeet archives come from the sherpa-onnx <c>asr-models</c> release.</summary>
    public const string ReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";

    /// <summary>
    /// GigaAM is trained on segments up to about 25 seconds and degrades on longer input
    /// (GigaAMChunker's default). Parakeet gets the same windows: sherpa-onnx's export
    /// attends over the whole input, so memory grows with its square.
    /// </summary>
    const double MaxChunkSeconds = 20;

    // Sizes of the files in the release archives. GigaAM matches GigaAMEngine.Variant.files
    // on macOS; Parakeet was read from the tar headers of the published archive.
    public static readonly SherpaModelSpec GigaamRnnt = new(
        EngineId.GigaamRnnt, SherpaModelKind.NemoTransducer, EngineId.GigaamRnnt.ModelFolder()!,
        [new("encoder.int8.onnx", 224_570_820), new("decoder.onnx", 4_600_132),
         new("joiner.onnx", 2_712_896), new("tokens.txt", 13_354)],
        // The same numbers as the macOS app: the extracted release folders. The bzip2
        // archives are only a little smaller because int8 weights compress poorly.
        DownloadBytes: 232_300_000, FeatureDim: 64, MaxChunkSeconds);

    public static readonly SherpaModelSpec GigaamCtc = new(
        EngineId.GigaamCtc, SherpaModelKind.NemoCtc, EngineId.GigaamCtc.ModelFolder()!,
        [new("model.int8.onnx", 224_893_661), new("tokens.txt", 2_007)],
        DownloadBytes: 225_300_000, FeatureDim: 64, MaxChunkSeconds);

    public static readonly SherpaModelSpec Parakeet = new(
        EngineId.Parakeet, SherpaModelKind.NemoTransducer, EngineId.Parakeet.ModelFolder()!,
        [new("encoder.int8.onnx", 652_184_281), new("decoder.int8.onnx", 11_845_275),
         new("joiner.int8.onnx", 6_355_277), new("tokens.txt", 93_939)],
        // The extracted folder with its test recordings; the archive itself is 487 MB.
        DownloadBytes: 671_200_000, FeatureDim: 80, MaxChunkSeconds);

    public static IReadOnlyList<SherpaModelSpec> All { get; } = [GigaamRnnt, GigaamCtc, Parakeet];

    /// <summary>The model of an engine, or null for Apple Dictation.</summary>
    public static SherpaModelSpec? For(EngineId id) => id switch
    {
        EngineId.GigaamRnnt => GigaamRnnt,
        EngineId.GigaamCtc => GigaamCtc,
        EngineId.Parakeet => Parakeet,
        _ => null,
    };

    /// <summary>Approximate download size shown next to "Not downloaded"; 0 for engines without files.</summary>
    public static long DownloadBytes(EngineId id) => For(id)?.DownloadBytes ?? 0;
}
