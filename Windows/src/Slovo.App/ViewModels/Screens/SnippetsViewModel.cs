using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Screens;

public sealed record SnippetExample(string Trigger, string Text);

/// <summary>The Snippets screen: trigger phrases that expand into longer text.</summary>
public sealed partial class SnippetsViewModel : ViewModelBase
{
    public const string BannerId = "snippets.examples";

    readonly ISnippetStore store;
    readonly AppSettings settings;
    readonly ISheetHost sheets;

    public SnippetsViewModel(ISnippetStore store, AppSettings settings, ISheetHost sheets)
    {
        this.store = store;
        this.settings = settings;
        this.sheets = sheets;
        store.PropertyChanged += (_, _) => Refresh();
        var loc = Loc.Instance;
        Examples = Enumerable.Range(1, 3)
            .Select(i => new SnippetExample(loc.Format("snippets.quoted", loc.Get($"snippets.example{i}.trigger")), loc.Get($"snippets.example{i}.text")))
            .ToList();
        Refresh();
    }

    public IReadOnlyList<SnippetExample> Examples { get; }

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<Snippet> Rows { get; private set; } = [];

    public bool IsEmpty => store.Snippets.Count == 0;
    public bool NothingFound => !IsEmpty && Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;
    public bool IsBannerVisible => !settings.DismissedBanners.Contains(BannerId);

    partial void OnQueryChanged(string value) => Refresh();

    void Refresh()
    {
        Rows = store.Snippets.Where(s => Search.Matches(Query, s.Trigger, s.Expansion)).OrderByDescending(s => s.UpdatedAt).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NothingFound));
        OnPropertyChanged(nameof(HasRows));
    }

    [RelayCommand]
    void AddNew() => sheets.ShowSheet(new SnippetEditorViewModel(store, null));

    [RelayCommand]
    void Edit(Snippet snippet) => sheets.ShowSheet(new SnippetEditorViewModel(store, snippet));

    [RelayCommand]
    void Delete(Snippet snippet) => store.Delete(snippet.Id);

    [RelayCommand]
    void DismissBanner()
    {
        var dismissed = settings.DismissedBanners.ToHashSet();
        dismissed.Add(BannerId);
        settings.DismissedBanners = dismissed;
        OnPropertyChanged(nameof(IsBannerVisible));
    }
}

public sealed partial class SnippetEditorViewModel : SheetViewModel
{
    readonly ISnippetStore store;
    readonly Snippet? original;

    public SnippetEditorViewModel(ISnippetStore store, Snippet? original)
    {
        this.store = store;
        this.original = original;
        Trigger = original?.Trigger ?? "";
        Expansion = original?.Expansion ?? "";
    }

    public string Title => Loc.Instance.Get(original is null ? "snippets.editor.newTitle" : "snippets.editor.editTitle");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string Trigger { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string Expansion { get; set; } = "";

    public bool CanSave => Trigger.Trim().Length > 0 && Expansion.Trim().Length > 0;

    [RelayCommand]
    void Save()
    {
        if (!CanSave)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (original is null)
        {
            store.Add(new Snippet { Trigger = Trigger.Trim(), Expansion = Expansion, CreatedAt = now, UpdatedAt = now });
        }
        else
        {
            store.Update(original with { Trigger = Trigger.Trim(), Expansion = Expansion, UpdatedAt = now });
        }
        Close();
    }

    [RelayCommand]
    void Cancel() => Close();
}
