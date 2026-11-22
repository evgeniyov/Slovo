using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Screens;

public sealed partial class StyleCardViewModel(StylePreset preset, AppCategory category, bool selected) : ViewModelBase
{
    public StylePreset Preset { get; } = preset;
    public string Title => Loc.Instance.Get("style.preset." + EnumText.Raw(Preset));
    public string Detail => Loc.Instance.Get("style.preset." + EnumText.Raw(Preset) + ".detail");
    public string Sample => Loc.Instance.Get($"style.sample.{EnumText.Raw(category)}.{EnumText.Raw(Preset)}");

    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;
}

public sealed record StyleTabItem(AppCategory? Category, string Title);

public sealed record CategoryMove(AppIdentity App, AppCategory Target, string Title);

public sealed record AppRowViewModel(AppIdentity App, IReadOnlyList<CategoryMove> Moves);

/// <summary>The Style screen: a style per app category, the apps in it, and auto cleanup.</summary>
public sealed partial class StyleViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly IAppCategoryStore categories;
    readonly IHistoryStore history;

    public StyleViewModel(AppSettings settings, IAppCategoryStore categories, IHistoryStore history)
    {
        this.settings = settings;
        this.categories = categories;
        this.history = history;
        var loc = Loc.Instance;
        Tabs = CategoryDefaults.All.Select(c => new StyleTabItem(c, loc.Get("category." + EnumText.Raw(c))))
            .Append(new StyleTabItem(null, loc.Get("style.tab.cleanup"))).ToList();
        categories.PropertyChanged += (_, _) => Refresh();
        history.PropertyChanged += (_, _) => Refresh();
        SelectedTab = Tabs[0];
    }

    public IReadOnlyList<StyleTabItem> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCleanup), nameof(IsCategory))]
    public partial StyleTabItem SelectedTab { get; set; }

    [ObservableProperty] public partial IReadOnlyList<StyleCardViewModel> Cards { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<AppRowViewModel> Apps { get; private set; } = [];

    public bool IsCleanup => SelectedTab.Category is null;
    public bool IsCategory => !IsCleanup;
    public bool HasApps => Apps.Count > 0;

    public bool RemoveFillers
    {
        get => settings.Cleanup.RemoveFillers;
        set { settings.Cleanup = settings.Cleanup with { RemoveFillers = value }; OnPropertyChanged(); }
    }

    public bool RemoveRepetitions
    {
        get => settings.Cleanup.RemoveRepetitions;
        set { settings.Cleanup = settings.Cleanup with { RemoveRepetitions = value }; OnPropertyChanged(); }
    }

    partial void OnSelectedTabChanged(StyleTabItem value) => Refresh();

    void Refresh()
    {
        if (SelectedTab?.Category is not { } category)
        {
            Cards = [];
            Apps = [];
            OnPropertyChanged(nameof(HasApps));
            return;
        }
        var current = settings.Style(category);
        Cards = new[] { StylePreset.Formal, StylePreset.Casual, StylePreset.VeryCasual }
            .Select(p => new StyleCardViewModel(p, category, p == current)).ToList();
        var loc = Loc.Instance;
        Apps = AppsIn(category)
            .Select(app => new AppRowViewModel(app, CategoryDefaults.All.Where(c => c != category)
                .Select(c => new CategoryMove(app, c, loc.Get("category." + EnumText.Raw(c)))).ToList()))
            .ToList();
        OnPropertyChanged(nameof(HasApps));
    }

    /// <summary>Apps seen in history or assigned by the user that now fall in the category.</summary>
    IEnumerable<AppIdentity> AppsIn(AppCategory category)
    {
        var seen = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in categories.Assignments)
        {
            seen[assignment.BundleId] = new AppIdentity(assignment.BundleId, assignment.AppName);
        }
        foreach (var entry in history.Entries)
        {
            if (entry.AppBundleId is { Length: > 0 } id && !seen.ContainsKey(id))
            {
                seen[id] = new AppIdentity(id, entry.AppName ?? id);
            }
        }
        return seen.Values.Where(app => categories.CategoryFor(app) == category)
            .OrderBy(app => app.Name, StringComparer.Create(Loc.Instance.Culture, ignoreCase: true));
    }

    [RelayCommand]
    void Choose(StyleCardViewModel card)
    {
        if (SelectedTab.Category is not { } category)
        {
            return;
        }
        settings.SetStyle(card.Preset, category);
        foreach (var other in Cards)
        {
            other.IsSelected = other.Preset == card.Preset;
        }
    }

    [RelayCommand]
    void Move(CategoryMove move) => categories.SetCategory(move.Target, move.App);
}
