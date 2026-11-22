using System.ComponentModel;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Engines.Models;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Tests.Models;

/// <summary>
/// The port of DictationModelManagerTests.swift. Every test uses a temporary models folder
/// and a fake downloader: nothing is ever downloaded.
/// </summary>
public sealed class ModelManagerTests : IDisposable
{
    sealed class Downloader : IModelDownloader
    {
        public Func<EngineId, string, Action<double>, CancellationToken, Task> Behavior { get; set; } = (_, _, _, _) => Task.CompletedTask;
        public List<EngineId> Requests { get; } = [];

        public Task DownloadAsync(EngineId id, string modelsRoot, Action<double> progress, CancellationToken cancellation)
        {
            lock (Requests)
            {
                Requests.Add(id);
            }
            return Behavior(id, modelsRoot, progress, cancellation);
        }
    }

    readonly TemporaryFolder temporary = new("SlovoModelManagerTests");
    readonly Downloader downloader = new();
    readonly ModelManager manager;
    readonly List<string> changes = [];

    public ModelManagerTests()
    {
        manager = new ModelManager(Root, downloader: downloader);
        manager.PropertyChanged += (_, args) =>
        {
            lock (changes)
            {
                changes.Add(args.PropertyName!);
            }
        };
    }

    string Root => temporary.Path;

    public void Dispose() => temporary.Dispose();

    static ModelStatus NotDownloaded(EngineId id) => ModelStatus.NotDownloaded(ModelCatalog.DownloadBytes(id));

    // Status.

    [Fact]
    public void AnEmptyFolderReportsNotDownloaded()
    {
        Assert.Equal(ModelStatus.Unknown, manager.Status(EngineId.GigaamRnnt));
        manager.Refresh();
        foreach (var id in EngineIds.Available)
        {
            Assert.Equal(NotDownloaded(id), manager.Status(id));
        }
        Assert.Equal(ModelStatus.Unknown, manager.Status(EngineId.AppleDictation));
        Assert.Contains("Status", changes);
    }

    [Fact]
    public void ACompleteModelIsReadyWithItsSize()
    {
        TestSupport.InstallFakeModel(ModelCatalog.GigaamRnnt, Root);
        manager.Refresh();
        var expected = ModelCatalog.GigaamRnnt.Files.Sum(file => file.Size);
        Assert.Equal(ModelStatus.Ready(expected), manager.Status(EngineId.GigaamRnnt));
        Assert.Equal(NotDownloaded(EngineId.GigaamCtc), manager.Status(EngineId.GigaamCtc));
    }

    [Fact]
    public void TruncatedOrPartialModelsAreDamagedWithTheFileNames()
    {
        TestSupport.InstallFakeModel(ModelCatalog.GigaamCtc, Root, resize: "model.int8.onnx", delta: -1);
        manager.Refresh();
        var truncated = manager.Status(EngineId.GigaamCtc);
        Assert.Equal(ModelStatusKind.Damaged, truncated.Kind);
        Assert.Contains("model.int8.onnx", truncated.Reason);

        TestSupport.InstallFakeModel(ModelCatalog.GigaamRnnt, Root, skip: "joiner.onnx");
        manager.Refresh();
        var partial = manager.Status(EngineId.GigaamRnnt);
        Assert.Equal(ModelStatusKind.Damaged, partial.Kind);
        Assert.Contains("joiner.onnx", partial.Reason);
        Assert.Equal(CoreStrings.Format("dictation.model.missingFiles", "joiner.onnx"), partial.Reason);
    }

    [Fact]
    public void DownloadSizesAreShownForEveryEngine()
    {
        Assert.True(ModelCatalog.DownloadBytes(EngineId.GigaamRnnt) > 200_000_000);
        Assert.True(ModelCatalog.DownloadBytes(EngineId.GigaamCtc) > 200_000_000);
        Assert.True(ModelCatalog.DownloadBytes(EngineId.Parakeet) > 600_000_000);
        Assert.Equal(0, ModelCatalog.DownloadBytes(EngineId.AppleDictation));
    }

    [Fact]
    public void RefreshRemovesStagingLeftovers()
    {
        var leftover = Path.Combine(Root, ".downloads", "old", "archive.tar.bz2");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllBytes(leftover, [1]);
        manager.Refresh();
        Assert.False(Directory.Exists(Path.Combine(Root, ".downloads")));
    }

    // Downloads.

    [Fact]
    public async Task ADownloadReportsProgressAndEndsReady()
    {
        var gate = new Gate();
        downloader.Behavior = async (id, root, progress, _) =>
        {
            progress(0.5);
            await gate.WaitAsync();
            TestSupport.InstallFakeModel(ModelCatalog.For(id)!, root);
            progress(1);
        };
        manager.StartDownload(EngineId.GigaamCtc);
        Assert.True(manager.IsDownloading);

        await TestSupport.WaitUntil(() => manager.Status(EngineId.GigaamCtc) == ModelStatus.Downloading(0.5));
        manager.StartDownload(EngineId.GigaamCtc);
        Assert.Equal([EngineId.GigaamCtc], downloader.Requests);

        gate.Open();
        await manager.WaitForDownloadsAsync();
        Assert.True(manager.Status(EngineId.GigaamCtc).IsUsable, $"{manager.Status(EngineId.GigaamCtc)}");
        Assert.False(manager.IsDownloading);
        Assert.Null(manager.LastError(EngineId.GigaamCtc));
        Assert.Equal(ModelManager.DiskStatus(EngineId.GigaamCtc, Root), manager.Status(EngineId.GigaamCtc));
        Assert.Contains("IsDownloading", changes);
    }

