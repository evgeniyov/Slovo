using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Slovo.App.FlowBar;
using Slovo.App.Localization;
using Slovo.App.Native;
using Slovo.App.Onboarding;
using Slovo.App.Tray;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Screens;
using Slovo.App.ViewModels.Settings;
using Slovo.App.Views;
using Slovo.App.Views.Settings;
using Slovo.Core;
using Slovo.Core.Dictation;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Core.Storage;
using Slovo.Core.Text;
using Slovo.Engines;
using Slovo.Engines.Diarization;
using Slovo.Engines.Models;
using Slovo.Platform.Windows;
using Slovo.Platform.Windows.Spelling;

namespace Slovo.App.Composition;

/// <summary>
/// Builds the app once Avalonia is up (the macOS AppModel and ServiceWiring): settings,
/// stores, models and engines, dictation and meetings, the Flow bar, the tray icon and the
/// windows, and takes them down again on quit.
/// </summary>
sealed partial class AppController : ITrayActions, IShellActions
{
    static readonly Logger Logger = Log.For("App");

    readonly Application application;
    readonly Action exitApplication;
    readonly AppPaths paths;
    readonly IMainThread mainThread = AvaloniaMainThread.Shared;
    readonly IUptime uptime = SystemUptime.Shared;

    AppSettings settings = null!;
    PlatformServices platform = null!;
    IWordLexicon lexicon = null!;
    HistoryStore history = null!;
    DictionaryStore dictionary = null!;
    SnippetStore snippets = null!;
    NoteStore notes = null!;
    AppCategoryStore categories = null!;
    MeetingStore meetings = null!;
    ModelManager models = null!;
    EngineManager engines = null!;
    SherpaDiarizationService diarization = null!;
    DictationController dictation = null!;
    DictationWiring wiring = null!;
    RetentionCleaner retention = null!;
    HistoryActions historyActions = null!;
    NoteDictation noteDictation = null!;
    PracticeDictation practiceDictation = null!;

    FlowBarViewModel flowBarModel = null!;
    FlowBarWindow flowBar = null!;
    FocusWatcher focusWatcher = null!;
    bool? focusIsText;
    TrayController? tray;

    MainWindow? mainWindow;
    MainWindowViewModel? mainModel;
    SettingsWindow? settingsWindow;
    SettingsViewModel? settingsModel;
    OnboardingWindow? onboardingWindow;
    bool quitting;
    /// <summary>Damaged data files found at launch, said once the main window first opens.</summary>
    (string Title, string Message)? damagedFilesAlert;

    /// <param name="exitApplication">Ends the Avalonia lifetime; called by Quit after everything is saved.</param>
    public AppController(Application application, AppPaths paths, Action exitApplication)
    {
        this.application = application;
        this.paths = paths;
        this.exitApplication = exitApplication;
    }

    /// <summary>Makes the players for dictations and meetings; tests put in one that records what it is asked.</summary>
    internal Func<IAudioPlayer> MakeAudioPlayer { get; init; } = UnavailableAudioPlayer.ForCurrentSystem;

    internal Func<IMainThread, IUptime, PlatformServices> MakePlatformServices { get; init; } =
        PlatformServices.CreateForCurrentSystem;

    /// <summary>The main window's model once the window exists (tests look at it).</summary>
    internal MainWindowViewModel? MainModel => mainModel;
    internal MainWindow? MainWindowView => mainWindow;
    internal SettingsViewModel? SettingsModel => settingsModel;
    internal bool IsOnboardingOpen => onboardingWindow is not null;

