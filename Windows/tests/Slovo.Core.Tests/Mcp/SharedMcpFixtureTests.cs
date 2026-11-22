using System.Text;
using Slovo.Core.Json;
using Slovo.Core.Meetings;
using Slovo.Core.Mcp;
using Slovo.Core.Model;

namespace Slovo.Core.Tests.Mcp;

/// <summary>
/// Replays <c>Shared/fixtures/mcp/requests.jsonl</c> against a copy of the shared data folder
/// and checks that every reply is byte for byte what the macOS server answered
/// (<c>responses.jsonl</c>, recorded by <c>Tests/SlovoCoreTests/SharedMCPFixtureTests.swift</c>,
/// which documents the format). Same clock, time zone, settings and server version.
/// </summary>
public sealed class SharedMcpFixtureTests
{
    static List<JsonValue> ReadLines(string name) =>
        File.ReadAllLines(Path.Combine(McpFixture.Folder, name), Encoding.UTF8)
            .Where(line => line.Length > 0)
            .Select(JsonValue.Parse)
            .ToList();

    sealed class Settings
    {
        public McpAccess Access { get; private set; } = new(true, true, 0);
        public AppLanguage Language { get; private set; } = AppLanguage.En;

        public void Apply(JsonValue? value)
        {
            Access = new McpAccess(value?["enabled"]?.BoolValue ?? true, value?["canWrite"]?.BoolValue ?? true,
                (int)(value?["maxAgeDays"]?.IntValue ?? 0));
            Language = EnumText.ParseOr(value?["language"]?.StringValue, AppLanguage.En);
        }
    }

    /// <summary>The next_cursor of a reply: <c>structuredContent.next_cursor</c>, or the <c>next_cursor:</c> line of a page.</summary>
    static string? NextCursor(string? reply)
    {
        if (reply is null)
        {
            return null;
        }
        var result = JsonValue.Parse(reply)["result"];
        if (result?["structuredContent"]?["next_cursor"]?.StringValue is { } cursor)
        {
            return cursor;
        }
        var text = result?["content"]?.ArrayValue?.FirstOrDefault()?["text"]?.StringValue ?? "";
        return text.Split('\n').FirstOrDefault(line => line.StartsWith("next_cursor: ", StringComparison.Ordinal))?["next_cursor: ".Length..];
    }

