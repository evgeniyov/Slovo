using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.ViewModels.Settings;

/// <summary>What a model row needs: its status, the actions, and a name and trade-off line.</summary>
public interface IModelRowSource : INotifyPropertyChanged
{
    ModelStatus Status { get; }
    string? LastError { get; }
    /// <summary>A load reads the model files, so file actions wait for it.</summary>
    bool IsLoading { get; }
    void StartDownload();
    void CancelDownload();
    /// <summary>Deletes the files; throws SlovoException or an IO exception.</summary>
    Task DeleteAsync();
}

/// <summary>One engine's model files seen through the model manager.</summary>
public sealed class EngineModelSource : IModelRowSource
{
    readonly IModelManager manager;
    readonly EngineId id;
    readonly Func<EngineId, Task>? delete;
    readonly IEngineProvider? engines;

    /// <param name="delete">
    /// The safe deletion that first releases the engine and waits for a load still reading the
    /// files (DictationWiring.DeleteModelAsync); deleting under a running load aborts ONNX Runtime.
    /// </param>
    /// <param name="engines">Where the engine's load state comes from; without it the row never shows Loading.</param>
    public EngineModelSource(IModelManager manager, EngineId id, Func<EngineId, Task>? delete = null, IEngineProvider? engines = null)
    {
        this.manager = manager;
        this.id = id;
        this.delete = delete;
        this.engines = engines;
        manager.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        if (engines is not null)
        {
            engines.LoadStateChanged += changed =>
            {
                if (changed == id)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
                }
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ModelStatus Status => manager.Status(id);
    public string? LastError => manager.LastError(id);
    public bool IsLoading => engines?.LoadState(id).Kind == EngineLoadStateKind.Loading;
    public void StartDownload() => manager.StartDownload(id);
    public void CancelDownload() => manager.CancelDownload(id);
    public Task DeleteAsync()
    {
        if (delete is not null)
        {
            return delete(id);
        }
        manager.Delete(id);
        return Task.CompletedTask;
    }
}

/// <summary>The speaker model files.</summary>
public sealed class SpeakerModelSource : IModelRowSource
{
    readonly IDiarizationModelManager manager;
    readonly Func<Task>? delete;

    /// <param name="delete">The safe deletion (MeetingWiring.DeleteDiarizationModelAsync) that waits for a load still reading the files.</param>
    public SpeakerModelSource(IDiarizationModelManager manager, Func<Task>? delete = null)
    {
        this.manager = manager;
        this.delete = delete;
        manager.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ModelStatus Status => manager.Status;
    public string? LastError => manager.LastError;
    public bool IsLoading => manager.LoadState.Kind == EngineLoadStateKind.Loading;
    public void StartDownload() => manager.StartDownload();
    public void CancelDownload() => manager.CancelDownload();
    public Task DeleteAsync()
    {
        if (delete is not null)
        {
            return delete();
        }
        manager.Delete();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A model row: name, trade-off, status, size, the damaged reason, and Download / Cancel /
/// Delete (the macOS ModelRow).
/// </summary>
public sealed partial class ModelRowViewModel : ViewModelBase
{
    readonly IModelRowSource source;
    readonly ISheetHost sheets;
    readonly Action? beforeDelete;
    readonly string deleteTitle;
    readonly string deleteMessage;

    /// <param name="recommended">The accent badge beside the name, such as "Best for Russian".</param>
    public ModelRowViewModel(string name, string tradeoff, IModelRowSource source, ISheetHost sheets,
                             string deleteTitle, string deleteMessage, Action? beforeDelete = null, string? recommended = null)
    {
        Name = name;
        Tradeoff = tradeoff;
        Recommended = recommended;
        this.source = source;
        this.sheets = sheets;
        this.deleteTitle = deleteTitle;
        this.deleteMessage = deleteMessage;
        this.beforeDelete = beforeDelete;
        source.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    public string Name { get; }
    public string Tradeoff { get; }
    public string? Recommended { get; }
    public bool IsRecommended => Recommended is not null;

    ModelStatus Status => source.Status;

    public string StatusText
    {
        get
        {
            var loc = Loc.Instance;
            return Status.Kind switch
            {
                ModelStatusKind.Ready => loc.Get("engine.status.ready"),
                ModelStatusKind.NotDownloaded => loc.Get("engine.status.notDownloaded"),
                ModelStatusKind.Downloading => loc.Format("engine.status.downloading", (long)Math.Round(Status.Progress * 100)),
                ModelStatusKind.Damaged => loc.Get("engine.status.damaged"),
                _ => loc.Get("engine.status.checking"),
            };
        }
    }

    public string? SizeText => Status.Kind switch
    {
        ModelStatusKind.Ready => Loc.Instance.Format("engine.size.onDisk", Formatting.Megabytes(Status.Bytes)),
        ModelStatusKind.NotDownloaded when Status.Bytes > 0 => Loc.Instance.Format("engine.size.download", Formatting.Megabytes(Status.Bytes)),
        _ => null,
    };

    /// <summary>Why the files are damaged, said under the status in the destructive colour.</summary>
    public string? DamagedReason => IsDamaged && !string.IsNullOrEmpty(Status.Reason) ? Status.Reason : null;
    public bool IsDamaged => Status.Kind == ModelStatusKind.Damaged;

    /// <summary>
    /// The model is being read into memory. Replacing or deleting its files under the load would
    /// fail the load or crash the runtime reading them, so the file actions wait.
    /// </summary>
    public bool IsLoading => source.IsLoading;
    public bool CanChangeFiles => !IsLoading;
    /// <summary>Why the file actions are unavailable, on hover; nothing while they work.</summary>
    public string? BusyText => IsLoading ? Loc.Instance.Get("engine.busy.loading") : null;

    public bool IsReady => Status.Kind == ModelStatusKind.Ready;
    public bool IsDownloading => Status.Kind == ModelStatusKind.Downloading;
    public double Progress => Status.Progress * 100;
    public bool CanDownload => Status.Kind is ModelStatusKind.NotDownloaded or ModelStatusKind.Damaged;
    public string DownloadText => Loc.Instance.Get(Status.Kind == ModelStatusKind.Damaged ? "engine.redownload" : "engine.download");
    public bool CanDelete => Status.Kind is ModelStatusKind.Ready or ModelStatusKind.Damaged;
    public string? Error => source.LastError;
    public bool HasError => !string.IsNullOrEmpty(source.LastError);

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        DownloadCommand.NotifyCanExecuteChanged();
        CancelDownloadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanChangeFiles))]
    void Download() => source.StartDownload();

    [RelayCommand(CanExecute = nameof(CanChangeFiles))]
    void CancelDownload() => source.CancelDownload();

    [RelayCommand(CanExecute = nameof(CanChangeFiles))]
    async Task DeleteAsync()
    {
        if (!await sheets.ConfirmAsync(deleteTitle, deleteMessage, Loc.Instance.Get("common.delete")))
        {
            return;
        }
        try
        {
            beforeDelete?.Invoke();
            await source.DeleteAsync();
        }
        catch (Exception error) when (error is SlovoException or IOException or UnauthorizedAccessException)
        {
            sheets.ShowAlert(Loc.Instance.Get("engine.delete.failed"), error.Message);
        }
        Refresh();
    }
}
