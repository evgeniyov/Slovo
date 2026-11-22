using Avalonia.Headless.XUnit;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Tests.Rendering;

public class TroubleshootingTests
{
    [AvaloniaFact]
    public void OffersToOpenWindowsSettingsWhenTheMicrophoneIsOff()
    {
        var permissions = new FakePermissions { Microphone = PermissionStatus.Denied };
        var model = new TroubleshootingViewModel(permissions, new FakeShellActions(), new FakeHotkeys());
        model.RunCommand.Execute(model.Items[1]);
        Assert.Equal([PermissionKind.Microphone], permissions.Opened);
    }

    [AvaloniaFact]
    public void Render()
    {
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            var (window, shell) = ScreenSnapshotTests.Shell(Screen.Home, _ => new EmptyScreen());
            shell.ShowSheet(new TroubleshootingViewModel(new FakePermissions { Microphone = PermissionStatus.Denied }, new FakeShellActions(), new FakeHotkeys()));
            Snapshot.RenderWindow(window, "sheet-troubleshooting-ru", 1100, 720);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
