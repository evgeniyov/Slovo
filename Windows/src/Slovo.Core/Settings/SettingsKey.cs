namespace Slovo.Core.Settings;

/// <summary>
/// Every settings key Slovo uses. The names are the macOS app's UserDefaults keys, so
/// both apps describe the same setting the same way; Windows stores them in
/// <c>settings.json</c> in the data folder.
/// </summary>
public static class SettingsKey
{
    // General
    public const string AppLanguage = "app.language";
    public const string DictationKey = "dictation.key";
    public const string HandsFreeEnabled = "dictation.handsFreeEnabled";
    public const string EscapeCancels = "dictation.escapeCancels";
    public const string MaxHandsFreeSeconds = "dictation.maxHandsFreeSeconds";
    public const string PasteLastShortcut = "dictation.pasteLastShortcut";
    /// <summary>Empty or missing means the system default input.</summary>
    public const string MicrophoneUid = "dictation.microphoneUID";
    public const string LanguageMode = "dictation.languageMode";

    // Recognition
    public const string RussianEngine = "recognition.engine.russian";
    public const string EnglishEngine = "recognition.engine.english";
    public const string MeetingEngine = "recognition.engine.meetings";
    public const string IdleUnloadMinutes = "recognition.idleUnloadMinutes";

    // System
    public const string ShowFlowBarWhenIdle = "system.showFlowBarWhenIdle";
    public const string SoundsEnabled = "system.soundsEnabled";
    /// <summary>The tray icon (the menu bar icon on macOS); a meeting recording shows it anyway.</summary>
    public const string ShowMenuBarIcon = "system.showMenuBarIcon";
    public const string LaunchAtLogin = "system.launchAtLogin";

    // Meetings
    public const string RecordSystemAudio = "meetings.recordSystemAudio";
    public const string RecordMicrophone = "meetings.recordMicrophone";
    public const string KeepMeetingAudio = "meetings.keepAudio";
    public const string InboxEnabled = "inbox.enabled";
    public const string SpeakerSeparation = "meetings.speakerSeparation";
    /// <summary>0 = automatic, otherwise 2–8 people.</summary>
    public const string ExpectedSpeakers = "meetings.expectedSpeakers";
    public const string HighlightShortcut = "meetings.highlightShortcut";
    public const string MicrophoneShortcut = "meetings.microphoneShortcut";
    /// <summary>
    /// Whether a new meeting recording also captures the screen. Off by default: unlike the
    /// audio tracks, a screen video is not something every meeting needs.
    /// </summary>
    public const string RecordScreen = "meetings.recordScreen";
    /// <summary>
    /// Empty for the whole display; otherwise the app (its <see cref="Model.AppIdentity.BundleId"/>)
    /// whose windows a screen recording captures, video and audio alike.
    /// </summary>
    public const string ScreenCaptureApp = "meetings.screenCaptureApp";

    // Integrations
    public const string McpEnabled = "integrations.mcp.enabled";
    /// <summary>0 = all meetings, otherwise 90, 30 or 7 days.</summary>
    public const string McpMaxAgeDays = "integrations.mcp.maxAgeDays";
    public const string McpWriteEnabled = "integrations.mcp.writeEnabled";

    // Data and privacy
    public const string KeepHistory = "privacy.keepHistory";
    public const string KeepDictationAudio = "privacy.keepDictationAudio";

    // Style
    public const string RemoveFillers = "cleanup.removeFillers";
    public const string RemoveRepetitions = "cleanup.removeRepetitions";

    public static string Style(Model.AppCategory category) => "style." + Model.EnumText.Raw(category);

    // Interface state
    public const string OnboardingCompleted = "onboarding.completed";
    public const string DismissedBanners = "ui.dismissedBanners";
    public const string DictionarySeeded = "dictionary.seeded";
    /// <summary>The last <c>DictionarySeed.Additions</c> version added to an already seeded Dictionary.</summary>
    public const string DictionarySeedVersion = "dictionary.seedVersion";
}
