using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Settings;

/// <summary>Settings → Recognition: an engine per purpose, idle unload, and the model files.</summary>
public sealed partial class RecognitionSettingsViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly IModelManager models;

    public RecognitionSettingsViewModel(AppSettings settings, IModelManager models, IEngineProvider engines, ISheetHost sheets,
                                        Func<EngineId, Task>? deleteModel = null)
    {
        this.settings = settings;
        this.models = models;
        var loc = Loc.Instance;
        RussianChoices = Choices(EngineIds.RussianChoices);
        EnglishChoices = Choices(EngineIds.EnglishChoices);
        MeetingChoices = Choices(EngineIds.MeetingChoices);
        IdleChoices =
        [
            new(EngineIdleTimeout.Minutes5, loc.Plural("plural.minutes", 5)),
            new(EngineIdleTimeout.Minutes15, loc.Plural("plural.minutes", 15)),
            new(EngineIdleTimeout.Minutes60, loc.Plural("plural.minutes", 60)),
            new(EngineIdleTimeout.Never, loc.Get("retention.never")),
        ];
        Models = EngineIds.Available.Select(id => new ModelRowViewModel(
            id.DisplayName(), loc.Get(TradeoffKey(id)), new EngineModelSource(models, id, deleteModel, engines), sheets,
            loc.Format("engine.delete.title", id.DisplayName()), loc.Get("engine.delete.message"),
            beforeDelete: deleteModel is null ? () => engines.Unload(id) : null, recommended: Recommended(id))).ToList();
        models.PropertyChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshChoices);
        models.Refresh();
    }

    public IReadOnlyList<ChoiceItem<EngineId>> RussianChoices { get; private set; }
    public IReadOnlyList<ChoiceItem<EngineId>> EnglishChoices { get; private set; }
    public IReadOnlyList<ChoiceItem<EngineId>> MeetingChoices { get; private set; }
    public IReadOnlyList<ChoiceItem<EngineIdleTimeout>> IdleChoices { get; }
    public IReadOnlyList<ModelRowViewModel> Models { get; }

    public ChoiceItem<EngineId> SelectedRussian
    {
        get => RussianChoices.FirstOrDefault(c => c.Value == settings.RussianEngine) ?? RussianChoices[0];
        set { if (value is not null) { settings.RussianEngine = value.Value; OnPropertyChanged(); } }
    }

    public ChoiceItem<EngineId> SelectedEnglish
    {
        get => EnglishChoices.FirstOrDefault(c => c.Value == settings.EnglishEngine) ?? EnglishChoices[0];
        set { if (value is not null) { settings.EnglishEngine = value.Value; OnPropertyChanged(); } }
    }

    public ChoiceItem<EngineId> SelectedMeeting
    {
        get => MeetingChoices.FirstOrDefault(c => c.Value == settings.MeetingEngine) ?? MeetingChoices[0];
        set { if (value is not null) { settings.MeetingEngine = value.Value; OnPropertyChanged(); } }
    }

    public ChoiceItem<EngineIdleTimeout> SelectedIdle
    {
        get => IdleChoices.FirstOrDefault(c => c.Value == settings.IdleUnload) ?? IdleChoices[1];
        set { if (value is not null) { settings.IdleUnload = value.Value; OnPropertyChanged(); } }
    }

    public static string TradeoffKey(EngineId id) => id switch
    {
        EngineId.GigaamRnnt => "engine.tradeoff.gigaamRNNT",
        EngineId.GigaamCtc => "engine.tradeoff.gigaamCTC",
        EngineId.Parakeet => "engine.tradeoff.parakeet",
        _ => "engine.tradeoff.apple",
    };

    /// <summary>The badge of the engine to pick for a language (EngineModelRow.isRecommended), or null.</summary>
    public static string? Recommended(EngineId id) => id switch
    {
        EngineId.GigaamRnnt => Loc.Instance.Get("engine.recommended.russian"),
        EngineId.Parakeet => Loc.Instance.Get("engine.recommended.english"),
        _ => null,
    };

    /// <summary>"GigaAM v3 CTC (not downloaded)" for an engine whose model cannot run yet.</summary>
    IReadOnlyList<ChoiceItem<EngineId>> Choices(IReadOnlyList<EngineId> ids) => ids
        .Select(id => new ChoiceItem<EngineId>(id, models.Status(id).IsUsable
            ? id.DisplayName()
            : Loc.Instance.Format("settings.recognition.choice.notReady", id.DisplayName())))
        .ToList();

    void RefreshChoices()
    {
        RussianChoices = Choices(EngineIds.RussianChoices);
        EnglishChoices = Choices(EngineIds.EnglishChoices);
        MeetingChoices = Choices(EngineIds.MeetingChoices);
        OnPropertyChanged(string.Empty);
    }
}
