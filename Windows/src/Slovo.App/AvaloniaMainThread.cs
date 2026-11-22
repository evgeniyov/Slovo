using Avalonia.Threading;
using Slovo.Core.Platform;

namespace Slovo.App;

/// <summary>The UI thread for services that report back to it.</summary>
public sealed class AvaloniaMainThread : IMainThread
{
    public static readonly AvaloniaMainThread Shared = new();

    public bool IsMainThread => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
