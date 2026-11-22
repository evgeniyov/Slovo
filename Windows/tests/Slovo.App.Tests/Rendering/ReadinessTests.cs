using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.Views;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

/// <summary>The sidebar card that says whether dictation can work (the macOS ReadinessCard).</summary>
public class ReadinessTests
{
    public ReadinessTests() => CoreStrings.SetLanguage(AppLanguage.En);

    sealed class Rig
    {
        public AppSettings Settings { get; } = new(SettingsStore.InMemory());
        public FakePermissions Permissions { get; } = new();
        public FakeHotkeys Hotkeys { get; } = new();
        public FakeModels Models { get; } = new();
        public FakeShellActions Shell { get; } = new();
        public int SetupOpened { get; private set; }

        public DictationReadiness Compute() => DictationReadiness.Compute(Permissions, Hotkeys, Models, Settings);

        public ReadinessViewModel Make() => new(Permissions, Hotkeys, Models, Settings, Shell, () => SetupOpened++);
    }

    [AvaloniaFact]
    public void TheMicrophoneComesFirstThenTheKeyThenTheModel()
    {
        var rig = new Rig();
        rig.Permissions.Microphone = PermissionStatus.Denied;
        rig.Hotkeys.IsRunning = false;
        rig.Models.Statuses[EngineId.GigaamRnnt] = ModelStatus.NotDownloaded(1);
        Assert.Equal(DictationReadiness.PermissionsNeeded, rig.Compute());

        rig.Permissions.Microphone = PermissionStatus.Granted;
        Assert.Equal(DictationReadiness.KeyUnavailable, rig.Compute());

        rig.Hotkeys.IsRunning = true;
        Assert.Equal(DictationReadiness.ModelMissing(EngineId.GigaamRnnt), rig.Compute());
        rig.Models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Damaged("truncated");
        Assert.Equal(DictationReadiness.ModelMissing(EngineId.GigaamRnnt), rig.Compute());
        rig.Models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Downloading(0.42);
        Assert.Equal(DictationReadiness.Downloading(EngineId.GigaamRnnt, 0.42), rig.Compute());
        rig.Models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Ready(1);
        Assert.Equal(DictationReadiness.Ready(EngineId.GigaamRnnt), rig.Compute());
        rig.Models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Unknown;
        Assert.Equal(DictationReadiness.Checking, rig.Compute());
    }

    [AvaloniaFact]
    public void AnUnreadableMicrophoneSwitchDoesNotBlock()
    {
        // Windows then refuses the recording with its own message; the card cannot know better.
        var rig = new Rig();
        rig.Permissions.Microphone = PermissionStatus.Unknown;
        Assert.Equal(DictationReadiness.Ready(EngineId.GigaamRnnt), rig.Compute());
        rig.Permissions.Microphone = PermissionStatus.NotDetermined;
        Assert.Equal(DictationReadiness.PermissionsNeeded, rig.Compute());
    }

    [AvaloniaFact]
    public void TheEngineIsTheOneTheLanguageModeDictatesWith()
    {
        var rig = new Rig();
        rig.Settings.LanguageMode = LanguageMode.English;
        Assert.Equal(DictationReadiness.ModelMissing(EngineId.Parakeet), rig.Compute());
        rig.Settings.RussianEngine = EngineId.GigaamCtc;
        rig.Settings.LanguageMode = LanguageMode.Russian;
        Assert.Equal(DictationReadiness.Downloading(EngineId.GigaamCtc, 0.42), rig.Compute());
    }

