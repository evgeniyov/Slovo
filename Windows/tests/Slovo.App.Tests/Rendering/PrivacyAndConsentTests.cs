using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Slovo.App.Localization;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels.Settings;
using Slovo.App.Views.Settings;
using Slovo.Core;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

/// <summary>Settings → Data and privacy's Permissions group, and the consent line under Settings → Meetings.</summary>
public class PrivacyAndConsentTests
{
    public PrivacyAndConsentTests() => CoreStrings.SetLanguage(AppLanguage.En);

    static PrivacySettingsViewModel Privacy(FakePermissions permissions) =>
        new(new AppSettings(SettingsStore.InMemory()), new FakeHistory(), new FakeShell(), permissions,
            new AppPaths("C:/Users/me/AppData/Local/Slovo"), (_, _) => Task.FromResult(false), () => { });

    static MeetingsSettingsViewModel Meetings(Slovo.App.ViewModels.ISheetHost host) =>
        new(new AppSettings(SettingsStore.InMemory()), new FakeModels(), new FakeSpeakerModel(), new FakeSpeakerModel(), host,
            new FakeShell(), new AppPaths("/tmp/slovo"), _ => true, _ => false);

    [AvaloniaFact]
    public async Task DeleteAllWaitsForADictationToDelete()
    {
        var history = new FakeHistory();
        var asked = 0;
        var model = new PrivacySettingsViewModel(new AppSettings(SettingsStore.InMemory()), history, new FakeShell(), new FakePermissions(),
            new AppPaths("C:/Users/me/AppData/Local/Slovo"), (_, _) => { asked++; return Task.FromResult(true); }, () => { });
        Assert.False(model.DeleteAllCommand.CanExecute(null));

        history.Add(new HistoryEntry { Text = "Привет", Date = DateTimeOffset.Now });
        Assert.True(model.DeleteAllCommand.CanExecute(null));

        await model.DeleteAllCommand.ExecuteAsync(null);
        Assert.Equal(1, asked);
        Assert.Empty(history.Entries);
        Assert.False(model.DeleteAllCommand.CanExecute(null));

        // The whole section, tall enough to show the button, faded with nothing to delete.
        var window = new SettingsWindow { DataContext = new SettingsViewModel((_, _) => model, SettingsSection.Privacy) };
        Snapshot.RenderWindow(window, "settings-privacy-deleteall-empty-en", 820, 1300);
    }

    [AvaloniaFact]
    public void TheMicrophoneRowShowsItsStatusAndOpensWindowsSettings()
    {
        var permissions = new FakePermissions { Microphone = PermissionStatus.Denied };
        var model = Privacy(permissions);
        var row = model.Microphone;
        Assert.Equal(PermissionKind.Microphone, row.Kind);
        Assert.Equal("Microphone", row.Title);
        Assert.True(row.IsMissing);
        Assert.Equal("Not allowed", row.StatusText);
        row.OpenCommand.Execute(null);
        Assert.Equal([PermissionKind.Microphone], permissions.Opened);

        permissions.Microphone = PermissionStatus.Granted;
        model.RefreshPermissions();
        Assert.True(row.IsGranted);
        Assert.False(row.IsMissing);
        Assert.Equal("Allowed", row.StatusText);

        // The switch could not be read: neither allowed nor refused, and Windows Settings says which.
        permissions.Microphone = PermissionStatus.Unknown;
        model.RefreshPermissions();
        Assert.False(row.IsGranted);
        Assert.False(row.IsMissing);
        Assert.Equal("See in Windows Settings", row.StatusText);
    }

    [AvaloniaFact]
    public void ListsOnlyTheMicrophoneAndLooksAgainWhenTheWindowComesBack()
    {
        var permissions = new FakePermissions { Microphone = PermissionStatus.Denied };
        var model = new SettingsViewModel((_, _) => Privacy(permissions), SettingsSection.Privacy);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        try
        {
            var privacy = window.GetLogicalDescendants().OfType<PrivacySettingsView>().Single();
            // Accessibility, Input Monitoring, System Audio Recording and Screen Recording have no Windows switch.
            Assert.Single(privacy.GetLogicalDescendants().OfType<PermissionRowView>());
            var row = ((PrivacySettingsViewModel)model.Current!).Microphone;
            Assert.True(row.IsMissing);
            permissions.Microphone = PermissionStatus.Granted;
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            Assert.True(row.IsGranted);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MeetingsSettingsEndWithTheConsentLine()
    {
        var settings = new SettingsViewModel((_, host) => Meetings(host), SettingsSection.Meetings);
        var view = new MeetingsSettingsView { DataContext = settings.Current };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            var last = Assert.IsType<TextBlock>(Assert.IsType<StackPanel>(view.Content).Children[^1]);
            Assert.Equal(Loc.Instance.Get("meetings.consent.detail"), last.Text);
            Assert.EndsWith("stay on this computer.", last.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void RendersTheWholeSections(string language)
    {
        CoreStrings.SetLanguage(language == "ru" ? AppLanguage.Ru : AppLanguage.En);
        try
        {
            var privacy = new PrivacySettingsView { DataContext = Privacy(new FakePermissions { Microphone = PermissionStatus.Denied }) };
            Snapshot.Render(privacy, "settings-privacy-permissions-" + language, 640, 640);
            var granted = new PrivacySettingsView { DataContext = Privacy(new FakePermissions()) };
            Snapshot.Render(granted, "settings-privacy-permissions-granted-" + language, 640, 640, ThemeVariant.Dark);
            var host = new SettingsViewModel((_, _) => new EmptyScreen());
            Snapshot.Render(new MeetingsSettingsView { DataContext = Meetings(host) }, "settings-meetings-consent-" + language, 640, 1100);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
