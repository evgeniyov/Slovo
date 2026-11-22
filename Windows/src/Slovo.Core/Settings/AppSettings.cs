using Slovo.Core.Model;

namespace Slovo.Core.Settings;

/// <summary>Typed access to settings with their defaults (port of the macOS <c>AppSettings</c>).</summary>
public sealed class AppSettings(SettingsStore store)
{
    public SettingsStore Store { get; } = store;

    /// <summary>Choices for "People in a meeting": 0 means automatic.</summary>
    public static readonly IReadOnlyList<int> ExpectedSpeakerChoices = [0, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Choices for "Meetings Claude can see", in days: 0 means all meetings.</summary>
    public static readonly IReadOnlyList<int> McpMaxAgeDayChoices = [0, 90, 30, 7];

    public static readonly IReadOnlyList<double> MaxHandsFreeChoices = [120, 360, 600, 1200];

    public static int NormalizedExpectedSpeakers(int value) => ExpectedSpeakerChoices.Contains(value) ? value : 0;

    public static int NormalizedMcpMaxAgeDays(int value) => McpMaxAgeDayChoices.Contains(value) ? value : 0;

    public AppLanguage AppLanguage
    {
        get => EnumText.ParseOr(Store.GetString(SettingsKey.AppLanguage), AppLanguage.System);
        set => Store.Set(SettingsKey.AppLanguage, EnumText.Raw(value));
    }

    public DictationKey DictationKey
    {
        get => EnumText.ParseOr(Store.GetString(SettingsKey.DictationKey), DictationKey.CtrlWin);
        set => Store.Set(SettingsKey.DictationKey, EnumText.Raw(value));
    }

    public bool HandsFreeEnabled
    {
        get => Store.GetBool(SettingsKey.HandsFreeEnabled) ?? true;
        set => Store.Set(SettingsKey.HandsFreeEnabled, value);
    }

    public bool EscapeCancels
    {
        get => Store.GetBool(SettingsKey.EscapeCancels) ?? true;
        set => Store.Set(SettingsKey.EscapeCancels, value);
    }

    public double MaxHandsFreeSeconds
    {
        get => Store.GetDouble(SettingsKey.MaxHandsFreeSeconds) is > 0 and var value ? value : 360;
        set => Store.Set(SettingsKey.MaxHandsFreeSeconds, value);
    }

    public HotkeyShortcut PasteLastShortcut
    {
        get => Store.GetObject<HotkeyShortcut>(SettingsKey.PasteLastShortcut) ?? HotkeyShortcut.PasteLastDefault;
        set => Store.SetObject(SettingsKey.PasteLastShortcut, value);
    }

    /// <summary>Null means the system default input device.</summary>
    public string? MicrophoneUid
    {
        get => Store.GetString(SettingsKey.MicrophoneUid) is { Length: > 0 } value ? value : null;
        set => Store.Set(SettingsKey.MicrophoneUid, value ?? "");
    }

    public LanguageMode LanguageMode
    {
        get => EnumText.ParseOr(Store.GetString(SettingsKey.LanguageMode), LanguageMode.Mixed);
        set => Store.Set(SettingsKey.LanguageMode, EnumText.Raw(value));
    }

    public EngineId RussianEngine
    {
        get => AvailableOr(Store.GetString(SettingsKey.RussianEngine), EngineId.GigaamRnnt);
        set => Store.Set(SettingsKey.RussianEngine, EnumText.Raw(value));
    }

    public EngineId EnglishEngine
    {
        get => AvailableOr(Store.GetString(SettingsKey.EnglishEngine), EngineId.Parakeet);
        set => Store.Set(SettingsKey.EnglishEngine, EnumText.Raw(value));
    }

    public EngineId MeetingEngine
    {
        get => AvailableOr(Store.GetString(SettingsKey.MeetingEngine), EngineId.GigaamCtc);
        set => Store.Set(SettingsKey.MeetingEngine, EnumText.Raw(value));
    }

    public EngineIdleTimeout IdleUnload
    {
        get => Store.GetInt(SettingsKey.IdleUnloadMinutes) is { } minutes && Enum.IsDefined(typeof(EngineIdleTimeout), minutes)
            ? (EngineIdleTimeout)minutes
            : EngineIdleTimeout.Minutes15;
        set => Store.Set(SettingsKey.IdleUnloadMinutes, (int)value);
    }

    public bool ShowFlowBarWhenIdle
    {
        get => Store.GetBool(SettingsKey.ShowFlowBarWhenIdle) ?? true;
        set => Store.Set(SettingsKey.ShowFlowBarWhenIdle, value);
    }

    public bool SoundsEnabled
    {
        get => Store.GetBool(SettingsKey.SoundsEnabled) ?? true;
        set => Store.Set(SettingsKey.SoundsEnabled, value);
    }

    public bool ShowMenuBarIcon
    {
        get => Store.GetBool(SettingsKey.ShowMenuBarIcon) ?? true;
        set => Store.Set(SettingsKey.ShowMenuBarIcon, value);
    }

    public bool LaunchAtLogin
    {
        get => Store.GetBool(SettingsKey.LaunchAtLogin) ?? false;
        set => Store.Set(SettingsKey.LaunchAtLogin, value);
    }

    public bool RecordSystemAudio
    {
        get => Store.GetBool(SettingsKey.RecordSystemAudio) ?? true;
        set => Store.Set(SettingsKey.RecordSystemAudio, value);
    }

    public bool RecordMicrophone
    {
        get => Store.GetBool(SettingsKey.RecordMicrophone) ?? true;
        set => Store.Set(SettingsKey.RecordMicrophone, value);
    }

    public RetentionPeriod KeepMeetingAudio
    {
        get => EnumText.ParseOr(Store.GetString(SettingsKey.KeepMeetingAudio), RetentionPeriod.Forever);
        set => Store.Set(SettingsKey.KeepMeetingAudio, EnumText.Raw(value));
    }

    /// <summary>
    /// Whether a new meeting recording also captures the screen. Off by default; also the
    /// "with screen / without" choice next to the new-recording button.
    /// </summary>
    public bool RecordScreen
    {
        get => Store.GetBool(SettingsKey.RecordScreen) ?? false;
        set => Store.Set(SettingsKey.RecordScreen, value);
    }

    /// <summary>What a screen recording captures; read by the meeting recorder only when <see cref="RecordScreen"/> is on.</summary>
    public ScreenCaptureTarget ScreenCaptureTarget
    {
        get => ScreenCaptureTarget.FromStoredBundleId(Store.GetString(SettingsKey.ScreenCaptureApp));
        set => Store.Set(SettingsKey.ScreenCaptureApp, value.StoredBundleId ?? "");
    }

    public bool InboxEnabled
    {
        get => Store.GetBool(SettingsKey.InboxEnabled) ?? false;
        set => Store.Set(SettingsKey.InboxEnabled, value);
    }

    public bool SpeakerSeparation
    {
        get => Store.GetBool(SettingsKey.SpeakerSeparation) ?? true;
        set => Store.Set(SettingsKey.SpeakerSeparation, value);
    }

    /// <summary>0 = automatic, otherwise 2–8.</summary>
    public int ExpectedSpeakers
    {
        get => NormalizedExpectedSpeakers(Store.GetInt(SettingsKey.ExpectedSpeakers) ?? 0);
        set => Store.Set(SettingsKey.ExpectedSpeakers, NormalizedExpectedSpeakers(value));
    }

    public HotkeyShortcut HighlightShortcut
    {
        get => Store.GetObject<HotkeyShortcut>(SettingsKey.HighlightShortcut) ?? HotkeyShortcut.MarkMomentDefault;
        set => Store.SetObject(SettingsKey.HighlightShortcut, value);
    }

    public HotkeyShortcut MicrophoneShortcut
    {
        get => Store.GetObject<HotkeyShortcut>(SettingsKey.MicrophoneShortcut) ?? HotkeyShortcut.MicrophoneToggleDefault;
        set => Store.SetObject(SettingsKey.MicrophoneShortcut, value);
    }

    public bool McpEnabled
    {
        get => Store.GetBool(SettingsKey.McpEnabled) ?? false;
        set => Store.Set(SettingsKey.McpEnabled, value);
    }

    public int McpMaxAgeDays
    {
        get => NormalizedMcpMaxAgeDays(Store.GetInt(SettingsKey.McpMaxAgeDays) ?? 0);
        set => Store.Set(SettingsKey.McpMaxAgeDays, NormalizedMcpMaxAgeDays(value));
    }

    /// <summary>Does nothing while McpEnabled is off: Claude cannot write into meetings it may not read.</summary>
    public bool McpWriteEnabled
    {
        get => Store.GetBool(SettingsKey.McpWriteEnabled) ?? false;
        set => Store.Set(SettingsKey.McpWriteEnabled, value);
    }

    public RetentionPeriod KeepHistory
    {
        get => EnumText.ParseOr(Store.GetString(SettingsKey.KeepHistory), RetentionPeriod.Forever);
        set => Store.Set(SettingsKey.KeepHistory, EnumText.Raw(value));
    }

    public RetentionPeriod KeepDictationAudio
    {
        get => EnumText.ParseOr(Store.GetString(SettingsKey.KeepDictationAudio), RetentionPeriod.Days7);
        set => Store.Set(SettingsKey.KeepDictationAudio, EnumText.Raw(value));
    }

    public AutoCleanupOptions Cleanup
    {
        get => new(Store.GetBool(SettingsKey.RemoveFillers) ?? true, Store.GetBool(SettingsKey.RemoveRepetitions) ?? true);
        set
        {
            Store.Set(SettingsKey.RemoveFillers, value.RemoveFillers);
            Store.Set(SettingsKey.RemoveRepetitions, value.RemoveRepetitions);
        }
    }

    public StylePreset Style(AppCategory category) =>
        EnumText.ParseOr(Store.GetString(SettingsKey.Style(category)), category.DefaultStyle());

    public void SetStyle(StylePreset style, AppCategory category) =>
        Store.Set(SettingsKey.Style(category), EnumText.Raw(style));

    public bool OnboardingCompleted
    {
        get => Store.GetBool(SettingsKey.OnboardingCompleted) ?? false;
        set => Store.Set(SettingsKey.OnboardingCompleted, value);
    }

    public IReadOnlySet<string> DismissedBanners
    {
        get => new HashSet<string>(Store.GetStringArray(SettingsKey.DismissedBanners) ?? [], StringComparer.Ordinal);
        set => Store.Set(SettingsKey.DismissedBanners, value.Order(StringComparer.Ordinal));
    }

    public bool DictionarySeeded
    {
        get => Store.GetBool(SettingsKey.DictionarySeeded) ?? false;
        set => Store.Set(SettingsKey.DictionarySeeded, value);
    }

    public int DictionarySeedVersion
    {
        get => Store.GetInt(SettingsKey.DictionarySeedVersion) ?? 1;
        set => Store.Set(SettingsKey.DictionarySeedVersion, value);
    }

    /// <summary>The hotkey state machine configuration derived from General settings.</summary>
    public HotkeyConfiguration HotkeyConfiguration => new(DictationKey, HandsFreeEnabled, EscapeCancels);

    /// <summary>The engine Recognition settings choose for a purpose.</summary>
    public EngineId EngineFor(EnginePurpose purpose) => purpose.IsMeetings
        ? MeetingEngine
        : purpose.LanguageMode == LanguageMode.English ? EnglishEngine : RussianEngine;

    /// <summary>A stored engine Windows cannot run (Apple Dictation) falls back to the default.</summary>
    static EngineId AvailableOr(string? raw, EngineId fallback) =>
        EnumText.TryParse<EngineId>(raw, out var id) && id.IsAvailableOnWindows() ? id : fallback;
}
