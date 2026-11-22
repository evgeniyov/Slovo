using CommunityToolkit.Mvvm.ComponentModel;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Settings;

/// <summary>Settings → General: the key, hands-free, Esc, paste last, microphone, languages.</summary>
public sealed partial class GeneralSettingsViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly IAudioDevices devices;
    readonly IHotkeyMonitor hotkeys;
    readonly Func<HotkeyShortcut, bool> registerPasteLast;
    readonly Loc loc = Loc.Instance;

    public GeneralSettingsViewModel(AppSettings settings, IAudioDevices devices, IHotkeyMonitor hotkeys, Func<HotkeyShortcut, bool> registerPasteLast)
    {
        this.settings = settings;
        this.devices = devices;
        this.hotkeys = hotkeys;
        this.registerPasteLast = registerPasteLast;

        KeyChoices = DictationKeys.All.Select(key => new ChoiceItem<DictationKey>(key, loc.Get("key." + EnumText.Raw(key)))).ToList();
        MaxLengthChoices = new double[] { 120, 360, 600, 900 }
            .Select(seconds => new ChoiceItem<double>(seconds, loc.Plural("plural.minutes", (long)(seconds / 60)))).ToList();
        PasteLastChoices = HotkeyShortcut.PasteLastChoices.Select(s => new ChoiceItem<HotkeyShortcut>(s, s.DisplayText)).ToList();
        LanguageModeChoices =
        [
            new(LanguageMode.Mixed, loc.Get("languageMode.mixed")),
            new(LanguageMode.Russian, loc.Get("languageMode.russian")),
            new(LanguageMode.English, loc.Get("languageMode.english")),
        ];
        LanguageChoices =
        [
            new(AppLanguage.System, loc.Get("language.system")),
            new(AppLanguage.En, loc.Get("language.en")),
            new(AppLanguage.Ru, loc.Get("language.ru")),
        ];
        MicrophoneChoices = BuildMicrophones();
        hotkeys.KeyStateChanged += OnKeyState;
        devices.DevicesChanged += OnDevicesChanged;
    }

    public IReadOnlyList<ChoiceItem<DictationKey>> KeyChoices { get; }
    public IReadOnlyList<ChoiceItem<double>> MaxLengthChoices { get; }
    public IReadOnlyList<ChoiceItem<HotkeyShortcut>> PasteLastChoices { get; }
    public IReadOnlyList<ChoiceItem<LanguageMode>> LanguageModeChoices { get; }
    public IReadOnlyList<ChoiceItem<AppLanguage>> LanguageChoices { get; }

    [ObservableProperty] public partial IReadOnlyList<ChoiceItem<string>> MicrophoneChoices { get; private set; }
    [ObservableProperty] public partial bool IsKeyDown { get; private set; }
    [ObservableProperty] public partial bool PasteLastRefused { get; private set; }

    public bool IsHotkeyRunning => hotkeys.IsRunning;

    public string KeySymbol => settings.DictationKey.Symbol();

    public ChoiceItem<DictationKey> SelectedKey
    {
        get => KeyChoices.First(c => c.Value == settings.DictationKey);
        set
        {
            if (value is null || value.Value == settings.DictationKey) return;
            settings.DictationKey = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeySymbol));
        }
    }

    public bool HandsFreeEnabled
    {
        get => settings.HandsFreeEnabled;
        set { settings.HandsFreeEnabled = value; OnPropertyChanged(); }
    }

    public ChoiceItem<double> SelectedMaxLength
    {
        get => MaxLengthChoices.FirstOrDefault(c => Math.Abs(c.Value - settings.MaxHandsFreeSeconds) < 0.5) ?? MaxLengthChoices[1];
        set
        {
            if (value is null) return;
            settings.MaxHandsFreeSeconds = value.Value;
            OnPropertyChanged();
        }
    }

    public bool EscapeCancels
    {
        get => settings.EscapeCancels;
        set { settings.EscapeCancels = value; OnPropertyChanged(); }
    }

    public ChoiceItem<HotkeyShortcut> SelectedPasteLast
    {
        get => PasteLastChoices.FirstOrDefault(c => c.Value == settings.PasteLastShortcut) ?? PasteLastChoices[0];
        set
        {
            if (value is null || value.Value == settings.PasteLastShortcut) return;
            settings.PasteLastShortcut = value.Value;
            // Register at once, so a refused shortcut is reported while the choice is on screen.
            PasteLastRefused = !registerPasteLast(value.Value);
            OnPropertyChanged();
        }
    }

    public ChoiceItem<string> SelectedMicrophone
    {
        get => MicrophoneChoices.FirstOrDefault(c => c.Value == (settings.MicrophoneUid ?? "")) ?? MicrophoneChoices[0];
        set
        {
            if (value is null) return;
            settings.MicrophoneUid = value.Value.Length == 0 ? null : value.Value;
            OnPropertyChanged();
        }
    }

    public ChoiceItem<LanguageMode> SelectedLanguageMode
    {
        get => LanguageModeChoices.First(c => c.Value == settings.LanguageMode);
        set
        {
            if (value is null) return;
            settings.LanguageMode = value.Value;
            OnPropertyChanged();
        }
    }

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

    IReadOnlyList<ChoiceItem<string>> BuildMicrophones()
    {
        var list = new List<ChoiceItem<string>> { new("", loc.Get("settings.general.microphone.default")) };
        IReadOnlyList<AudioInputDevice> inputs;
        try
        {
            inputs = devices.InputDevices();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            inputs = [];
        }
        list.AddRange(inputs.Select(d => new ChoiceItem<string>(d.Uid, d.Name)));
        if (settings.MicrophoneUid is { } chosen && inputs.All(d => d.Uid != chosen))
        {
            list.Add(new ChoiceItem<string>(chosen, loc.Get("settings.general.microphone.disconnected")));
        }
        return list;
    }

    void OnKeyState(bool isDown) => IsKeyDown = isDown;

    void OnDevicesChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        MicrophoneChoices = BuildMicrophones();
        OnPropertyChanged(nameof(SelectedMicrophone));
    });

    /// <summary>Stops listening to services when the section closes.</summary>
    public void Detach()
    {
        hotkeys.KeyStateChanged -= OnKeyState;
        devices.DevicesChanged -= OnDevicesChanged;
    }
}
