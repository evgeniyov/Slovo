using Avalonia.Controls;
using Avalonia.Input;
using Slovo.App.ViewModels.Screens;

namespace Slovo.App.Views.Screens;

public partial class DictionaryView : UserControl
{
    public DictionaryView() => InitializeComponent();

    void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: DictionaryRowViewModel row } && DataContext is DictionaryViewModel model)
        {
            model.EditCommand.Execute(row);
        }
    }
}
