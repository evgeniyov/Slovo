using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Slovo.App.Integration;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Settings;
using Slovo.App.Views.Settings;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

/// <summary>Settings → Integrations against the macOS IntegrationsSettings: Copied, and Checking… while files or the server are read.</summary>
public class IntegrationsTests
{
    const string Executable = @"C:\Users\me\AppData\Local\Programs\Slovo\Slovo.exe";

    public IntegrationsTests() => CoreStrings.SetLanguage(AppLanguage.En);

    static IntegrationsSettingsViewModel Model(Action<string>? copy = null, Func<string, Task<McpSelfCheck.Outcome>>? runCheck = null,
                                               Func<Task<ClaudeRegistration>>? detect = null) =>
        new(new AppSettings(SettingsStore.InMemory()), new FakeShell(), copy ?? (_ => { }), Executable, () => 3, runCheck,
            detect ?? (() => Task.FromResult(new ClaudeRegistration(false, false))));

    [AvaloniaFact]
    public void CopySaysCopiedForAMomentOnTheButtonThatWasPressed()
    {
        var copied = new List<string>();
        var model = Model(copied.Add);

        model.CopyClaudeCodeCommand.Execute(null);
        Assert.Equal([model.ClaudeCodeCommand], copied);
        Assert.True(model.ClaudeCodeCopied.IsOn);
        Assert.False(model.ClaudeAppCopied.IsOn);
        Assert.True(Wait.Until(() => !model.ClaudeCodeCopied.IsOn, CopiedFlag.Duration + TimeSpan.FromSeconds(5)));

        model.CopyClaudeAppCommand.Execute(null);
        Assert.Equal(model.ClaudeAppSnippet, copied[^1]);
        Assert.True(model.ClaudeAppCopied.IsOn);
        Assert.False(model.ClaudeCodeCopied.IsOn);
    }

    [AvaloniaFact]
    public void TheCopyButtonReadsCopied()
    {
        var model = Model();
        var host = new SettingsViewModel((_, _) => model, SettingsSection.Integrations);
        var window = new SettingsWindow { DataContext = host };
        window.Show();
        try
        {
            var view = window.GetVisualDescendants().OfType<IntegrationsSettingsView>().Single();
            string[] Shown() => view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToArray();
            Assert.DoesNotContain("Copied", Shown());
            model.CopyClaudeCodeCommand.Execute(null);
            window.UpdateLayout();
            Assert.Single(Shown(), text => text == "Copied");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheClientsSayCheckingUntilClaudesFilesAreRead()
    {
        var detection = new TaskCompletionSource<ClaudeRegistration>();
        var model = Model(detect: () => detection.Task);
        Assert.Null(model.Registration);
        Assert.Equal("Checking…", model.ClaudeCodeStatus);
        Assert.Equal("Checking…", model.ClaudeAppStatus);

        detection.SetResult(new ClaudeRegistration(ClaudeCode: true, ClaudeApp: false));
        Assert.True(Wait.Until(() => model.Registration is not null, TimeSpan.FromSeconds(5)));
        Assert.Equal("Added to Claude Code", model.ClaudeCodeStatus);
        Assert.Equal("Not added to the Claude app yet", model.ClaudeAppStatus);
    }

    [AvaloniaFact]
    public void TheCheckSaysCheckingWhileTheServerIsAsked()
    {
        var answer = new TaskCompletionSource<McpSelfCheck.Outcome>();
        var model = Model(runCheck: _ => answer.Task);
        var running = model.CheckCommand.ExecuteAsync(null);
        Assert.True(model.IsChecking);
        Assert.False(model.CheckCommand.CanExecute(null));
        Assert.Null(model.CheckMessage);

        answer.SetResult(new McpSelfCheck.Outcome.Answered(false, null));
        Assert.True(Wait.Until(() => running.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.False(model.IsChecking);
        Assert.True(model.CheckWorks);
        Assert.True(model.CheckCommand.CanExecute(null));
    }
}
