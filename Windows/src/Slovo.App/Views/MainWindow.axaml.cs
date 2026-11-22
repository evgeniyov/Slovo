using Avalonia.Controls;
using Avalonia.Input;
using Slovo.App.ViewModels;

namespace Slovo.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // The microphone switch changes in Windows Settings while Slovo waits; look again on return.
        Activated += (_, _) => (DataContext as MainWindowViewModel)?.Readiness?.Refresh();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is MainWindowViewModel { HasSheet: true } model)
            {
                model.CloseSheet();
                e.Handled = true;
            }
        };
    }

    /// <summary>A click on the dimmed area outside the sheet closes it, like Esc.</summary>
    void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is MainWindowViewModel model)
        {
            model.CloseSheet();
        }
    }
}
