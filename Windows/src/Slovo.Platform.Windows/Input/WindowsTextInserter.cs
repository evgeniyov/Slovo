using System.Runtime.Versioning;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Platform.Windows.Input;

/// <summary>
/// Pastes with Ctrl+V through SendInput (TextInserter.swift). The clipboard work is
/// <see cref="TextInsertion"/>; this adds the Windows parts: the Win32 clipboard, the
/// UIPI and secure-desktop check, and the keystroke that first lets go of any modifier the
/// user still holds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTextInserter : ITextInserter, IDisposable
{
    /// <summary>Some apps (Electron, remote desktops) miss a Ctrl+V shorter than this.</summary>
    public static readonly TimeSpan ChordHold = TimeSpan.FromMilliseconds(60);

    readonly Win32Clipboard clipboard = new();
    readonly TextInsertion insertion;

    public WindowsTextInserter()
    {
        insertion = new TextInsertion(clipboard, SecureInput.IsActive, PasteKeystrokeAsync, TextInsertion.RestoreDelay);
    }

    public Task<InsertionResult> InsertAsync(string text) => insertion.InsertAsync(text);

    public void CopyToClipboard(string text) => insertion.CopyToClipboard(text);

    public void Dispose() => clipboard.Dispose();

    /// <summary>
    /// Ctrl down, V down, a short hold, V up, Ctrl up. A modifier still held — the
    /// dictation key a moment after release, or Ctrl+Alt of "Paste last transcript" — is
    /// released first, so the chord is exactly Ctrl+V and not Ctrl+Alt+V or Win+V.
    /// </summary>
    static async Task<bool> PasteKeystrokeAsync()
    {
        var (press, release) = KeySequence.PasteChord(KeyboardInjector.HeldModifiers());
        if (!KeyboardInjector.Send(press))
        {
            // Whatever did go down must come up again.
            KeyboardInjector.Send(release);
            return false;
        }
        await Task.Delay(ChordHold).ConfigureAwait(false);
        return KeyboardInjector.Send(release);
    }
}
