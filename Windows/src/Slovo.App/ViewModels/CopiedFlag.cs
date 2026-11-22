using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Slovo.App.ViewModels;

/// <summary>
/// A Copy button's moment of "Copied" (the macOS <c>justCopied</c>): on for a little over a
/// second after a copy, then back to Copy. Copying again starts the moment over.
/// </summary>
public sealed partial class CopiedFlag : ViewModelBase
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.2);

    int flashes;

    [ObservableProperty] public partial bool IsOn { get; private set; }

    public void Flash()
    {
        var flash = ++flashes;
        IsOn = true;
        _ = Task.Delay(Duration).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            // A later copy keeps it on for its own moment.
            if (flash == flashes)
            {
                IsOn = false;
            }
        }), TaskScheduler.Default);
    }
}