    /// <summary>Everything Slovo does after launch; <paramref name="startedAtSignIn"/> keeps the window closed.</summary>
    public void Start(bool startedAtSignIn)
    {
        try
        {
            paths.CreateFolders();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Could not create the data folders", error);
        }
        settings = new AppSettings(SettingsStore.Open(paths.SettingsFile));
        Loc.SetLanguage(settings.AppLanguage);
        platform = MakePlatformServices(mainThread, uptime);
        if (platform.IsInert)
        {
            Logger.Info("Not on Windows: capture, hotkeys and insertion are stand-ins");
        }

        lexicon = SpellingLexicon.CreateSystem();
        history = new HistoryStore(paths, mainThread);
        dictionary = new DictionaryStore(paths, mainThread);
        dictionary.SeedIfNeeded(settings);
        snippets = new SnippetStore(paths, mainThread);
        notes = new NoteStore(paths, mainThread);
        categories = new AppCategoryStore(paths, mainThread);
        meetings = new MeetingStore(paths, mainThread);
        ReportDamagedFiles();

        models = new ModelManager(paths.ModelsFolder, mainThread);
        engines = new EngineManager(settings, models, EngineManager.Factory(paths.ModelsFolder), uptime, mainThread);
        diarization = new SherpaDiarizationService(paths.ModelsFolder, mainThread);

        StartMeetings();

        dictation = new DictationController(new DictationController.Dependencies
        {
            Recorder = platform.Recorder,
            Hotkeys = platform.Hotkey,
            Inserter = platform.TextInserter,
            ForegroundApp = platform.ForegroundApp,
            Permissions = platform.Permissions,
            Engines = engines,
            Pipeline = new TextPipeline(lexicon),
            History = history,
            Dictionary = dictionary,
            Snippets = snippets,
            Notes = notes,
            Categories = categories,
            Meetings = MeetingActivity,
            Sounds = platform.Sounds,
            Settings = settings,
            Paths = paths,
            MainThread = mainThread,
            Uptime = uptime,
        });
        wiring = new DictationWiring(dictation, settings, models, engines, platform.Shortcuts, mainThread);
        historyActions = new HistoryActions(dictation, models, paths, MakeAudioPlayer(), Copy);
        noteDictation = new NoteDictation(dictation);
        practiceDictation = new PracticeDictation(dictation);

        retention = new RetentionCleaner(history, meetings, settings, mainThread);
        retention.Start();

        StartFlowBar();
        wiring.Start();
        engines.PreloadForCurrentSettings();
        SyncAutostart();

        tray = new TrayController(application, this);
        dictation.PropertyChanged += (_, _) => UpdateTray();
        wiring.PropertyChanged += (_, _) => UpdateTray();
        settings.Store.Changed += key =>
        {
            ApplyFlowBar();
            UpdateTray();
        };
        UpdateTray();

        App.SingleInstanceHandle?.Listen(() => Dispatcher.UIThread.Post(OpenMainWindow));

        if (!settings.OnboardingCompleted)
        {
            ShowOnboarding();
        }
        else if (!startedAtSignIn)
        {
            OpenMainWindow();
        }
        Logger.Info("Started");
    }

    // MARK: - Flow bar

    void StartFlowBar()
    {
        flowBarModel = new FlowBarViewModel();
        flowBarModel.StartRequested += () => dictation.StartHandsFree();
        flowBarModel.StopRequested += dictation.Finish;
        flowBarModel.CancelRequested += dictation.Cancel;
        flowBarModel.MessageTapped += OnFlowMessageTapped;
        flowBar = new FlowBarWindow(flowBarModel);
        dictation.PropertyChanged += OnDictationChanged;
        focusWatcher = new FocusWatcher(platform.FocusedInput,
            () => settings.ShowFlowBarWhenIdle && dictation.State is DictationState.Idle,
            focused =>
            {
                focusIsText = focused;
                ApplyFlowBar();
            });
        focusWatcher.Start();
        ApplyFlowBar();
    }

