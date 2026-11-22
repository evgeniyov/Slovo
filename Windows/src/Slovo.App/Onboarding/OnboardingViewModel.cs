using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Settings;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.Onboarding;

/// <summary>Onboarding's practice step: the dictation key (or hands-free) writes into the card, not into an app.</summary>
public interface IPracticeDictation
{
    /// <summary>Routes key dictations to the practice card while it is on screen.</summary>
    void SetActive(bool active);
    void StartHandsFree();
    void Finish();
    bool IsRecording { get; }
    event Action<string>? Transcribed;
    event Action? StateChanged;
}

public enum OnboardingStep
{
    Welcome,
    Microphone,
    Key,
    Models,
    Practice,
}

public sealed partial class KeyCardViewModel(DictationKey key, string title, string detail, string symbol) : ViewModelBase
{
    public DictationKey Key { get; } = key;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Symbol { get; } = symbol;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>First launch: language, microphone, key with a live test, models, a practice dictation.</summary>
public sealed partial class OnboardingViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly IPermissions permissions;
    readonly IHotkeyMonitor hotkeys;
    readonly IPracticeDictation practice;
    readonly IModelManager models;

    /// <param name="initialStep">Help starts from the welcome step, the sidebar's setup card from the microphone.</param>
    public OnboardingViewModel(AppSettings settings, IPermissions permissions, IHotkeyMonitor hotkeys, IModelManager models,
                               IEngineProvider engines, IPracticeDictation practice, ISheetHost sheets,
                               OnboardingStep initialStep = OnboardingStep.Welcome)
    {
        this.settings = settings;
        this.permissions = permissions;
        this.hotkeys = hotkeys;
        this.practice = practice;
        this.models = models;
        var loc = Loc.Instance;
        LanguageChoices =
        [
            new(AppLanguage.System, loc.Get("language.system")),
            new(AppLanguage.En, loc.Get("language.en")),
            new(AppLanguage.Ru, loc.Get("language.ru")),
        ];
        Keys = DictationKeys.All.Select(key => new KeyCardViewModel(key, loc.Get("key." + EnumText.Raw(key)),
            loc.Get("key." + EnumText.Raw(key) + ".detail"), key.Symbol())).ToList();
        ModelRows = new[] { EngineId.GigaamRnnt, EngineId.Parakeet }.Select(id => new ModelRowViewModel(
            id.DisplayName(), loc.Get(RecognitionSettingsViewModel.TradeoffKey(id)), new EngineModelSource(models, id, engines: engines), sheets,
            loc.Format("engine.delete.title", id.DisplayName()), loc.Get("engine.delete.message"), () => engines.Unload(id),
            RecognitionSettingsViewModel.Recommended(id))).ToList();
        hotkeys.KeyStateChanged += down => IsKeyDown = down;
        practice.Transcribed += text => PracticeResult = text;
        practice.StateChanged += () => OnPropertyChanged(nameof(IsPracticeRecording));
        models.PropertyChanged += (_, _) => OnPropertyChanged(nameof(NothingUsable));
        SelectedKey = Keys.First(k => k.Key == settings.DictationKey);
        Step = initialStep;
    }

