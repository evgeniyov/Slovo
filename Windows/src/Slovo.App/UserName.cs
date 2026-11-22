using System.Runtime.InteropServices;

namespace Slovo.App;

/// <summary>The first name the Home greeting uses: the Windows display name's first word, else the login name.</summary>
static partial class UserName
{
    public static string FirstName()
    {
        string? display = null;
        if (OperatingSystem.IsWindows())
        {
            display = DisplayName();
        }
        var name = string.IsNullOrWhiteSpace(display) ? Environment.UserName : display;
        var first = name.Split([' ', '.', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        return first.Length > 0 ? char.ToUpperInvariant(first[0]) + first[1..] : first;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? DisplayName()
    {
        // NameDisplay = 3: the full name of a domain or Microsoft account user; fails for plain local accounts.
        var buffer = new char[256];
        var size = (uint)buffer.Length;
        return GetUserNameExW(3, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [LibraryImport("secur32.dll", EntryPoint = "GetUserNameExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserNameExW(int nameFormat, [Out] char[] buffer, ref uint size);
}
