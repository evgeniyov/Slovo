using Avalonia.Headless.XUnit;
using Slovo.App.Onboarding;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels.Settings;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

public class OnboardingTests
{
    static (OnboardingViewModel Model, AppSettings Settings, FakePractice Practice, FakePermissions Permissions) Make()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var practice = new FakePractice();
        var permissions = new FakePermissions { Microphone = PermissionStatus.Denied };
        var host = new SettingsViewModel((_, _) => new Slovo.App.Tests.Rendering.EmptyScreen());
        var model = new OnboardingViewModel(settings, permissions, new FakeHotkeys(), new FakeModels(), new FakeEngines(), practice, host);
        return (model, settings, practice, permissions);
    }

    [AvaloniaFact]
    public async Task WalksThroughTheStepsAndFinishes()
    {
        var (model, settings, practice, permissions) = Make();
        var finished = false;
        model.Finished += () => finished = true;
        model.NextCommand.Execute(null);
        Assert.True(model.IsMicrophone);
        Assert.False(model.MicrophoneAllowed);
        await model.AllowMicrophoneCommand.ExecuteAsync(null);
        Assert.Equal([PermissionKind.Microphone], permissions.Opened);
        model.NextCommand.Execute(null);
        model.SelectKeyCommand.Execute(model.Keys.Single(k => k.Key == DictationKey.RightCtrl));
        Assert.Equal(DictationKey.RightCtrl, settings.DictationKey);
        Assert.True(model.Keys.Single(k => k.Key == DictationKey.RightCtrl).IsSelected);
        model.NextCommand.Execute(null);
        model.NextCommand.Execute(null);
        Assert.True(model.IsPractice);
        Assert.True(practice.Active);
        model.ToggleHandsFreeCommand.Execute(null);
        model.ToggleHandsFreeCommand.Execute(null);
        Assert.StartsWith("Привет", model.PracticeResult);
        model.NextCommand.Execute(null);
        Assert.True(finished);
        Assert.True(settings.OnboardingCompleted);
        Assert.False(practice.Active);
    }

    [AvaloniaFact]
    public void TheSidebarCardStartsAtTheMicrophone()
    {
        var permissions = new FakePermissions { Microphone = PermissionStatus.Denied };
        var model = new OnboardingViewModel(new AppSettings(SettingsStore.InMemory()), permissions, new FakeHotkeys(), new FakeModels(),
            new FakeEngines(), new FakePractice(), new SettingsViewModel((_, _) => new EmptyScreen()), OnboardingStep.Microphone);
        Assert.True(model.IsMicrophone);
        Assert.False(model.MicrophoneAllowed);
        model.BackCommand.Execute(null);
        Assert.True(model.IsWelcome);
    }

    [AvaloniaFact]
    public void Render()
    {
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            foreach (var step in Enum.GetValues<OnboardingStep>())
            {
                var (model, _, _, _) = Make();
                for (var i = 0; i < (int)step; i++)
                {
                    model.NextCommand.Execute(null);
                }
                Snapshot.RenderWindow(new OnboardingWindow { DataContext = model }, "onboarding-" + step.ToString().ToLowerInvariant() + "-ru", 720, 600);
            }
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }
}
