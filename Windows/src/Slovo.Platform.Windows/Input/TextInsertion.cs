using Slovo.Core.Logging;
using Slovo.Core.Model;

namespace Slovo.Platform.Windows.Input;

/// <summary>One clipboard format and its bytes, as the previous owner left them.</summary>
public sealed record ClipboardItem(uint Format, byte[] Data);

/// <summary>What was on the clipboard before Slovo wrote to it.</summary>
public sealed record ClipboardSnapshot(IReadOnlyList<ClipboardItem> Items)
{
    public static readonly ClipboardSnapshot Empty = new([]);
}

/// <summary>The clipboard as text insertion uses it; the Win32 one on Windows, a fake in tests.</summary>
public interface IClipboardBackend
{
    /// <summary>GetClipboardSequenceNumber: changes whenever anyone changes the clipboard.</summary>
    uint SequenceNumber { get; }

    /// <summary>The current contents, or null when the clipboard could not be opened.</summary>
    ClipboardSnapshot? Snapshot();

    /// <summary>
    /// Replaces the contents with the text, marked so clipboard history and cloud sync skip
    /// it. Returns the sequence number the clipboard has afterwards, or null on failure.
    /// </summary>
    uint? WriteText(string text);

    /// <summary>Puts a snapshot back, but only while the sequence number is still <paramref name="expectedSequence"/>.</summary>
    bool RestoreIfUnchanged(ClipboardSnapshot snapshot, uint expectedSequence);
}

/// <summary>
/// Pastes text into the focused field (TextInserter.swift): saves the clipboard, writes the
/// text with the markers that keep it out of clipboard history, posts Ctrl+V, then restores
/// the previous contents after <see cref="RestoreDelay"/> if the clipboard still holds
/// what Slovo wrote. Falls back to copying when input would not reach the foreground
/// window.
/// </summary>
/// <remarks>
/// Inserts are serialized: the next one waits for the one in progress, so a second paste
/// never replaces the clipboard while the target app may still be reading the first. The
/// delayed restore takes the same turn, so it never interleaves with an insert.
/// </remarks>
public sealed class TextInsertion
{
    /// <summary>How long the target app gets to read the clipboard before it is restored.</summary>
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(1);

    readonly IClipboardBackend clipboard;
    readonly Func<bool> isSecureInputActive;
    readonly Func<Task<bool>> pasteKeystroke;
    readonly TimeSpan delay;
    readonly SemaphoreSlim turn = new(1, 1);
    readonly Lock gate = new();
    readonly Logger log = Log.For("TextInserter");

    // Guarded by gate.
    PendingRestore? pendingRestore;
    CancellationTokenSource? restoreCancellation;

    sealed record PendingRestore(ClipboardSnapshot? Items, uint Sequence);

    /// <param name="pasteKeystroke">Posts the paste chord; false when the events could not be injected.</param>
    public TextInsertion(IClipboardBackend clipboard, Func<bool> isSecureInputActive, Func<Task<bool>> pasteKeystroke, TimeSpan restoreDelay)
    {
        this.clipboard = clipboard;
        this.isSecureInputActive = isSecureInputActive;
        this.pasteKeystroke = pasteKeystroke;
        delay = restoreDelay;
    }

    public async Task<InsertionResult> InsertAsync(string text)
    {
        await turn.WaitAsync().ConfigureAwait(false);
        try
        {
            // Off the calling (UI) thread: opening a busy clipboard is retried for a while.
            return await Task.Run(() => PerformInsertAsync(text)).ConfigureAwait(false);
        }
        finally
        {
            turn.Release();
        }
    }

    /// <summary>Copies without pasting; a pending restore is dropped so the copy stays.</summary>
    public void CopyToClipboard(string text)
    {
        CancelRestore();
        if (clipboard.WriteText(text) is null)
        {
            log.Error("Could not write to the clipboard");
        }
    }

    async Task<InsertionResult> PerformInsertAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return InsertionResult.EmptyResult;
        }
        // The keystroke would be dropped by UIPI, or go nowhere on the secure desktop.
        if (isSecureInputActive())
        {
            CopyToClipboard(text);
            return new InsertionResult.Copied(CopyReason.SecureInput);
        }

        var previous = SnapshotForRestore();
        if (clipboard.WriteText(text) is not { } written)
        {
            log.Error("Could not write the text to the clipboard");
            return new InsertionResult.Copied(CopyReason.PasteFailed);
        }
        lock (gate)
        {
            // Recorded before the keystroke: an insert that starts while this chord is
            // held finds the user's contents here instead of snapshotting Slovo's text.
            restoreCancellation?.Cancel();
            restoreCancellation = null;
            pendingRestore = new PendingRestore(previous, written);
        }
        var posted = await pasteKeystroke().ConfigureAwait(false);
        lock (gate)
        {
            // A copy took over the clipboard meanwhile; it stays.
            if (pendingRestore?.Sequence != written)
            {
                return posted ? InsertionResult.PastedResult : new InsertionResult.Copied(CopyReason.PasteFailed);
            }
        }
        if (!posted)
        {
            log.Error("Could not inject the Ctrl+V keystroke");
            CancelRestore();
            return new InsertionResult.Copied(CopyReason.PasteFailed);
        }
        ScheduleRestore(new PendingRestore(previous, written));
        return InsertionResult.PastedResult;
    }

    /// <summary>
    /// The user's contents to put back later. While an earlier paste still waits for its
    /// restore, the clipboard holds Slovo's text, so the earlier snapshot is reused.
    /// </summary>
    ClipboardSnapshot? SnapshotForRestore()
    {
        lock (gate)
        {
            if (pendingRestore is { } pending && pending.Sequence == clipboard.SequenceNumber)
            {
                return pending.Items;
            }
        }
        return clipboard.Snapshot();
    }

    void ScheduleRestore(PendingRestore restore)
    {
        CancellationTokenSource cancellation;
        lock (gate)
        {
            restoreCancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            restoreCancellation = cancellation;
            pendingRestore = restore;
        }
        _ = RestoreLaterAsync(restore, cancellation.Token);
    }

    async Task RestoreLaterAsync(PendingRestore restore, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation).ConfigureAwait(false);
            await turn.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            RestoreIfUnchanged(restore);
        }
        finally
        {
            turn.Release();
        }
    }

    /// <summary>Puts the previous contents back unless someone copied something since Slovo wrote.</summary>
    void RestoreIfUnchanged(PendingRestore restore)
    {
        lock (gate)
        {
            if (pendingRestore?.Sequence != restore.Sequence)
            {
                return;
            }
            pendingRestore = null;
            restoreCancellation = null;
        }
        // A clipboard that could not be read is left with Slovo's text rather than emptied.
        if (restore.Items is { } items)
        {
            clipboard.RestoreIfUnchanged(items, restore.Sequence);
        }
    }

    void CancelRestore()
    {
        lock (gate)
        {
            restoreCancellation?.Cancel();
            restoreCancellation = null;
            pendingRestore = null;
        }
    }
}
