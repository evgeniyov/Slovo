namespace Slovo.Core.Model;

public static class EngineIds
{
    /// <summary>Engines Windows can run, in the order Settings shows them.</summary>
    public static readonly IReadOnlyList<EngineId> Available = [EngineId.GigaamRnnt, EngineId.GigaamCtc, EngineId.Parakeet];

    /// <summary>Choices for Russian and mixed speech, recommended first.</summary>
    public static readonly IReadOnlyList<EngineId> RussianChoices = [EngineId.GigaamRnnt, EngineId.GigaamCtc, EngineId.Parakeet];

    /// <summary>Choices for English speech, recommended first.</summary>
    public static readonly IReadOnlyList<EngineId> EnglishChoices = [EngineId.Parakeet, EngineId.GigaamRnnt];

    /// <summary>Choices for meeting transcription, default first.</summary>
    public static readonly IReadOnlyList<EngineId> MeetingChoices = [EngineId.GigaamCtc, EngineId.Parakeet];

    /// <summary>Product names are the same in every interface language.</summary>
    public static string DisplayName(this EngineId id) => id switch
    {
        EngineId.GigaamRnnt => "GigaAM v3 RNNT",
        EngineId.GigaamCtc => "GigaAM v3 CTC",
        EngineId.Parakeet => "Parakeet v3",
        EngineId.AppleDictation => "Apple Dictation",
        _ => id.ToString(),
    };

    public static string RawValue(this EngineId id) => EnumText.Raw(id);

    /// <summary>
    /// The model folder relative to the models root. GigaAM uses the very same sherpa-onnx
    /// release folders as the macOS app. Parakeet differs: macOS runs FluidAudio's Core ML
    /// build, Windows the sherpa-onnx int8 ONNX export.
    /// </summary>
    public static string? ModelFolder(this EngineId id) => id switch
    {
        EngineId.GigaamRnnt => "gigaam-v3/sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16",
        EngineId.GigaamCtc => "gigaam-v3/sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16",
        EngineId.Parakeet => "parakeet-v3-onnx/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
        _ => null,
    };

    public static bool IsAvailableOnWindows(this EngineId id) => id != EngineId.AppleDictation;
}

/// <summary>What an engine is chosen for: dictation in a language mode, or meetings.</summary>
public readonly record struct EnginePurpose(bool IsMeetings, LanguageMode LanguageMode)
{
    public static EnginePurpose Dictation(LanguageMode mode) => new(false, mode);
    public static readonly EnginePurpose Meetings = new(true, LanguageMode.Mixed);
}

public enum ModelStatusKind
{
    Unknown,
    Ready,
    NotDownloaded,
    Downloading,
    Damaged,
}

/// <summary>Whether an engine's model files are on disk and usable.</summary>
public readonly record struct ModelStatus(ModelStatusKind Kind, long Bytes = 0, double Progress = 0, string? Reason = null)
{
    public static readonly ModelStatus Unknown = new(ModelStatusKind.Unknown);
    public static ModelStatus Ready(long diskBytes) => new(ModelStatusKind.Ready, diskBytes);
    public static ModelStatus NotDownloaded(long downloadBytes) => new(ModelStatusKind.NotDownloaded, downloadBytes);
    public static ModelStatus Downloading(double progress) => new(ModelStatusKind.Downloading, Progress: progress);
    /// <summary>Files are present but truncated or unreadable; the reason is already localized.</summary>
    public static ModelStatus Damaged(string reason) => new(ModelStatusKind.Damaged, Reason: reason);

    public bool IsUsable => Kind == ModelStatusKind.Ready;
}

public enum EngineLoadStateKind
{
    Unloaded,
    Loading,
    Ready,
    Failed,
}

public readonly record struct EngineLoadState(EngineLoadStateKind Kind, string? Message = null)
{
    public static readonly EngineLoadState Unloaded = new(EngineLoadStateKind.Unloaded);
    public static readonly EngineLoadState Loading = new(EngineLoadStateKind.Loading);
    public static readonly EngineLoadState Ready = new(EngineLoadStateKind.Ready);
    public static EngineLoadState Failed(string message) => new(EngineLoadStateKind.Failed, message);
}
