using System.Text;
using Slovo.Core.Model;
using Slovo.Platform.Windows.Input;

namespace Slovo.Platform.Windows.Tests;

/// <summary>
/// Text insertion against a fake clipboard with a fake keystroke
/// (InputSystemTests.swift and FixCoreTextInsertionTests.swift). Nothing is pasted for real.
/// </summary>
public class TextInsertionTests
{
    const uint CustomFormat = 0xC123;
    readonly FakeClipboard clipboard = new();
    int keystrokes;
    bool keystrokeSucceeds = true;
    bool secureInput;

    TextInsertion MakeInserter(TimeSpan? restoreDelay = null, Func<Task<bool>>? keystroke = null) =>
        new(clipboard, () => secureInput, keystroke ?? (() =>
        {
            Interlocked.Increment(ref keystrokes);
            return Task.FromResult(keystrokeSucceeds);
        }), restoreDelay ?? TimeSpan.FromMilliseconds(60));

    void WriteOriginal(string text) => clipboard.UserCopies(text, new ClipboardItem(CustomFormat, [7, 8, 9]));

    [Fact]
    public async Task EmptyTextInsertsNothing()
    {
        var result = await MakeInserter().InsertAsync(" \n ");
        Assert.Equal(InsertionResult.EmptyResult, result);
        Assert.Equal(0, keystrokes);
    }

    [Fact]
    public async Task SecureInputCopiesWithMarkers()
    {
        secureInput = true;
        var result = await MakeInserter().InsertAsync("пароль не вставляем");
        Assert.Equal(new InsertionResult.Copied(CopyReason.SecureInput), result);
        Assert.Equal(0, keystrokes);
        Assert.Equal("пароль не вставляем", clipboard.Text);
        Assert.True(clipboard.HasPrivacyMarkers);
    }

    [Fact]
    public async Task PasteRestoresThePreviousItems()
    {
        WriteOriginal("user clipboard");
        var result = await MakeInserter().InsertAsync("dictated text");
        Assert.Equal(InsertionResult.PastedResult, result);
        Assert.Equal(1, keystrokes);
        Assert.Equal("dictated text", clipboard.Text);
        Assert.True(clipboard.HasPrivacyMarkers);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal("user clipboard", clipboard.Text);
        Assert.Equal([7, 8, 9], clipboard.Data(CustomFormat));
        Assert.False(clipboard.HasPrivacyMarkers);
    }

    [Fact]
    public async Task RestoreSkipsWhenSomeoneCopiedMeanwhile()
    {
        WriteOriginal("user clipboard");
        await MakeInserter().InsertAsync("dictated text");
        clipboard.UserCopies("copied during the delay");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal("copied during the delay", clipboard.Text);
    }

    [Fact]
    public async Task BackToBackPastesRestoreTheOriginalClipboard()
    {
        WriteOriginal("user clipboard");
        var inserter = MakeInserter();
        await inserter.InsertAsync("first");
        await inserter.InsertAsync("second");
        Assert.Equal("second", clipboard.Text);
        Assert.Equal(2, keystrokes);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal("user clipboard", clipboard.Text);
    }

    [Fact]
    public async Task EmptyClipboardIsRestoredEmpty()
    {
        await MakeInserter().InsertAsync("text");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Null(clipboard.Text);
        Assert.Empty(clipboard.Items);
    }

    [Fact]
    public async Task FailedKeystrokeLeavesTheTextToPasteByHand()
    {
        WriteOriginal("user clipboard");
        keystrokeSucceeds = false;
        var result = await MakeInserter().InsertAsync("dictated text");
        Assert.Equal(new InsertionResult.Copied(CopyReason.PasteFailed), result);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal("dictated text", clipboard.Text);
    }

    [Fact]
    public async Task CopyCancelsAPendingRestore()
    {
        WriteOriginal("user clipboard");
        var inserter = MakeInserter();
        await inserter.InsertAsync("dictated text");
        inserter.CopyToClipboard("paste last transcript");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal("paste last transcript", clipboard.Text);
        Assert.True(clipboard.HasPrivacyMarkers);
    }

    /// <summary>The clipboard stayed busy (another app held it open): nothing is pasted, and the result says so.</summary>
    [Fact]
    public async Task BusyClipboardIsAFailedPaste()
    {
        WriteOriginal("user clipboard");
        clipboard.Busy = true;
        var result = await MakeInserter().InsertAsync("dictated text");
        Assert.Equal(new InsertionResult.Copied(CopyReason.PasteFailed), result);
        Assert.Equal(0, keystrokes);
    }

    /// <summary>When the old contents could not be read, the pasted text stays rather than an emptied clipboard.</summary>
    [Fact]
    public async Task UnreadableClipboardIsLeftWithTheText()
    {
        WriteOriginal("user clipboard");
        clipboard.SnapshotFails = true;
        var result = await MakeInserter().InsertAsync("dictated text");
        Assert.Equal(InsertionResult.PastedResult, result);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal("dictated text", clipboard.Text);
    }

    /// <summary>A dictation's paste and "Paste last transcript" overlapping inside the 60 ms chord.</summary>
    [Fact]
    public async Task OverlappingPastesRestoreTheUsersClipboard()
    {
        clipboard.UserCopies("USER CLIPBOARD");
        var inserter = MakeInserter(TimeSpan.FromMilliseconds(300), async () =>
        {
            await Task.Delay(60, TestContext.Current.CancellationToken);
            return true;
        });
        var first = inserter.InsertAsync("first dictation");
        await Task.Delay(15, TestContext.Current.CancellationToken);
        var second = inserter.InsertAsync("pasted again");
        Assert.Equal(InsertionResult.PastedResult, await first);
        Assert.Equal(InsertionResult.PastedResult, await second);
        Assert.Equal("pasted again", clipboard.Text);

        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.Equal("USER CLIPBOARD", clipboard.Text);
    }

