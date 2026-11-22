using Slovo.Core.Input;
using Slovo.Core.Model;
using Slovo.Platform.Windows.Input;
using Slovo.Platform.Windows.Shell;

namespace Slovo.Platform.Windows.Tests;

public class KeySequenceTests
{
    static KeyStroke Down(int key, bool extended = false) => new(key, Up: false, extended);
    static KeyStroke Up(int key, bool extended = false) => new(key, Up: true, extended);

    [Fact]
    public void PlainPasteIsCtrlVAroundAHold()
    {
        var (press, release) = KeySequence.PasteChord([]);
        Assert.Equal([Down(VirtualKey.LeftCtrl), Down(VirtualKey.V)], press);
        Assert.Equal([Up(VirtualKey.V), Up(VirtualKey.LeftCtrl)], release);
    }

    /// <summary>"Paste last transcript" runs while Ctrl+Alt are still held: Alt goes first, behind the mask key.</summary>
    [Fact]
    public void HeldAltIsMaskedAndReleasedFirst()
    {
        var (press, _) = KeySequence.PasteChord([VirtualKey.LeftCtrl, VirtualKey.LeftAlt]);
        Assert.Equal(
        [
            Down(HotkeyKeys.MenuMaskKey), Up(HotkeyKeys.MenuMaskKey),
            Up(VirtualKey.LeftAlt), Up(VirtualKey.LeftCtrl),
            Down(VirtualKey.LeftCtrl), Down(VirtualKey.V),
        ], press);
    }

    /// <summary>Win+V would open clipboard history instead of pasting.</summary>
    [Fact]
    public void HeldWinIsReleasedWithTheExtendedFlag()
    {
        var (press, _) = KeySequence.PasteChord([VirtualKey.LeftWin, VirtualKey.RightCtrl]);
        Assert.Equal(
        [
            Down(HotkeyKeys.MenuMaskKey), Up(HotkeyKeys.MenuMaskKey),
            Up(VirtualKey.LeftWin, extended: true), Up(VirtualKey.RightCtrl, extended: true),
            Down(VirtualKey.LeftCtrl), Down(VirtualKey.V),
        ], press);
    }

    [Fact]
    public void ShiftAloneNeedsNoMask()
    {
        var (press, _) = KeySequence.PasteChord([VirtualKey.RightShift]);
        Assert.Equal([Up(VirtualKey.RightShift), Down(VirtualKey.LeftCtrl), Down(VirtualKey.V)], press);
    }

    [Fact]
    public void NonModifiersAreNotReleased()
    {
        var (press, _) = KeySequence.PasteChord([VirtualKey.V, VirtualKey.Escape]);
        Assert.Equal([Down(VirtualKey.LeftCtrl), Down(VirtualKey.V)], press);
    }

    [Fact]
    public void ExtendedKeys()
    {
        Assert.True(KeySequence.IsExtended(VirtualKey.RightCtrl));
        Assert.True(KeySequence.IsExtended(VirtualKey.RightAlt));
        Assert.True(KeySequence.IsExtended(VirtualKey.LeftWin));
        Assert.True(KeySequence.IsExtended(0x25));
        Assert.True(KeySequence.IsExtended(0x2E));
        Assert.False(KeySequence.IsExtended(VirtualKey.LeftCtrl));
        Assert.False(KeySequence.IsExtended(VirtualKey.LeftAlt));
        Assert.False(KeySequence.IsExtended(VirtualKey.V));
        Assert.False(KeySequence.IsExtended(HotkeyKeys.MenuMaskKey));
    }

