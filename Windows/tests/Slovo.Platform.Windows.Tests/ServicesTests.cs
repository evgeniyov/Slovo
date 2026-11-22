using Slovo.Core.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Platform.Windows.Inert;
using Slovo.Platform.Windows.Threading;

namespace Slovo.Platform.Windows.Tests;

public class WorkerQueueTests
{
    [Fact]
    public void RunsInOrderOnItsOwnThread()
    {
        using var queue = new WorkerQueue("test queue");
        var order = new List<int>();
        for (var i = 0; i < 100; i++)
        {
            var value = i;
            queue.Post(() => order.Add(value));
        }
        var thread = queue.Invoke(() => Environment.CurrentManagedThreadId, TimeSpan.FromSeconds(5), -1);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
        Assert.Equal(Enumerable.Range(0, 100), order);
    }

    [Fact]
    public void InvokeRethrowsAndTimesOut()
    {
        using var queue = new WorkerQueue("test queue");
        Assert.Throws<InvalidOperationException>(() =>
            queue.Invoke<int>(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), 0));
        using var release = new ManualResetEventSlim();
        queue.Post(() => release.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(-1, queue.Invoke(() => 1, TimeSpan.FromMilliseconds(50), -1));
        release.Set();
        Assert.Equal(2, queue.Invoke(() => 2, TimeSpan.FromSeconds(5), -1));
    }

    [Fact]
    public void PostAfterWaits()
    {
        using var queue = new WorkerQueue("test queue");
        using var done = new ManualResetEventSlim();
        var started = Environment.TickCount64;
        queue.PostAfter(TimeSpan.FromMilliseconds(80), done.Set);
        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(Environment.TickCount64 - started >= 70);
    }

    [Fact]
    public void NothingRunsAfterDispose()
    {
        var queue = new WorkerQueue("test queue");
        queue.Dispose();
        var ran = false;
        queue.Post(() => ran = true);
        queue.PostAfter(TimeSpan.Zero, () => ran = true);
        Thread.Sleep(50);
        Assert.False(ran);
    }
}

public class InertServicesTests
{
    [Fact]
    public async Task StandInsSayPlainlyThatNothingHappens()
    {
        using var services = PlatformServices.CreateInert(ImmediateMainThread.Shared, SystemUptime.Shared);
        Assert.True(services.IsInert);

        var failure = Assert.Throws<SlovoException>(() => services.Recorder.Start(null));
        Assert.Equal(CoreStrings.Get("audio.error.unavailableHere"), failure.Message);
        Assert.False(services.Recorder.IsRecording);
        Assert.Equal(RecordedAudio.Empty, services.Recorder.Stop());

        Assert.Empty(services.AudioDevices.InputDevices());
        Assert.False(services.Hotkey.Start(new HotkeyConfiguration()));
        Assert.NotNull(services.Shortcuts.Register(HotkeyShortcut.PasteLastDefault, () => { }));
        Assert.Equal(new InsertionResult.Copied(CopyReason.PasteFailed), await services.TextInserter.InsertAsync("text"));
        Assert.Equal(InsertionResult.EmptyResult, await services.TextInserter.InsertAsync(" "));
        Assert.Null(services.ForegroundApp.CurrentApp());
        Assert.Null(services.FocusedInput.IsTextInputFocused());
        Assert.Equal(PermissionStatus.Granted, services.Permissions.Status(PermissionKind.Microphone));
        Assert.False(services.Permissions.IsSecureInputActive);
        Assert.False(services.Autostart.SetEnabled(true));
        Assert.Null(services.MeetingAudio.SystemAudio());
        Assert.Throws<SlovoException>(() => services.MeetingAudio.Microphone(null).Start((_, _) => { }));
        Assert.False(services.Decoder.CanDecode("clip.m4a"));
    }

    [Fact]
    public void CurrentSystemGetsTheStandInsOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var services = PlatformServices.CreateForCurrentSystem(ImmediateMainThread.Shared, SystemUptime.Shared);
        Assert.True(services.IsInert);
    }
}

public class WindowsStringsTests
{
    static readonly string[] Keys =
    [
        "audio.error.microphoneLost",
        "audio.error.systemAudioLost",
        "audio.error.noOutputDevice",
        "audio.error.unavailableHere",
        "input.error.hookFailed",
        "input.warning.elevatedWindow",
    ];

    /// <summary>The messages the Windows layer shows, besides the shared ones it reuses.</summary>
    static readonly string[] SharedKeys =
    [
        "audio.error.noInputDevice",
        "audio.error.engineStartFailed",
        "audio.error.formatUnsupported",
        "audio.error.readFailed",
        "meetings.error.noMicrophone",
        "meetings.error.microphoneFailed",
        "meetings.error.tapFailed",
    ];

    [Fact]
    public void WindowsKeysExistInBothLanguages()
    {
        var english = CoreStrings.TableFor("en");
        var russian = CoreStrings.TableFor("ru");
        foreach (var key in Keys.Concat(SharedKeys))
        {
            Assert.True(english.Contains(key), key);
            Assert.True(russian.Contains(key), key);
            Assert.NotEqual(english.Get(key), russian.Get(key));
        }
    }

    [Fact]
    public void NoMessageTheWindowsLayerShowsNamesMacOs()
    {
        foreach (var code in new[] { "en", "ru" })
        {
            var table = CoreStrings.TableFor(code);
            foreach (var key in Keys.Concat(SharedKeys))
            {
                Assert.DoesNotContain("macOS", table.Get(key), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("System Settings", table.Get(key), StringComparison.Ordinal);
                Assert.DoesNotContain("⌘", table.Get(key), StringComparison.Ordinal);
            }
        }
    }
}