    /// <summary>
    /// The second paste writes the clipboard only after the first chord was released, so the
    /// first Ctrl+V never pastes the second text.
    /// </summary>
    [Fact]
    public async Task OverlappingPastesEachPasteTheirOwnText()
    {
        clipboard.UserCopies("USER CLIPBOARD");
        var seen = new List<string?>();
        var inserter = MakeInserter(TimeSpan.FromMilliseconds(100), async () =>
        {
            lock (seen)
            {
                seen.Add(clipboard.Text);
            }
            await Task.Delay(60, TestContext.Current.CancellationToken);
            lock (seen)
            {
                seen.Add(clipboard.Text);
            }
            return true;
        });
        var first = inserter.InsertAsync("first dictation");
        await Task.Delay(15, TestContext.Current.CancellationToken);
        var second = inserter.InsertAsync("pasted again");
        await first;
        await second;
        Assert.Equal(["first dictation", "first dictation", "pasted again", "pasted again"], seen);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal("USER CLIPBOARD", clipboard.Text);
    }

    [Fact]
    public void OnlyHandleFreeFormatsAreSaved()
    {
        Assert.True(ClipboardFormats.IsRestorable(ClipboardFormats.UnicodeText));
        Assert.True(ClipboardFormats.IsRestorable(1));   // CF_TEXT
        Assert.True(ClipboardFormats.IsRestorable(8));   // CF_DIB
        Assert.True(ClipboardFormats.IsRestorable(15));  // CF_HDROP
        Assert.True(ClipboardFormats.IsRestorable(17));  // CF_DIBV5
        Assert.True(ClipboardFormats.IsRestorable(0xC0F3)); // a registered format such as "HTML Format"
        Assert.False(ClipboardFormats.IsRestorable(0));
        Assert.False(ClipboardFormats.IsRestorable(2));  // CF_BITMAP
        Assert.False(ClipboardFormats.IsRestorable(3));  // CF_METAFILEPICT
        Assert.False(ClipboardFormats.IsRestorable(9));  // CF_PALETTE
        Assert.False(ClipboardFormats.IsRestorable(14)); // CF_ENHMETAFILE
        Assert.False(ClipboardFormats.IsRestorable(0x80));
        Assert.False(ClipboardFormats.IsRestorable(0x82));
        Assert.False(ClipboardFormats.IsRestorable(0x8E));
        Assert.False(ClipboardFormats.IsRestorable(0x200));
        Assert.False(ClipboardFormats.IsRestorable(0x3FF));
    }

    /// <summary>
    /// A clipboard like Windows': every change bumps the sequence number, and a restore
    /// happens only while the number is still the expected one.
    /// </summary>
    sealed class FakeClipboard : IClipboardBackend
    {
        const uint ExcludeFormat = 0xC001;
        const uint HistoryFormat = 0xC002;
        const uint CloudFormat = 0xC003;
        readonly Lock gate = new();
        List<ClipboardItem> items = [];
        uint sequence = 1;

        public bool Busy { get; set; }
        public bool SnapshotFails { get; set; }

        public IReadOnlyList<ClipboardItem> Items
        {
            get
            {
                lock (gate)
                {
                    return [.. items];
                }
            }
        }

        public string? Text => Data(ClipboardFormats.UnicodeText) is { } bytes
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0')
            : null;

        public bool HasPrivacyMarkers => Items.Any(item => item.Format == ExcludeFormat)
            && Items.Any(item => item.Format == HistoryFormat && item.Data.All(b => b == 0))
            && Items.Any(item => item.Format == CloudFormat && item.Data.All(b => b == 0));

        public byte[]? Data(uint format) => Items.FirstOrDefault(item => item.Format == format)?.Data;

        public uint SequenceNumber
        {
            get
            {
                lock (gate)
                {
                    return sequence;
                }
            }
        }

        public void UserCopies(string text, params ClipboardItem[] extra)
        {
            lock (gate)
            {
                items = [new ClipboardItem(ClipboardFormats.UnicodeText, Encoding.Unicode.GetBytes(text + "\0")), .. extra];
                sequence++;
            }
        }

        public ClipboardSnapshot? Snapshot()
        {
            lock (gate)
            {
                return Busy || SnapshotFails ? null : new ClipboardSnapshot([.. items]);
            }
        }

        public uint? WriteText(string text)
        {
            lock (gate)
            {
                if (Busy)
                {
                    return null;
                }
                items =
                [
                    new ClipboardItem(ClipboardFormats.UnicodeText, Encoding.Unicode.GetBytes(text + "\0")),
                    new ClipboardItem(ExcludeFormat, new byte[4]),
                    new ClipboardItem(HistoryFormat, new byte[4]),
                    new ClipboardItem(CloudFormat, new byte[4]),
                ];
                sequence += 4;
                return sequence;
            }
        }

        public bool RestoreIfUnchanged(ClipboardSnapshot snapshot, uint expectedSequence)
        {
            lock (gate)
            {
                if (Busy || sequence != expectedSequence)
                {
                    return false;
                }
                items = [.. snapshot.Items];
                sequence++;
                return true;
            }
        }
    }
}
