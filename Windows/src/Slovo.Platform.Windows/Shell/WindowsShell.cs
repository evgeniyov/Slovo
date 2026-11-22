using System.Diagnostics;
using Slovo.Core.Logging;
using Slovo.Core.Platform;

namespace Slovo.Platform.Windows.Shell;

/// <summary>Which links Slovo hands to the system: web pages, mail and Settings pages, never files or programs.</summary>
public static class ShellLinks
{
    static readonly string[] Schemes = ["http", "https", "mailto", "ms-settings"];

    public static bool IsOpenable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && Schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    /// <summary>explorer.exe's argument that opens a folder with the file selected.</summary>
    public static string RevealArguments(string path) => $"/select,\"{path}\"";

    /// <summary>The only files Slovo opens in another app: video it recorded. Never a program or a script.</summary>
    static readonly string[] OpenableExtensions = [".mov", ".mp4"];

    public static bool IsOpenableFile(string path) =>
        OpenableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Opening folders, files and links with the system: Explorer on Windows, <c>open</c> or
/// <c>xdg-open</c> when the app runs elsewhere for interface work.
/// </summary>
public sealed class WindowsShell : IShell
{
    readonly Logger log = Log.For("Shell");

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            log.Warning("The folder to open does not exist");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        else
        {
            StartElsewhere(path);
        }
    }

    public void RevealFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is not null)
            {
                OpenFolder(folder);
            }
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            // Explorer parses /select itself; a Windows path cannot contain a quote.
            Start(new ProcessStartInfo("explorer.exe", ShellLinks.RevealArguments(Path.GetFullPath(path))) { UseShellExecute = false });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } });
        }
        else
        {
            StartElsewhere(Path.GetDirectoryName(path) ?? path);
        }
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            RevealFile(path);
            return;
        }
        if (!ShellLinks.IsOpenableFile(path))
        {
            log.Warning("Refused to open a file that is not a recording; showing it instead");
            RevealFile(path);
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            // The default app for .mov: Media Player on Windows 11, Films & TV on Windows 10.
            Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
        }
        else
        {
            StartElsewhere(path);
        }
    }

    public void OpenUrl(string url)
    {
        if (!ShellLinks.IsOpenable(url))
        {
            log.Warning("Refused to open a link that is not a web, mail or Settings link");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else
        {
            StartElsewhere(url);
        }
    }

    void StartElsewhere(string target) =>
        Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { target } });

    void Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Exception exception)
        {
            log.Error($"Could not start {info.FileName}", exception);
        }
    }
}
