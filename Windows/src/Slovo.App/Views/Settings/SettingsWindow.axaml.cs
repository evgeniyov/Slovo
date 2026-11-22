using Avalonia.Controls;
using Slovo.App.ViewModels.Settings;

namespace Slovo.App.Views.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel model)
            {
                model.CloseRequested += Close;
            }
        };
    }
}
