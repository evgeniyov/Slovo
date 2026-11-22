using Avalonia.Threading;
using Slovo.Core.Logging;
using Slovo.Core.Platform;

namespace Slovo.App.Composition;

/// <summary>
/// Asks, a few times a second and off the interface thread, whether keyboard focus is in a
/// text field, so the idle Flow bar shows only where dictating makes sense. UI Automation
/// can hang on an unresponsive app; a hung query only delays the next one.
/// </summary>
sealed class FocusWatcher(IFocusedInput focus, Func<bool> wanted, Action<bool?> changed) : IDisposable
{
    static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(700);
    readonly CancellationTokenSource stop = new();
    bool? last;
    bool started;

    public void Start()
    {
        if (started)
        {
            return;
        }
        started = true;
        _ = Task.Run(LoopAsync);
    }

    async Task LoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (await Dispatcher.UIThread.InvokeAsync(wanted))
                {
                    var now = focus.IsTextInputFocused();
                    if (now != last)
                    {
                        last = now;
                        Dispatcher.UIThread.Post(() => changed(now));
                    }
                }
                await Task.Delay(Interval, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                Log.For("Focus").Warning("Focus check failed: " + error.Message);
                try
                {
                    await Task.Delay(Interval * 4, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}
