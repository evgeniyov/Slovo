using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>A monotonic clock the test moves by hand.</summary>
sealed class FakeClock(double step = 0) : IUptime
{
    readonly Lock gate = new();
    double current = 1_000;

    public double Now
    {
        get
        {
            lock (gate)
            {
                var value = current;
                current += step;
                return value;
            }
        }
    }

    public void Advance(double seconds)
    {
        lock (gate)
        {
            current += seconds;
        }
    }
}

sealed class AccessSwitch
{
    public McpAccess Access { get; set; } = new(Enabled: true, CanWrite: false, MaxAgeDays: 0);
}

/// <summary>Meetings held in memory, for protocol tests that need no files.</summary>
sealed class StaticArchive(params MeetingRecord[] records) : IMeetingArchiveReading
{
    public List<ArchivedMeeting> Items { get; } =
        records.Select(record => new ArchivedMeeting(record, McpFixture.Now, 100)).ToList();

    public IReadOnlyList<ArchivedMeeting> Meetings()
    {
        var sorted = Items.ToList();
        sorted.Sort(MeetingArchive.Compare);
        return sorted;
    }

    public ArchivedMeeting? Meeting(Guid id) => Items.FirstOrDefault(item => item.Record.Id == id);
}

/// <summary>A tool reply taken apart.</summary>
sealed record ToolReply(string Text, JsonValue? Structured, bool IsError);

/// <summary>
/// The shared MCP fixture (<c>Shared/fixtures/mcp</c>): the macOS test's data folder copied to
/// a temporary place with its recorded dates, the fixed clock and time zone.
/// </summary>
sealed class McpFixture : IDisposable
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "fixtures", "mcp");
    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T18:04:00+03:00", System.Globalization.CultureInfo.InvariantCulture);

    public static readonly Guid CoworkId = Guid.Parse("3F2A9C1E-5B7D-4C1A-9E0F-2B6D8A4C7E11");
    public static readonly Guid CallId = Guid.Parse("7B1D2E3F-1111-4222-8333-444455556666");
    public static readonly Guid StandupId = Guid.Parse("11111111-2222-4333-8444-555566667777");
    public static readonly Guid PlanningId = Guid.Parse("3F2A9C1E-AAAA-4BBB-8CCC-000000000004");
    public static readonly Guid RecoveredId = Guid.Parse("A0A0A0A0-B1B1-4C2C-8D3D-E4E4E4E4E4E4");
    public static readonly Guid RetroId = Guid.Parse("C0FFEE00-0000-4000-8000-00000000000F");
    public static readonly Guid WaitingId = Guid.Parse("D00DFEED-0000-4000-8000-000000000001");
    public static readonly Guid BrokenId = Guid.Parse("DEADBEEF-0000-4000-8000-000000000002");
    public static readonly Guid ExtremeId = Guid.Parse("E0E0E0E0-0000-4000-8000-000000000003");
    public static readonly Guid LongId = Guid.Parse("10000000-0000-4000-8000-00000000000A");
    public static readonly Guid HostileId = Guid.Parse("B0B0B0B0-0000-4000-8000-00000000000B");
    public static readonly Guid NotesId = Guid.Parse("C1C1C1C1-0000-4000-8000-00000000000C");

    public McpFixture(bool withData = true)
    {
        Base = Path.Combine(Path.GetTempPath(), "SlovoMcpTests-" + Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Base);
        Directory.CreateDirectory(Paths.MeetingsFolder);
        if (withData)
        {
            CopyDirectory(Path.Combine(Folder, "data", "Meetings"), Paths.MeetingsFolder);
            ApplyTimes(JsonValue.Parse(File.ReadAllBytes(Path.Combine(Folder, "fixture.json"))), Base);
        }
    }

    public string Base { get; }
    public AppPaths Paths { get; }

    public string MeetingFolder(Guid id) => Paths.MeetingFolder(id);

    public string RecordPath(Guid id) => Path.Combine(MeetingFolder(id), "meeting.json");

    public MeetingRecord Record(Guid id) => MeetingArchive.DecodeRecord(File.ReadAllBytes(RecordPath(id)))! with { Id = id };

    /// <summary>Writes a record the way the meeting store does.</summary>
    public void Write(MeetingRecord record)
    {
        Directory.CreateDirectory(MeetingFolder(record.Id));
        File.WriteAllBytes(RecordPath(record.Id), SlovoJson.SerializeToUtf8(record));
    }

    public void WriteRaw(string text, string name, Guid id)
    {
        Directory.CreateDirectory(MeetingFolder(id));
        File.WriteAllText(Path.Combine(MeetingFolder(id), name), text, new UTF8Encoding(false));
    }

    public McpHandler Handler(AccessSwitch? access = null, AppLanguage language = AppLanguage.En, IUptime? clock = null,
                              bool canWrite = false)
    {
        var accessSwitch = access ?? new AccessSwitch { Access = new McpAccess(true, canWrite, 0) };
        return new McpHandler(new MeetingArchive(Paths), () => accessSwitch.Access, () => language, () => Now, "1.2.0",
            notes: new MeetingNotesFile(Paths), highlights: new MeetingHighlightsFile(Paths), timeZone: TimeZone,
            clock: clock ?? new FakeClock(step: 1));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.GetDirectories(source))
        {
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
        }
    }

    /// <summary>Gives every file and folder of a copy the dates the manifest records, since git keeps none.</summary>
    public static void ApplyTimes(JsonValue manifest, string root)
    {
        foreach (var entry in manifest["times"]?.ArrayValue ?? [])
        {
            var path = Path.Combine(root, entry["path"]!.StringValue!.Replace('/', Path.DirectorySeparatorChar));
            var isFolder = Directory.Exists(path);
            if (entry["modified"]?.DoubleValue is { } modified)
            {
                var time = AppleDate.FromReferenceSeconds(modified).UtcDateTime;
                if (isFolder)
                {
                    Directory.SetLastWriteTimeUtc(path, time);
                }
                else
                {
                    File.SetLastWriteTimeUtc(path, time);
                }
            }
            if (entry["created"]?.DoubleValue is { } created)
            {
                var time = AppleDate.FromReferenceSeconds(created).UtcDateTime;
                if (isFolder)
                {
                    Directory.SetCreationTimeUtc(path, time);
                }
                else
                {
                    File.SetCreationTimeUtc(path, time);
                }
            }
        }
    }
}

