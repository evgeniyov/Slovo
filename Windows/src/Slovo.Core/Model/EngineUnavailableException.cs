using Slovo.Core.Localization;

namespace Slovo.Core.Model;

/// <summary>
/// Thrown instead of loading an engine whose model is not usable, so an engine never
/// downloads anything on its own. The message is localized.
/// </summary>
public sealed class EngineUnavailableException(EngineId engineId, ModelStatus status)
    : Exception(MessageFor(engineId, status))
{
    public EngineId EngineId { get; } = engineId;
    public ModelStatus Status { get; } = status;

    static string MessageFor(EngineId id, ModelStatus status) => status.Kind switch
    {
        ModelStatusKind.Damaged => CoreStrings.Format("dictation.error.modelDamaged", id.DisplayName()),
        ModelStatusKind.Downloading => CoreStrings.Format("dictation.error.modelDownloading", id.DisplayName()),
        _ => CoreStrings.Format("dictation.error.modelMissing", id.DisplayName()),
    };
}
