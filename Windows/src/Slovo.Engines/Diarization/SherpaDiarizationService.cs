using System.ComponentModel;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Engines.Models;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Diarization;

/// <summary>Fetches the speaker model files.</summary>
public interface IDiarizationModelDownloader
{
    /// <summary>Downloads into <paramref name="modelFolder"/>, reporting progress from 0 to 1 on any thread.</summary>
    Task DownloadAsync(string modelFolder, Action<double> progress, CancellationToken cancellation);
}

/// <summary>Loads a diarizer from the model folder; tests pass a fake.</summary>
public delegate Task<ISpeakerDiarizer> DiarizerFactory(string modelFolder, CancellationToken cancellation);

/// <summary>
/// The speaker model on disk and the diarizer loaded from it (the port of
/// DiarizationService.swift).
///
/// The model is downloaded only when the user presses Download, and loading never
/// downloads: a missing or damaged model is an error that the meeting pipeline turns into
/// fallback speakers. The meeting pipeline calls <see cref="Release"/> after every run,
/// because meetings are rare and the loaded models are large; a released diarizer cannot
/// be used any more. Call from the UI thread; PropertyChanged ("Status", "ModelStatus",
/// "LastError", "LoadState") is raised through the main-thread dispatcher.
/// </summary>
public sealed class SherpaDiarizationService : IDiarizationProvider, IDiarizationModelManager
{
    static readonly Logger Logger = Log.For("Diarization");

    readonly IDiarizationModelDownloader downloader;
    readonly DiarizerFactory factory;
    readonly IMainThread mainThread;
    readonly bool systemSupported;
    readonly Lock gate = new();

    ModelStatus status = ModelStatus.Unknown;
    string? lastError;
    EngineLoadState loadState = EngineLoadState.Unloaded;
    (CancellationTokenSource Cancellation, Task Task)? download;
    ISpeakerDiarizer? loaded;
    Task<ISpeakerDiarizer>? load;
    /// <summary>Bumped by <see cref="Release"/>, so a load that finishes afterwards is not kept.</summary>
    int loadGeneration;
    /// <summary>Callers inside <see cref="WaitForLoadAsync"/>. The files are about to be deleted, so no new load may start.</summary>
    int drainingCount;