    [Fact]
    public void InjectedKeysCarryTheMarker()
    {
        var input = KeySequence.KeyInput(VirtualKey.RightCtrl, 0x1D, keyUp: true, extended: true);
        Assert.Equal(1u, input.Type);
        Assert.Equal(VirtualKey.RightCtrl, input.Data.Keyboard.VirtualKey);
        Assert.Equal(0x1D, input.Data.Keyboard.ScanCode);
        Assert.Equal(0x0003u, input.Data.Keyboard.Flags);
        Assert.Equal((nuint)0x534C_564F, input.Data.Keyboard.ExtraInfo);
        Assert.Equal(0u, KeySequence.KeyInput(VirtualKey.V, 0x2F, keyUp: false, extended: false).Data.Keyboard.Flags);
    }
}

public class ShortcutModifiersTests
{
    [Fact]
    public void MapsToRegisterHotKeyFlagsWithoutRepeat()
    {
        Assert.Equal(0x4000u | 0x2 | 0x1, ShortcutModifiers.ToWin32(HotkeyModifiers.Control | HotkeyModifiers.Alt));
        Assert.Equal(0x4000u | 0x2 | 0x4, ShortcutModifiers.ToWin32(HotkeyModifiers.Control | HotkeyModifiers.Shift));
        Assert.Equal(0x4000u | 0x2 | 0x8, ShortcutModifiers.ToWin32(HotkeyModifiers.Control | HotkeyModifiers.Win));
        Assert.Equal(0x4000u, ShortcutModifiers.ToWin32(HotkeyModifiers.None));
    }

    [Fact]
    public void DefaultShortcutsUseLetterKeys()
    {
        Assert.Equal(0x56, HotkeyShortcut.PasteLastDefault.KeyCode);
        Assert.Equal("Ctrl+Alt+V", HotkeyShortcut.PasteLastDefault.DisplayText);
    }
}

public class SecureInputRuleTests
{
    [Fact]
    public void ElevatedWindowsAndTheSecureDesktopBlockInput()
    {
        var medium = SecureInputRule.MediumIntegrity;
        Assert.True(SecureInputRule.IsBlocked(inputDesktopIsDefault: false, medium, false, medium));
        Assert.True(SecureInputRule.IsBlocked(true, SecureInputRule.HighIntegrity, false, medium));
        Assert.True(SecureInputRule.IsBlocked(true, SecureInputRule.SystemIntegrity, false, medium));
        Assert.False(SecureInputRule.IsBlocked(true, medium, false, medium));
        // Store apps and browsers' sandboxes run lower than Slovo; input reaches them.
        Assert.False(SecureInputRule.IsBlocked(true, 0x1000, false, medium));
        // Slovo run as administrator reaches elevated windows too.
        Assert.False(SecureInputRule.IsBlocked(true, SecureInputRule.HighIntegrity, false, SecureInputRule.HighIntegrity));
        // A process Slovo may not even query is above it; other failures say nothing.
        Assert.True(SecureInputRule.IsBlocked(true, null, foregroundAccessDenied: true, medium));
        Assert.False(SecureInputRule.IsBlocked(true, null, foregroundAccessDenied: false, medium));
    }
}

public class ForegroundAppIdentityTests
{
    [Fact]
    public void SlovoAndNamelessProcessesAreIgnored()
    {
        Assert.Null(ForegroundAppIdentity.Identity(@"C:\Program Files\Slovo\Slovo.exe", "Slovo", "Slovo", isOwnProcess: true));
        Assert.Null(ForegroundAppIdentity.Identity(null, "Nameless", null, isOwnProcess: false));
        Assert.Null(ForegroundAppIdentity.Identity("  ", null, null, isOwnProcess: false));
    }