    [Fact]
    public async Task ProgressOnlyMovesForwardInHundredths()
    {
        var gate = new Gate();
        downloader.Behavior = async (_, _, progress, _) =>
        {
            progress(0.304);
            progress(0.2);
            progress(double.NaN);
            await gate.WaitAsync();
        };
        manager.StartDownload(EngineId.Parakeet);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        Assert.Equal(ModelStatus.Downloading(0.3), manager.Status(EngineId.Parakeet));
        gate.Open();
        await manager.WaitForDownloadsAsync();
    }

    [Fact]
    public async Task AFailedDownloadSetsTheLastError()
    {
        downloader.Behavior = (_, _, _, _) => throw new SlovoException("offline");
        manager.StartDownload(EngineId.Parakeet);
        await manager.WaitForDownloadsAsync();

        var message = manager.LastError(EngineId.Parakeet);
        Assert.NotNull(message);
        Assert.Contains("offline", message);
        Assert.Contains("Parakeet v3", message);
        Assert.Equal(NotDownloaded(EngineId.Parakeet), manager.Status(EngineId.Parakeet));

        // A new download clears the previous error.
        var gate = new Gate();
        downloader.Behavior = (_, _, _, _) => gate.WaitAsync();
        manager.StartDownload(EngineId.Parakeet);
        Assert.Null(manager.LastError(EngineId.Parakeet));
        gate.Open();
        await manager.WaitForDownloadsAsync();
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNoError()
    {
        downloader.Behavior = (_, _, _, cancellation) => Task.Delay(TimeSpan.FromSeconds(30), cancellation);
        manager.StartDownload(EngineId.GigaamRnnt);
        manager.CancelDownload(EngineId.GigaamRnnt);
        await manager.WaitForDownloadsAsync();

        Assert.False(manager.IsDownloading);
        Assert.Null(manager.LastError(EngineId.GigaamRnnt));
        Assert.Equal(NotDownloaded(EngineId.GigaamRnnt), manager.Status(EngineId.GigaamRnnt));
    }

    [Fact]
    public void AppleDictationHasNothingToDownloadOrDelete()
    {
        manager.StartDownload(EngineId.AppleDictation);
        Assert.Empty(downloader.Requests);
        Assert.False(manager.IsDownloading);
        manager.Delete(EngineId.AppleDictation);
        Assert.True(Directory.Exists(Root));
    }

    // Delete.

    [Fact]
    public void DeleteRemovesOneVariantThenTheSharedFolder()
    {
        TestSupport.InstallFakeModel(ModelCatalog.GigaamRnnt, Root);
        TestSupport.InstallFakeModel(ModelCatalog.GigaamCtc, Root);
        manager.Refresh();
        var shared = Path.Combine(Root, "gigaam-v3");

        manager.Delete(EngineId.GigaamCtc);
        Assert.Equal(NotDownloaded(EngineId.GigaamCtc), manager.Status(EngineId.GigaamCtc));
        Assert.True(Directory.Exists(shared));
        Assert.True(manager.Status(EngineId.GigaamRnnt).IsUsable, "Deleting CTC must keep RNNT");

        // Hidden files alone do not keep the shared folder.
        File.WriteAllBytes(Path.Combine(shared, ".DS_Store"), []);
        manager.Delete(EngineId.GigaamRnnt);
        Assert.False(Directory.Exists(shared));
        Assert.True(Directory.Exists(Root), "The models root itself stays");
        Assert.Null(manager.LastError(EngineId.GigaamRnnt));
    }

    [Fact]
    public async Task DeleteWhileDownloadingIsRefused()
    {
        // Parakeet's files are big; a damaged copy with only the tokens will do.
        Directory.CreateDirectory(ModelCatalog.Parakeet.FolderPath(Root));
        File.WriteAllText(Path.Combine(ModelCatalog.Parakeet.FolderPath(Root), "tokens.txt"), "x");
        var gate = new Gate();
        downloader.Behavior = (_, _, _, _) => gate.WaitAsync();
        manager.StartDownload(EngineId.Parakeet);

        manager.Delete(EngineId.Parakeet);
        Assert.Equal(CoreStrings.Format("dictation.error.deleteWhileDownloading", "Parakeet v3"), manager.LastError(EngineId.Parakeet));
        Assert.True(Directory.Exists(ModelCatalog.Parakeet.FolderPath(Root)));

        gate.Open();
        await manager.WaitForDownloadsAsync();
        manager.Delete(EngineId.Parakeet);
        Assert.False(Directory.Exists(ModelCatalog.Parakeet.FolderPath(Root)));
        Assert.Null(manager.LastError(EngineId.Parakeet));
    }

    [Fact]
    public void ChangesAreRaisedThroughTheMainThread()
    {
        var posted = new List<Action>();
        var main = new QueueMainThread(posted);
        var queued = new ModelManager(Root, main, downloader);
        var names = new List<string>();
        queued.PropertyChanged += (_, args) => names.Add(args.PropertyName!);

        queued.Refresh();
        Assert.Empty(names);
        Assert.NotEmpty(posted);
        foreach (var action in posted)
        {
            action();
        }
        Assert.All(names, name => Assert.Equal("Status", name));
    }

    sealed class QueueMainThread(List<Action> posted) : Core.Platform.IMainThread
    {
        public bool IsMainThread => true;
        public void Post(Action action) => posted.Add(action);
    }
}
