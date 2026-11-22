using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Screens;

public enum DictionaryTab
{
    All,
    Terms,
    Replacements,
}

public sealed record DictionaryRowViewModel(DictionaryItem Item, bool ShowsKind)
{
    public bool IsTerm => Item.Kind == DictionaryItemKind.Term;
    public string KindTitle => Loc.Instance.Get(IsTerm ? "dictionary.kind.term" : "dictionary.kind.replacement");
}

/// <summary>The Dictionary screen: terms and "from → to" replacements (the macOS DictionaryScreen).</summary>
public sealed partial class DictionaryViewModel : ViewModelBase
{
    public const string BannerId = "dictionary.terms";

    readonly IDictionaryStore store;
    readonly AppSettings settings;
    readonly ISheetHost sheets;

    public DictionaryViewModel(IDictionaryStore store, AppSettings settings, ISheetHost sheets)
    {
        this.store = store;
        this.settings = settings;
        this.sheets = sheets;
        store.PropertyChanged += OnStoreChanged;
        Refresh();
    }

    [ObservableProperty] public partial DictionaryTab Tab { get; set; } = DictionaryTab.All;
    [ObservableProperty] public partial bool SortAlphabetically { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<DictionaryRowViewModel> Rows { get; private set; } = [];
    [ObservableProperty] public partial int AllCount { get; private set; }
    [ObservableProperty] public partial int TermCount { get; private set; }
    [ObservableProperty] public partial int ReplacementCount { get; private set; }

    public bool IsEmpty => AllCount == 0;
    public bool NothingFound => AllCount > 0 && Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;
    public bool IsBannerVisible => !settings.DismissedBanners.Contains(BannerId);

    public bool IsAllTab { get => Tab == DictionaryTab.All; set { if (value) Tab = DictionaryTab.All; } }
    public bool IsTermsTab { get => Tab == DictionaryTab.Terms; set { if (value) Tab = DictionaryTab.Terms; } }
    public bool IsReplacementsTab { get => Tab == DictionaryTab.Replacements; set { if (value) Tab = DictionaryTab.Replacements; } }

    public IReadOnlyList<string> SortChoices => [Loc.Instance.Get("dictionary.sort.newest"), Loc.Instance.Get("dictionary.sort.alphabetical")];

    public int SortIndex
    {
        get => SortAlphabetically ? 1 : 0;
        set => SortAlphabetically = value == 1;
    }

    partial void OnTabChanged(DictionaryTab value)
    {
        OnPropertyChanged(nameof(IsAllTab));
        OnPropertyChanged(nameof(IsTermsTab));
        OnPropertyChanged(nameof(IsReplacementsTab));
        Refresh();
    }

    partial void OnSortAlphabeticallyChanged(bool value) => Refresh();

    partial void OnQueryChanged(string value) => Refresh();

    void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    void Refresh()
    {
        var items = store.Items;
        AllCount = items.Count;
        TermCount = items.Count(i => i.Kind == DictionaryItemKind.Term);
        ReplacementCount = AllCount - TermCount;
        var filtered = items.Where(item => Tab switch
        {
            DictionaryTab.Terms => item.Kind == DictionaryItemKind.Term,
            DictionaryTab.Replacements => item.Kind == DictionaryItemKind.Replacement,
            _ => true,
        } && Search.Matches(Query, item.Text, item.Replacement));
        var sorted = SortAlphabetically
            ? filtered.OrderBy(i => i.Text, StringComparer.Create(Loc.Instance.Culture, ignoreCase: true))
            : filtered.OrderByDescending(i => i.CreatedAt);
        Rows = sorted.Select(item => new DictionaryRowViewModel(item, Tab == DictionaryTab.All)).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NothingFound));
        OnPropertyChanged(nameof(HasRows));
    }

    [RelayCommand]
    void AddNew() => sheets.ShowSheet(new DictionaryEditorViewModel(store, null));

    [RelayCommand]
    void Edit(DictionaryRowViewModel row) => sheets.ShowSheet(new DictionaryEditorViewModel(store, row.Item));

    [RelayCommand]
    void Delete(DictionaryRowViewModel row) => store.Delete(row.Item.Id);

    [RelayCommand]
    void DismissBanner()
    {
        var dismissed = settings.DismissedBanners.ToHashSet();
        dismissed.Add(BannerId);
        settings.DismissedBanners = dismissed;
        OnPropertyChanged(nameof(IsBannerVisible));
    }
}

/// <summary>New or edited dictionary entry: a term, or a replacement "from → to".</summary>
public sealed partial class DictionaryEditorViewModel : SheetViewModel
{
    readonly IDictionaryStore store;
    readonly DictionaryItem? original;

    public DictionaryEditorViewModel(IDictionaryStore store, DictionaryItem? original)
    {
        this.store = store;
        this.original = original;
        IsReplacement = original?.Kind == DictionaryItemKind.Replacement;
        Text = original?.Text ?? "";
        Replacement = original?.Replacement ?? "";
    }

    public string Title => Loc.Instance.Get(original is null ? "dictionary.editor.newTitle" : "dictionary.editor.editTitle");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTerm), nameof(IsDuplicate), nameof(CanSave))]
    public partial bool IsReplacement { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDuplicate), nameof(CanSave))]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDuplicate), nameof(CanSave))]
    public partial string Replacement { get; set; } = "";

    public bool IsTerm
    {
        get => !IsReplacement;
        set => IsReplacement = !value;
    }

    public bool IsDuplicate
    {
        get
        {
            var text = Text.Trim();
            var replacement = Replacement.Trim();
            var kind = IsReplacement ? DictionaryItemKind.Replacement : DictionaryItemKind.Term;
            return store.Items.Any(item => item.Id != original?.Id && item.Kind == kind
                && string.Equals(item.Text.Trim(), text, StringComparison.OrdinalIgnoreCase)
                && (kind == DictionaryItemKind.Term || string.Equals(item.Replacement.Trim(), replacement, StringComparison.Ordinal)));
        }
    }

    public bool CanSave => Text.Trim().Length > 0 && (!IsReplacement || Replacement.Trim().Length > 0) && !IsDuplicate;

    [RelayCommand]
    void Save()
    {
        if (!CanSave)
        {
            return;
        }
        var kind = IsReplacement ? DictionaryItemKind.Replacement : DictionaryItemKind.Term;
        var replacement = IsReplacement ? Replacement.Trim() : "";
        if (original is null)
        {
            store.Add(new DictionaryItem { Kind = kind, Text = Text.Trim(), Replacement = replacement, CreatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            store.Update(original with { Kind = kind, Text = Text.Trim(), Replacement = replacement });
        }
        Close();
    }

    [RelayCommand]
    void Cancel() => Close();
}
