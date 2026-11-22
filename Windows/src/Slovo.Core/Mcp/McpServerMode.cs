using System.Globalization;
using Slovo.Core.Logging;
using Slovo.Core.Meetings;

namespace Slovo.Core.Mcp;

/// <summary>
/// <c>Slovo.exe --mcp</c>: the headless MCP server Claude starts over stdio (the platform-free
/// half of the macOS <c>MCPServerMode</c>; the app's <c>McpMode</c> hands it the process's
/// streams). Reading changes nothing under Slovo's data folder; the only things it can write
/// are what Claude concluded about a meeting and which of its moments are marked important,
/// and only while that separate switch is on.
/// </summary>
public static class McpServerMode
{
    public const string Argument = "--mcp";
    public const string VerboseArgument = "--verbose";

    /// <summary>
    /// Serves MCP over <paramref name="input"/> and <paramref name="output"/> until input ends,
    /// then returns the process exit code (0). <paramref name="diagnostics"/> receives the
    /// <c>--verbose</c> lines; it must not be the protocol stream.
    /// </summary>
    public static int Run(AppPaths paths, IReadOnlyList<string> arguments, Stream input, Stream output, TextWriter diagnostics,
                          string serverVersion)
    {
        var logger = Log.For("MCP");
        var preferences = new SlovoPreferences(paths);
        var handler = new McpHandler(
            new MeetingArchive(paths), preferences.Access, preferences.Language, () => DateTimeOffset.UtcNow, serverVersion,
            notes: new MeetingNotesFile(paths), highlights: new MeetingHighlightsFile(paths),
            instructions: McpText.WindowsInstructions);
        if (arguments.Contains(VerboseArgument))
        {
            handler.RequestObserver = summary =>
            {
                try
                {
                    diagnostics.WriteLine(VerboseLine(summary));
                    diagnostics.Flush();
                }
                catch (IOException)
                {
                }
            };
        }
        logger.Info($"MCP server {serverVersion} started");
        new McpServer(input, output, handler).Run();
        logger.Info("MCP server stopped: input ended");
        return 0;
    }

    /// <summary>Method, tool, duration and error code only: never arguments or meeting content.</summary>
    public static string VerboseLine(McpRequestSummary summary)
    {
        var parts = new List<string> { "slovo mcp:", summary.Method ?? "invalid request" };
        if (summary.Tool is { } tool)
        {
            parts.Add(tool);
        }
        parts.Add((summary.Duration * 1000).ToString("0.0", CultureInfo.InvariantCulture) + " ms");
        if (summary.ErrorCode is { } code)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"error {code}"));
        }
        if (summary.IsToolError)
        {
            parts.Add("tool error");
        }
        return string.Join(" ", parts);
    }
}
