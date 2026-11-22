namespace Slovo.App.ViewModels.Settings;

public enum SettingsSection
{
    General,
    Recognition,
    System,
    Meetings,
    Integrations,
    Privacy,
    About,
}

public static class SettingsSections
{
    public static readonly IReadOnlyList<SettingsSection> All =
    [
        SettingsSection.General, SettingsSection.Recognition, SettingsSection.System, SettingsSection.Meetings,
        SettingsSection.Integrations, SettingsSection.Privacy, SettingsSection.About,
    ];

    public static string TitleKey(this SettingsSection section) => "settings.section." + section switch
    {
        SettingsSection.General => "general",
        SettingsSection.Recognition => "recognition",
        SettingsSection.System => "system",
        SettingsSection.Meetings => "meetings",
        SettingsSection.Integrations => "integrations",
        SettingsSection.Privacy => "privacy",
        _ => "about",
    };

    public static string IconKey(this SettingsSection section) => section switch
    {
        SettingsSection.General => "Icon.Sliders",
        SettingsSection.Recognition => "Icon.Sparkle",
        SettingsSection.System => "Icon.Keyboard",
        SettingsSection.Meetings => "Icon.People",
        SettingsSection.Integrations => "Icon.Link",
        SettingsSection.Privacy => "Icon.Folder",
        _ => "Icon.Help",
    };

    public static SettingsSection? Parse(string? name) =>
        Enum.TryParse<SettingsSection>(name, ignoreCase: true, out var section) ? section : null;
}
