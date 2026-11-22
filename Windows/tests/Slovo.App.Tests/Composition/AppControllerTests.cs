using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Slovo.App.Composition;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Settings;
using Slovo.Core;
using Slovo.Platform.Windows;

namespace Slovo.App.Tests.Composition;

/// <summary>
/// The whole app built the way Program builds it, on a temporary data folder and the
/// stand-in platform services (so it runs on any system): every screen and settings
/// section opens, and quitting saves and stops everything.
/// </summary>
public class AppControllerTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "slovo-app-controller-" + Guid.NewGuid().ToString("N"));
    readonly AppPaths paths;

    public AppControllerTests()
    {
        paths = new AppPaths(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    void CompleteOnboarding()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(paths.SettingsFile, new JsonObject { [Slovo.Core.Settings.SettingsKey.OnboardingCompleted] = true }.ToJsonString());
    }

    [AvaloniaFact]
    public void OpensEveryScreenAndSettingsSectionThenQuitsCleanly()
    {
        CompleteOnboarding();
        var exited = 0;
        var controller = new AppController(Application.Current!, paths, () => exited++)
        {
            MakePlatformServices = PlatformServices.CreateInert,
        };
        controller.Start(startedAtSignIn: false);
        Dispatcher.UIThread.RunJobs();

        var main = Assert.IsType<MainWindowViewModel>(controller.MainModel);
        Assert.False(controller.IsOnboardingOpen);
        foreach (var screen in ScreenInfo.All)
        {
            main.Select(screen);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(main.CurrentScreen);
        }

        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            controller.OpenSettings(section.ToString());
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(controller.SettingsModel);
        }
        controller.ShowTroubleshooting();
        Assert.True(main.HasSheet);
        controller.ShowShortcuts();
        Assert.IsType<ShortcutsSheetViewModel>(main.Sheet);
        // The stand-in keyboard hook never runs, so the sidebar card says the key is not working.
        Assert.Equal(DictationReadinessKind.KeyUnavailable, main.Readiness!.State.Kind);

        controller.Quit();
        Assert.Equal(1, exited);
        controller.Shutdown();
        Assert.True(File.Exists(Path.Combine(root, "dictionary.json")));
    }

    [AvaloniaFact]
    public void AMeetingThatCannotRecordSaysWhyInsteadOfCrashing()
    {
        CompleteOnboarding();
        var controller = new AppController(Application.Current!, paths, () => { });
        controller.Start(startedAtSignIn: false);
        var main = controller.MainModel!;
        main.Select(Screen.Meetings);
        var meetings = Assert.IsType<Slovo.App.ViewModels.Meetings.MeetingsViewModel>(main.CurrentScreen);

        // The stand-in microphone never starts, so the recorder gives up with a message.
        controller.NewMeeting();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!meetings.Recording.HasFailure && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Assert.True(meetings.Recording.HasFailure);
        Assert.False(string.IsNullOrWhiteSpace(meetings.Recording.FailureText));
        Assert.True(meetings.CanStart);
        controller.Shutdown();
    }

    [AvaloniaFact]
    public void AFirstLaunchShowsOnboardingInsteadOfTheWindow()
    {
        var controller = new AppController(Application.Current!, paths, () => { });
        controller.Start(startedAtSignIn: false);
        Dispatcher.UIThread.RunJobs();
        Assert.True(controller.IsOnboardingOpen);
        Assert.Null(controller.MainModel);
        controller.Shutdown();
    }

    [AvaloniaFact]
    public void DamagedDataFilesAreReportedOnceWhenTheWindowOpens()
    {
        CompleteOnboarding();
        File.WriteAllText(paths.HistoryFile, "{ not json");
        File.WriteAllText(paths.SnippetsFile, "[1, 2,");
        var controller = new AppController(Application.Current!, paths, () => { });
        controller.Start(startedAtSignIn: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(controller.MainModel);

        controller.OpenMainWindow();
        var main = controller.MainModel!;
        var alert = Assert.IsType<AlertSheetViewModel>(main.Sheet);
        Assert.Equal(Slovo.App.Localization.Loc.Instance.Get("data.damaged.title"), alert.Title);
        Assert.Contains("history.json", alert.Message);
        Assert.Contains("snippets.json", alert.Message);
        Assert.Equal(2, alert.Message.Split('\n').Length);

        alert.OkCommand.Execute(null);
        controller.OpenMainWindow();
        Assert.False(main.HasSheet);
        controller.Shutdown();
    }

    [AvaloniaFact]
    public void IntactDataFilesSayNothing()
    {
        CompleteOnboarding();
        var controller = new AppController(Application.Current!, paths, () => { });
        controller.Start(startedAtSignIn: false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(controller.MainModel!.HasSheet);
        controller.Shutdown();
    }

    [AvaloniaFact]
    public void NotesClaudeWroteWhileSlovoWasInTheBackgroundAppearWhenTheWindowComesForward()
    {
        CompleteOnboarding();
        var disk = new Slovo.Core.Meetings.MeetingStore(paths, Slovo.Core.Platform.ImmediateMainThread.Shared, saveDelay: TimeSpan.Zero);
        var id = disk.Create("Созвон с командой").Id;
        disk.Flush();
        var controller = new AppController(Application.Current!, paths, () => { });
        controller.Start(startedAtSignIn: false);
        var main = controller.MainModel!;
        main.Select(Screen.Meetings);
        Dispatcher.UIThread.RunJobs();
        var meetings = Assert.IsType<Slovo.App.ViewModels.Meetings.MeetingsViewModel>(main.CurrentScreen);
        Assert.Equal(id, meetings.Detail?.Id);
        Assert.False(meetings.Detail!.HasClaudeNotes);

        // Slovo.exe --mcp, a process of its own, writes the notes straight into meeting.json.
        new Slovo.Core.Meetings.MeetingNotesFile(paths).Write(new Slovo.Core.Model.MeetingNotes("Договорились о сроках.", ["Релиз в пятницу"]), id);
        Dispatcher.UIThread.RunJobs();
        Assert.False(meetings.Detail!.HasClaudeNotes);

        controller.OpenMainWindow();
        Dispatcher.UIThread.RunJobs();
        Assert.True(meetings.Detail!.HasClaudeNotes);
        Assert.Equal("Договорились о сроках.", meetings.Detail.ClaudeSummary);
        controller.Shutdown();
    }

    /// <summary>A player that plays until told to stop.</summary>
    sealed class RecordingPlayer : Slovo.Core.Platform.IAudioPlayer
    {
        public int Stops { get; private set; }
        public bool IsPlaying { get; private set; }
        public double Position => 0;
        public double Duration => 1;
        public double Rate { get; set; } = 1;
        public event Action? StateChanged;

        public void Play(IReadOnlyList<Slovo.Core.Platform.IAudioSource> tracks, double startSeconds = 0)
        {
            IsPlaying = true;
            StateChanged?.Invoke();
        }

        public void Pause() => IsPlaying = false;
        public void Resume() => IsPlaying = true;

        public void Stop()
        {
            Stops++;
            IsPlaying = false;
            StateChanged?.Invoke();
        }

        public void Seek(double seconds) { }
        public void Dispose() { }
    }

    [AvaloniaFact]
    public void ClosingTheWindowStopsWhatPlaysAndKeepsSlovoInTheTray()
    {
        CompleteOnboarding();
        var entry = new Slovo.Core.Model.HistoryEntry { Text = "Привет", Date = DateTimeOffset.Now, Duration = 1, AudioFileName = "2026-09-15/clip.wav" };
        Slovo.Core.Audio.WavFile.Write(new Slovo.Core.Model.RecordedAudio(new float[16_000]), paths.RecordingPath(entry.AudioFileName));
        var disk = new Slovo.Core.Storage.HistoryStore(paths, Slovo.Core.Platform.ImmediateMainThread.Shared);
        disk.Add(entry);
        disk.Flush();

        var players = new List<RecordingPlayer>();
        var controller = new AppController(Application.Current!, paths, () => { })
        {
            MakeAudioPlayer = () =>
            {
                var player = new RecordingPlayer();
                players.Add(player);
                return player;
            },
        };
        controller.Start(startedAtSignIn: false);
        Dispatcher.UIThread.RunJobs();
        // One for dictations, one for meetings.
        Assert.Equal(2, players.Count);

        var main = controller.MainModel!;
        main.Select(Screen.Home);
        var home = Assert.IsType<Slovo.App.ViewModels.Screens.HomeViewModel>(main.CurrentScreen);
        var row = home.Groups.SelectMany(group => group.Rows).Single();
        home.TogglePlayCommand.Execute(row);
        Assert.True(row.IsPlaying);
        Assert.Contains(players, player => player.IsPlaying);
        var stopsBefore = players.Select(player => player.Stops).ToList();

        var window = controller.MainWindowView!;
        window.Close();
        Dispatcher.UIThread.RunJobs();

        // Hidden, not closed: Slovo stays in the tray, and nothing plays behind the window.
        Assert.False(window.IsVisible);
        Assert.False(row.IsPlaying);
        Assert.All(players, player => Assert.False(player.IsPlaying));
        Assert.All(players.Zip(stopsBefore), pair => Assert.True(pair.First.Stops > pair.Second));
        controller.OpenMainWindow();
        Assert.Same(window, controller.MainWindowView);
        Assert.True(window.IsVisible);
        controller.Shutdown();
    }

    [AvaloniaFact]
    public void AStartAtSignInStaysInTheTray()
    {
        CompleteOnboarding();
        var controller = new AppController(Application.Current!, paths, () => { });
        controller.Start(startedAtSignIn: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(controller.MainModel);
        controller.OpenMainWindow();
        Assert.NotNull(controller.MainModel);
        controller.Shutdown();
    }
}
