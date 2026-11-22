using System.Diagnostics;
using System.Net;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Models;

/// <summary>Fetches and installs model files the user asked for.</summary>
public interface IModelDownloader
{
    /// <summary>
    /// Downloads and installs the model of <paramref name="id"/> under <paramref name="modelsRoot"/>,
    /// reporting progress from 0 to 1 on any thread. Stops promptly when cancelled.
    /// </summary>
    Task DownloadAsync(EngineId id, string modelsRoot, Action<double> progress, CancellationToken cancellation);
}

/// <summary>
/// The real downloads: GigaAM and Parakeet archives from the sherpa-onnx release, unpacked
/// with the system's tar (the port of LiveModelDownloader). Used only when the user
/// presses Download.
/// </summary>
public sealed class SherpaModelDownloader : IModelDownloader
{
    /// <summary>Download progress fills this share of the bar; unpacking the archive takes the rest.</summary>
    internal const double DownloadShare = 0.9;

    readonly HttpClient http;
    readonly Func<EngineId, SherpaModelSpec?> catalog;

    /// <param name="http">The client to download with; a shared one by default.</param>
    public SherpaModelDownloader(HttpClient? http = null) : this(http, ModelCatalog.For)
    {
    }

    /// <summary>Lets tests install small archives described by their own specs.</summary>
    internal SherpaModelDownloader(HttpClient? http, Func<EngineId, SherpaModelSpec?> catalog)
    {
        this.http = http ?? FileDownload.SharedClient;
        this.catalog = catalog;
    }

    public async Task DownloadAsync(EngineId id, string modelsRoot, Action<double> progress, CancellationToken cancellation)
    {
        if (catalog(id) is not { } spec)
        {
            return;
        }
        var staging = ModelInstaller.MakeStagingFolder(modelsRoot);
        try
        {
            var archive = Path.Combine(staging, spec.ArchiveName);
            await FileDownload.RunAsync(http, spec.ArchiveUrl, archive, fraction => progress(fraction * DownloadShare), cancellation)
                .ConfigureAwait(false);
            progress(DownloadShare);
            var destinationParent = Path.GetDirectoryName(spec.FolderPath(modelsRoot))!;
            await ModelInstaller.InstallArchiveAsync(archive, spec.ReleaseFolderName, spec.Files, destinationParent, staging,
                id.DisplayName(), cancellation).ConfigureAwait(false);
            progress(1);
        }
        finally
        {
            ModelInstaller.RemoveQuietly(staging);
        }
    }
}

/// <summary>Unpacking and moving downloaded models into place.</summary>
public static class ModelInstaller
{
    /// <summary>
    /// Hidden folder under the models root for downloads in progress. It lives on the
    /// same volume as the models, so the final move is a rename.
    /// </summary>
    public const string StagingFolderName = ".downloads";

    public static string MakeStagingFolder(string modelsRoot)
    {
        var parent = Path.Combine(modelsRoot, StagingFolderName);
        var folder = Path.Combine(parent, Guid.NewGuid().ToString("D").ToUpperInvariant());
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(parent, File.GetAttributes(parent) | FileAttributes.Hidden);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return folder;
    }