    void OnDictationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DictationController.Level))
        {
            flowBarModel.Level = dictation.Level;
            return;
        }
        ApplyFlowBar();
    }

    void ApplyFlowBar() => flowBarModel.Apply(dictation.State, dictation.HandsFreeRemaining, dictation.LastInputDeviceName,
        settings.ShowFlowBarWhenIdle, focusIsText, settings.DictationKey);

    void OnFlowMessageTapped(FlowMessage message)
    {
        dictation.DismissMessage();
        if (message.Failure is not { } failure)
        {
            return;
        }
        if (failure.Permission is { } permission)
        {
            platform.Permissions.OpenSystemSettings(permission);
        }
        else if (failure.Kind == DictationFailureKind.ModelMissing)
        {
            OpenSettings(nameof(SettingsSection.Recognition));
        }
        else
        {
            ShowTroubleshooting();
        }
    }

    // MARK: - Tray

    void UpdateTray()
    {
        if (tray is null)
        {
            return;
        }
        var state = dictation.State;
        var recording = state as DictationState.Recording;
        tray.Update(new TrayState(
            IsRecording: recording is not null,
            IsHandsFree: recording?.Info.Mode == DictationMode.HandsFree,
            IsProcessing: state is DictationState.Processing,
            IsMeetingRecording: MeetingActivity.IsMeetingRecording,
            IsMeetingMicrophoneOn: IsMeetingMicrophoneOn,
            MeetingMicrophoneStatus: MeetingMicrophoneStatus,
            CanPasteLast: dictation.LastTranscript is not null,
            PasteLastShortcut: wiring.IsPasteLastRegistered ? settings.PasteLastShortcut.DisplayText : null,
            MarkMomentShortcut: settings.HighlightShortcut.DisplayText,
            MicrophoneShortcut: settings.MicrophoneShortcut.DisplayText,
            ShowIcon: settings.ShowMenuBarIcon,
            DictationKey: settings.DictationKey));
    }

    public void StartHandsFree() => dictation.StartHandsFree();
    public void FinishDictation() => dictation.Finish();
    public void CancelDictation() => dictation.Cancel();
    public void PasteLast() => _ = dictation.PasteLastTranscriptAsync();

    // MARK: - Windows

    public void OpenMainWindow()
    {
        if (mainWindow is null)
        {
            var readiness = new ReadinessViewModel(platform.Permissions, platform.Hotkey, models, settings, this,
                () => ShowOnboarding(OnboardingStep.Microphone));
            mainModel = new MainWindowViewModel(MakeScreen, this, readiness);
            mainWindow = new MainWindow { DataContext = mainModel };
            mainWindow.Closing += (_, e) =>
            {
                // Closing the window keeps Slovo in the tray, like the macOS app in the menu bar.
                if (!quitting)
                {
                    e.Cancel = true;
                    mainModel.CloseSheet();
                    mainWindow.Hide();
                    MainWindowWillClose();
                }
            };
            mainWindow.Activated += (_, _) => ApplicationDidBecomeActive();
            if (damagedFilesAlert is { } alert)
            {
                damagedFilesAlert = null;
                mainModel.ShowAlert(alert.Title, alert.Message);
            }
        }
        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }
        mainWindow.Activate();
    }

    /// <summary>
    /// The main window closed (on Windows it hides): nothing it started keeps playing behind it,
    /// as the macOS MainWindowController's windowWillClose stops both players.
    /// </summary>
    void MainWindowWillClose()
    {
        historyActions.StopPlayback();
        meetingPlayback.Stop();
    }

    ViewModelBase MakeScreen(Screen screen, ISheetHost sheets) => screen switch
    {
        Screen.Home => new HomeViewModel(history, settings, ComputeInsights, historyActions, sheets,
            () => mainModel?.Select(Screen.Dictionary), UserName.FirstName()),
        Screen.Meetings => MakeMeetingsScreen(sheets),
        Screen.Insights => new InsightsViewModel(history, ComputeInsights),
        Screen.Dictionary => new DictionaryViewModel(dictionary, settings, sheets),
        Screen.Snippets => new SnippetsViewModel(snippets, settings, sheets),
        Screen.Style => new StyleViewModel(settings, categories, history),
        Screen.Scratchpad => new ScratchpadViewModel(notes, noteDictation, sheets, Copy),
        _ => throw new ArgumentOutOfRangeException(nameof(screen)),
    };

    Insights ComputeInsights() => InsightsCalculator.Compute(history.Entries, DateTimeOffset.Now, DayCalendar.Current);

    public void OpenSettings() => OpenSettings(null);

    public void OpenSettings(string? section)
    {
        var target = Enum.TryParse<SettingsSection>(section, ignoreCase: true, out var parsed) ? parsed : (SettingsSection?)null;
        if (settingsWindow is null)
        {
            settingsModel = new SettingsViewModel(MakeSettingsSection, target ?? SettingsSection.General);
            settingsWindow = new SettingsWindow { DataContext = settingsModel };
            settingsWindow.Closed += (_, _) =>
            {
                settingsWindow = null;
                settingsModel = null;
            };
            settingsWindow.Show();
        }
        else if (target is { } value)
        {
            settingsModel?.Select(value);
        }
        if (settingsWindow.WindowState == WindowState.Minimized)
        {
            settingsWindow.WindowState = WindowState.Normal;
        }
        settingsWindow.Activate();
    }

    ViewModelBase MakeSettingsSection(SettingsSection section, ISheetHost sheets) => section switch
    {
        SettingsSection.General => new GeneralSettingsViewModel(settings, platform.AudioDevices, platform.Hotkey, _ => wiring.ApplySettings()),
        SettingsSection.Recognition => new RecognitionSettingsViewModel(settings, models, engines, sheets, wiring.DeleteModelAsync),
        SettingsSection.System => new SystemSettingsViewModel(settings, platform.Autostart),
        SettingsSection.Meetings => MakeMeetingsSettings(sheets),
        SettingsSection.Integrations => new IntegrationsSettingsViewModel(settings, platform.Shell, Copy,
            Environment.ProcessPath ?? "Slovo.exe", () => meetings.Meetings.Count(m => m.Status == MeetingStatus.Transcribed)),
        SettingsSection.Privacy => new PrivacySettingsViewModel(settings, history, platform.Shell, platform.Permissions, paths,
            (title, message) => sheets.ConfirmAsync(title, message, Loc.Instance.Get("common.delete")),
            () => settingsModel?.Select(SettingsSection.Integrations)),
        SettingsSection.About => new AboutSettingsViewModel(platform.Shell, paths),
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    public void ShowShortcuts()
    {
        OpenMainWindow();
        mainModel?.ShowSheet(new ShortcutsSheetViewModel(settings));
    }

    public void ShowTroubleshooting()
    {
        OpenMainWindow();
        mainModel?.ShowSheet(new TroubleshootingViewModel(platform.Permissions, this, platform.Hotkey));
    }

    public void OpenModelsFolder() => platform.Shell.OpenFolder(paths.ModelsFolder);

    public void OpenLogs() => platform.Shell.OpenFolder(paths.LogsFolder);

    public void ShowOnboarding() => ShowOnboarding(OnboardingStep.Welcome);

    /// <summary>
    /// Opens onboarding; Help starts from the welcome step, the sidebar's setup card from the
    /// microphone. A walkthrough already open stays where the user is.
    /// </summary>
    public void ShowOnboarding(OnboardingStep step)
    {
        if (onboardingWindow is not null)
        {
            onboardingWindow.Activate();
            return;
        }
        var host = new WindowSheetHost();
        var model = new OnboardingViewModel(settings, platform.Permissions, platform.Hotkey, models, engines, practiceDictation, host, step);
        onboardingWindow = new OnboardingWindow { DataContext = model };
        host.Window = onboardingWindow;
        onboardingWindow.Closed += (_, _) =>
        {
            onboardingWindow = null;
            practiceDictation.SetActive(false);
            if (settings.OnboardingCompleted && !quitting)
            {
                OpenMainWindow();
            }
        };
        onboardingWindow.Show();
        onboardingWindow.Activate();
    }

    // MARK: - Helpers

    /// <summary>
    /// Re-reads what another process may have changed while Slovo was in the background (the
    /// macOS applicationDidBecomeActive), here whenever the main window comes forward. The MCP
    /// server (<c>Slovo.exe --mcp</c>) writes Claude's notes and marks moments from its own
    /// process and tells the app nothing, so the meeting card learns about them here.
    /// </summary>
    internal void ApplicationDidBecomeActive()
    {
        if (quitting)
        {
            return;
        }
        meetings.RefreshFromDisk();
    }

    /// <summary>Stores that found a damaged file moved it aside and started empty; say so once (the macOS reportDamagedFiles).</summary>
    void ReportDamagedFiles()
    {
        var warnings = new[] { history.LoadWarning, dictionary.LoadWarning, snippets.LoadWarning, notes.LoadWarning, categories.LoadWarning }
            .OfType<string>()
            .ToList();
        if (warnings.Count == 0)
        {
            return;
        }
        foreach (var warning in warnings)
        {
            Logger.Error(warning);
        }
        damagedFilesAlert = (Loc.Instance.Get("data.damaged.title"), string.Join("\n", warnings));
    }

    /// <summary>Copies what the user asked to copy onto the ordinary clipboard (history included).</summary>
    void Copy(string text)
    {
        TopLevel? top = mainWindow is { IsVisible: true } ? mainWindow : settingsWindow;
        if (top?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(text);
        }
        else
        {
            platform.TextInserter.CopyToClipboard(text);
        }
    }

    /// <summary>
    /// The Run entry holds the path of the executable that wrote it; after Slovo moves
    /// (reinstall, new version folder), write it again so sign-in still starts this copy.
    /// </summary>
    void SyncAutostart()
    {
        if (settings.LaunchAtLogin && !platform.Autostart.SetEnabled(true))
        {
            Logger.Error("Could not refresh the sign-in entry");
        }
    }

    // MARK: - Quit

    public void Quit()
    {
        Shutdown();
        exitApplication();
    }

    /// <summary>Stops everything and saves what is pending; safe to call more than once (Windows sign-out calls it too).</summary>
    public void Shutdown()
    {
        if (quitting)
        {
            return;
        }
        quitting = true;
        Logger.Info("Quitting");
        Try("dictation", () => wiring.Stop());
        Try("meetings", StopMeetings);
        Try("retention", retention.Stop);
        Try("focus", focusWatcher.Dispose);
        Try("playback", historyActions.Dispose);
        foreach (var (name, flush) in new (string, Action)[]
                 {
                     ("history", history.Flush), ("dictionary", dictionary.Flush), ("snippets", snippets.Flush),
                     ("notes", notes.Flush), ("categories", categories.Flush), ("meetings", meetings.Flush),
                 })
        {
            Try("saving " + name, flush);
        }
        Try("engines", engines.Dispose);
        Try("tray", () => tray?.Dispose());
        Try("platform", platform.Dispose);
        Try("spelling", () => (lexicon as IDisposable)?.Dispose());
        Try("windows", () =>
        {
            flowBar.Close();
            settingsWindow?.Close();
            onboardingWindow?.Close();
            mainWindow?.Close();
        });
        Log.Flush();
    }

    static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Logger.Error($"Stopping {what} failed", error);
        }
    }
}

