using System.Text.Json.Nodes;
using Slovo.Core.Json;
using Slovo.Core.Model;
using Slovo.Core.Storage;

namespace Slovo.Core.Tests.Storage;

/// <summary>JSONFile of InputStoreTests and InputStoreRecoveryTests, and the macOS file format.</summary>
public sealed class JsonFileTests : IDisposable
{
    readonly TemporaryFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void MissingThenRoundTrip()
    {
        var file = new JsonFile<List<Snippet>>(Path.Combine(folder.Root, "nested", "snippets.json"));
        Assert.Equal(JsonFileLoadKind.Missing, file.Load().Kind);

        var created = DateTimeOffset.FromUnixTimeMilliseconds(1_789_000_000_123);
        List<Snippet> snippets = [new() { Trigger = "мой адрес", Expansion = "Примерск, ул. Примерная, 1", CreatedAt = created, UpdatedAt = created }];
        file.Save(snippets);
        var load = file.Load();
        Assert.Equal(JsonFileLoadKind.Loaded, load.Kind);
        Assert.Equal(snippets, load.Value);

        // The atomic write leaves no temporary files next to the store.
        Assert.Equal(["snippets.json"], Directory.GetFiles(Path.GetDirectoryName(file.Path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void MovesACorruptFileAside()
    {
        var path = folder.Paths.HistoryFile;
        File.WriteAllText(path, "{ not json");
        var file = new JsonFile<List<HistoryEntry>>(path);

        var load = file.Load();
        Assert.Equal(JsonFileLoadKind.Corrupt, load.Kind);
        var backup = Assert.IsType<string>(load.MovedTo);
        Assert.False(File.Exists(path));
        Assert.StartsWith("history.corrupt-", Path.GetFileName(backup));
        Assert.Equal(".json", Path.GetExtension(backup));
        Assert.Equal("{ not json", File.ReadAllText(backup));
        Assert.Equal(JsonFileLoadKind.Missing, file.Load().Kind);
    }

    [Fact]
    public void WrongShapeCountsAsCorrupt()
    {
        var path = Path.Combine(folder.Root, "notes.json");
        File.WriteAllText(path, """{"entries": 5}""");
        Assert.Equal(JsonFileLoadKind.Corrupt, new JsonFile<List<Note>>(path).Load().Kind);
    }

    [Fact]
    public void CorruptBackupsNeverOverwriteEachOther()
    {
        var path = Path.Combine(folder.Root, "dictionary.json");
        var date = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000);
        var first = JsonFileCorruption.BackupPath(path, date);
        File.WriteAllText(first, "a");
        var second = JsonFileCorruption.BackupPath(path, date);
        Assert.NotEqual(first, second);
        Assert.StartsWith("dictionary.corrupt-", Path.GetFileName(second));
        Assert.EndsWith("-2.json", second);
    }

    [Fact]
    public void SaveFailureThrowsALocalizedError()
    {
        var blocker = Path.Combine(folder.Root, "file");
        File.WriteAllText(blocker, "x");
        var file = new JsonFile<List<Note>>(Path.Combine(blocker, "notes.json"));
        var error = Assert.Throws<SlovoException>(() => file.Save([]));
        Assert.Contains("notes.json", error.Message);
    }

    [Fact]
    public void ALockedFileIsUnreadableAndLeftAlone()
    {
        var path = Path.Combine(folder.Root, "notes.json");
        File.WriteAllText(path, "[]");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(JsonFileLoadKind.Unreadable, new JsonFile<List<Note>>(path).Load().Kind);
        }
        Assert.Equal("[]", File.ReadAllText(path));
        Assert.Empty(folder.Files("notes.corrupt-"));
    }

    // MARK: - Dropping unreadable entries

    [Fact]
    public void ReportsWhatItDropped()
    {
        var path = Path.Combine(folder.Root, "notes.json");
        var note = new Note { Text = "ok", CreatedAt = AppleDate.ReferenceDate, UpdatedAt = AppleDate.ReferenceDate };
        WriteArray(path, [note, note, note], (index, entry) =>
        {
            if (index > 0)
            {
                entry["id"] = "not a uuid";
            }
        });
        var load = new JsonFile<List<Note>>(path).Load();
        Assert.Equal(JsonFileLoadKind.Partial, load.Kind);
        Assert.Equal([note], load.Value);
        Assert.Equal(2, load.Dropped);
        Assert.NotNull(load.MovedTo);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(load.MovedTo!));

        // A file that is not an array at all is still damaged as a whole.
        File.WriteAllText(path, """{"entries": 5}""");
        Assert.Equal(JsonFileLoadKind.Corrupt, new JsonFile<List<Note>>(path).Load().Kind);
    }

    [Fact]
    public void AnEntryIsDroppedWhereSwiftsDecodableWouldRejectIt()
    {
        var path = Path.Combine(folder.Root, "history.json");
        var entry = new HistoryEntry { Date = AppleDate.ReferenceDate, Text = "текст", RawText = "текст", EngineId = EngineId.GigaamRnnt };
        HistoryEntry[] entries = [entry with { Id = Guid.NewGuid() }, entry with { Id = Guid.NewGuid() }, entry with { Id = Guid.NewGuid() },
                                  entry with { Id = Guid.NewGuid() }, entry with { Id = Guid.NewGuid() }, entry with { Id = Guid.NewGuid() }];
        WriteArray(path, entries, (index, json) =>
        {
            switch (index)
            {
                case 1: json.Remove("text"); break;             // a missing non-optional key
                case 2: json["rawText"] = null; break;          // null for a non-optional value
                case 3: json["engineID"] = "whisper-v9"; break; // an engine a newer build knows
                case 4: json["wordCount"] = "five"; break;      // a value of the wrong type
                case 5: json.Remove("appName"); json["newKey"] = 1; break; // optional key missing, unknown key: still readable
            }
        });
        var load = new JsonFile<List<HistoryEntry>>(path).Load();
        Assert.Equal(JsonFileLoadKind.Partial, load.Kind);
        Assert.Equal(4, load.Dropped);
        Assert.Equal([entries[0].Id, entries[5].Id], load.Value!.Select(e => e.Id));
    }

    [Fact]
    public void ArraysOfEveryKindDecodeElementByElement()
    {
        var path = Path.Combine(folder.Root, "app-categories.json");
        File.WriteAllText(path, """
            [{"bundleID": "slack.exe", "appName": "Slack", "category": "work"},
             {"bundleID": "a.exe", "appName": "A", "category": "gaming"},
             null,
             {"bundleID": "b.exe", "category": "email"}]
            """);
        var load = new JsonFile<AppCategoryAssignment[]>(path).Load();
        Assert.Equal(JsonFileLoadKind.Partial, load.Kind);
        Assert.Equal([new AppCategoryAssignment("slack.exe", "Slack", AppCategory.Work)], load.Value!);
        Assert.Equal(3, load.Dropped);
    }

    // MARK: - The macOS format

    [Fact]
    public void WritesTheMacOSFormat()
    {
        var path = Path.Combine(folder.Root, "history.json");
        var id = Guid.Parse("e621e1f8-c36c-495a-93fc-0c247a3e6e5f");
        var entry = new HistoryEntry
        {
            Id = id, Date = AppleDate.ReferenceDate.AddSeconds(800_000_000.5), Text = "текст/1", RawText = "текст", EngineId = EngineId.Parakeet,
            LanguageMode = LanguageMode.English, AppBundleId = "telegram.exe", AppName = "Telegram", Category = AppCategory.Personal,
            Duration = 2.5, WordCount = 1, FixesCount = 0, AudioFileName = "2026-09-15/E621E1F8-C36C-495A-93FC-0C247A3E6E5F.wav",
        };
        new JsonFile<List<HistoryEntry>>(path).Save([entry]);
        var text = File.ReadAllText(path);
        Assert.Contains("\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\"", text);
        Assert.Contains("800000000.5", text);
        Assert.Contains("\"engineID\": \"parakeet-v3\"", text);
        Assert.Contains("текст/1", text);
        var keys = JsonNode.Parse(text)!.AsArray()[0]!.AsObject().Select(pair => pair.Key).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        // Optional values that are not set are left out, as Swift's encodeIfPresent does.
        new JsonFile<List<HistoryEntry>>(path).Save([entry with { AudioFileName = null }]);
        Assert.DoesNotContain("audioFileName", File.ReadAllText(path));
    }

    [Fact]
    public void ReadsWhatTheMacOSAppWrote()
    {
        // JSONEncoder with .prettyPrinted and .sortedKeys, slashes escaped as Swift does by default.
        const string swift = """
            [
              {
                "appBundleID" : "ru.keepcoder.Telegram",
                "appName" : "Telegram",
                "audioFileName" : "2026-09-15\/0B8C6C62-5E5D-4B63-9C51-0F3D1E0B1A2C.wav",
                "category" : "personal",
                "date" : 779626800.25,
                "duration" : 3.5,
                "engineID" : "gigaam-v3-rnnt",
                "fixesCount" : 1,
                "id" : "0B8C6C62-5E5D-4B63-9C51-0F3D1E0B1A2C",
                "languageMode" : "mixed",
                "rawText" : "запусти докер",
                "text" : "Запусти Docker.",
                "wordCount" : 2
              }
            ]
            """;
        var path = Path.Combine(folder.Root, "history.json");
        File.WriteAllText(path, swift);
        var load = new JsonFile<List<HistoryEntry>>(path).Load();
        Assert.Equal(JsonFileLoadKind.Loaded, load.Kind);
        var entry = Assert.Single(load.Value!);
        Assert.Equal(Guid.Parse("0B8C6C62-5E5D-4B63-9C51-0F3D1E0B1A2C"), entry.Id);
        Assert.Equal(AppleDate.ReferenceDate.AddSeconds(779_626_800.25), entry.Date);
        Assert.Equal("2026-09-15/0B8C6C62-5E5D-4B63-9C51-0F3D1E0B1A2C.wav", entry.AudioFileName);
        Assert.Equal(EngineId.GigaamRnnt, entry.EngineId);
        Assert.Equal(AppCategory.Personal, entry.Category);
        Assert.Equal(3.5, entry.Duration);
    }

    static void WriteArray<T>(string path, IEnumerable<T> values, Action<int, JsonObject> edit)
    {
        var array = JsonNode.Parse(SlovoJson.Serialize(values.ToList()))!.AsArray();
        for (var index = 0; index < array.Count; index++)
        {
            edit(index, array[index]!.AsObject());
        }
        File.WriteAllText(path, array.ToJsonString());
    }
}
