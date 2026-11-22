using Avalonia.Controls;
using Avalonia.Input;
using Slovo.App.ViewModels.Screens;
using Slovo.Core.Model;

namespace Slovo.App.Views.Screens;

public partial class SnippetsView : UserControl
{
    public SnippetsView() => InitializeComponent();

    void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: Snippet snippet } && DataContext is SnippetsViewModel model)
        {
            model.EditCommand.Execute(snippet);
        }
    }
}
