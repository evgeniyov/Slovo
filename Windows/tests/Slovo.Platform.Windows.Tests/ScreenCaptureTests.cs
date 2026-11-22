using System.Runtime.InteropServices;
using Slovo.Core.Model;
using Slovo.Platform.Windows.Interop;
using Slovo.Platform.Windows.Screen;
using Slovo.Platform.Windows.Shell;

namespace Slovo.Platform.Windows.Tests;

/// <summary>
/// The pure parts of Windows screen recording: which windows make up the app picker, which
/// window of an app is recorded, which process tree plays its sound, the structures handed to
/// Direct3D, Media Foundation and the process list, and what the shell may open.
/// </summary>
public unsafe class ScreenCaptureTests
{
    static WindowCandidate Window(nint handle, string bundleId, string name, bool minimized = false, uint process = 10) =>
        new(handle, process, bundleId, name, 800, 600, minimized);

    [Fact]
    public void ThePickerListsEachAppOnceSortedByName()
    {
        var apps = CaptureTargets.Apps([
            Window(1, "zoom.exe", "Zoom"),
            Window(2, "ms-teams.exe", "Microsoft Teams"),
            Window(3, "zoom.exe", "Zoom"),
            Window(4, "chrome.exe", "Google Chrome"),
        ]);
        Assert.Equal(["Google Chrome", "Microsoft Teams", "Zoom"], apps.Select(app => app.Name));
        Assert.Equal(new AppIdentity("zoom.exe", "Zoom"), apps[^1]);
        Assert.Empty(CaptureTargets.Apps([]));
    }

    [Fact]
    public void AnAppIsRecordedThroughItsFrontmostWindowThatIsNotMinimized()
    {
        // The window list is in z-order, frontmost first.
        WindowCandidate[] windows =
        [
            Window(1, "chrome.exe", "Chrome"),
            Window(2, "zoom.exe", "Zoom", minimized: true),
            Window(3, "zoom.exe", "Zoom"),
            Window(4, "zoom.exe", "Zoom"),
        ];
        Assert.Equal(3, CaptureTargets.BestWindow(windows, "zoom.exe")!.Handle);
        Assert.Equal(3, CaptureTargets.BestWindow(windows, "ZOOM.EXE")!.Handle);
        // Only minimized windows: the frontmost of them.
        Assert.Equal(2, CaptureTargets.BestWindow([windows[1], Window(5, "zoom.exe", "Zoom", minimized: true)], "zoom.exe")!.Handle);
        Assert.Null(CaptureTargets.BestWindow(windows, "teams.exe"));
    }

    [Fact]
    public void SmallWindowsAndTheShellAreLeftOut()
    {
        Assert.True(CaptureTargets.IsLargeEnough(800, 600, isMinimized: false));
        Assert.False(CaptureTargets.IsLargeEnough(100, 600, isMinimized: false));
        Assert.False(CaptureTargets.IsLargeEnough(800, 40, isMinimized: false));
        // A minimized window reports a token size; it still counts.
        Assert.True(CaptureTargets.IsLargeEnough(160, 28, isMinimized: true));
        Assert.True(CaptureTargets.IsShellWindowClass("Shell_TrayWnd"));
        Assert.True(CaptureTargets.IsShellWindowClass("Progman"));
        Assert.False(CaptureTargets.IsShellWindowClass("CabinetWClass"));
    }

    [Fact]
    public void AnAppsSoundComesFromTheTopOfItsOwnProcessTree()
    {
        var processes = new Dictionary<uint, (uint Parent, string Executable)>
        {
            [1] = (0, "explorer.exe"),
            [10] = (1, "chrome.exe"),
            [11] = (10, "chrome.exe"),
            [12] = (11, "chrome.exe"),
            [20] = (1, "ms-teams.exe"),
            [21] = (20, "msedgewebview2.exe"),
            // A loop in the parent ids (a reused id) must not hang the walk.
            [30] = (31, "loop.exe"),
            [31] = (30, "loop.exe"),
        };
        Assert.Equal(10u, CaptureTargets.RootProcess(12, processes));
        Assert.Equal(10u, CaptureTargets.RootProcess(10, processes));
        // A helper of another name is below the app, not the app itself.
        Assert.Equal(21u, CaptureTargets.RootProcess(21, processes));
        Assert.Equal(20u, CaptureTargets.RootProcess(20, processes));
        Assert.Contains(CaptureTargets.RootProcess(30, processes), new uint[] { 30, 31 });
        Assert.Equal(99u, CaptureTargets.RootProcess(99, processes));
        Assert.Equal(10u, CaptureTargets.AnyProcess("chrome.exe", processes));
        Assert.Null(CaptureTargets.AnyProcess("zoom.exe", processes));
    }

    [Fact]
    public void StructuresMatchTheSdkHeaders()
    {
        Assert.Equal(8, sizeof(Point));
        Assert.Equal(16, sizeof(Rect));
        Assert.Equal(40, sizeof(MonitorInfo));
        Assert.Equal(8, sizeof(SizeInt32));
        Assert.Equal(44, sizeof(Texture2DDescription));
        Assert.Equal(16, sizeof(MappedSubresource));
        Assert.Equal(24, sizeof(Box3D));
        Assert.Equal(12, sizeof(MftOutputStreamInfo));
        Assert.Equal(32, sizeof(MftOutputDataBuffer));
        Assert.Equal(8, Marshal.OffsetOf<MftOutputDataBuffer>(nameof(MftOutputDataBuffer.Sample)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<MftOutputDataBuffer>(nameof(MftOutputDataBuffer.Status)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<MftOutputDataBuffer>(nameof(MftOutputDataBuffer.Events)).ToInt32());
        // PROCESSENTRY32W on 64-bit Windows.
        Assert.Equal(568, sizeof(ProcessEntry32));
        var entry = default(ProcessEntry32);
        var start = (byte*)&entry;
        Assert.Equal(8, (byte*)&entry.ProcessId - start);
        Assert.Equal(32, (byte*)&entry.ParentProcessId - start);
        Assert.Equal(44, (byte*)entry.ExeFile - start);
        // VT_UI4 for ICodecAPI values.
        Assert.Equal(19, Variant.UInt32);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Slovo\Meetings\A\screen.mov", true)]
    [InlineData(@"C:\Videos\clip.MP4", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Slovo\Meetings\A\meeting.json", false)]
    [InlineData(@"C:\Windows\System32\calc.exe", false)]
    [InlineData(@"C:\screen.mov.cmd", false)]
    public void TheShellOpensRecordingsOnly(string path, bool openable) => Assert.Equal(openable, ShellLinks.IsOpenableFile(path));
}
