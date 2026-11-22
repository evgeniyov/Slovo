using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Engines.Diarization;
using Slovo.Engines.Models;
using Slovo.Engines.Tests.Models;

namespace Slovo.Engines.Tests.Diarization;

/// <summary>
/// The port of DiarizationServiceTests.swift: a fake downloader and a fake loader in a
/// temporary models folder, so nothing touches the network or the real Models folder.
/// </summary>
public sealed class SherpaDiarizationServiceTests : IDisposable
{
    sealed class Downloader : IDiarizationModelDownloader
    {
        public Func<string, Action<double>, CancellationToken, Task> Behavior { get; set; } = (_, _, _) => Task.CompletedTask;
        public int Calls;

        public Task DownloadAsync(string modelFolder, Action<double> progress, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return Behavior(modelFolder, progress, cancellation);
        }
    }

    sealed class Diarizer : ISpeakerDiarizer, IDisposable
    {
        public bool Disposed;

        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(float[] samples, SpeakerCountHint hint, IProgress<double>? progress, CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<SpeakerTurn>>([new SpeakerTurn(0, 1, 0)]);

        public void Dispose() => Disposed = true;
    }

    readonly TemporaryFolder temporary = new("SlovoDiarizationService");
    readonly Downloader downloader = new();
    int loads;
    Gate? loadGate;
    Exception? loadError;

    public void Dispose() => temporary.Dispose();

    string Root => temporary.Path;

    string ModelFolder => Path.Combine(Root, "diarization-onnx");

    SherpaDiarizationService MakeService(bool systemSupported = true) =>
        new(Root, downloader: downloader, systemSupported: systemSupported, factory: async (folder, _) =>
        {
            Assert.Equal(ModelFolder, folder);
            Interlocked.Increment(ref loads);
            if (loadGate is { } gate)
            {
                await gate.WaitAsync();
            }
            if (loadError is { } error)
            {
                throw error;
            }
            return new Diarizer();
        });

    static string String(string key) => CoreStrings.Get(key);

    static ModelStatus NotDownloaded => ModelStatus.NotDownloaded(DiarizationModelFiles.DownloadBytes);

    // Status.

    [Fact]
    public void TheStatusComesFromDiskAtStart()
    {
        Assert.Equal(NotDownloaded, MakeService().Status);
        var bytes = DiarizationFixtures.InstallModel(ModelFolder);
        var service = MakeService();
        Assert.Equal(ModelStatus.Ready(bytes), service.Status);
        Assert.Equal(service.Status, service.ModelStatus);
        Assert.Equal(ModelFolder, service.ModelFolder);
        Assert.True(service.IsSupportedOnThisSystem);
        Assert.Equal(DiarizationModelFiles.ModelIdentifier, service.ModelName);
        Assert.Equal(SherpaDiarizationTuning.Standard.Description, service.Configuration);
    }

    [Fact]
    public void RefreshSeesFilesChangedOnDisk()
    {
        var service = MakeService();
        var changes = new List<string>();
        service.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);
        DiarizationFixtures.InstallModel(ModelFolder, skipEmbedding: true);
        service.Refresh();
        Assert.Equal(ModelStatus.Damaged(String("diarization.error.modelDamaged")), service.Status);
        Assert.Contains("Status", changes);
        Assert.Contains("ModelStatus", changes);
    }

    // Download.

