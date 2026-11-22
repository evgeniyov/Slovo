using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Slovo.App.Integration;

public sealed record ClaudeRegistration(bool ClaudeCode, bool ClaudeApp);

/// <summary>
/// Registering Slovo as an MCP server in Claude Code and the Claude app (the Windows port of
/// the macOS ClaudeIntegration). Slovo never runs <c>claude</c> or edits Claude's files; it
/// shows the commands to copy and reads the files to say whether it is registered.
/// </summary>
public static class ClaudeIntegration
{
    public const string ServerName = "slovo";
    const long MaximumConfigBytes = 64L * 1024 * 1024;

    /// <summary>Where install.ps1 puts Slovo, a path that stays the same across updates.</summary>
    public static string InstalledFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Slovo");

    public static bool IsInstalled(string executablePath) =>
        executablePath.StartsWith(InstalledFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || executablePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Claude Code command, for PowerShell. <c>add-json</c> avoids passing <c>--</c>
    /// through npm's PowerShell shim, which swallows it.
    /// </summary>
    public static string ClaudeCodeCommand(string executablePath)
    {
        var json = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = executablePath,
            ["args"] = new JsonArray("--mcp"),
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $"claude mcp add-json --scope user {ServerName} '{json.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary>The entry to add under <c>mcpServers</c> in claude_desktop_config.json.</summary>
    public static string ClaudeAppSnippet(string executablePath) =>
        $$"""
        "{{ServerName}}": {
          "command": {{JsonQuoted(executablePath)}},
          "args": ["--mcp"]
        }
        """;

    public static string ClaudeCodeConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static string ClaudeAppConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    public static Task<ClaudeRegistration> DetectRegistrationAsync() => Task.Run(() =>
        new ClaudeRegistration(
            ReadJson(ClaudeCodeConfigPath) is { } code && ClaudeCodeConfigListsSlovo(code),
            ReadJson(ClaudeAppConfigPath) is { } app && ClaudeAppConfigListsSlovo(app)));

    /// <summary>User scope (<c>mcpServers</c>) or any project's own servers.</summary>
    public static bool ClaudeCodeConfigListsSlovo(JsonNode root)
    {
        if (root is not JsonObject obj)
        {
            return false;
        }
        if (HasSlovo(obj["mcpServers"]))
        {
            return true;
        }
        return obj["projects"] is JsonObject projects && projects.Any(p => HasSlovo((p.Value as JsonObject)?["mcpServers"]));
    }

    public static bool ClaudeAppConfigListsSlovo(JsonNode root) => HasSlovo((root as JsonObject)?["mcpServers"]);

    static bool HasSlovo(JsonNode? servers) => servers is JsonObject map && map.ContainsKey(ServerName);

    static JsonNode? ReadJson(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumConfigBytes)
            {
                return null;
            }
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string JsonQuoted(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.Append('"').ToString();
    }
}

/// <summary>Starts <c>Slovo.exe --mcp</c> the way Claude does and asks for one meeting.</summary>
public static class McpSelfCheck
{
    public abstract record Outcome
    {
        public sealed record Answered(bool IsError, string? Message) : Outcome;
        public sealed record TimedOut : Outcome;
        public sealed record ExitedWithoutAnswer : Outcome;
        public sealed record CouldNotStart(string Message) : Outcome;
    }

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    const int CallId = 2;

    public static readonly IReadOnlyList<string> RequestLines =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"slovo-settings-check","version":"1"}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
        """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_meetings","arguments":{"limit":1}}}""",
    ];

    public static async Task<Outcome> RunAsync(string executable, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(executable, "--mcp")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new Outcome.CouldNotStart(error.Message);
        }
        using (process)
        using (var cancel = new CancellationTokenSource(timeout ?? Timeout))
        {
            // Nothing reads stderr, so drain it; a chatty child must not stall on a full pipe.
            _ = process.StandardError.ReadToEndAsync(cancel.Token);
            try
            {
                foreach (var line in RequestLines)
                {
                    await process.StandardInput.WriteAsync(line + "\n");
                }
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child exited early; read whatever it said.
            }
            try
            {
                while (await process.StandardOutput.ReadLineAsync(cancel.Token) is { } line)
                {
                    if (ParseReply(line) is { } reply)
                    {
                        return reply;
                    }
                }
                return new Outcome.ExitedWithoutAnswer();
            }
            catch (OperationCanceledException)
            {
                return new Outcome.TimedOut();
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    public static Outcome? ParseReply(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject obj || obj["id"]?.GetValueKind() != JsonValueKind.Number || obj["id"]!.GetValue<int>() != CallId)
            {
                return null;
            }
            if (obj["error"] is JsonObject error)
            {
                return new Outcome.Answered(true, error["message"]?.GetValue<string>());
            }
            var result = obj["result"] as JsonObject;
            var isError = result?["isError"]?.GetValue<bool>() ?? false;
            var text = (result?["content"] as JsonArray)?.FirstOrDefault()?["text"]?.GetValue<string>();
            return new Outcome.Answered(isError, text);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