    /// <param name="downloader">Fetches the model files; tests pass a fake.</param>
    /// <param name="factory">Loads a diarizer; by default <see cref="SherpaSpeakerDiarizer.Load"/> with <paramref name="tuning"/>.</param>
    /// <param name="systemSupported">Lets tests play a system that cannot separate speakers.</param>
    public SherpaDiarizationService(string modelsRoot, IMainThread? mainThread = null, IDiarizationModelDownloader? downloader = null,
        DiarizerFactory? factory = null, SherpaDiarizationTuning? tuning = null, bool systemSupported = true)
    {
        ModelsRoot = modelsRoot;
        Tuning = tuning ?? SherpaDiarizationTuning.Standard;
        this.mainThread = mainThread ?? ImmediateMainThread.Shared;
        this.downloader = downloader ?? new SherpaDiarizationModelDownloader();
        var chosen = Tuning;
        this.factory = factory ?? ((folder, _) => Task.Run<ISpeakerDiarizer>(() => SherpaSpeakerDiarizer.Load(folder, chosen)));
        this.systemSupported = systemSupported;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModelsRoot { get; }

    /// <summary><c>Models/diarization-onnx</c>.</summary>
    public string ModelFolder => DiarizationModelFiles.ModelFolder(ModelsRoot);

    public SherpaDiarizationTuning Tuning { get; }

    public string ModelName => DiarizationModelFiles.ModelIdentifier;

    public string Configuration => Tuning.Description;

    /// <summary>sherpa-onnx ships native libraries for every system the Windows app runs on.</summary>
    public bool IsSupportedOnThisSystem => systemSupported;

    public ModelStatus Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    public ModelStatus ModelStatus => Status;

    public string? LastError
    {
        get
        {
            lock (gate)
            {
                return lastError;
            }
        }
    }

    public EngineLoadState LoadState
    {
        get
        {
            lock (gate)
            {
                return loadState;
            }
        }
    }

    // Files.

    /// <summary>Re-reads the model folder; a running download keeps its progress.</summary>
    public void Refresh()
    {
        lock (gate)
        {
            if (download is null)
            {
                SetStatusLocked(DiarizationModelFiles.Status(ModelFolder));
            }
        }
    }

    public void StartDownload()
    {
        lock (gate)
        {
            if (!systemSupported || download is not null)
            {
                return;
            }
            SetStatusLocked(DiarizationModelFiles.Status(ModelFolder));
            if (status.IsUsable)
            {
                return;
            }
            SetErrorLocked(null);
            SetStatusLocked(ModelStatus.Downloading(0));
            Logger.Info("Downloading the speaker model");
            var cancellation = new CancellationTokenSource();
            var folder = ModelFolder;
            var task = Task.Run(async () =>
            {
                try
                {
                    await downloader.DownloadAsync(folder, UpdateProgress, cancellation.Token).ConfigureAwait(false);
                    DownloadFinished(null, cancellation);
                }
                catch (Exception error)
                {
                    DownloadFinished(error, cancellation);
                }
            });
            download = (cancellation, task);
        }
    }

    public void CancelDownload()
    {
        lock (gate)
        {
            if (download is { } running)
            {
                Logger.Info("Cancelling the speaker model download");
                running.Cancellation.Cancel();
            }
        }
    }

    /// <summary>
    /// Removes <see cref="ModelFolder"/>. Refused while a download runs. Releases the
    /// diarizer; callers wait for a running load first (<see cref="DeleteAsync"/> does both).
    /// Failures are reported in <see cref="LastError"/>, never thrown.
    /// </summary>
    public void Delete()
    {
        lock (gate)
        {
            if (download is not null)
            {
                SetErrorLocked(CoreStrings.Get("diarization.error.deleteWhileDownloading"));
                return;
            }
        }
        Release();
        lock (gate)
        {
            try
            {
                if (Directory.Exists(ModelFolder))
                {
                    Directory.Delete(ModelFolder, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                SetErrorLocked(error.Message);
                SetStatusLocked(DiarizationModelFiles.Status(ModelFolder));
                Logger.Error("Could not delete the speaker model", error);
                return;
            }
            Logger.Info("Deleted the speaker model");
            SetErrorLocked(null);
            SetStatusLocked(DiarizationModelFiles.Status(ModelFolder));
        }
    }

    /// <summary>Releases the diarizer, waits until no load reads the files, then deletes them.</summary>
    public async Task DeleteAsync()
    {
        Release();
        await WaitForLoadAsync().ConfigureAwait(false);
        Delete();
    }

    /// <summary>Waits for a running download to end; for tests.</summary>
    internal Task WaitForDownloadAsync()
    {
        lock (gate)
        {
            return download?.Task ?? Task.CompletedTask;
        }
    }

    // Loading.

    /// <summary>
    /// The loaded diarizer, loading it first. Throws a localized <see cref="SlovoException"/>
    /// when the model is not ready or the system cannot run it. Never downloads. The token
    /// cancels this caller's wait; a load other callers share goes on.
    /// </summary>
    public async Task<ISpeakerDiarizer> DiarizerAsync(CancellationToken cancellation)
    {
        if (!systemSupported)
        {
            throw new SlovoException(CoreStrings.Get("diarization.error.unsupportedSystem"));
        }
        Task<ISpeakerDiarizer> task;
        lock (gate)
        {
            if (loaded is not null)
            {
                return loaded;
            }
            // Checked before joining a running load: a caller that joined would get models
            // whose files are deleted as soon as the load ends.
            if (drainingCount > 0)
            {
                throw new SlovoException(CoreStrings.Get("diarization.error.modelMissing"));
            }
            if (load is null)
            {
                if (download is null)
                {
                    SetStatusLocked(DiarizationModelFiles.Status(ModelFolder));
                }
                switch (status.Kind)
                {
                    case ModelStatusKind.Ready:
                        break;
                    case ModelStatusKind.Damaged:
                        throw new SlovoException(CoreStrings.Get("diarization.error.modelDamaged"));
                    default:
                        throw new SlovoException(CoreStrings.Get("diarization.error.modelMissing"));
                }
                var generation = loadGeneration;
                var folder = ModelFolder;
                SetLoadStateLocked(EngineLoadState.Loading);
                // The task records its own outcome, so callers that join a running load and a
                // caller that stops waiting leave the same state behind.
                load = Task.Run(async () =>
                {
                    ISpeakerDiarizer diarizer;
                    try
                    {
                        diarizer = await factory(folder, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        LoadFinished(generation, null, error);
                        throw;
                    }
                    LoadFinished(generation, diarizer, null);
                    return diarizer;
                });
            }
            task = load;
        }
        return await task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Drops the loaded diarizer and frees its memory once a running separation ends.</summary>
    public void Release()
    {
        ISpeakerDiarizer? released;
        lock (gate)
        {
            loadGeneration++;
            released = loaded;
            loaded = null;
            if (load is null)
            {
                SetLoadStateLocked(EngineLoadState.Unloaded);
            }
        }
        (released as IDisposable)?.Dispose();
    }

    /// <summary>Completes once no load reads the model files; no new load starts meanwhile.</summary>
    public async Task WaitForLoadAsync()
    {
        lock (gate)
        {
            drainingCount++;
        }
        try
        {
            while (true)
            {
                Task? running;
                lock (gate)
                {
                    running = load;
                }
                if (running is null)
                {
                    return;
                }
                try
                {
                    await running.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Only the end of the load matters here.
                }
            }
        }
        finally
        {
            lock (gate)
            {
                drainingCount--;
            }
        }
    }

    // Private.

    void LoadFinished(int generation, ISpeakerDiarizer? diarizer, Exception? error)
    {
        var drop = false;
        lock (gate)
        {
            load = null;
            if (generation != loadGeneration)
            {
                SetLoadStateLocked(EngineLoadState.Unloaded);
                drop = true;
            }
            else if (diarizer is not null)
            {
                loaded = diarizer;
                SetLoadStateLocked(EngineLoadState.Ready);
            }
            else if (error is OperationCanceledException)
            {
                SetLoadStateLocked(EngineLoadState.Unloaded);
            }
            else
            {
                SetLoadStateLocked(EngineLoadState.Failed(error?.Message ?? ""));
                Logger.Error($"Loading the speaker model failed: {error?.Message}");
            }
        }
        if (drop)
        {
            (diarizer as IDisposable)?.Dispose();
        }
    }

    void UpdateProgress(double fraction)
    {
        lock (gate)
        {
            // Late updates can arrive after the download finished.
            if (download is null || status.Kind != ModelStatusKind.Downloading)
            {
                return;
            }
            var rounded = Math.Round(Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * 100) / 100;
            if (rounded > status.Progress)
            {
                SetStatusLocked(ModelStatus.Downloading(rounded));
            }
        }
    }

    void DownloadFinished(Exception? error, CancellationTokenSource cancellation)
    {
        lock (gate)
        {
            download = null;
            cancellation.Dispose();
            if (error is null)
            {
                Logger.Info("Downloaded the speaker model");
            }
            else if (error is OperationCanceledException)
            {
                Logger.Info("The speaker model download was cancelled");
            }
            else
            {
                SetErrorLocked(error is SlovoException ? error.Message : CoreStrings.Format("diarization.error.downloadFailed", error.Message));
                Logger.Error("Downloading the speaker model failed", error);
            }
            SetStatusLocked(DiarizationModelFiles.Status(ModelFolder));
        }
    }

    void SetStatusLocked(ModelStatus value)
    {
        if (status == value)
        {
            return;
        }
        status = value;
        Notify(nameof(Status));
        Notify(nameof(ModelStatus));
    }

    void SetErrorLocked(string? value)
    {
        if (lastError == value)
        {
            return;
        }
        lastError = value;
        Notify(nameof(LastError));
    }

    void SetLoadStateLocked(EngineLoadState value)
    {
        if (loadState == value)
        {
            return;
        }
        loadState = value;
        Notify(nameof(LoadState));
    }

    void Notify(string name) => mainThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
}

/// <summary>
/// Downloads the segmentation archive and the embedding model from the sherpa-onnx
/// releases into a staging folder, checks their sizes and moves them into place.
/// </summary>
public sealed class SherpaDiarizationModelDownloader(HttpClient? http = null) : IDiarizationModelDownloader
{
    readonly HttpClient http = http ?? FileDownload.SharedClient;

    public async Task DownloadAsync(string modelFolder, Action<double> progress, CancellationToken cancellation)
    {
        var modelsRoot = Path.GetDirectoryName(Path.GetFullPath(modelFolder).TrimEnd(Path.DirectorySeparatorChar))!;
        var staging = ModelInstaller.MakeStagingFolder(modelsRoot);
        try
        {
            // Progress follows bytes: the archive is a fifth of the whole, and unpacking it is quick.
            var total = (double)DiarizationModelFiles.DownloadBytes;
            var archiveShare = DiarizationModelFiles.SegmentationArchiveBytes / total;
            var archive = Path.Combine(staging, DiarizationModelFiles.SegmentationFolderName + ".tar.bz2");
            await FileDownload.RunAsync(http, DiarizationModelFiles.SegmentationArchiveUrl, archive,
                fraction => progress(fraction * archiveShare), cancellation, DiarizationModelFiles.SegmentationArchiveBytes)
                .ConfigureAwait(false);
            var embedding = Path.Combine(staging, DiarizationModelFiles.Embedding.Name);
            await FileDownload.RunAsync(http, DiarizationModelFiles.EmbeddingUrl, embedding,
                fraction => progress(archiveShare + fraction * (1 - archiveShare)), cancellation, DiarizationModelFiles.Embedding.Size)
                .ConfigureAwait(false);

            var extracted = Path.Combine(staging, "extracted");
            Directory.CreateDirectory(extracted);
            await TarExtractor.ExtractAsync(archive, extracted, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var segmentation = Path.Combine(extracted, DiarizationModelFiles.SegmentationFolderName);
            if (ModelFiles.RegularFileSize(Path.Combine(segmentation, DiarizationModelFiles.Segmentation.Name)) != DiarizationModelFiles.Segmentation.Size
                || ModelFiles.RegularFileSize(embedding) != DiarizationModelFiles.Embedding.Size)
            {
                throw new SlovoException(CoreStrings.Get("diarization.error.filesMissingAfterDownload"));
            }
            ModelInstaller.MoveIntoPlace(segmentation, modelFolder, DiarizationModelFiles.SegmentationFolderName, staging);
            ModelInstaller.MoveIntoPlace(embedding, modelFolder, DiarizationModelFiles.Embedding.Name, staging);
            progress(1);
        }
        catch (SlovoException error) when (error.Message == CoreStrings.Get("diarization.error.filesMissingAfterDownload"))
        {
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Network, HTTP and unpacking errors are fragments; the message says what failed.
            throw new SlovoException(CoreStrings.Format("diarization.error.downloadFailed", error.Message), error);
        }
        finally
        {
            ModelInstaller.RemoveQuietly(staging);
        }
    }
}
