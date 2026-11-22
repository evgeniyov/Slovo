using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Integration;
using Slovo.App.Localization;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Settings;

public enum CheckStateKind
{
    Idle,
    Running,
    Works,
    AccessOff,
    Failed,
}

/// <summary>Settings → Integrations: Claude's read and write switches, registration and a check.</summary>
public sealed partial class IntegrationsSettingsViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly IShell shell;
    readonly Action<string> copy;
    readonly Func<int> meetingsVisibleToClaude;
    readonly Func<string, Task<McpSelfCheck.Outcome>> runCheck;
    readonly Func<Task<ClaudeRegistration>> detectRegistration;

    /// <param name="detectRegistration">Reads Claude's configuration files; ClaudeIntegration.DetectRegistrationAsync by default.</param>
    public IntegrationsSettingsViewModel(AppSettings settings, IShell shell, Action<string> copy, string executablePath,
                                         Func<int> meetingsVisibleToClaude, Func<string, Task<McpSelfCheck.Outcome>>? runCheck = null,
                                         Func<Task<ClaudeRegistration>>? detectRegistration = null)
    {
        this.settings = settings;
        this.shell = shell;
        this.copy = copy;
        this.meetingsVisibleToClaude = meetingsVisibleToClaude;
        this.runCheck = runCheck ?? (path => McpSelfCheck.RunAsync(path));
        this.detectRegistration = detectRegistration ?? ClaudeIntegration.DetectRegistrationAsync;
        ExecutablePath = executablePath;
        var loc = Loc.Instance;
        MaxAgeChoices = AppSettings.McpMaxAgeDayChoices
            .Select(days => new ChoiceItem<int>(days, days == 0 ? loc.Get("settings.integrations.maxAge.all") : loc.Format("settings.integrations.maxAge.days", days)))
            .ToList();
        _ = RefreshRegistrationAsync();
    }

    public string ExecutablePath { get; }
    public string ClaudeCodeCommand => ClaudeIntegration.ClaudeCodeCommand(ExecutablePath);
    public string ClaudeAppSnippet => ClaudeIntegration.ClaudeAppSnippet(ExecutablePath);
    public bool IsInstalled => ClaudeIntegration.IsInstalled(ExecutablePath);
    public IReadOnlyList<ChoiceItem<int>> MaxAgeChoices { get; }

    /// <summary>Null while the configuration files have not been read yet.</summary>
    [ObservableProperty] public partial ClaudeRegistration? Registration { get; private set; }
    [ObservableProperty] public partial CheckStateKind CheckState { get; private set; } = CheckStateKind.Idle;
    [ObservableProperty] public partial string? CheckMessage { get; private set; }

    /// <summary>"Copied" for a moment on each client's Copy button.</summary>
    public CopiedFlag ClaudeCodeCopied { get; } = new();
    public CopiedFlag ClaudeAppCopied { get; } = new();

    public string ClaudeCodeStatus => Loc.Instance.Get(Registration switch
    {
        null => "engine.status.checking",
        { ClaudeCode: true } => "settings.integrations.status.claudeCode.added",
        _ => "settings.integrations.status.claudeCode.missing",
    });

    public string ClaudeAppStatus => Loc.Instance.Get(Registration switch
    {
        null => "engine.status.checking",
        { ClaudeApp: true } => "settings.integrations.status.claudeApp.added",
        _ => "settings.integrations.status.claudeApp.missing",
    });
    public bool IsChecking => CheckState == CheckStateKind.Running;
    public bool CheckWorks => CheckState == CheckStateKind.Works;
    public bool CheckProblem => CheckState is CheckStateKind.AccessOff or CheckStateKind.Failed;

    partial void OnRegistrationChanged(ClaudeRegistration? value)
    {
        OnPropertyChanged(nameof(ClaudeCodeStatus));
        OnPropertyChanged(nameof(ClaudeAppStatus));
    }

    partial void OnCheckStateChanged(CheckStateKind value)
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(CheckWorks));
        OnPropertyChanged(nameof(CheckProblem));
    }

    public bool McpEnabled
    {
        get => settings.McpEnabled;
        set { settings.McpEnabled = value; OnPropertyChanged(); }
    }

    public bool McpWriteEnabled
    {
        get => settings.McpWriteEnabled;
        set { settings.McpWriteEnabled = value; OnPropertyChanged(); }
    }

    public ChoiceItem<int> SelectedMaxAge
    {
        get => MaxAgeChoices.FirstOrDefault(c => c.Value == settings.McpMaxAgeDays) ?? MaxAgeChoices[0];
        set
        {
            if (value is null) return;
            settings.McpMaxAgeDays = value.Value;
            OnPropertyChanged();
        }
    }

    async Task RefreshRegistrationAsync() => Registration = await detectRegistration();

    [RelayCommand]
    void CopyClaudeCode()
    {
        copy(ClaudeCodeCommand);
        ClaudeCodeCopied.Flash();
    }

    [RelayCommand]
    void CopyClaudeApp()
    {
        copy(ClaudeAppSnippet);
        ClaudeAppCopied.Flash();
    }

    [RelayCommand]
    void ShowConfig()
    {
        var path = ClaudeIntegration.ClaudeAppConfigPath;
        if (File.Exists(path))
        {
            shell.RevealFile(path);
        }
        else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
        {
            shell.OpenFolder(folder);
        }
    }

    [RelayCommand]
    async Task CheckAsync()
    {
        if (IsChecking)
        {
            return;
        }
        CheckState = CheckStateKind.Running;
        CheckMessage = null;
        var loc = Loc.Instance;
        var outcome = await runCheck(ExecutablePath);
        switch (outcome)
        {
            case McpSelfCheck.Outcome.Answered { IsError: false }:
                CheckState = CheckStateKind.Works;
                CheckMessage = loc.Format("settings.integrations.check.works", loc.Plural("plural.meetings", meetingsVisibleToClaude()));
                break;
            case McpSelfCheck.Outcome.Answered answered when !settings.McpEnabled:
                _ = answered;
                CheckState = CheckStateKind.AccessOff;
                CheckMessage = loc.Get("settings.integrations.check.accessOff");
                break;
            case McpSelfCheck.Outcome.Answered answered:
                Fail(answered.Message ?? loc.Get("settings.integrations.check.noAnswer"));
                break;
            case McpSelfCheck.Outcome.TimedOut:
                Fail(loc.Get("settings.integrations.check.timeout"));
                break;
            case McpSelfCheck.Outcome.ExitedWithoutAnswer:
                Fail(loc.Get("settings.integrations.check.exited"));
                break;
            case McpSelfCheck.Outcome.CouldNotStart failed:
                Fail(failed.Message);
                break;
        }
        await RefreshRegistrationAsync();

        void Fail(string reason)
        {
            CheckState = CheckStateKind.Failed;
            CheckMessage = loc.Format("settings.integrations.check.failed", reason);
        }
    }
}
