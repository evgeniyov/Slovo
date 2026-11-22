using System.ComponentModel;
using Slovo.Core.Dictation;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Dictation;

/// <summary>
/// Port of the ServiceWiring and dictation parts of IntegrationWiringTests.swift: the
/// paste-last shortcut, preloading, model deletion, the key listener's lifecycle, and the
/// app category and style a dictation gets. Retention, the real stores and the engine
/// manager are tested with their own modules.
/// </summary>
public sealed class DictationWiringTests : IDisposable
{
    readonly DictationHarness h = new();
    readonly FakeShortcuts shortcuts = new();
    readonly FakeModels models = new();

    public DictationWiringTests()
    {
        models.Events = h.Engines.Events;
    }

    public void Dispose() => h.Dispose();

    DictationWiring MakeWiring() => new(h.Controller, h.Settings, models, h.Engines, shortcuts, h.MainThread);

    // MARK: - Dictation over the stores

    [Fact]
    public Task KeyDictationUsesTheCategoryOfTheAppFoundByName() => h.Run(async () =>
    {
        // An app with no known executable name; the category store matches it by name.
        h.ForegroundApp.App = new AppIdentity("sparkbeta.exe", "Spark");
        h.Categories.NameMapping["Spark"] = AppCategory.Email;
        await h.Dictate();

        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(AppCategory.Email, entry.Category);
        Assert.Equal("Spark", entry.AppName);
        Assert.Equal("sparkbeta.exe", entry.AppBundleId);
        Assert.Equal(StylePreset.Formal, h.FakePipeline.Contexts[^1].Style);
    });

    [Fact]
    public Task StyleFollowsTheCategorySetting() => h.Run(async () =>
    {
        h.ForegroundApp.App = new AppIdentity("telegram.exe", "Telegram");
        h.Categories.Mapping["telegram.exe"] = AppCategory.Personal;
        h.Settings.SetStyle(StylePreset.VeryCasual, AppCategory.Personal);
        await h.Dictate();

        Assert.Equal(StylePreset.VeryCasual, h.FakePipeline.Contexts[^1].Style);
        Assert.Equal(AppCategory.Personal, h.History.Entries[0].Category);
    });

    [Fact]
    public Task AppWithNoIdentityIsOther() => h.Run(async () =>
    {
        h.ForegroundApp.App = null;
        await h.Dictate();

        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(AppCategory.Other, entry.Category);
        Assert.Null(entry.AppBundleId);
    });

    // MARK: - Lifecycle

    [Fact]
    public Task StartRunsDictationAndStopEndsIt() => h.Run(() =>
    {
        var wiring = MakeWiring();
        wiring.Start();
        Assert.Equal([h.Settings.HotkeyConfiguration], h.Hotkeys.StartedConfigurations);
        Assert.True(h.Hotkeys.HasTriggerListeners);
        Assert.True(wiring.IsPasteLastRegistered);

        h.Hotkeys.Fire(DictationTrigger.BeginPushToTalk);
        h.ConfirmHold();
        Assert.True(h.Controller.State.IsRecording);

        h.Settings.DictationKey = DictationKey.RightAlt;
        Assert.Equal(DictationKey.RightAlt, h.Hotkeys.UpdatedConfigurations[^1].Key);

        wiring.Stop();
        Assert.Equal(DictationState.IdleState, h.Controller.State);
        Assert.Equal(1, h.Hotkeys.StopCount);
        Assert.False(h.Hotkeys.HasTriggerListeners);
        Assert.Null(shortcuts.Shortcut);
        Assert.False(wiring.IsPasteLastRegistered);

        h.Settings.PasteLastShortcut = HotkeyShortcut.PasteLastChoices[1];
        Assert.Single(shortcuts.Registrations); // a stopped wiring follows nothing
    });

    // MARK: - Paste last

