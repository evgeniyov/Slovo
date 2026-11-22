using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.App.ViewModels.Settings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels;

public enum DictationReadinessKind
{
    /// <summary>The model files have not been looked at yet; the card stays hidden.</summary>
    Checking,
    Ready,
    PermissionsNeeded,
    /// <summary>
    /// The keyboard hook could not be installed, so the dictation key does nothing. Windows
    /// has no permission for it; on the Mac this is the Input Monitoring permission.
    /// </summary>
    KeyUnavailable,
    ModelMissing,
    Downloading,
}

/// <summary>What the dictation key can do right now, for the sidebar status line (the macOS DictationReadiness).</summary>
public readonly record struct DictationReadiness(DictationReadinessKind Kind, EngineId Engine = default, double Progress = 0)
{
    public static readonly DictationReadiness Checking = new(DictationReadinessKind.Checking);
    public static readonly DictationReadiness PermissionsNeeded = new(DictationReadinessKind.PermissionsNeeded);
    public static readonly DictationReadiness KeyUnavailable = new(DictationReadinessKind.KeyUnavailable);
    public static DictationReadiness Ready(EngineId engine) => new(DictationReadinessKind.Ready, engine);
    public static DictationReadiness ModelMissing(EngineId engine) => new(DictationReadinessKind.ModelMissing, engine);
    public static DictationReadiness Downloading(EngineId engine, double progress) => new(DictationReadinessKind.Downloading, engine, progress);

    /// <summary>
    /// The microphone switch first (the only permission a Windows desktop app needs), then the
    /// key listener, then the model of the engine the current language mode dictates with.
    /// </summary>
    public static DictationReadiness Compute(IPermissions permissions, IHotkeyMonitor hotkeys, IModelManager models, AppSettings settings)
    {
        if (permissions.Status(PermissionKind.Microphone) is PermissionStatus.Denied or PermissionStatus.NotDetermined)
        {
            return PermissionsNeeded;
        }
        if (!hotkeys.IsRunning)
        {
            return KeyUnavailable;
        }
        var engine = settings.EngineFor(EnginePurpose.Dictation(settings.LanguageMode));
        var status = models.Status(engine);
        return status.Kind switch
        {
            ModelStatusKind.Unknown => Checking,
            ModelStatusKind.Ready => Ready(engine),
            ModelStatusKind.Downloading => Downloading(engine, status.Progress),
            _ => ModelMissing(engine),
        };
    }
}

/// <summary>
/// A small card at the bottom of the sidebar that says whether dictation can work, and leads
/// to the fix when not (the macOS ReadinessCard). Model and settings changes update it at
/// once; the microphone switch and the key listener are looked at again whenever the main
/// window comes forward, since Windows does not announce them.
/// </summary>
public sealed partial class ReadinessViewModel : ViewModelBase
{
    readonly IPermissions permissions;
    readonly IHotkeyMonitor hotkeys;
    readonly IModelManager models;
    readonly AppSettings settings;
    readonly IShellActions shell;
    readonly Action finishSetup;

    /// <param name="finishSetup">Opens onboarding at the microphone step (the macOS showOnboarding(.permissions)).</param>
    public ReadinessViewModel(IPermissions permissions, IHotkeyMonitor hotkeys, IModelManager models, AppSettings settings,
                              IShellActions shell, Action finishSetup)
    {
        this.permissions = permissions;
        this.hotkeys = hotkeys;
        this.models = models;
        this.settings = settings;
        this.shell = shell;
        this.finishSetup = finishSetup;
        models.PropertyChanged += (_, _) => Refresh();
        settings.Store.Changed += _ => Refresh();
        Loc.Instance.LanguageChanged += Announce;
        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown), nameof(Title), nameof(Detail), nameof(HasAction), nameof(IsReady), nameof(IsWarning), nameof(IsProgress))]
    public partial DictationReadiness State { get; private set; }

    public bool IsShown => State.Kind != DictationReadinessKind.Checking;

    public string Title
    {
        get
        {
            var loc = Loc.Instance;
            return State.Kind switch
            {
                DictationReadinessKind.Ready => loc.Get("readiness.ready"),
                DictationReadinessKind.PermissionsNeeded => loc.Get("readiness.permissions"),
                DictationReadinessKind.KeyUnavailable => loc.Get("readiness.keyUnavailable"),
                DictationReadinessKind.ModelMissing => loc.Get("readiness.modelMissing"),
                DictationReadinessKind.Downloading => loc.Format("readiness.downloading", (long)Math.Round(State.Progress * 100)),
                _ => "",
            };
        }
    }

    public string Detail => State.Kind switch
    {
        DictationReadinessKind.PermissionsNeeded => Loc.Instance.Get("readiness.permissions.detail"),
        DictationReadinessKind.KeyUnavailable => Loc.Instance.Get("readiness.keyUnavailable.detail"),
        DictationReadinessKind.Checking => "",
        _ => State.Engine.DisplayName(),
    };

    /// <summary>Everything but Ready leads somewhere; Ready is a plain status line.</summary>
    public bool HasAction => State.Kind is not (DictationReadinessKind.Ready or DictationReadinessKind.Checking);

    public bool IsReady => State.Kind == DictationReadinessKind.Ready;
    public bool IsWarning => State.Kind is DictationReadinessKind.PermissionsNeeded or DictationReadinessKind.KeyUnavailable or DictationReadinessKind.ModelMissing;
    public bool IsProgress => State.Kind == DictationReadinessKind.Downloading;

    /// <summary>Looks at the permission, the key listener and the model again.</summary>
    public void Refresh() => State = DictationReadiness.Compute(permissions, hotkeys, models, settings);

    void Announce()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
    }

    [RelayCommand]
    void Open()
    {
        switch (State.Kind)
        {
            case DictationReadinessKind.PermissionsNeeded:
                finishSetup();
                break;
            case DictationReadinessKind.KeyUnavailable:
                shell.ShowTroubleshooting();
                break;
            case DictationReadinessKind.ModelMissing or DictationReadinessKind.Downloading:
                shell.OpenSettings(nameof(SettingsSection.Recognition));
                break;
        }
    }
}
