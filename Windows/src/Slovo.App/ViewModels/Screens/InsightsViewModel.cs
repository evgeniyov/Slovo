using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.ViewModels.Screens;

public sealed record CategoryShareRow(string Title, string Percent, double Fraction);

public sealed record TopAppRow(string Name, string Words);

/// <summary>The Insights screen: pace, totals, fixes, streaks with a calendar, categories, top apps.</summary>
public sealed partial class InsightsViewModel : ViewModelBase
{
    readonly IHistoryStore history;
    readonly Func<Insights> compute;

    public InsightsViewModel(IHistoryStore history, Func<Insights> compute)
    {
        this.history = history;
        this.compute = compute;
        history.PropertyChanged += OnHistoryChanged;
        Refresh();
    }

    [ObservableProperty] public partial Insights Current { get; private set; } = Insights.Empty;

    public bool IsEmpty => Current.DictationCount == 0;
    public string Wpm => Formatting.Number((long)Math.Round(Current.WordsPerMinute));
    public string TotalWords => Formatting.Number(Current.TotalWords);
    public string Fixes => Formatting.Number(Current.FixesMade);
    public string TimeSaved => Formatting.Duration(Current.TimeSaved);
    public string CurrentStreak => Loc.Instance.Plural("plural.days", Current.CurrentStreak);
    public string LongestStreak => Loc.Instance.Plural("plural.days", Current.LongestStreak);

    public IReadOnlyList<CategoryShareRow> Categories => Current.CategoryShares
        .Select(share => new CategoryShareRow(Loc.Instance.Get("category." + EnumText.Raw(share.Category)),
                                             ((int)Math.Round(share.Fraction * 100)).ToString(Loc.Instance.Culture) + " %",
                                             share.Fraction))
        .ToList();

    public IReadOnlyList<TopAppRow> TopApps => Current.TopApps.Take(6)
        .Select(app => new TopAppRow(app.App.Name, Loc.Instance.Plural("plural.words", app.Words)))
        .ToList();

    public bool HasTopApps => Current.TopApps.Count > 0;

    void OnHistoryChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    void Refresh()
    {
        Current = compute();
        OnPropertyChanged(string.Empty);
    }
}