    [AvaloniaFact]
    public void SaysWhatIsWrongInWords()
    {
        var rig = new Rig();
        var model = rig.Make();
        Assert.True(model.IsShown);
        Assert.True(model.IsReady);
        Assert.False(model.HasAction);
        Assert.Equal("Ready to dictate", model.Title);
        Assert.Equal("GigaAM v3 RNNT", model.Detail);

        rig.Settings.RussianEngine = EngineId.GigaamCtc;
        Assert.True(model.IsProgress);
        Assert.Equal("Downloading model · 42%", model.Title);
        Assert.Equal("GigaAM v3 CTC", model.Detail);

        rig.Hotkeys.IsRunning = false;
        model.Refresh();
        Assert.True(model.IsWarning);
        Assert.Equal("Dictation key not working", model.Title);
        Assert.Equal("Slovo could not listen to the keyboard", model.Detail);

        rig.Models.Statuses[EngineId.GigaamCtc] = ModelStatus.Unknown;
        rig.Hotkeys.IsRunning = true;
        model.Refresh();
        Assert.False(model.IsShown);
    }

    [AvaloniaFact]
    public void EachProblemLeadsToItsFix()
    {
        var rig = new Rig();
        var model = rig.Make();
        model.OpenCommand.Execute(null);
        Assert.Empty(rig.Shell.Calls);
        Assert.Equal(0, rig.SetupOpened);

        rig.Permissions.Microphone = PermissionStatus.Denied;
        model.Refresh();
        Assert.True(model.HasAction);
        model.OpenCommand.Execute(null);
        Assert.Equal(1, rig.SetupOpened);

        rig.Permissions.Microphone = PermissionStatus.Granted;
        rig.Hotkeys.IsRunning = false;
        model.Refresh();
        model.OpenCommand.Execute(null);
        Assert.Equal(["ShowTroubleshooting"], rig.Shell.Calls);

        rig.Hotkeys.IsRunning = true;
        rig.Settings.LanguageMode = LanguageMode.English;
        Assert.Equal(DictationReadinessKind.ModelMissing, model.State.Kind);
        model.OpenCommand.Execute(null);
        Assert.Equal("OpenSettings:Recognition", rig.Shell.Calls[^1]);
    }

    [AvaloniaFact]
    public void FollowsDownloadsAndSettingsAtOnce()
    {
        var rig = new Rig();
        var model = rig.Make();
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        rig.Settings.LanguageMode = LanguageMode.English;
        Assert.Equal(DictationReadiness.ModelMissing(EngineId.Parakeet), model.State);
        Assert.Contains(nameof(ReadinessViewModel.Title), changes);

        rig.Models.StartDownload(EngineId.Parakeet);
        Assert.Equal(DictationReadiness.Downloading(EngineId.Parakeet, 0), model.State);
    }

    [AvaloniaFact]
    public void TheMainWindowLooksAgainWhenItComesBack()
    {
        var rig = new Rig();
        var readiness = rig.Make();
        var window = new MainWindow { DataContext = new MainWindowViewModel((_, _) => new EmptyScreen(), rig.Shell, readiness) };
        window.Show();
        try
        {
            // The switch was turned off in Windows Settings, which says nothing to Slovo.
            rig.Permissions.Microphone = PermissionStatus.Denied;
            Assert.Equal(DictationReadinessKind.Ready, readiness.State.Kind);
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(DictationReadinessKind.PermissionsNeeded, readiness.State.Kind);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Each state the card can show, set up on a fresh rig.</summary>
    static readonly (string Name, Action<Rig> Arrange)[] States =
    [
        ("ready", _ => { }),
        ("permissions", rig => rig.Permissions.Microphone = PermissionStatus.Denied),
        ("key", rig => rig.Hotkeys.IsRunning = false),
        ("missing", rig => rig.Settings.LanguageMode = LanguageMode.English),
        ("downloading", rig => rig.Settings.RussianEngine = EngineId.GigaamCtc),
    ];

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void RendersEachStateInTheSidebar(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            foreach (var (name, arrange) in States)
            {
                var rig = new Rig();
                arrange(rig);
                var shell = new MainWindowViewModel((_, _) => new EmptyScreen(), rig.Shell, rig.Make());
                Assert.True(shell.Readiness!.IsShown);
                Snapshot.RenderWindow(new MainWindow { DataContext = shell }, $"sidebar-readiness-{name}-{language}", 1100, 720,
                    name == "missing" ? ThemeVariant.Dark : null);
            }
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
