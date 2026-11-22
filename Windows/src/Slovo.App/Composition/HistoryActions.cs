using Slovo.App.ViewModels.Screens;
using Slovo.Core;
using Slovo.Core.Audio;
using Slovo.Core.Dictation;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.Composition;

/// <summary>What Home does with a history entry: run it again, copy it, play its audio.</summary>
sealed class HistoryActions(DictationController dictation, IModelManager models, AppPaths paths, IAudioPlayer player, Action<string> copy)
    : IHistoryActions, IDisposable
{
    static readonly Logger Logger = Log.For("History");
    Guid? playingId;
    bool subscribed;

    public Guid? PlayingId => playingId;
    public event Action? PlaybackChanged;

    public IReadOnlyList<EngineId> ReadyEngines(EngineId except) =>
        EngineIds.Available.Where(id => id != except && models.Status(id).IsUsable).ToList();

    public Task RerunAsync(HistoryEntry entry, EngineId engine) => dictation.RerunAsync(entry.Id, engine);

    public void Copy(string text) => copy(text);

    public bool Play(HistoryEntry entry)
    {
        if (entry.AudioFileName is not { } relative)
        {
            return false;
        }
        try
        {
            var audio = WavFile.Read(paths.RecordingPath(relative));
            if (!subscribed)
            {
                subscribed = true;
                player.StateChanged += OnPlayerChanged;
            }
            player.Play([new MemoryAudioSource(audio.Samples, audio.SampleRate)]);
            playingId = entry.Id;
            PlaybackChanged?.Invoke();
            return true;
        }
        catch (SlovoException error)
        {
            Logger.Error("Could not play a dictation: " + error.Message);
            return false;
        }
    }

    public void StopPlayback()
    {
        player.Stop();
        Clear();
    }

    void OnPlayerChanged()
    {
        if (!player.IsPlaying)
        {
            Clear();
        }
    }

    void Clear()
    {
        if (playingId is null)
        {
            return;
        }
        playingId = null;
        PlaybackChanged?.Invoke();
    }

    public void Dispose()
    {
        player.StateChanged -= OnPlayerChanged;
        player.Dispose();
    }
}