    [Fact]
    public Task PasteLastShortcutFollowsSettingAndPastesLastTranscript() => h.Run(async () =>
    {
        h.Engine.Text = "привет";
        await h.Dictate();
        Assert.Single(h.Inserter.Inserted);

        var wiring = MakeWiring();
        var changes = new List<string?>();
        wiring.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        wiring.Start();
        Assert.Equal([HotkeyShortcut.PasteLastDefault], shortcuts.Registrations);
        Assert.True(wiring.IsPasteLastRegistered);
        Assert.Equal([nameof(DictationWiring.IsPasteLastRegistered)], changes);

        shortcuts.Fire();
        Assert.True(await TestAudio.WaitUntil(() => h.Inserter.Inserted.Count == 2));
        Assert.Equal(h.Controller.LastTranscript, h.Inserter.Inserted[^1]);

        // Unrelated settings leave the registration alone; a new shortcut replaces it.
        h.Settings.SoundsEnabled = false;
        Assert.True(wiring.ApplySettings());
        Assert.Single(shortcuts.Registrations);
        var custom = new HotkeyShortcut(VirtualKey.V, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        h.Settings.PasteLastShortcut = custom;
        Assert.Equal(custom, shortcuts.Registrations[^1]);
        Assert.Equal(custom, shortcuts.Shortcut);
        Assert.True(wiring.IsPasteLastRegistered);
        wiring.Stop();
    });

    [Fact]
    public Task SettingsChangedOnAnotherThreadReachTheShortcut() => h.Run(async () =>
    {
        var wiring = MakeWiring();
        wiring.Start();
        var custom = HotkeyShortcut.PasteLastChoices[2];
        await Task.Run(() => h.Settings.PasteLastShortcut = custom);
        Assert.True(await TestAudio.WaitUntil(() => shortcuts.Shortcut == custom));
        wiring.Stop();
    });

    [Fact]
    public Task RefusedShortcutIsNotRetriedUntilTheSettingChanges() => h.Run(() =>
    {
        shortcuts.Refuses = true;
        var wiring = MakeWiring();
        Assert.False(wiring.ApplySettings());
        Assert.False(wiring.ApplySettings());
        Assert.Single(shortcuts.Registrations);
        Assert.False(wiring.IsPasteLastRegistered);

        h.Settings.PasteLastShortcut = new HotkeyShortcut(VirtualKey.V, HotkeyModifiers.Control | HotkeyModifiers.Win);
        Assert.False(wiring.ApplySettings());
        Assert.Equal(2, shortcuts.Registrations.Count);
    });

    [Fact]
    public Task PastingLastWithoutTextDoesNothing() => h.Run(async () =>
    {
        var wiring = MakeWiring();
        wiring.Start();
        shortcuts.Fire();
        await Task.Yield();
        Assert.Empty(h.Inserter.Inserted);
        wiring.Stop();
    });

    // MARK: - Models

    [Fact]
    public Task DictationEngineIsPreloadedOnceItsModelBecomesUsable() => h.Run(() =>
    {
        models.Statuses = new() { [EngineId.GigaamRnnt] = ModelStatus.Downloading(0.5), [EngineId.Parakeet] = ModelStatus.Ready(1) };
        var wiring = MakeWiring();
        wiring.Start();

        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Downloading(0.9);
        wiring.ModelStatusesDidChange();
        Assert.Empty(h.Engines.Events);

        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Ready(1);
        wiring.ModelStatusesDidChange();
        wiring.ModelStatusesDidChange();
        Assert.Equal(["preload"], h.Engines.Events);

        // A model the dictation engine does not use becoming usable changes nothing.
        models.Statuses[EngineId.GigaamCtc] = ModelStatus.Ready(1);
        wiring.ModelStatusesDidChange();
        Assert.Equal(["preload"], h.Engines.Events);
        wiring.Stop();
    });

    [Fact]
    public Task RepairedModelIsPreloaded() => h.Run(() =>
    {
        h.Engines.DictationEngine = EngineId.Parakeet;
        models.Statuses = new() { [EngineId.Parakeet] = ModelStatus.Damaged("truncated") };
        var wiring = MakeWiring();
        wiring.Start();

        models.Statuses[EngineId.Parakeet] = ModelStatus.Downloading(0.2);
        wiring.ModelStatusesDidChange();
        Assert.Empty(h.Engines.Events);
        models.Statuses[EngineId.Parakeet] = ModelStatus.Ready(1);
        wiring.ModelStatusesDidChange();
        Assert.Equal(["preload"], h.Engines.Events);
        wiring.Stop();
    });

    [Fact]
    public Task ModelChangesAreFollowedWithoutHelp() => h.Run(async () =>
    {
        var wiring = MakeWiring();
        wiring.Start();
        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Ready(1);
        models.RaiseChanged();
        Assert.Equal(["preload"], h.Engines.Events);

        // A change reported from a download thread arrives on the main thread.
        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.NotDownloaded(1);
        await Task.Run(models.RaiseChanged);
        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Ready(1);
        await Task.Run(models.RaiseChanged);
        Assert.True(await TestAudio.WaitUntil(() => h.Engines.Events.Count == 2));

        wiring.Stop();
        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.NotDownloaded(1);
        models.RaiseChanged();
        models.Statuses[EngineId.GigaamRnnt] = ModelStatus.Ready(1);
        models.RaiseChanged();
        Assert.Equal(2, h.Engines.Events.Count);
    });

    [Fact]
    public Task DeletingAModelReleasesItsEngineFirst() => h.Run(async () =>
    {
        models.Statuses = new() { [EngineId.Parakeet] = ModelStatus.Ready(1) };
        h.Engines.DictationEngine = EngineId.Parakeet;
        var wiring = MakeWiring();
        wiring.Start();

        await wiring.DeleteModelAsync(EngineId.Parakeet);
        Assert.Equal(["unload:parakeet-v3", "wait:parakeet-v3", "delete:parakeet-v3"], h.Engines.Events);

        // Downloading it again counts as becoming usable.
        models.Statuses[EngineId.Parakeet] = ModelStatus.Ready(1);
        wiring.ModelStatusesDidChange();
        Assert.Equal("preload", h.Engines.Events[^1]);
        wiring.Stop();
    });

    [Fact]
    public Task FailedDeleteIsReported() => h.Run(async () =>
    {
        models.DeleteError = new SlovoException("busy");
        var wiring = MakeWiring();
        var error = await Assert.ThrowsAsync<SlovoException>(() => wiring.DeleteModelAsync(EngineId.GigaamCtc));
        Assert.Equal("busy", error.Message);
        Assert.Equal(["unload:gigaam-v3-ctc", "wait:gigaam-v3-ctc", "delete:gigaam-v3-ctc"], h.Engines.Events);
    });
}