    [Fact]
    public void TheIdIsTheLowerCaseExecutableAndTheNameItsDescription()
    {
        Assert.Equal(new AppIdentity("telegram.exe", "Telegram Desktop"),
            ForegroundAppIdentity.Identity(@"C:\Users\me\AppData\Roaming\Telegram Desktop\Telegram.exe", "Telegram Desktop", "Telegram", false));
        Assert.Equal(new AppIdentity("code.exe", "Visual Studio Code"),
            ForegroundAppIdentity.Identity(@"C:\Program Files\Microsoft VS Code\Code.exe", " ", "Visual Studio Code", false));
        Assert.Equal(new AppIdentity("tool.exe", "tool"), ForegroundAppIdentity.Identity(@"D:\bin\tool.EXE", null, "", false) is { } identity
            ? identity with { Name = identity.Name.ToLowerInvariant() }
            : null);
        Assert.Equal(new AppIdentity("applicationframehost.exe", "ApplicationFrameHost"),
            ForegroundAppIdentity.Identity(@"C:\Windows\System32\ApplicationFrameHost.exe", null, null, false));
        Assert.Equal("Code.exe", ForegroundAppIdentity.FileName(@"C:\Program Files\Microsoft VS Code\Code.exe"));
        Assert.Equal("a.exe", ForegroundAppIdentity.FileName("a.exe"));
    }
}

public class PermissionTests
{
    [Fact]
    public void AnyApplicableDenyDeniesTheMicrophone()
    {
        Assert.Equal(PermissionStatus.Granted, MicrophoneConsent.Decide(null, null, null));
        Assert.Equal(PermissionStatus.Granted, MicrophoneConsent.Decide("Allow", "Allow", "Allow", "Allow"));
        Assert.Equal(PermissionStatus.Denied, MicrophoneConsent.Decide("Deny", "Allow", "Allow"));
        Assert.Equal(PermissionStatus.Denied, MicrophoneConsent.Decide("Allow", "Deny", null));
        Assert.Equal(PermissionStatus.Denied, MicrophoneConsent.Decide(null, "Allow", "deny"));
        Assert.Equal(PermissionStatus.Denied, MicrophoneConsent.Decide(null, null, null, "Deny"));
        Assert.Equal(PermissionStatus.Granted, MicrophoneConsent.Decide("Prompt", null, null));
    }

    [Fact]
    public void SettingsLinksPointAtWindowsSettings()
    {
        foreach (var kind in Enum.GetValues<PermissionKind>())
        {
            Assert.StartsWith("ms-settings:", MicrophoneConsent.SettingsUri(kind));
        }
        Assert.Equal("ms-settings:privacy-microphone", MicrophoneConsent.SettingsUri(PermissionKind.Microphone));
    }
}

public class ShellTests
{
    [Fact]
    public void AutostartRunsTheQuotedExecutableUnlessTaskManagerTurnedItOff()
    {
        Assert.Equal("\"C:\\Program Files\\Slovo\\Slovo.exe\" --autostart", AutostartEntry.Command(@"C:\Program Files\Slovo\Slovo.exe"));
        Assert.True(AutostartEntry.IsApproved(null));
        Assert.True(AutostartEntry.IsApproved([]));
        Assert.True(AutostartEntry.IsApproved([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(AutostartEntry.IsApproved([0x06, 0, 0, 0]));
        Assert.False(AutostartEntry.IsApproved([0x03, 0, 0, 0, 0x5A, 0x11, 0, 0, 0, 0, 0, 0]));
        Assert.False(AutostartEntry.IsApproved([0x07]));
    }

    [Fact]
    public void OnlyWebMailAndSettingsLinksOpen()
    {
        Assert.True(ShellLinks.IsOpenable("https://claude.ai/download"));
        Assert.True(ShellLinks.IsOpenable("mailto:someone@example.com"));
        Assert.True(ShellLinks.IsOpenable("ms-settings:privacy-microphone"));
        Assert.False(ShellLinks.IsOpenable(@"C:\Windows\System32\cmd.exe"));
        Assert.False(ShellLinks.IsOpenable("file:///C:/Windows/System32/calc.exe"));
        Assert.False(ShellLinks.IsOpenable("not a link"));
        Assert.Equal("/select,\"C:\\a b\\c.txt\"", ShellLinks.RevealArguments(@"C:\a b\c.txt"));
    }
}
