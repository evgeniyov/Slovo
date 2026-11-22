using System.ComponentModel;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Models;

/// <summary>
/// Model files on disk: status, user-started downloads and deletion (the port of
/// ModelManager.swift).
///
/// Every archive comes from the sherpa-onnx <c>asr-models</c> release and is extracted
/// with the system's tar. Nothing downloads unless the user presses Download. Call from
/// the UI thread; PropertyChanged ("Status", "LastError", "IsDownloading") is raised
/// through the main-thread dispatcher.
/// </summary>
public sealed class ModelManager : IModelManager
{
    static readonly Logger Logger = Log.For("Models");

    readonly IModelDownloader downloader;
    readonly IMainThread mainThread;
    readonly Lock gate = new();
    readonly Dictionary<EngineId, ModelStatus> statuses = [];
    readonly Dictionary<EngineId, string> lastErrors = [];
    readonly Dictionary<EngineId, (CancellationTokenSource Cancellation, Task Task)> downloads = [];

    /// <param name="downloader">Performs downloads; tests pass a fake so nothing touches the network.</param>
    public ModelManager(string modelsRoot, IMainThread? mainThread = null, IModelDownloader? downloader = null)
    {
        ModelsRoot = modelsRoot;
        this.mainThread = mainThread ?? ImmediateMainThread.Shared;
        this.downloader = downloader ?? new SherpaModelDownloader();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModelsRoot { get; }

    public ModelStatus Status(EngineId id)
    {
        lock (gate)
        {
            return statuses.GetValueOrDefault(id, ModelStatus.Unknown);
        }
    }

    public string? LastError(EngineId id)
    {
        lock (gate)
        {
            return lastErrors.GetValueOrDefault(id);
        }
    }

    public bool IsDownloading
    {
        get
        {
            lock (gate)
            {
                return downloads.Count > 0;
            }
        }
    }

    /// <summary>Re-reads every model folder; a running download keeps its progress.</summary>
    public void Refresh()
    {
        bool idle;
        lock (gate)
        {
            foreach (var spec in ModelCatalog.All.Where(spec => !downloads.ContainsKey(spec.Engine)))
            {
                SetStatusLocked(spec.Engine, DiskStatus(spec.Engine, ModelsRoot));
            }
            idle = downloads.Count == 0;
        }
        if (idle)
        {
            RemoveStagingLeftovers();
        }
    }

    public void StartDownload(EngineId id)
    {
        lock (gate)
        {
            if (ModelCatalog.For(id) is null || downloads.ContainsKey(id))
            {
                return;
            }
            SetErrorLocked(id, null);
            SetStatusLocked(id, ModelStatus.Downloading(0));
            Logger.Info($"Downloading {id.RawValue()}");
            var cancellation = new CancellationTokenSource();
            var root = ModelsRoot;
            var task = Task.Run(async () =>
            {
                try
                {
                    await downloader.DownloadAsync(id, root, fraction => UpdateProgress(id, fraction), cancellation.Token)
                        .ConfigureAwait(false);
                    DownloadFinished(id, null, cancellation);
                }
                catch (Exception error)
                {
                    DownloadFinished(id, error, cancellation);
                }
            });
            downloads[id] = (cancellation, task);
            Notify(nameof(IsDownloading));
        }
    }

    public void CancelDownload(EngineId id)
    {
        lock (gate)
        {
            if (!downloads.TryGetValue(id, out var download))
            {
                return;
            }
            Logger.Info($"Cancelling the {id.RawValue()} download");
            download.Cancellation.Cancel();
        }
    }

    /// <summary>
    /// Removes an engine's model folder. Refused while it downloads. Callers release the
    /// engine and wait for a running load first (<see cref="EngineManager.DeleteModelAsync"/>).
    /// Failures are reported in <see cref="LastError"/>, never thrown.
    /// </summary>
    public void Delete(EngineId id)
    {
        if (ModelCatalog.For(id) is not { } spec)
        {
            return;
        }
        lock (gate)
        {
            if (downloads.ContainsKey(id))
            {
                SetErrorLocked(id, CoreStrings.Format("dictation.error.deleteWhileDownloading", id.DisplayName()));
                return;
            }
            var folder = spec.FolderPath(ModelsRoot);
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
                // Both GigaAM variants live in one parent folder; it goes once both are deleted.
                var parent = Path.GetDirectoryName(folder);
                if (parent is not null && Directory.Exists(parent)
                    && !Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(
                        Path.GetFullPath(ModelsRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
                    && Directory.EnumerateFileSystemEntries(parent).All(entry => Path.GetFileName(entry).StartsWith('.')))
                {
                    Directory.Delete(parent, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                SetErrorLocked(id, CoreStrings.Format("dictation.error.deleteFailed", id.DisplayName(), error.Message));
                SetStatusLocked(id, DiskStatus(id, ModelsRoot));
                Logger.Error($"Could not delete {id.RawValue()}", error);
                return;
            }
            Logger.Info($"Deleted {id.RawValue()}");
            SetErrorLocked(id, null);
            SetStatusLocked(id, DiskStatus(id, ModelsRoot));
        }
    }

    /// <summary>Waits for running downloads; for tests.</summary>
    internal async Task WaitForDownloadsAsync()
    {
        while (true)
        {
            Task[] running;
            lock (gate)
            {
                running = downloads.Values.Select(download => download.Task).ToArray();
            }
            if (running.Length == 0)
            {
                return;
            }
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    // Downloads.

    void UpdateProgress(EngineId id, double fraction)
    {
        lock (gate)
        {
            // Late updates can arrive after the download finished.
            if (!downloads.ContainsKey(id) || Status(id) is not { Kind: ModelStatusKind.Downloading } current)
            {
                return;
            }
            var rounded = Math.Round(Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * 100) / 100;
            if (rounded > current.Progress)
            {
                SetStatusLocked(id, ModelStatus.Downloading(rounded));
            }
        }
    }

    void DownloadFinished(EngineId id, Exception? error, CancellationTokenSource cancellation)
    {
        bool idle;
        lock (gate)
        {
            downloads.Remove(id);
            cancellation.Dispose();
            if (error is null)
            {
                Logger.Info($"Downloaded {id.RawValue()}");
            }
            else if (error is OperationCanceledException)
            {
                Logger.Info($"The {id.RawValue()} download was cancelled");
            }
            else
            {
                SetErrorLocked(id, CoreStrings.Format("dictation.error.downloadFailed", id.DisplayName(), error.Message));
                Logger.Error($"Downloading {id.RawValue()} failed", error);
            }
            SetStatusLocked(id, DiskStatus(id, ModelsRoot));
            idle = downloads.Count == 0;
            Notify(nameof(IsDownloading));
        }
        if (idle)
        {
            RemoveStagingLeftovers();
        }
    }

    void SetStatusLocked(EngineId id, ModelStatus status)
    {
        if (statuses.TryGetValue(id, out var current) && current == status)
        {
            return;
        }
        statuses[id] = status;
        Notify("Status");
    }

    void SetErrorLocked(EngineId id, string? message)
    {
        if (lastErrors.GetValueOrDefault(id) == message)
        {
            return;
        }
        if (message is null)
        {
            lastErrors.Remove(id);
        }
        else
        {
            lastErrors[id] = message;
        }
        Notify("LastError");
    }

    void Notify(string name) => mainThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    /// <summary>Staging folders left by a download that was interrupted by quitting the app.</summary>
    void RemoveStagingLeftovers() => ModelInstaller.RemoveQuietly(Path.Combine(ModelsRoot, ModelInstaller.StagingFolderName));

    // Disk status.

    /// <summary>The status the files on disk give; Unknown for engines without model files.</summary>
    public static ModelStatus DiskStatus(EngineId id, string modelsRoot)
    {
        if (ModelCatalog.For(id) is not { } spec)
        {
            return ModelStatus.Unknown;
        }
        var folder = spec.FolderPath(modelsRoot);
        var (missing, incomplete) = ModelFiles.Check(folder, spec.Files);
        if (missing.Count == spec.Files.Count)
        {
            return ModelStatus.NotDownloaded(spec.DownloadBytes);
        }
        if (missing.Count == 0 && incomplete.Count == 0)
        {
            return ModelStatus.Ready(ModelFiles.FolderSize(folder));
        }
        var reasons = new List<string>();
        if (missing.Count > 0)
        {
            reasons.Add(CoreStrings.Format("dictation.model.missingFiles", string.Join(", ", missing)));
        }
        if (incomplete.Count > 0)
        {
            reasons.Add(CoreStrings.Format("dictation.model.incompleteFiles", string.Join(", ", incomplete)));
        }
        return ModelStatus.Damaged(string.Join(' ', reasons));
    }
}