    static List<string?> Exchanges(IReadOnlyList<JsonValue> requests, JsonValue manifest)
    {
        using var fixture = new McpFixture();
        var settings = new Settings();
        var clock = new FakeClock();
        var now = AppleDate.FromReferenceSeconds(manifest["nowSeconds"]!.DoubleValue!.Value);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(manifest["timeZone"]!.StringValue!);
        var handler = new McpHandler(new MeetingArchive(fixture.Paths), () => settings.Access, () => settings.Language, () => now,
            manifest["serverVersion"]!.StringValue!, notes: new MeetingNotesFile(fixture.Paths),
            highlights: new MeetingHighlightsFile(fixture.Paths), timeZone: timeZone, clock: clock);

        var replies = new List<string?>();
        string? previous = null;
        var volatileIds = new List<(string Id, string Placeholder)>();
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            settings.Apply(request["settings"]);
            clock.Advance(100);
            var line = request["line"]!.StringValue!;
            if (line.Contains("$NEXT_CURSOR", StringComparison.Ordinal))
            {
                var cursor = NextCursor(previous);
                Assert.True(cursor is not null, $"request {index + 1}: no cursor to continue");
                line = line.Replace("$NEXT_CURSOR", cursor, StringComparison.Ordinal);
            }
            var data = handler.Handle(Encoding.UTF8.GetBytes(line));
            if (data is null)
            {
                replies.Add(null);
                continue;
            }
            var reply = Encoding.UTF8.GetString(data);
            if (request["capture"]?.StringValue == "highlight")
            {
                var id = JsonValue.Parse(data)["result"]?["structuredContent"]?["highlight"]?["id"]?.StringValue;
                Assert.True(id is not null, $"request {index + 1}: no highlight id to capture");
                if (!volatileIds.Any(pair => pair.Id == id))
                {
                    volatileIds.Add((id, $"volatile-highlight-{volatileIds.Count + 1}"));
                }
            }
            foreach (var (id, placeholder) in volatileIds)
            {
                reply = reply.Replace(id, placeholder, StringComparison.Ordinal);
            }
            previous = reply;
            replies.Add(reply);
        }
        return replies;
    }

    static string Difference(string expected, string actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        var at = 0;
        while (at < length && expected[at] == actual[at]) at++;
        var start = Math.Max(0, at - 80);
        string Around(string text) => text.Substring(start, Math.Min(text.Length - start, 200));
        return $"first difference at character {at}\n  expected …{Around(expected)}…\n  actual   …{Around(actual)}…";
    }

    [Fact]
    public void EveryExchangeMatchesTheRecording()
    {
        var requests = ReadLines("requests.jsonl");
        var recorded = ReadLines("responses.jsonl");
        var manifest = JsonValue.Parse(File.ReadAllBytes(Path.Combine(McpFixture.Folder, "fixture.json")));
        Assert.True(requests.Count >= 60);
        Assert.Equal(requests.Count, recorded.Count);

        var replies = Exchanges(requests, manifest);
        var failures = new List<string>();
        for (var index = 0; index < requests.Count; index++)
        {
            var expected = recorded[index]["reply"]?.StringValue;
            var actual = replies[index];
            if (expected == actual)
            {
                continue;
            }
            var comment = requests[index]["comment"]?.StringValue;
            if (expected is null || actual is null)
            {
                failures.Add($"request {index + 1} ({comment}): expected {expected ?? "no reply"}, got {actual ?? "no reply"}");
                continue;
            }
            var semantic = JsonValue.Parse(expected).Equals(JsonValue.Parse(actual)) ? "same JSON, different bytes" : "different JSON";
            failures.Add($"request {index + 1} ({comment}): {semantic}; {Difference(expected, actual)}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {requests.Count} replies differ:\n" + string.Join("\n\n", failures.Take(12)));

        // A second run over a fresh copy answers the same, so nothing depends on leftovers.
        Assert.Equal(replies, Exchanges(requests, manifest));
    }

    [Fact]
    public void TheManifestDatesEveryMeetingFolder()
    {
        var manifest = JsonValue.Parse(File.ReadAllBytes(Path.Combine(McpFixture.Folder, "fixture.json")));
        var paths = manifest["times"]!.ArrayValue!.Select(entry => entry["path"]!.StringValue!).ToHashSet();
        foreach (var folder in Directory.GetDirectories(Path.Combine(McpFixture.Folder, "data", "Meetings")))
        {
            Assert.Contains("Meetings/" + Path.GetFileName(folder), paths);
        }
        Assert.Equal("2026-09-15T18:04:00+03:00", manifest["now"]!.StringValue);
        using var fixture = new McpFixture();
        var transcript = Path.Combine(fixture.MeetingFolder(McpFixture.RecoveredId), "transcript.md");
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(File.GetCreationTimeUtc(transcript), TimeSpan.Zero));
    }

    /// <summary>Search folding against what Foundation folds on macOS, character by character.</summary>
    [Fact]
    public void SearchNormalizationMatchesTheMac()
    {
        var document = JsonValue.Parse(File.ReadAllBytes(Path.Combine(McpFixture.Folder, "search-normalization.json")));
        var failures = new List<string>();
        foreach (var sample in document["samples"]!.ArrayValue!)
        {
            var text = sample["text"]!.StringValue!;
            var expected = sample["normalized"]!.StringValue!;
            var actual = McpSearch.Normalized(text);
            if (actual != expected)
            {
                failures.Add($"{Escape(text)} → expected {Escape(expected)}, got {Escape(actual)}");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} samples differ:\n" + string.Join("\n", failures.Take(2000)));
    }

    static string Escape(string text) =>
        string.Concat(text.EnumerateRunes().Select(rune => rune.Value is >= 0x20 and < 0x7F ? rune.ToString() : $"\\u{{{rune.Value:X4}}}"));
}
