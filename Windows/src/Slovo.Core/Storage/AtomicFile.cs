using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Slovo.Core.Storage;

/// <summary>Writes a file in one rename, so a crash never leaves a half-written file behind.</summary>
public static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        path = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            Replace(temporary, path);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static void WriteText(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));

    static void Replace(string temporary, string path)
    {
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (OperatingSystem.IsWindows() &&
            (error is UnauthorizedAccessException || error is IOException { HResult: unchecked((int)0x80070020) }))
        {
            // MoveFileEx can reject replacement while a reader holds the destination
            // open, even with FileShare.Delete. POSIX rename keeps that reader's handle
            // valid and makes new readers see the new file.
            if (!TryReplaceOpenFile(temporary, path))
            {
                throw;
            }
        }
    }

    const uint DeleteAccess = 0x00010000;
    const uint OpenExisting = 3;
    const uint ReplaceIfExists = 1;
    const uint PosixSemantics = 2;
    const int FileRenameInfoEx = 22;

    static bool TryReplaceOpenFile(string temporary, string path)
    {
        using var file = CreateFileW(temporary, DeleteAccess, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (file.IsInvalid)
        {
            return false;
        }

        // FILE_RENAME_INFO contains flags, an aligned directory handle (null for an
        // absolute path), the byte length, then the UTF-16 path.
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameLength = Encoding.Unicode.GetByteCount(path);
        var info = new byte[nameOffset + nameLength + sizeof(char)];
        BinaryPrimitives.WriteUInt32LittleEndian(info, ReplaceIfExists | PosixSemantics);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(lengthOffset), (uint)nameLength);
        Encoding.Unicode.GetBytes(path, info.AsSpan(nameOffset));
        return SetFileInformationByHandle(file, FileRenameInfoEx, info, (uint)info.Length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, uint size);

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
