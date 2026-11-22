using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Slovo.Core.Mcp;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.Core.Tests.Mcp;

/// <summary>
/// The server as Claude starts it: <see cref="McpServerMode"/> over operating-system pipes with
/// the settings file switched between requests, and the built <c>Slovo.exe --mcp</c> itself
/// when the app has been built (port of MCPProcessTests).
/// </summary>
public sealed class McpProcessTests
{
    static readonly string[] Requests =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
        """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
        """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_meetings","arguments":{"limit":1}}}""",
        """{"jsonrpc":"2.0","id":4,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
    ];

    const string AfterAccessOff = """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"list_meetings","arguments":{}}}""";

    /// <summary>Every file under the meetings folder with its size and date.</summary>
    static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(folder, path),
                path => File.Exists(path) ? $"{new FileInfo(path).Length} {File.GetLastWriteTimeUtc(path).Ticks}" : "folder");

    static void CheckReplies(IReadOnlyList<string> lines, string version)
    {
        var replies = lines.Select(JsonValue.Parse).ToList();
        Assert.Equal([1, 2, 3, 4, 5], replies.Select(reply => reply["id"]!.IntValue!.Value));
        Assert.Equal(JsonValue.Str("2025-11-25"), replies[0]["result"]!["protocolVersion"]);
        Assert.Equal(JsonValue.Str(version), replies[0]["result"]!["serverInfo"]!["version"]);
        Assert.Equal(8, replies[1]["result"]!["tools"]!.ArrayValue!.Count);
        Assert.Equal(JsonValue.Str(McpFixture.CallId.ToString("D")),
            replies[2]["result"]!["structuredContent"]!["meetings"]!.ArrayValue![0]["id"]);
        Assert.Equal(JsonValue.Str("complete"), replies[3]["result"]!["resultType"]);
        Assert.Equal(JsonValue.True, replies[4]["result"]!["isError"]);
        Assert.Equal(JsonValue.Str(McpClient.Message("integration.mcp.disabled")), replies[4]["result"]!["content"]!.ArrayValue![0]["text"]);
    }

    static void SwitchAccess(string settingsFile, bool enabled)
    {
        var settings = new AppSettings(SettingsStore.Open(settingsFile));
        settings.McpEnabled = enabled;
        settings.AppLanguage = AppLanguage.En;
    }

    [Fact]
    public async Task ServerModeAnswersOverPipesAndEndsWithItsInput()
    {
        using var fixture = new McpFixture();
        SwitchAccess(fixture.Paths.SettingsFile, enabled: true);
        var before = Snapshot(fixture.Paths.MeetingsFolder);

        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverInput = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var fromServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var serverOutput = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle);
        var diagnostics = new StringWriter();
        var server = Task.Run(() =>
        {
            var code = McpServerMode.Run(fixture.Paths, ["--mcp", "--verbose"], serverInput, serverOutput, diagnostics, "0.3.0");
            serverOutput.Dispose();
            return code;
        }, TestContext.Current.CancellationToken);

        var reader = new StreamReader(fromServer, new UTF8Encoding(false));
        var lines = new List<string>();
        void Send(string line)
        {
            toServer.Write(Encoding.UTF8.GetBytes(line + "\n"));
            toServer.Flush();
        }
        foreach (var request in Requests)
        {
            Send(request);
        }
        for (var index = 0; index < 4; index++)
        {
            lines.Add(reader.ReadLine()!);
        }
        // Switching access off in the running app applies to the next request.
        SwitchAccess(fixture.Paths.SettingsFile, enabled: false);
        Send(AfterAccessOff);
        lines.Add(reader.ReadLine()!);
        toServer.Dispose();

        Assert.Equal(0, await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Null(reader.ReadLine());
        CheckReplies(lines, "0.3.0");
        var log = diagnostics.ToString();
        Assert.Contains("slovo mcp: tools/call list_meetings", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Коворк", log, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(fixture.Paths.MeetingsFolder));
    }

    /// <summary>The repository root, found from the test's output folder.</summary>
    static string? RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Windows", "Slovo.slnx")))
            {
                return folder.FullName;
            }
        }
        return null;
    }

    [Fact]
    public async Task TheBuiltAppServesMcpOnStandardOutputOnly()
    {
        var root = RepositoryRoot();
        var app = root is null ? null : new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(root, "Windows", "src", "Slovo.App", "bin", configuration, "net10.0", "Slovo.dll"))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (app is null || root is null)
        {
            Assert.Skip("Slovo.App is not built; build the solution to run the process test.");
            return;
        }
        // A binary older than the MCP mode could open the tray app instead of the server.
        if (File.GetLastWriteTimeUtc(app) < File.GetLastWriteTimeUtc(Path.Combine(root, "Windows", "src", "Slovo.App", "McpMode.cs")))
        {
            Assert.Skip("Slovo.App predates McpMode.cs; rebuild it to run the process test.");
            return;
        }

        using var fixture = new McpFixture();
        SwitchAccess(fixture.Paths.SettingsFile, enabled: true);
        var before = Snapshot(fixture.Paths.MeetingsFolder);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[] { "exec", app, "--mcp", "--verbose", "--data-dir", fixture.Base })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["SLOVO_DATA_DIR"] = fixture.Base;
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var input = process.StandardInput;
            input.NewLine = "\n";
            var lines = new List<string>();
            var watch = Stopwatch.StartNew();
            input.WriteLine(Requests[0]);
            input.Flush();
            lines.Add(process.StandardOutput.ReadLine()!);
            var firstResponse = watch.Elapsed;
            foreach (var request in Requests.Skip(1))
            {
                input.WriteLine(request);
            }
            input.Flush();
            for (var index = 0; index < 3; index++)
            {
                lines.Add(process.StandardOutput.ReadLine()!);
            }
            SwitchAccess(fixture.Paths.SettingsFile, enabled: false);
            input.WriteLine(AfterAccessOff);
            input.Flush();
            lines.Add(process.StandardOutput.ReadLine()!);
            input.Close();

            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the server must exit when its input ends");
            Assert.Equal(0, process.ExitCode);
            Assert.Null(process.StandardOutput.ReadLine());
            Assert.True(firstResponse < TimeSpan.FromSeconds(30), $"first response after {firstResponse}");
            CheckReplies(lines, lines.Count > 0 ? JsonValue.Parse(lines[0])["result"]!["serverInfo"]!["version"]!.StringValue! : "");
            var log = await errors;
            Assert.Contains("slovo mcp: tools/call list_meetings", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Коворк", log, StringComparison.Ordinal);
            Assert.Equal(before, Snapshot(fixture.Paths.MeetingsFolder));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
