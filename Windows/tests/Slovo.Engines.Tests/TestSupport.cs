using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Engines.Recognition;

namespace Slovo.Engines.Tests;

/// <summary>Temporary folders, fixtures and the real model folders (read-only).</summary>
static class TestSupport
{
    public static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures", "audio");

    public static string Fixture(string name) => Path.Combine(FixturesFolder, name);

    /// <summary>
    /// The installed models: <c>SLOVO_MODELS_DIR</c>, else the macOS app's folder, which uses
    /// the very same GigaAM files. Only ever read.
    /// </summary>
    public static string RealModelsRoot
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("SLOVO_MODELS_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Slovo", "Models")
                : Path.Combine(home, "Library", "Application Support", "Slovo", "Models");
        }
    }

    /// <summary>Skips the test unless the real model of an engine is installed and complete.</summary>
    public static string RequireRealModel(EngineId id)
    {
        var root = RealModelsRoot;
        var status = Slovo.Engines.Models.ModelManager.DiskStatus(id, root);
        Assert.SkipUnless(status.IsUsable, $"The {id.DisplayName()} model is not installed in {root} ({status.Kind})");
        return root;
    }

    /// <summary>
    /// The speaker models for the end-to-end diarization test: <c>SLOVO_DIARIZATION_MODELS_DIR</c>
    /// laid out like <c>Models/diarization-onnx</c>. Never downloaded by the tests.
    /// </summary>
    public static string RequireDiarizationModels()
    {
        var folder = Environment.GetEnvironmentVariable("SLOVO_DIARIZATION_MODELS_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), "SLOVO_DIARIZATION_MODELS_DIR is not set");
        var status = Slovo.Engines.Diarization.DiarizationModelFiles.Status(folder!);
        Assert.SkipUnless(status.IsUsable, $"The speaker models in {folder} are not usable ({status.Kind}: {status.Reason})");
        return folder!;
    }

    /// <summary>Writes files of the release sizes without their content (sparse where the file system can).</summary>
    public static void InstallFakeModel(SherpaModelSpec spec, string modelsRoot, string? resize = null, long delta = 0, string? skip = null)
    {
        var folder = spec.FolderPath(modelsRoot);
        Directory.CreateDirectory(folder);
        foreach (var file in spec.Files.Where(file => file.Name != skip))
        {
            using var stream = new FileStream(Path.Combine(folder, file.Name), FileMode.Create, FileAccess.Write);
            stream.SetLength(file.Size + (file.Name == resize ? delta : 0));
        }
    }

    /// <summary>Polls a condition, failing after the timeout.</summary>
    public static async Task WaitUntil(Func<bool> condition, double timeoutSeconds = 5, string? message = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(5);
        }
        Assert.Fail(message ?? $"Condition not met within {timeoutSeconds} s");
    }
}

/// <summary>A temporary folder removed at the end of a test.</summary>
sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder(string name = "SlovoEnginesTests")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A gate test fakes wait at until the test opens it.</summary>
sealed class Gate
{
    readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int arrived;

    public int Arrived => Volatile.Read(ref arrived);

    public Task WaitAsync()
    {
        Interlocked.Increment(ref arrived);
        return opened.Task;
    }

    public void Open() => opened.TrySetResult();
}

/// <summary>A fake monotonic clock.</summary>
sealed class FakeUptime : IUptime
{
    public double Now { get; private set; } = 1_000;

    public void Advance(double seconds) => Now += seconds;
}