    [Fact]
    public async Task ADownloadReportsProgressAndCompletes()
    {
        var gate = new Gate();
        downloader.Behavior = async (folder, progress, _) =>
        {
            progress(0.25);
            progress(0.5);
            await gate.WaitAsync();
            DiarizationFixtures.InstallModel(folder);
        };
        var service = MakeService();
        service.StartDownload();
        Assert.Equal(ModelStatus.Downloading(0), service.Status);
        await TestSupport.WaitUntil(() => service.Status == ModelStatus.Downloading(0.5));

        // A second press while downloading starts nothing.
        service.StartDownload();
        gate.Open();
        await service.WaitForDownloadAsync();

        Assert.True(service.Status.IsUsable, $"{service.Status}");
        Assert.Null(service.LastError);
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNoErrorAndReReadsTheDisk()
    {
        downloader.Behavior = async (_, progress, cancellation) =>
        {
            progress(0.1);
            await Task.Delay(Timeout.Infinite, cancellation);
        };
        var service = MakeService();
        service.StartDownload();
        await TestSupport.WaitUntil(() => downloader.Calls == 1);
        service.CancelDownload();
        await service.WaitForDownloadAsync();

        Assert.Equal(NotDownloaded, service.Status);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task AFailedDownloadReportsTheError()
    {
        downloader.Behavior = (_, _, _) => throw new SlovoException("already localized");
        var service = MakeService();
        service.StartDownload();
        await service.WaitForDownloadAsync();
        Assert.Equal("already localized", service.LastError);

        downloader.Behavior = (folder, _, _) =>
        {
            DiarizationFixtures.InstallModel(folder, skipEmbedding: true);
            throw new IOException("disk on fire");
        };
        service.StartDownload();
        Assert.Null(service.LastError);
        await service.WaitForDownloadAsync();
        Assert.Equal(CoreStrings.Format("diarization.error.downloadFailed", "disk on fire"), service.LastError);
        Assert.Equal(ModelStatus.Damaged(String("diarization.error.modelDamaged")), service.Status);
    }

    [Fact]
    public async Task DownloadingAReadyModelDoesNothing()
    {
        DiarizationFixtures.InstallModel(ModelFolder);
        var service = MakeService();
        service.StartDownload();
        await service.WaitForDownloadAsync();
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task DeleteIsRefusedWhileDownloading()
    {
        DiarizationFixtures.InstallModel(ModelFolder, skipEmbedding: true);
        var gate = new Gate();
        downloader.Behavior = (_, _, _) => gate.WaitAsync();
        var service = MakeService();
        service.StartDownload();

        service.Delete();
        Assert.Equal(String("diarization.error.deleteWhileDownloading"), service.LastError);
        Assert.True(Directory.Exists(ModelFolder));

        gate.Open();
        await service.WaitForDownloadAsync();
        service.Delete();
        Assert.False(Directory.Exists(ModelFolder));
        Assert.Equal(NotDownloaded, service.Status);
        Assert.Null(service.LastError);
    }

    // Loading.

    [Fact]
    public async Task TheDiarizerIsRefusedAndNeverDownloadedWhenTheModelIsNotReady()
    {
        var service = MakeService();
        Assert.Equal(String("diarization.error.modelMissing"),
            (await Assert.ThrowsAsync<SlovoException>(() => service.DiarizerAsync(TestContext.Current.CancellationToken))).Message);

        DiarizationFixtures.InstallModel(ModelFolder, truncateSegmentation: true);
        Assert.Equal(String("diarization.error.modelDamaged"),
            (await Assert.ThrowsAsync<SlovoException>(() => service.DiarizerAsync(TestContext.Current.CancellationToken))).Message);

        Assert.Equal(0, downloader.Calls);
        Assert.Equal(0, loads);
        Assert.Equal(EngineLoadState.Unloaded, service.LoadState);
    }

    [Fact]
    public async Task AnUnsupportedSystemNeitherDownloadsNorLoads()
    {
        var service = MakeService(systemSupported: false);
        Assert.False(service.IsSupportedOnThisSystem);
        service.StartDownload();
        await service.WaitForDownloadAsync();
        Assert.Equal(0, downloader.Calls);

        DiarizationFixtures.InstallModel(ModelFolder);
        service.Refresh();
        Assert.Equal(String("diarization.error.unsupportedSystem"),
            (await Assert.ThrowsAsync<SlovoException>(() => service.DiarizerAsync(TestContext.Current.CancellationToken))).Message);
        Assert.Equal(0, loads);
    }

    [Fact]
    public async Task TheDiarizerLoadsOnceUntilReleased()
    {
        DiarizationFixtures.InstallModel(ModelFolder);
        var service = MakeService();

        var first = await service.DiarizerAsync(TestContext.Current.CancellationToken);
        var second = await service.DiarizerAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(EngineLoadState.Ready, service.LoadState);

        service.Release();
        Assert.Equal(EngineLoadState.Unloaded, service.LoadState);
        Assert.True(((Diarizer)first).Disposed, "Releasing frees the models");
        var third = await service.DiarizerAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first, third);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneLoad()
    {
        DiarizationFixtures.InstallModel(ModelFolder);
        var gate = new Gate();
        loadGate = gate;
        var service = MakeService();

        var one = service.DiarizerAsync(TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        Assert.Equal(EngineLoadState.Loading, service.LoadState);
        var two = service.DiarizerAsync(TestContext.Current.CancellationToken);
        gate.Open();

        Assert.Same(await one, await two);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task AFailedLoadIsReportedAndRetried()
    {
        DiarizationFixtures.InstallModel(ModelFolder);
        loadError = new SlovoException("cannot load");
        var service = MakeService();
        Assert.Equal("cannot load", (await Assert.ThrowsAsync<SlovoException>(() => service.DiarizerAsync(TestContext.Current.CancellationToken))).Message);
        Assert.Equal(EngineLoadState.Failed("cannot load"), service.LoadState);

        loadError = null;
        await service.DiarizerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EngineLoadState.Ready, service.LoadState);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task DeletingAfterReleaseWaitsForARunningLoad()
    {
        DiarizationFixtures.InstallModel(ModelFolder);
        var gate = new Gate();
        loadGate = gate;
        var service = MakeService();

        var pipeline = service.DiarizerAsync(TestContext.Current.CancellationToken);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);

        var deletion = service.DeleteAsync();
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.False(deletion.IsCompleted, "Delete must wait while a load reads the files");
        Assert.True(Directory.Exists(ModelFolder));
        Assert.Equal(String("diarization.error.modelMissing"),
            (await Assert.ThrowsAsync<SlovoException>(() => service.DiarizerAsync(TestContext.Current.CancellationToken))).Message);
        Assert.Equal(1, loads);

        gate.Open();
        var loaded = (Diarizer)await pipeline;
        await deletion;
        Assert.False(Directory.Exists(ModelFolder));
        Assert.Equal(EngineLoadState.Unloaded, service.LoadState);
        Assert.True(loaded.Disposed, "A load released while running is not kept");
        Assert.Equal(String("diarization.error.modelMissing"),
            (await Assert.ThrowsAsync<SlovoException>(() => service.DiarizerAsync(TestContext.Current.CancellationToken))).Message);
    }

    [Fact]
    public async Task ACancelledCallerStopsWaitingWhileTheLoadGoesOn()
    {
        DiarizationFixtures.InstallModel(ModelFolder);
        var gate = new Gate();
        loadGate = gate;
        var service = MakeService();
        using var cancellation = new CancellationTokenSource();

        var cancelled = service.DiarizerAsync(cancellation.Token);
        await TestSupport.WaitUntil(() => gate.Arrived == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var waiting = service.DiarizerAsync(TestContext.Current.CancellationToken);
        gate.Open();
        await waiting;
        Assert.Equal(1, loads);
        Assert.Equal(EngineLoadState.Ready, service.LoadState);
    }

    [Fact]
    public async Task WaitForLoadReturnsAtOnceWithoutALoad()
    {
        var service = MakeService();
        await service.WaitForLoadAsync();
        service.Release();
        Assert.Equal(EngineLoadState.Unloaded, service.LoadState);
    }

    // The real downloader against a fake server.

    [Fact]
    public async Task TheDownloaderInstallsBothModels()
    {
        var handler = new ModelDownloaderTests.FakeHandler();
        handler.Bodies[DiarizationModelFiles.SegmentationArchiveUrl.AbsoluteUri] = ModelDownloaderTests.MakeArchive(Root,
            DiarizationModelFiles.SegmentationFolderName,
            new() { ["model.onnx"] = new byte[DiarizationModelFiles.Segmentation.Size], ["LICENSE"] = "MIT"u8.ToArray() });
        handler.Bodies[DiarizationModelFiles.EmbeddingUrl.AbsoluteUri] = new byte[DiarizationModelFiles.Embedding.Size];
        var service = new SherpaDiarizationService(Root, downloader: new SherpaDiarizationModelDownloader(new HttpClient(handler)));
        var progress = new List<double>();
        service.PropertyChanged += (_, _) =>
        {
            lock (progress)
            {
                progress.Add(service.Status.Progress);
            }
        };

        service.StartDownload();
        await service.WaitForDownloadAsync();

        Assert.Null(service.LastError);
        Assert.True(service.Status.IsUsable, $"{service.Status}");
        Assert.True(File.Exists(Path.Combine(ModelFolder, DiarizationModelFiles.SegmentationFolderName, "LICENSE")));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(Directory.GetDirectories(Path.Combine(Root, ModelInstaller.StagingFolderName)));
    }

    [Fact]
    public async Task TheDownloaderRejectsWrongSizes()
    {
        var handler = new ModelDownloaderTests.FakeHandler();
        handler.Bodies[DiarizationModelFiles.SegmentationArchiveUrl.AbsoluteUri] = ModelDownloaderTests.MakeArchive(Root,
            DiarizationModelFiles.SegmentationFolderName, new() { ["model.onnx"] = new byte[100] });
        handler.Bodies[DiarizationModelFiles.EmbeddingUrl.AbsoluteUri] = new byte[DiarizationModelFiles.Embedding.Size];
        var service = new SherpaDiarizationService(Root, downloader: new SherpaDiarizationModelDownloader(new HttpClient(handler)));

        service.StartDownload();
        await service.WaitForDownloadAsync();

        Assert.Equal(String("diarization.error.filesMissingAfterDownload"), service.LastError);
        Assert.Equal(NotDownloaded, service.Status);

        handler.Bodies.Clear();
        service.StartDownload();
        await service.WaitForDownloadAsync();
        Assert.StartsWith(CoreStrings.Format("diarization.error.downloadFailed", ""), service.LastError);
        Assert.Contains("404", service.LastError);
    }
}
