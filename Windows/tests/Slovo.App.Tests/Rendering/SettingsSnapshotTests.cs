using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Settings;
using Slovo.App.Views.Settings;
using Slovo.Core;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

public class SettingsSnapshotTests
{
    static ViewModelBase Section(SettingsSection section, AppSettings settings, ISheetHost? host = null) => section switch
    {
        SettingsSection.Recognition => new RecognitionSettingsViewModel(settings, new FakeModels(), new FakeEngines(), host!),
        SettingsSection.Meetings => new MeetingsSettingsViewModel(settings, new FakeModels(), new FakeSpeakerModel(), new FakeSpeakerModel(), host!,
            new FakeShell(), new AppPaths("/tmp/slovo"), _ => true, _ => false),
        SettingsSection.Integrations => new IntegrationsSettingsViewModel(settings, new FakeShell(), _ => { },
            @"C:\Users\me\AppData\Local\Programs\Slovo\Slovo.exe", () => 12),
        SettingsSection.General => new GeneralSettingsViewModel(settings, new FakeDevices(), new FakeHotkeys(), _ => true),
        SettingsSection.System => new SystemSettingsViewModel(settings, new FakeAutostart()),
        SettingsSection.Privacy => new PrivacySettingsViewModel(settings, new FakeHistory(), new FakeShell(),
            new FakePermissions { Microphone = PermissionStatus.Denied }, new AppPaths("C:/Users/me/AppData/Local/Slovo"),
            (_, _) => Task.FromResult(false), () => { }),
        _ => new AboutSettingsViewModel(new FakeShell(), new AppPaths("/tmp/slovo")),
    };

    public static TheoryData<string, string> Cases => new()
    {
        { "General", "en" }, { "General", "ru" }, { "System", "ru" }, { "Privacy", "ru" }, { "About", "en" },
        { "Recognition", "ru" }, { "Meetings", "ru" }, { "Integrations", "en" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void RendersSection(string name, string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            var settings = new AppSettings(SettingsStore.InMemory());
            settings.McpEnabled = true;
            var section = Enum.Parse<SettingsSection>(name);
            var model = new SettingsViewModel((s, host) => Section(s, settings, host), section);
            Snapshot.RenderWindow(new SettingsWindow { DataContext = model }, $"settings-{name.ToLowerInvariant()}-{language}", 820, 620);
            if (name == "General")
            {
                var darkModel = new SettingsViewModel((s, host) => Section(s, settings, host), section);
                var dark = new SettingsWindow { DataContext = darkModel };
                Snapshot.RenderWindow(dark, $"settings-{name.ToLowerInvariant()}-{language}", 820, 620, ThemeVariant.Dark);
            }
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
