using Slovo.Core.Dictation;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.Core.Tests.Dictation;

/// <summary>
/// A dictation controller over fakes, in a temporary data folder, with a manual clock and a
/// test main thread. Tests run their body through <see cref="Run(Func{Task})"/>, which
/// afterwards opens every gate, waits for the last dictation and stops the controller.
/// </summary>
sealed class DictationHarness : IDisposable
{
    public readonly string Temporary = Path.Combine(Path.GetTempPath(), "SlovoDictationTests-" + Guid.NewGuid().ToString("N"));
    public readonly AppPaths Paths;
    public readonly AppSettings Settings = new(SettingsStore.InMemory());
    public readonly TestMainThread MainThread = new();
    public readonly ManualClock Clock = new();

    public readonly FakeRecorder Recorder = new() { NextAudio = TestAudio.Speech(1) };
    public FakeHotkeys Hotkeys = new();
    public readonly FakeInserter Inserter = new();
    public readonly FakeForegroundApp ForegroundApp = new();
    public readonly FakePermissions Permissions = new();
    public FakeEngines Engines = new();
    public readonly FakeEngine Engine = new(EngineId.GigaamRnnt.RawValue(), "привет мир");
    public ITextProcessor Pipeline;
    public readonly FakePipeline FakePipeline = new();
    public readonly FakeHistory History;
    public readonly FakeDictionary Dictionary = new();
    public readonly FakeSnippets Snippets = new();
    public readonly FakeNotes Notes = new();
    public readonly FakeCategories Categories = new();
    public readonly FakeMeetings Meetings = new();
    public readonly FakeSounds Sounds = new();

    readonly List<Gate> gates = [];
    DictationController? controller;

    public DictationHarness()
    {
        Paths = new AppPaths(Temporary);
        Paths.CreateFolders();
        Directory.CreateDirectory(TemporaryFolder);
        History = new FakeHistory(Paths);
        Pipeline = FakePipeline;
        Engines.Engines[EngineId.GigaamRnnt] = Engine;
        Engines.LoadStates[EngineId.GigaamRnnt] = EngineLoadState.Ready;
    }

    /// <summary>Where the controller writes the recordings engines read.</summary>
    public string TemporaryFolder => Path.Combine(Temporary, "tmp");

    public DictationController Controller => controller ??= Build();

    /// <summary>A new controller over the same fakes, as after a relaunch.</summary>
    public DictationController Build()
    {
        // Engines built after a test swapped the provider still hand out the default engine.
        if (!Engines.Engines.ContainsKey(EngineId.GigaamRnnt))
        {
            Engines.Engines[EngineId.GigaamRnnt] = Engine;
            Engines.LoadStates[EngineId.GigaamRnnt] = EngineLoadState.Ready;
        }
        controller = new DictationController(new DictationController.Dependencies
        {
            Recorder = Recorder,
            Hotkeys = Hotkeys,
            Inserter = Inserter,
            ForegroundApp = ForegroundApp,
            Permissions = Permissions,
            Engines = Engines,
            Pipeline = Pipeline,
            History = History,
            Dictionary = Dictionary,
            Snippets = Snippets,
            Notes = Notes,
            Categories = Categories,
            Meetings = Meetings,
            Sounds = Sounds,
            Settings = Settings,
            Paths = Paths,
            MainThread = MainThread,
            Uptime = Clock,
            Scheduler = Clock,
        })
        {
            // Messages stay until a test lets time pass; the time limit and the hold are checked by hand.
            MessageLifetime = 60,
            LimitCheckInterval = 3_600,
            HoldCheckDelay = 3_600,
            TemporaryFolder = TemporaryFolder,
        };
        return controller;
    }

    public Gate NewGate()
    {
        var gate = new Gate();
        gates.Add(gate);
        return gate;
    }

    /// <summary>Kept recordings under Recordings/, as paths relative to it with forward slashes.</summary>
    public List<string> RecordedFiles() =>
        Directory.Exists(Paths.RecordingsFolder)
            ? Directory.EnumerateFiles(Paths.RecordingsFolder, "*.wav", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Paths.RecordingsFolder, path).Replace(Path.DirectorySeparatorChar, '/'))
                .ToList()
            : [];

    public bool TemporaryRecordingExists(Guid id) =>
        File.Exists(Path.Combine(TemporaryFolder, "Slovo-" + id.ToString("D").ToUpperInvariant() + ".wav"));

    /// <summary>The key is still held when the minimum hold has passed.</summary>
    public void ConfirmHold()
    {
        Hotkeys.IsKeyDown = true;
        Controller.HoldCheckDue();
    }

    /// <summary>A whole push-to-talk dictation: key down, hold confirmed, key up, transcription.</summary>
    public async Task Dictate(DictationTrigger? trigger = null)
    {
        Controller.Handle(trigger ?? DictationTrigger.BeginPushToTalk);
        ConfirmHold();
        Hotkeys.IsKeyDown = false;
        Controller.Handle(DictationTrigger.FinishRecording);
        await Controller.WaitForProcessingAsync();
    }

    public Task Run(Func<Task> body) => MainThread.RunAsync(async () =>
    {
        try
        {
            await body();
        }
        finally
        {
            // Late engines finish into an abandoned dictation, which must change nothing.
            foreach (var gate in gates)
            {
                gate.Open();
            }
            if (controller is not null)
            {
                await controller.WaitForProcessingAsync();
                controller.Stop();
            }
        }
    });

    public Task Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    public void Dispose()
    {
        try
        {
            Directory.Delete(Temporary, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
