using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Slovo.Core.Logging;
using Slovo.Platform.Windows.Interop;

namespace Slovo.Platform.Windows.Input;

/// <summary>Which clipboard formats can be saved as bytes and put back.</summary>
public static class ClipboardFormats
{
    public const uint UnicodeText = 13;
    /// <summary>Formats larger than this are not kept for the restore (a copied 4K screenshot is about 33 MB).</summary>
    public const int MaximumItemBytes = 16 * 1024 * 1024;
    /// <summary>The whole snapshot stops growing past this.</summary>
    public const int MaximumSnapshotBytes = 64 * 1024 * 1024;

    /// <summary>Tells clipboard history and Cloud Clipboard to skip everything on the clipboard.</summary>
    public const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    /// <summary>DWORD 0: keep the item out of clipboard history (Win+V).</summary>
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    /// <summary>DWORD 0: do not sync the item to other devices.</summary>
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";

    /// <summary>
    /// Whether a format's data is an HGLOBAL that can be copied byte for byte. GDI handles
    /// (bitmaps, metafiles, palettes), owner-display formats and private formats are
    /// handles whose bytes mean nothing to another process or after the owner frees them.
    /// </summary>
    public static bool IsRestorable(uint format) => format switch
    {
        0 => false,
        2 /* CF_BITMAP */ or 3 /* CF_METAFILEPICT */ or 9 /* CF_PALETTE */ or 14 /* CF_ENHMETAFILE */ => false,
        0x0080 /* CF_OWNERDISPLAY */ or 0x0082 /* CF_DSPBITMAP */ or 0x0083 /* CF_DSPMETAFILEPICT */ or 0x008E /* CF_DSPENHMETAFILE */ => false,
        >= 0x0200 and <= 0x02FF /* CF_PRIVATEFIRST..LAST */ => false,
        >= 0x0300 and <= 0x03FF /* CF_GDIOBJFIRST..LAST */ => false,
        _ => true,
    };
}

/// <summary>
/// The Win32 clipboard for <see cref="TextInsertion"/>. Every call runs on a thread of its
/// own that owns a message-only window, the clipboard owner; callers wait for it.
/// OpenClipboard fails while another app holds the clipboard, so it is retried briefly.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Win32Clipboard : IClipboardBackend, IDisposable
{
    const int OpenAttempts = 12;
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    readonly MessageWindowThread thread = new("Slovo clipboard", messageOnly: true, ApartmentState.STA);
    readonly Logger log = Log.For("Clipboard");
    uint excludeFormat;
    uint historyFormat;
    uint cloudFormat;

    public Win32Clipboard()
    {
        thread.OnStart = () =>
        {
            excludeFormat = User32.RegisterClipboardFormat(ClipboardFormats.ExcludeFromMonitoring);
            historyFormat = User32.RegisterClipboardFormat(ClipboardFormats.CanIncludeInHistory);
            cloudFormat = User32.RegisterClipboardFormat(ClipboardFormats.CanUploadToCloud);
        };
        if (!thread.Start(TimeSpan.FromSeconds(3)))
        {
            log.Error("The clipboard thread did not start");
        }
    }

    public uint SequenceNumber => User32.GetClipboardSequenceNumber();

    public ClipboardSnapshot? Snapshot() => thread.Invoke(ReadSnapshot, CallTimeout, null);

    public uint? WriteText(string text) => thread.Invoke(() => Write(text), CallTimeout, null);

    public bool RestoreIfUnchanged(ClipboardSnapshot snapshot, uint expectedSequence) =>
        thread.Invoke(() => Restore(snapshot, expectedSequence), CallTimeout, false);

    public void Dispose() => thread.Dispose();

    bool Open()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (User32.OpenClipboard(thread.Window) != 0)
            {
                return true;
            }
            Thread.Sleep(10 + attempt * 10);
        }
        log.Warning($"The clipboard stayed busy: {Marshal.GetLastPInvokeError()}");
        return false;
    }

    ClipboardSnapshot? ReadSnapshot()
    {
        if (!Open())
        {
            return null;
        }
        try
        {
            var items = new List<ClipboardItem>();
            var total = 0L;
            for (var format = User32.EnumClipboardFormats(0); format != 0; format = User32.EnumClipboardFormats(format))
            {
                if (!ClipboardFormats.IsRestorable(format))
                {
                    continue;
                }
                // Delayed rendering happens here; an owner that fails to render gives null.
                var handle = User32.GetClipboardData(format);
                if (handle == 0)
                {
                    continue;
                }
                var size = (long)(ulong)Kernel32.GlobalSize(handle);
                if (size > ClipboardFormats.MaximumItemBytes || total + size > ClipboardFormats.MaximumSnapshotBytes)
                {
                    log.Info($"Clipboard format {format} ({size} bytes) is too large to restore");
                    continue;
                }
                var source = Kernel32.GlobalLock(handle);
                if (source == null)
                {
                    continue;
                }
                try
                {
                    items.Add(new ClipboardItem(format, new ReadOnlySpan<byte>(source, (int)size).ToArray()));
                    total += size;
                }
                finally
                {
                    Kernel32.GlobalUnlock(handle);
                }
            }
            return new ClipboardSnapshot(items);
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    uint? Write(string text)
    {
        if (!Open())
        {
            return null;
        }
        var ok = false;
        try
        {
            if (User32.EmptyClipboard() == 0)
            {
                return null;
            }
            var bytes = new byte[(text.Length + 1) * sizeof(char)];
            MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
            ok = Set(ClipboardFormats.UnicodeText, bytes);
            // The markers matter less than the text; a failed one is not a failed paste.
            var zero = new byte[sizeof(uint)];
            Set(excludeFormat, zero);
            Set(historyFormat, zero);
            Set(cloudFormat, zero);
        }
        finally
        {
            User32.CloseClipboard();
        }
        return ok ? User32.GetClipboardSequenceNumber() : null;
    }

    bool Restore(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        if (!Open())
        {
            return false;
        }
        try
        {
            // Checked with the clipboard open, so nobody can copy in between.
            if (User32.GetClipboardSequenceNumber() != expectedSequence)
            {
                return false;
            }
            User32.EmptyClipboard();
            foreach (var item in snapshot.Items)
            {
                Set(item.Format, item.Data);
            }
            return true;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    /// <summary>Copies bytes into a movable HGLOBAL and hands it to the clipboard, which then owns it.</summary>
    bool Set(uint format, byte[] data)
    {
        if (format == 0)
        {
            return false;
        }
        var handle = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (handle == 0)
        {
            return false;
        }
        var target = Kernel32.GlobalLock(handle);
        if (target == null)
        {
            Kernel32.GlobalFree(handle);
            return false;
        }
        data.CopyTo(new Span<byte>(target, data.Length));
        Kernel32.GlobalUnlock(handle);
        if (User32.SetClipboardData(format, handle) == 0)
        {
            log.Warning($"SetClipboardData({format}) failed: {Marshal.GetLastPInvokeError()}");
            Kernel32.GlobalFree(handle);
            return false;
        }
        return true;
    }
}
