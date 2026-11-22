using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Slovo.App.ViewModels;

/// <summary>What the shell asks the app to do (opening sheets, folders, onboarding).</summary>
public interface IShellActions
{
    void OpenSettings(string? section = null);
    void ShowShortcuts();
    void ShowTroubleshooting();
    void OpenModelsFolder();
    void OpenLogs();
    void ShowOnboarding();
}

/// <summary>The main window: sidebar selection, the screen it shows, and sheets over it.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, ISheetHost
{
    readonly Func<Screen, ISheetHost, ViewModelBase> screenFactory;
    readonly Dictionary<Screen, ViewModelBase> screens = [];
    readonly IShellActions actions;

    public MainWindowViewModel(Func<Screen, ISheetHost, ViewModelBase> screenFactory, IShellActions actions, ReadinessViewModel? readiness)
    {
        this.screenFactory = screenFactory;
        this.actions = actions;
        Readiness = readiness;
        SidebarItems = ScreenInfo.All.Select(screen => new SidebarItemViewModel(screen)).ToList();
        Select(Screen.Home);
    }

    public IReadOnlyList<SidebarItemViewModel> SidebarItems { get; }

    /// <summary>The sidebar status card; null in tests that do not need it.</summary>
    public ReadinessViewModel? Readiness { get; }

    [ObservableProperty] public partial Screen SelectedScreen { get; private set; }

    [ObservableProperty] public partial ViewModelBase? CurrentScreen { get; private set; }

    /// <summary>The sheet over the window, if any.</summary>
    [ObservableProperty] public partial SheetViewModel? Sheet { get; private set; }

    public bool HasSheet => Sheet is not null;

    partial void OnSheetChanged(SheetViewModel? value) => OnPropertyChanged(nameof(HasSheet));

    public void ShowSheet(SheetViewModel sheet)
    {
        (Sheet as ConfirmSheetViewModel)?.Dismissed();
        sheet.Host = this;
        Sheet = sheet;
    }

    public void CloseSheet()
    {
        (Sheet as ConfirmSheetViewModel)?.Dismissed();
        Sheet = null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = true)
    {
        var sheet = new ConfirmSheetViewModel(title, message, confirmText, destructive);
        ShowSheet(sheet);
        return sheet.Result;
    }

    public void ShowAlert(string title, string message) => ShowSheet(new AlertSheetViewModel(title, message));

    [RelayCommand]
    public void Select(Screen screen)
    {
        SelectedScreen = screen;
        foreach (var item in SidebarItems)
        {
            item.IsSelected = item.Screen == screen;
        }
        if (!screens.TryGetValue(screen, out var model))
        {
            model = screenFactory(screen, this);
            screens[screen] = model;
        }
        CurrentScreen = model;
    }

    /// <summary>The screen's view model when it has been created.</summary>
    public T? Existing<T>(Screen screen) where T : ViewModelBase =>
        screens.TryGetValue(screen, out var model) ? model as T : null;

    [RelayCommand]
    void OpenSettings() => actions.OpenSettings();

    [RelayCommand]
    void ShowShortcuts() => actions.ShowShortcuts();

    [RelayCommand]
    void ShowTroubleshooting() => actions.ShowTroubleshooting();

    [RelayCommand]
    void OpenModelsFolder() => actions.OpenModelsFolder();

    [RelayCommand]
    void OpenLogs() => actions.OpenLogs();

    [RelayCommand]
    void ShowOnboarding() => actions.ShowOnboarding();
}
