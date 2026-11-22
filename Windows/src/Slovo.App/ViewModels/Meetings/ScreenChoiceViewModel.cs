using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Meetings;

/// <summary>One line of the screen picker: the whole screen or one running app.</summary>
public sealed record ScreenTargetItem(ScreenCaptureTarget Target, string Title, bool IsChecked)
{
    public bool IsWholeScreen => Target is ScreenCaptureTarget.Display;
}

/// <summary>
/// The small square button next to "New recording" and its pop-up (ScreenCaptureButton and
/// ScreenCapturePopover in MeetingsScreen.swift): whether a new recording also captures the
/// screen and, once on, «Whole screen» or one of the apps running now. The choice is the
/// setting itself, so it holds for this and future meetings, as on macOS.
/// </summary>
public sealed partial class ScreenChoiceViewModel : ViewModelBase
{
    readonly AppSettings settings;
    readonly Func<IReadOnlyList<AppIdentity>> capturableApps;
    /// <summary>App names by id from the last listing, so a chosen app keeps its name while it is not running.</summary>
    readonly Dictionary<string, string> knownNames = new(StringComparer.OrdinalIgnoreCase);

    public ScreenChoiceViewModel(AppSettings settings, Func<IReadOnlyList<AppIdentity>> capturableApps)
    {
        this.settings = settings;
        this.capturableApps = capturableApps;
        settings.Store.Changed += key =>
        {
            if (key is SettingsKey.RecordScreen or SettingsKey.ScreenCaptureApp)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(RaiseChoice);
            }
        };
    }

    /// <summary>Whether a new recording also captures the screen.</summary>
    public bool RecordScreen
    {
        get => settings.RecordScreen;
        set
        {
            if (settings.RecordScreen == value)
            {
                return;
            }
            settings.RecordScreen = value;
            if (value)
            {
                Refresh();
            }
            RaiseChoice();
        }
    }

    /// <summary>«Whole screen» first, then every app with a window, the chosen one checked.</summary>
    [ObservableProperty] public partial IReadOnlyList<ScreenTargetItem> Targets { get; private set; } = [];

    /// <summary>The chosen app's name, or null for the whole screen.</summary>
    public string? ChosenAppName => settings.ScreenCaptureTarget is ScreenCaptureTarget.Application app
        ? knownNames.GetValueOrDefault(app.BundleId) ?? app.BundleId
        : null;

    /// <summary>The button's tooltip: what a new recording will capture.</summary>
    public string ButtonTip
    {
        get
        {
            var loc = Loc.Instance;
            if (!RecordScreen)
            {
                return loc.Get("meetings.recordScreen");
            }
            return loc.Get("meetings.recordScreen.title") + " · " + (ChosenAppName ?? loc.Get("meetings.recordScreen.wholeScreen"));
        }
    }

    /// <summary>
    /// Lists the apps again: built fresh every time the pop-up opens, so a just-launched app
    /// shows up without restarting anything. A chosen app that is not running stays listed.
    /// </summary>
    public void Refresh()
    {
        var chosen = settings.ScreenCaptureTarget;
        var items = new List<ScreenTargetItem>
        {
            new(ScreenCaptureTarget.WholeDisplay, Loc.Instance.Get("meetings.recordScreen.wholeScreen"), chosen is ScreenCaptureTarget.Display),
        };
        IReadOnlyList<AppIdentity> apps;
        try
        {
            apps = capturableApps();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Slovo.Core.Logging.Log.For("Meetings").Error("Listing apps failed", error);
            apps = [];
        }
        foreach (var app in apps)
        {
            knownNames[app.BundleId] = app.Name;
            items.Add(new ScreenTargetItem(new ScreenCaptureTarget.Application(app.BundleId), app.Name,
                chosen is ScreenCaptureTarget.Application current && string.Equals(current.BundleId, app.BundleId, StringComparison.OrdinalIgnoreCase)));
        }
        if (chosen is ScreenCaptureTarget.Application missing
            && !apps.Any(app => string.Equals(app.BundleId, missing.BundleId, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(1, new ScreenTargetItem(missing, knownNames.GetValueOrDefault(missing.BundleId) ?? missing.BundleId, true));
        }
        Targets = items;
        RaiseChoice();
    }

    [RelayCommand]
    void Choose(ScreenTargetItem? item)
    {
        if (item is null)
        {
            return;
        }
        settings.ScreenCaptureTarget = item.Target;
        Targets = Targets.Select(existing => existing with { IsChecked = existing.Target == item.Target }).ToList();
        RaiseChoice();
    }

    void RaiseChoice()
    {
        OnPropertyChanged(nameof(RecordScreen));
        OnPropertyChanged(nameof(ChosenAppName));
        OnPropertyChanged(nameof(ButtonTip));
    }
}
