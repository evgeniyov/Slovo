using System.Diagnostics;
using System.Net;
using Slovo.Core.Model;
using Slovo.Engines.Models;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Tests.Models;

/// <summary>
/// Downloading, unpacking and installing archives, against an in-memory HTTP handler and
/// small archives built with the system's tar. Nothing touches the network.
/// </summary>
public sealed class ModelDownloaderTests : IDisposable
{
    readonly TemporaryFolder temporary = new("SlovoModelDownloaderTests");

    string Root => temporary.Path;

    public void Dispose() => temporary.Dispose();

    /// <summary>Serves fixed bodies by URL; everything else is a 404.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Bodies { get; } = [];
        public List<Uri> Requests { get; } = [];
        /// <summary>Stops sending after this many bytes and waits (a stalled or slow server).</summary>
        public int? StallAfter { get; set; }
        /// <summary>Claims a longer body than it sends.</summary>
        public bool Truncate { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }
            if (!Bodies.TryGetValue(request.RequestUri!.AbsoluteUri, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
            }
            var content = new StreamContent(new SlowStream(Truncate ? body[..(body.Length / 2)] : body, StallAfter));
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Hands out data in small pieces and hangs after a point.</summary>
    sealed class SlowStream(byte[] data, int? stallAfter) : Stream
    {
        int position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAfter is { } limit && position >= limit)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            var count = Math.Min(Math.Min(buffer.Length, 4096), data.Length - position);
            data.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Packs <c>folderName/…</c> with the system's tar and returns the archive's bytes.</summary>
    internal static byte[] MakeArchive(string workFolder, string folderName, Dictionary<string, byte[]> files)
    {
        var source = Path.Combine(workFolder, "source-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(source, folderName);
        Directory.CreateDirectory(folder);
        foreach (var (name, data) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, name), data);
        }
        var archive = Path.Combine(workFolder, $"{folderName}-{Guid.NewGuid():N}.tar.bz2");
        var start = new ProcessStartInfo(TarExtractor.Executable) { UseShellExecute = false };
        foreach (var argument in new[] { "-cjf", archive, "-C", source, folderName })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Directory.Delete(source, recursive: true);
        return File.ReadAllBytes(archive);
    }

    static SherpaModelSpec SmallSpec => ModelCatalog.GigaamCtc with
    {
        Files = [new ModelFile("model.int8.onnx", 10), new ModelFile("tokens.txt", 3)],
    };

