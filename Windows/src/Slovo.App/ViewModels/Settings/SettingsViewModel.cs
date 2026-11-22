using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;

namespace Slovo.App.ViewModels.Settings;

public sealed partial class SettingsSectionItem : ViewModelBase
{
    public SettingsSectionItem(SettingsSection section)
    {
        Section = section;
        Loc.Instance.LanguageChanged += () => OnPropertyChanged(nameof(Title));
    }

    public SettingsSection Section { get; }
    public string Title => Loc.Instance.Get(Section.TitleKey());
    public string IconKey => Section.IconKey();

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>The Settings window: sections on the left, the chosen section on the right.</summary>
public sealed partial class SettingsViewModel : ViewModelBase, ISheetHost
{
    readonly Func<SettingsSection, ISheetHost, ViewModelBase> factory;

    public SettingsViewModel(Func<SettingsSection, ISheetHost, ViewModelBase> factory, SettingsSection initial = SettingsSection.General)
    {
        this.factory = factory;
        Sections = SettingsSections.All.Select(s => new SettingsSectionItem(s)).ToList();
        Select(initial);
        // Choice titles are built in the section's language; rebuild the section when it changes.
        Loc.Instance.LanguageChanged += () => Select(Selected);
    }

    public IReadOnlyList<SettingsSectionItem> Sections { get; }

    [ObservableProperty] public partial SettingsSection Selected { get; private set; }
    [ObservableProperty] public partial ViewModelBase? Current { get; private set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";

    /// <summary>An error to show inside the Settings window (the macOS settingsAlert).</summary>
    [ObservableProperty] public partial string? Alert { get; set; }

    public event Action? CloseRequested;

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

    /// <summary>Errors raised inside Settings show inside Settings.</summary>
    public void ShowAlert(string title, string message) => Alert = string.IsNullOrEmpty(message) ? title : $"{title}: {message}";

    [RelayCommand]
    public void Select(SettingsSection section)
    {
        (Current as GeneralSettingsViewModel)?.Detach();
        Selected = section;
        foreach (var item in Sections)
        {
            item.IsSelected = item.Section == section;
        }
        Title = Loc.Instance.Get(section.TitleKey());
        Current = factory(section, this);
    }

    [RelayCommand]
    void Close()
    {
        (Current as GeneralSettingsViewModel)?.Detach();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    void DismissAlert() => Alert = null;
}
