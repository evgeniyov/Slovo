using System.Text;
using Slovo.Core;
using Slovo.Core.Logging;
using Slovo.Core.Mcp;

namespace Slovo.App;

/// <summary>
/// <c>Slovo.exe --mcp</c>: the headless MCP server Claude starts over stdio (the counterpart of
/// the macOS <c>MCPServerMode</c>). It runs before anything of the interface exists: no window,
/// tray icon, store, engine or capture. The protocol keeps the process's real standard output;
/// <see cref="Console.Out"/> is pointed at standard error, so a stray console write from any
/// library lands in Claude's log instead of corrupting a JSON line. Reading changes nothing in
/// Slovo's data folder or settings; the only writes are Claude's notes and marked moments,
/// behind their own switch, plus the log.
/// </summary>
static class McpMode
{
    public static int Run(AppPaths paths)
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
#if DEBUG
        // Tests point the server at a temporary data folder, so they never read the user's
        // meetings or settings; release builds use SLOVO_DATA_DIR or the standard folder only.
        var dataFolder = arguments.IndexOf("--data-dir");
        if (dataFolder >= 0 && dataFolder + 1 < arguments.Count)
        {
            paths = new AppPaths(arguments[dataFolder + 1]);
        }
#endif
        var protocolOutput = Console.OpenStandardOutput();
        var protocolInput = Console.OpenStandardInput();
        // Standard error is Claude's log of the server: the log and the --verbose lines share it.
        var diagnostics = TextWriter.Synchronized(
            new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
        Console.SetOut(diagnostics);
        Console.SetError(diagnostics);
        Log.Configure(paths.LogsFolder, alsoToStderr: true);
        try
        {
            return McpServerMode.Run(paths, arguments, protocolInput, protocolOutput, diagnostics, AppInfo.Version);
        }
        finally
        {
            Log.Flush();
        }
    }
}