/// <summary>Request helpers shared by the MCP tests.</summary>
static class McpClient
{
    public static readonly JsonValue ModernMeta = JsonValue.Object(
        ("io.modelcontextprotocol/protocolVersion", "2026-07-28"),
        ("io.modelcontextprotocol/clientCapabilities", JsonValue.Object()));

    public static JsonValue Request(string method, JsonValue? parameters = null, bool modern = false, JsonValue? id = null)
    {
        var fields = new Dictionary<string, JsonValue> { ["jsonrpc"] = "2.0", ["id"] = id ?? 1, ["method"] = method };
        var parameterFields = parameters?.ObjectValue?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
        if (modern)
        {
            parameterFields["_meta"] = ModernMeta;
        }
        if (parameters is not null || modern)
        {
            fields["params"] = JsonValue.Object(parameterFields);
        }
        return JsonValue.Object(fields);
    }

    /// <summary>The raw reply line, or null for a notification.</summary>
    public static string? Raw(McpHandler handler, string line) =>
        handler.Handle(Encoding.UTF8.GetBytes(line)) is { } reply ? Encoding.UTF8.GetString(reply) : null;

    public static JsonValue? Send(McpHandler handler, JsonValue message) => Send(handler, message.SerializedString);

    public static JsonValue? Send(McpHandler handler, string line)
    {
        var reply = Raw(handler, line);
        if (reply is null)
        {
            return null;
        }
        Assert.DoesNotContain('\n', reply);
        return JsonValue.Parse(reply);
    }

    public static JsonValue Result(McpHandler handler, string method, JsonValue? parameters = null, bool modern = false)
    {
        var reply = Send(handler, Request(method, parameters, modern));
        Assert.NotNull(reply);
        Assert.Null(reply["error"]);
        return reply["result"] ?? JsonValue.Null;
    }

    public static long? ErrorCode(JsonValue? reply) => reply?["error"]?["code"]?.IntValue;

    public static ToolReply Tool(McpHandler handler, string name, JsonValue? arguments = null, bool modern = false)
    {
        var result = Result(handler, "tools/call",
            JsonValue.Object(("name", name), ("arguments", arguments ?? JsonValue.Object())), modern);
        var content = result["content"]?.ArrayValue ?? [];
        Assert.Single(content);
        var text = content[0]["text"]?.StringValue ?? "";
        var structured = result["structuredContent"];
        if (structured is not null)
        {
            Assert.Equal(structured, JsonValue.Parse(text));
        }
        Assert.True(text.Length <= 48_000);
        return new ToolReply(text, structured, result["isError"] == JsonValue.True);
    }

    public static string Message(string key, AppLanguage language = AppLanguage.En) =>
        Localization.CoreStrings.TableFor(Localization.CoreStrings.ResolveCode(language)).Get(key);
}
