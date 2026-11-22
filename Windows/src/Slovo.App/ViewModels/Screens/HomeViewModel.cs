using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Screens;

/// <summary>What the history rows can do beyond the store: play, copy, transcribe again.</summary>
public interface IHistoryActions
{
    /// <summary>Engines other than <paramref name="except"/> whose models can run now.</summary>
    IReadOnlyList<EngineId> ReadyEngines(EngineId except);
    /// <summary>Transcribes the entry's audio again with another engine and replaces its text. Throws SlovoException.</summary>
    Task RerunAsync(HistoryEntry entry, EngineId engine);
    void Copy(string text);
    /// <summary>Plays the entry's audio; false when it could not.</summary>
    bool Play(HistoryEntry entry);
    void StopPlayback();
    /// <summary>The entry being played, if any.</summary>
    Guid? PlayingId { get; }
    event Action? PlaybackChanged;
}

public sealed record RerunChoice(HistoryRowViewModel Row, EngineId Engine)
{
    public string Title => Loc.Instance.Format("home.action.rerunWith", Engine.DisplayName());
}

public sealed partial class HistoryRowViewModel(HistoryEntry entry, bool hasAudio, bool isPlaying, IReadOnlyList<EngineId> readyEngines) : ViewModelBase
{
    public HistoryEntry Entry { get; } = entry;
    public string Time => Formatting.Time(Entry.Date);
    public string Meta => string.Join(" · ", new[] { Entry.AppName, Entry.EngineId.DisplayName(), Formatting.Clock(Entry.Duration) }
        .Where(part => !string.IsNullOrEmpty(part)));
    public bool HasAudio { get; } = hasAudio;
    public IReadOnlyList<RerunChoice> RerunChoices => readyEngines.Select(engine => new RerunChoice(this, engine)).ToList();
    public bool CanRerun => HasAudio && readyEngines.Count > 0;

    [ObservableProperty] public partial bool IsPlaying { get; set; } = isPlaying;
    [ObservableProperty] public partial bool IsRerunning { get; set; }
}

public sealed record HistoryDayGroup(string Title, IReadOnlyList<HistoryRowViewModel> Rows);

public sealed record StatTile(string Value, string Title, string? Caption);

/// <summary>The Home screen: greeting with the key, statistics, and dictation history.</summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    public const string BannerId = "home.dictionary";

    readonly IHistoryStore history;
    readonly AppSettings settings;
    readonly Func<Insights> insights;
    readonly IHistoryActions actions;
    readonly ISheetHost sheets;
    readonly Action openDictionary;

    public HomeViewModel(IHistoryStore history, AppSettings settings, Func<Insights> insights, IHistoryActions actions,
                         ISheetHost sheets, Action openDictionary, string firstName)
    {
        this.history = history;
        this.settings = settings;
        this.insights = insights;
        this.actions = actions;
        this.sheets = sheets;
        this.openDictionary = openDictionary;
        FirstName = firstName;
        history.PropertyChanged += OnHistoryChanged;
        actions.PlaybackChanged += OnPlaybackChanged;
        settings.Store.Changed += key =>
        {
            if (key == SettingsKey.DictationKey)
            {
                OnPropertyChanged(nameof(KeySymbol));
                OnPropertyChanged(nameof(EmptyMessage));
            }
        };
        Refresh();
    }

    public string FirstName { get; }
    public string Greeting => Loc.Instance.Format("home.greeting", FirstName);
    public string KeySymbol => settings.DictationKey.Symbol();
    public string EmptyMessage => Loc.Instance.Format("home.empty.message", KeySymbol);

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<HistoryDayGroup> Groups { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<StatTile> Stats { get; private set; } = [];

    public bool HasHistory => history.Entries.Count > 0;
    public bool NothingFound => HasHistory && Groups.Count == 0;
    public string NothingFoundMessage => Loc.Instance.Format("home.search.empty.message", Query.Trim());
    public bool IsBannerVisible => !settings.DismissedBanners.Contains(BannerId);

    partial void OnQueryChanged(string value)
    {
        Refresh();
        OnPropertyChanged(nameof(NothingFoundMessage));
    }

    void OnHistoryChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    void OnPlaybackChanged()
    {
        foreach (var row in Groups.SelectMany(g => g.Rows))
        {
            row.IsPlaying = row.Entry.Id == actions.PlayingId;
        }
    }

    void Refresh()
    {
        var loc = Loc.Instance;
        var current = insights();
        Stats =
        [
            new(Formatting.Number(current.TotalWords), loc.Get("home.stats.totalWords"), null),
            new(Formatting.Number((long)Math.Round(current.WordsPerMinute)), loc.Get("home.stats.wpm"), loc.Get("home.stats.wpm.caption")),
            new(Formatting.Number(current.CurrentStreak), loc.Get("home.stats.streak"),
                loc.Format("home.stats.longest", loc.Plural("plural.days", current.LongestStreak))),
        ];
        Groups = history.Entries
            .Where(e => Search.Matches(Query, e.Text, e.AppName))
            .GroupBy(e => e.Date.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryDayGroup(Formatting.DayTitle(g.Key), g.OrderByDescending(e => e.Date)
                .Select(e => new HistoryRowViewModel(e, history.AudioPath(e) is not null, actions.PlayingId == e.Id,
                                                     history.AudioPath(e) is not null ? actions.ReadyEngines(e.EngineId) : []))
                .ToList()))
            .ToList();
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(NothingFound));
    }

    [RelayCommand]
    void TogglePlay(HistoryRowViewModel row)
    {
        if (row.IsPlaying)
        {
            actions.StopPlayback();
            return;
        }
        if (!actions.Play(row.Entry))
        {
            var loc = Loc.Instance;
            sheets.ShowAlert(loc.Get("home.play.failed"), loc.Get("home.play.missing"));
        }
    }

    [RelayCommand]
    void Copy(HistoryRowViewModel row) => actions.Copy(row.Entry.Text);

    [RelayCommand]
    async Task RerunAsync(RerunChoice choice)
    {
        choice.Row.IsRerunning = true;
        try
        {
            await actions.RerunAsync(choice.Row.Entry, choice.Engine);
        }
        catch (Exception error) when (error is Slovo.Core.Model.SlovoException or IOException or InvalidOperationException)
        {
            sheets.ShowAlert(Loc.Instance.Get("home.rerun.failed"), error.Message);
        }
        finally
        {
            choice.Row.IsRerunning = false;
        }
    }

    [RelayCommand]
    void Delete(HistoryRowViewModel row) => history.Delete(row.Entry.Id);

    [RelayCommand]
    void OpenDictionary() => openDictionary();

    [RelayCommand]
    void DismissBanner()
    {
        var dismissed = settings.DismissedBanners.ToHashSet();
        dismissed.Add(BannerId);
        settings.DismissedBanners = dismissed;
        OnPropertyChanged(nameof(IsBannerVisible));
    }
}
