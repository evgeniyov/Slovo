using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Slovo.App.Tests.Fakes;
using Slovo.App.ViewModels;
using Slovo.App.ViewModels.Settings;
using Slovo.App.Views.Settings;
using Slovo.Core;
using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.Tests.Rendering;

public class ModelRowTests
{
    [AvaloniaFact]
    public async Task DeletingAModelUnloadsTheEngineFirstAndOnlyAfterConfirmation()
    {
        var models = new FakeModels();
        var engines = new FakeEngines();
        var host = new SettingsViewModel((_, sheets) => new RecognitionSettingsViewModel(new AppSettings(SettingsStore.InMemory()), models, engines, sheets), SettingsSection.Recognition);
        var recognition = (RecognitionSettingsViewModel)host.Current!;
        var gigaam = recognition.Models.Single(m => m.Name == "GigaAM v3 RNNT");
        Assert.True(gigaam.CanDelete);

        var deleting = gigaam.DeleteCommand.ExecuteAsync(null);
        Assert.True(host.HasSheet);
        ((ConfirmSheetViewModel)host.Sheet!).ConfirmCommand.Execute(null);
        await deleting;

        Assert.Equal([EngineId.GigaamRnnt], engines.Unloaded);
        Assert.Equal(ModelStatusKind.NotDownloaded, models.Status(EngineId.GigaamRnnt).Kind);
    }

    [AvaloniaFact]
    public void TheEngineToPickForEachLanguageCarriesABadge()
    {
        CoreStrings.SetLanguage(AppLanguage.En);
        var host = new SettingsViewModel((_, sheets) => new RecognitionSettingsViewModel(new AppSettings(SettingsStore.InMemory()), new FakeModels(), new FakeEngines(), sheets), SettingsSection.Recognition);
        var rows = ((RecognitionSettingsViewModel)host.Current!).Models.ToDictionary(row => row.Name);
        Assert.Equal("Best for Russian", rows["GigaAM v3 RNNT"].Recommended);
        Assert.Equal("Best for English", rows["Parakeet v3"].Recommended);
        Assert.False(rows["GigaAM v3 CTC"].IsRecommended);
        Assert.Null(rows["GigaAM v3 CTC"].Recommended);
    }

    [AvaloniaFact]
    public void AModelThatIsLoadingSaysSoAndKeepsItsFilesUntilItIsDone()
    {
        CoreStrings.SetLanguage(AppLanguage.En);
        var engines = new FakeEngines();
        var host = new SettingsViewModel((_, sheets) => new RecognitionSettingsViewModel(new AppSettings(SettingsStore.InMemory()), new FakeModels(), engines, sheets), SettingsSection.Recognition);
        var recognition = (RecognitionSettingsViewModel)host.Current!;
        var gigaam = recognition.Models.Single(m => m.Name == "GigaAM v3 RNNT");
        var parakeet = recognition.Models.Single(m => m.Name == "Parakeet v3");
        Assert.False(gigaam.IsLoading);
        Assert.True(gigaam.DeleteCommand.CanExecute(null));
        Assert.Null(gigaam.BusyText);

        engines.SetLoadState(EngineId.GigaamRnnt, EngineLoadState.Loading);
        Dispatcher.UIThread.RunJobs();
        Assert.True(gigaam.IsLoading);
        Assert.False(gigaam.DeleteCommand.CanExecute(null));
        Assert.Equal("Available once the model finishes loading", gigaam.BusyText);
        // Only the engine that loads waits.
        Assert.False(parakeet.IsLoading);
        Assert.True(parakeet.DownloadCommand.CanExecute(null));

        engines.SetLoadState(EngineId.GigaamRnnt, EngineLoadState.Ready);
        Dispatcher.UIThread.RunJobs();
        Assert.False(gigaam.IsLoading);
        Assert.True(gigaam.DeleteCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void TheSpeakerModelWaitsForItsLoadToo()
    {
        var speakerModel = new FakeSpeakerModel { Status = ModelStatus.Ready(41_000_000), LoadState = EngineLoadState.Loading };
        var model = new MeetingsSettingsViewModel(new AppSettings(SettingsStore.InMemory()), new FakeModels(), speakerModel, speakerModel,
            new MainWindowViewModel((_, _) => new EmptyScreen(), new FakeShellActions(), null), new FakeShell(), new AppPaths("/tmp/slovo"), _ => true, _ => true);
        Assert.True(model.SpeakerModel.IsLoading);
        Assert.False(model.SpeakerModel.DeleteCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ADamagedModelSaysWhyUnderItsStatus()
    {
        CoreStrings.SetLanguage(AppLanguage.En);
        var models = new FakeModels();
        models.Statuses[EngineId.Parakeet] = ModelStatus.Damaged("encoder.int8.onnx is 12 MB short");
        var host = new SettingsViewModel((_, sheets) => new RecognitionSettingsViewModel(new AppSettings(SettingsStore.InMemory()), models, new FakeEngines(), sheets), SettingsSection.Recognition);
        var parakeet = ((RecognitionSettingsViewModel)host.Current!).Models.Single(m => m.Name == "Parakeet v3");
        Assert.True(parakeet.IsDamaged);
        Assert.Equal("Damaged", parakeet.StatusText);
        Assert.Equal("encoder.int8.onnx is 12 MB short", parakeet.DamagedReason);
        Assert.Null(parakeet.SizeText);
        Assert.Equal("Download again", parakeet.DownloadText);

        var gigaam = ((RecognitionSettingsViewModel)host.Current!).Models.Single(m => m.Name == "GigaAM v3 RNNT");
        Assert.Null(gigaam.DamagedReason);
    }

    [AvaloniaFact]
    public void RendersBadgesLoadingAndDamage()
    {
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            var models = new FakeModels();
            models.Statuses[EngineId.Parakeet] = ModelStatus.Damaged("Файл encoder.int8.onnx короче, чем должен быть");
            var engines = new FakeEngines();
            engines.SetLoadState(EngineId.GigaamRnnt, EngineLoadState.Loading);
            var model = new SettingsViewModel((_, sheets) => new RecognitionSettingsViewModel(new AppSettings(SettingsStore.InMemory()), models, engines, sheets), SettingsSection.Recognition);
            var window = new SettingsWindow { DataContext = model };
            Snapshot.RenderWindow(window, "settings-recognition-states-ru", 820, 760);
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [AvaloniaFact]
    public void ChoicesSayWhichEnginesAreNotDownloaded()
    {
        var host = new SettingsViewModel((_, sheets) => new RecognitionSettingsViewModel(new AppSettings(SettingsStore.InMemory()), new FakeModels(), new FakeEngines(), sheets), SettingsSection.Recognition);
        var recognition = (RecognitionSettingsViewModel)host.Current!;
        Assert.Equal("GigaAM v3 RNNT", recognition.RussianChoices[0].Title);
        Assert.Contains("Parakeet v3", recognition.EnglishChoices[0].Title);
        Assert.NotEqual("Parakeet v3", recognition.EnglishChoices[0].Title);
    }
}