    [Fact]
    public async Task DownloadsUnpacksChecksAndInstalls()
    {
        var spec = SmallSpec;
        var handler = new FakeHandler();
        handler.Bodies[spec.ArchiveUrl.AbsoluteUri] = MakeArchive(Root, spec.ReleaseFolderName,
            new() { ["model.int8.onnx"] = new byte[10], ["tokens.txt"] = "abc"u8.ToArray(), ["README.md"] = "x"u8.ToArray() });
        var downloader = new SherpaModelDownloader(new HttpClient(handler), id => id == EngineId.GigaamCtc ? spec : null);
        var models = Path.Combine(Root, "Models");
        var reports = new List<double>();

        await downloader.DownloadAsync(EngineId.GigaamCtc, models, reports.Add, TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16.tar.bz2",
            Assert.Single(handler.Requests).AbsoluteUri);
        var folder = spec.FolderPath(models);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(folder, "tokens.txt")));
        Assert.Equal(10, new FileInfo(Path.Combine(folder, "model.int8.onnx")).Length);
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));
        Assert.Equal(reports.Order(), reports);
        Assert.Equal(1, reports[^1]);
        Assert.Contains(SherpaModelDownloader.DownloadShare, reports);
        // The staging folder is gone; only its hidden parent may stay until the next refresh.
        Assert.Empty(Directory.GetDirectories(Path.Combine(models, ModelInstaller.StagingFolderName)));
    }

    [Fact]
    public async Task AnHttpErrorIsReportedWithItsStatus()
    {
        var downloader = new SherpaModelDownloader(new HttpClient(new FakeHandler()), _ => SmallSpec);
        var error = await Assert.ThrowsAsync<SlovoException>(() =>
            downloader.DownloadAsync(EngineId.GigaamCtc, Root, _ => { }, TestContext.Current.CancellationToken));
        Assert.Contains("404", error.Message);
        Assert.False(Directory.Exists(SmallSpec.FolderPath(Root)));
    }

    [Fact]
    public async Task ATruncatedBodyIsAnError()
    {
        var handler = new FakeHandler { Truncate = true };
        handler.Bodies[SmallSpec.ArchiveUrl.AbsoluteUri] = new byte[100_000];
        var destination = Path.Combine(Root, "file");
        await Assert.ThrowsAsync<SlovoException>(() => FileDownload.RunAsync(new HttpClient(handler), SmallSpec.ArchiveUrl, destination,
            _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingStopsTheDownloadAndLeavesNothing()
    {
        var handler = new FakeHandler { StallAfter = 50_000 };
        handler.Bodies[SmallSpec.ArchiveUrl.AbsoluteUri] = new byte[200_000];
        var downloader = new SherpaModelDownloader(new HttpClient(handler), _ => SmallSpec);
        using var cancellation = new CancellationTokenSource();
        var reports = new List<double>();

        var download = downloader.DownloadAsync(EngineId.GigaamCtc, Root, value =>
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }, cancellation.Token);
        await TestSupport.WaitUntil(() => { lock (reports) { return reports.Count > 0; } });
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.False(Directory.Exists(SmallSpec.FolderPath(Root)));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Root, ModelInstaller.StagingFolderName)));
    }

    [Fact]
    public async Task AStalledDownloadTimesOut()
    {
        var handler = new FakeHandler { StallAfter = 10_000 };
        handler.Bodies[SmallSpec.ArchiveUrl.AbsoluteUri] = new byte[100_000];
        var saved = FileDownload.StallTimeout;
        FileDownload.StallTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var error = await Assert.ThrowsAsync<SlovoException>(() => FileDownload.RunAsync(new HttpClient(handler), SmallSpec.ArchiveUrl,
                Path.Combine(Root, "file"), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(Core.Localization.CoreStrings.Format("engines.download.stalled", 0L), error.Message);
        }
        finally
        {
            FileDownload.StallTimeout = saved;
        }
    }

    // Installing archives (DictationModelManagerTests.swift).

    [Fact]
    public async Task InstallArchiveExtractsVerifiesAndReplacesADamagedCopy()
    {
        const string folderName = "sherpa-onnx-test-model";
        var archive = Path.Combine(Root, "archive.tar.bz2");
        File.WriteAllBytes(archive, MakeArchive(Root, folderName, new() { ["model.onnx"] = new byte[10], ["tokens.txt"] = "abc"u8.ToArray() }));
        var parent = Path.Combine(Root, "gigaam-v3");
        var destination = Path.Combine(parent, folderName);
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "model.onnx"), [9]);

        var staging = ModelInstaller.MakeStagingFolder(Root);
        await ModelInstaller.InstallArchiveAsync(archive, folderName, [new("model.onnx", 10), new("tokens.txt", 3)], parent, staging, "Test",
            TestContext.Current.CancellationToken);

        Assert.Equal(10, new FileInfo(Path.Combine(destination, "model.onnx")).Length);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(destination, "tokens.txt")));
        Assert.False(Directory.Exists(Path.Combine(staging, "extracted", folderName)));
    }

    [Fact]
    public async Task InstallArchiveRejectsWrongSizesAndKeepsTheOldCopy()
    {
        const string folderName = "sherpa-onnx-test-model";
        var archive = Path.Combine(Root, "archive.tar.bz2");
        File.WriteAllBytes(archive, MakeArchive(Root, folderName, new() { ["model.onnx"] = new byte[4] }));
        var parent = Path.Combine(Root, "gigaam-v3");
        var destination = Path.Combine(parent, folderName);
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "old.txt"), [9]);

        var staging = ModelInstaller.MakeStagingFolder(Root);
        var error = await Assert.ThrowsAsync<SlovoException>(() => ModelInstaller.InstallArchiveAsync(archive, folderName,
            [new("model.onnx", 10), new("tokens.txt", 3)], parent, staging, "Test", TestContext.Current.CancellationToken));

        Assert.Contains("model.onnx", error.Message);
        Assert.Contains("tokens.txt", error.Message);
        Assert.Contains("Test", error.Message);
        Assert.True(File.Exists(Path.Combine(destination, "old.txt")));
    }

    [Fact]
    public async Task ATarFailureNamesTheArchive()
    {
        var bogus = Path.Combine(Root, "broken.tar.bz2");
        File.WriteAllText(bogus, "not an archive");
        var target = Path.Combine(Root, "out");
        Directory.CreateDirectory(target);

        var error = await Assert.ThrowsAsync<SlovoException>(() => TarExtractor.ExtractAsync(bogus, target, TestContext.Current.CancellationToken));
        Assert.Contains("broken.tar.bz2", error.Message);
    }

    [Fact]
    public void TheSystemTarIsUsed()
    {
        Assert.True(File.Exists(TarExtractor.Executable), TarExtractor.Executable);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "tar.exe" : "tar", TarExtractor.Executable);
    }
}
