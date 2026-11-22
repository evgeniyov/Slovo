using CommunityToolkit.Mvvm.ComponentModel;
using Slovo.App.Localization;

namespace Slovo.App.ViewModels;

public sealed partial class SidebarItemViewModel : ViewModelBase
{
    public SidebarItemViewModel(Screen screen)
    {
        Screen = screen;
        Loc.Instance.LanguageChanged += () => OnPropertyChanged(nameof(Title));
    }

    public Screen Screen { get; }
    public string Title => Loc.Instance.Get(Screen.TitleKey());
    public string IconKey => Screen.IconKey();

    [ObservableProperty] public partial bool IsSelected { get; set; }
}