/// <summary>Sheets for a window without a sheet layer of its own (onboarding): each one opens as a small modal dialog.</summary>
sealed class WindowSheetHost : ISheetHost
{
    Window? dialog;
    SheetViewModel? current;

    public Window? Window { get; set; }

    public void ShowSheet(SheetViewModel sheet)
    {
        CloseSheet();
        if (Window is not { } owner)
        {
            return;
        }
        sheet.Host = this;
        current = sheet;
        var shown = new Window
        {
            Content = sheet,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Padding = new Thickness(24),
            Title = owner.Title,
        };
        if (Application.Current?.TryGetResource("Slovo.WindowBackground", owner.ActualThemeVariant, out var brush) == true)
        {
            shown.Background = brush as Avalonia.Media.IBrush;
        }
        shown.Closed += (_, _) =>
        {
            (sheet as ConfirmSheetViewModel)?.Dismissed();
            if (ReferenceEquals(dialog, shown))
            {
                dialog = null;
                current = null;
            }
        };
        dialog = shown;
        _ = shown.ShowDialog(owner);
    }

    public void CloseSheet()
    {
        var closing = dialog;
        dialog = null;
        (current as ConfirmSheetViewModel)?.Dismissed();
        current = null;
        closing?.Close();
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = true)
    {
        var sheet = new ConfirmSheetViewModel(title, message, confirmText, destructive);
        ShowSheet(sheet);
        return Window is null ? Task.FromResult(false) : sheet.Result;
    }

    public void ShowAlert(string title, string message) => ShowSheet(new AlertSheetViewModel(title, message));
}
