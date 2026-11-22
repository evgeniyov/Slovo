using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Dictation;

/// <summary>
/// A key listener that can also cancel a dictation still being transcribed (the Swift
/// <c>ProcessingAwareHotkeys</c>). Separate from the frozen <see cref="IHotkeyMonitor"/>,
/// so its fakes need not implement it; the Windows keyboard hook should.
/// </summary>
public interface IProcessingAwareHotkeys
{
    /// <summary>
    /// Tells the listener whether a finished dictation is being transcribed, so Esc cancels
    /// it although nothing records. Called on the main thread.
    /// </summary>
    void SyncProcessing(bool isProcessing);
}

/// <summary>
/// An engine provider that can load an engine for a language other than the current
/// setting (the Swift <c>LanguageEngineProviding</c>). Re-running an old dictation must
/// use the language it was dictated in. Separate from the frozen
/// <see cref="IEngineProvider"/>, so its fakes need not implement it; the controller uses
/// it when the provider has it.
/// </summary>
public interface ILanguageEngineProvider : IEngineProvider
{
    Task<ITranscriptionEngine> EngineAsync(EngineId id, LanguageMode languageMode, CancellationToken cancellation = default);
}