    /// <summary>
    /// Extracts a release archive inside <paramref name="staging"/>, checks every file
    /// against the release sizes, then moves the model folder into
    /// <paramref name="destinationParent"/> with one rename, so engines never see a
    /// half-extracted model. A damaged earlier copy is replaced; a failed install keeps it.
    /// </summary>
    public static async Task InstallArchiveAsync(string archive, string folderName, IReadOnlyList<ModelFile> expectedFiles,
        string destinationParent, string staging, string displayName, CancellationToken cancellation)
    {
        var extracted = Path.Combine(staging, "extracted");
        Directory.CreateDirectory(extracted);
        await TarExtractor.ExtractAsync(archive, extracted, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();

        var folder = Path.Combine(extracted, folderName);
        var (missing, wrongSize) = ModelFiles.Check(folder, expectedFiles);
        var wrong = expectedFiles.Select(file => file.Name).Where(name => missing.Contains(name) || wrongSize.Contains(name)).ToList();
        if (wrong.Count > 0)
        {
            throw new SlovoException(CoreStrings.Format("dictation.error.archiveIncomplete", displayName, string.Join(", ", wrong)));
        }
        MoveIntoPlace(folder, destinationParent, folderName, staging);
    }

    /// <summary>Moves a verified folder to <c>destinationParent/folderName</c>, replacing what is there.</summary>
    internal static void MoveIntoPlace(string folder, string destinationParent, string folderName, string staging)
    {
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, folderName);
        var previous = Path.Combine(staging, "previous-" + folderName);
        var hadPrevious = Directory.Exists(destination) || File.Exists(destination);
        if (hadPrevious)
        {
            // A damaged copy moves aside and is deleted together with the staging folder.
            if (File.Exists(destination))
            {
                File.Move(destination, previous);
            }
            else
            {
                Directory.Move(destination, previous);
            }
        }
        try
        {
            // A virus scanner that is still reading the fresh files can hold them open for a
            // moment on Windows, which fails the rename with "access denied".
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(folder))
                    {
                        File.Move(folder, destination);
                    }
                    else
                    {
                        Directory.Move(folder, destination);
                    }
                    break;
                }
                catch (Exception error) when (attempt < 5 && error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200 * attempt);
                }
            }
        }
        catch
        {
            if (hadPrevious)
            {
                try
                {
                    if (File.Exists(previous))
                    {
                        File.Move(previous, destination);
                    }
                    else
                    {
                        Directory.Move(previous, destination);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
            throw;
        }
    }

    internal static void RemoveQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Runs the system's tar on a bzip2 archive: <c>tar.exe</c> on Windows 10 1803 and later, <c>/usr/bin/tar</c> elsewhere.</summary>
public static class TarExtractor
{
    public static string Executable
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            }
            return File.Exists("/usr/bin/tar") ? "/usr/bin/tar" : "tar";
        }
    }

    public static async Task ExtractAsync(string archive, string folder, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // bsdtar strips leading slashes and refuses ".." entries unless told otherwise.
        foreach (var argument in new[] { "-xjf", archive, "-C", folder })
        {
            start.ArgumentList.Add(argument);
        }
        var name = Path.GetFileName(archive);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SlovoException(CoreStrings.Format("dictation.error.extractFailed", name, -1), error);
        }
        process.StandardInput.Close();
        // Drained so a chatty tar never blocks on a full pipe; the text is not used.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            throw;
        }
        await Task.WhenAll(output, errors).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new SlovoException(CoreStrings.Format("dictation.error.extractFailed", name, process.ExitCode));
        }
    }
}

/// <summary>One HTTP download into a file, with progress, cancellation and a stall timeout.</summary>
public static class FileDownload
{
    /// <summary>A download that receives nothing for this long fails, like URLSession's request timeout.</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    internal static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(60),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="destination"/>, reporting 0…1
    /// when the size is known. A non-2xx answer throws a localized <see cref="SlovoException"/>.
    /// </summary>
    public static async Task RunAsync(HttpClient http, Uri url, string destination, Action<double> progress,
        CancellationToken cancellation, long? expectedBytes = null)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Slovo-Windows/1.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SlovoException(CoreStrings.Format("dictation.error.httpStatus", (int)response.StatusCode));
            }
            var total = response.Content.Headers.ContentLength ?? expectedBytes;
            await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, useAsync: true);
            var buffer = new byte[1 << 20];
            long written = 0;
            var lastReported = -1.0;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                written += read;
                if (total is > 0)
                {
                    var fraction = Math.Min(1, (double)written / total.Value);
                    if (fraction - lastReported >= 0.005)
                    {
                        lastReported = fraction;
                        progress(fraction);
                    }
                }
            }
            if (total is > 0 && written < total)
            {
                throw new SlovoException(CoreStrings.Get("engines.download.truncated"));
            }
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new SlovoException(CoreStrings.Format("engines.download.stalled", (long)StallTimeout.TotalSeconds), error);
        }
    }
}