    public IReadOnlyList<ChoiceItem<AppLanguage>> LanguageChoices { get; }
    public IReadOnlyList<KeyCardViewModel> Keys { get; }
    public IReadOnlyList<ModelRowViewModel> ModelRows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText), nameof(IsWelcome), nameof(IsMicrophone), nameof(IsKey), nameof(IsModels), nameof(IsPractice), nameof(IsFirst), nameof(IsLast))]
    public partial OnboardingStep Step { get; private set; }

    [ObservableProperty] public partial bool IsKeyDown { get; private set; }
    [ObservableProperty] public partial string PracticeResult { get; private set; } = "";
    [ObservableProperty] public partial PermissionStatus MicrophoneStatus { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeySymbol))]
    public partial KeyCardViewModel SelectedKey { get; set; }

    public event Action? Finished;

    public string StepText => Loc.Instance.Format("onboarding.step", (long)Step + 1, 5L);
    public bool IsWelcome => Step == OnboardingStep.Welcome;
    public bool IsMicrophone => Step == OnboardingStep.Microphone;
    public bool IsKey => Step == OnboardingStep.Key;
    public bool IsModels => Step == OnboardingStep.Models;
    public bool IsPractice => Step == OnboardingStep.Practice;
    public bool IsFirst => Step == OnboardingStep.Welcome;
    public bool IsLast => Step == OnboardingStep.Practice;
    public string KeySymbol => SelectedKey.Symbol;
    public bool MicrophoneAllowed => MicrophoneStatus == PermissionStatus.Granted;
    public string MicrophoneStatusText => Loc.Instance.Get(MicrophoneAllowed ? "permission.status.granted" : "permission.status.missing");
    public bool HotkeyRunning => hotkeys.IsRunning;
    public bool IsPracticeRecording => practice.IsRecording;

    /// <summary>No engine for the chosen language can run yet.</summary>
    public bool NothingUsable => !models.Status(settings.EngineFor(EnginePurpose.Dictation(settings.LanguageMode))).IsUsable
        && !EngineIds.Available.Any(id => models.Status(id).IsUsable);

    public ChoiceItem<AppLanguage> SelectedLanguage
    {
        get => LanguageChoices.First(c => c.Value == settings.AppLanguage);
        set
        {
            if (value is null || value.Value == settings.AppLanguage) return;
            settings.AppLanguage = value.Value;
            Loc.SetLanguage(value.Value);
        }
    }

    partial void OnSelectedKeyChanged(KeyCardViewModel value)
    {
        foreach (var card in Keys)
        {
            card.IsSelected = ReferenceEquals(card, value);
        }
        if (value is not null && settings.DictationKey != value.Key)
        {
            settings.DictationKey = value.Key;
        }
    }

    partial void OnMicrophoneStatusChanged(PermissionStatus value)
    {
        OnPropertyChanged(nameof(MicrophoneAllowed));
        OnPropertyChanged(nameof(MicrophoneStatusText));
    }

    partial void OnStepChanged(OnboardingStep value)
    {
        practice.SetActive(value == OnboardingStep.Practice);
        if (value == OnboardingStep.Microphone)
        {
            RefreshPermissions();
        }
        if (value == OnboardingStep.Models)
        {
            models.Refresh();
        }
    }

    /// <summary>Statuses can change in Windows Settings while Slovo waits; the window calls this on activation.</summary>
    public void RefreshPermissions() => MicrophoneStatus = permissions.Status(PermissionKind.Microphone);

    [RelayCommand]
    async Task AllowMicrophoneAsync()
    {
        MicrophoneStatus = await permissions.RequestAsync(PermissionKind.Microphone);
        if (MicrophoneStatus != PermissionStatus.Granted)
        {
            permissions.OpenSystemSettings(PermissionKind.Microphone);
        }
    }

    [RelayCommand]
    void Next()
    {
        if (Step == OnboardingStep.Practice)
        {
            Finish();
            return;
        }
        Step = Step + 1;
    }

    [RelayCommand]
    void Back()
    {
        if (Step > OnboardingStep.Welcome)
        {
            Step = Step - 1;
        }
    }

    [RelayCommand]
    void SelectKey(KeyCardViewModel key) => SelectedKey = key;

    [RelayCommand]
    void ToggleHandsFree()
    {
        if (practice.IsRecording)
        {
            practice.Finish();
        }
        else
        {
            practice.StartHandsFree();
        }
    }

    void Finish()
    {
        practice.SetActive(false);
        settings.OnboardingCompleted = true;
        Finished?.Invoke();
    }

    /// <summary>The window closed without Finish: stop routing dictation to the card.</summary>
    public void Closed() => practice.SetActive(false);
}
