using System.ComponentModel;
using Slovo.Core.Audio;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Storage;

namespace Slovo.Core.Tests.Storage;

/// <summary>HistoryStore of InputStoreTests.</summary>
public sealed class HistoryStoreTests : IDisposable
{
    readonly TemporaryFolder folder = new();
    readonly ManualTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_789_000_000));

    public void Dispose() => folder.Dispose();

    HistoryStore Open() => new(folder.Paths, ImmediateMainThread.Shared, clock);

    HistoryEntry Entry(string text, double daysAgo, string? audio = null) => new()
    {
        Date = clock.GetUtcNow().AddDays(-daysAgo),
        Text = text,
        RawText = text,
        EngineId = EngineId.GigaamRnnt,
        LanguageMode = LanguageMode.Mixed,
        AppBundleId = "notepad.exe",
        AppName = "Notepad",
        Category = AppCategory.Other,
        Duration = 3,
        WordCount = HistoryEntry.CountWords(text),
        AudioFileName = audio,
    };

    string MakeRecording(string relativePath)
    {
        var path = folder.Paths.RecordingPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46]);
        return path;
    }

    [Fact]
    public void RoundTripKeepsNewestFirst()
    {
        var store = Open();
        Assert.Empty(store.Entries);
        Assert.Null(store.LoadWarning);
        var old = Entry("старая запись", 3);
        var recent = Entry("новая запись", 0);
        var middle = Entry("middle", 1);
        store.Add(old);
        store.Add(recent);
        store.Add(middle);
        Assert.Equal([recent.Id, middle.Id, old.Id], store.Entries.Select(e => e.Id));
        store.Flush();
        Assert.Equal([recent, middle, old], Open().Entries);
    }

    [Fact]
    public void SavesAfterTheDebounce()
    {
        var store = Open();
        store.Add(Entry("hello", 0));
        Assert.False(File.Exists(folder.Paths.HistoryFile));
        clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.True(File.Exists(folder.Paths.HistoryFile));
        Assert.Single(Open().Entries);
    }

    [Fact]
    public void ChangesAreAnnounced()
    {
        var store = Open();
        var changes = new List<string?>();
        ((INotifyPropertyChanged)store).PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var before = store.Entries;
        store.Add(Entry("a", 0));
        Assert.Equal([nameof(IHistoryStore.Entries)], changes);
        Assert.NotSame(before, store.Entries);
        store.Delete(Guid.NewGuid());
        Assert.Single(changes);
    }

    [Fact]
    public void AddWithExistingIdReplacesAndUpdateResorts()
    {
        var store = Open();
        var first = Entry("one", 2);
        var second = Entry("two", 1);
        store.Add(first);
        store.Add(second);
        first = first with { Text = "one, edited" };
        store.Add(first);
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal("one, edited", store.Entries[^1].Text);

        first = first with { Date = clock.GetUtcNow() };
        store.Update(first);
        Assert.Equal([first.Id, second.Id], store.Entries.Select(e => e.Id));

        store.Update(Entry("unknown", 0));
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void DeleteRemovesAudioAndItsDayFolder()
    {
        var store = Open();
        var relative = $"2026-09-15/{Guid.NewGuid().ToString("D").ToUpperInvariant()}.wav";
        var audio = MakeRecording(relative);
        var withAudio = Entry("with audio", 0, relative);
        store.Add(withAudio);
        Assert.Equal(Path.GetFullPath(audio), store.AudioPath(withAudio));

        store.Delete(withAudio.Id);
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(audio));
        Assert.False(Directory.Exists(Path.GetDirectoryName(audio)));
        Assert.True(Directory.Exists(folder.Paths.RecordingsFolder));
    }

    [Fact]
    public void RemoveAudioKeepsTheEntry()
    {
        var store = Open();
        var path = $"2026-09-14/{Guid.NewGuid():D}.wav";
        var audio = MakeRecording(path);
        var other = MakeRecording($"2026-09-14/{Guid.NewGuid():D}.wav");
        var withAudio = Entry("keep me", 0, path);
        store.Add(withAudio);

        store.RemoveAudio(withAudio.Id);
        Assert.Null(store.Entries[0].AudioFileName);
        Assert.False(File.Exists(audio));
        Assert.True(File.Exists(other), "the day folder still has another recording");
        Assert.Null(store.AudioPath(withAudio));
    }

    [Fact]
    public void AudioPathIsNullWhenTheFileIsGone()
    {
        var store = Open();
        Assert.Null(store.AudioPath(Entry("missing", 0, "2026-01-01/nothing.wav")));
        Assert.Null(store.AudioPath(Entry("no audio", 0)));
    }

    [Fact]
    public void NeverTouchesFilesOutsideRecordings()
    {
        var store = Open();
        var outside = Path.Combine(folder.Root, "secret.txt");
        File.WriteAllText(outside, "keep");
        foreach (var hostilePath in new[] { "../secret.txt", outside, "2026-01-01/../../secret.txt" })
        {
            var hostile = Entry("hostile", 0, hostilePath);
            store.Add(hostile);
            Assert.Null(store.AudioPath(hostile));
            store.RemoveAudio(hostile.Id);
            store.Delete(hostile.Id);
            Assert.True(File.Exists(outside));
        }
    }

    [Fact]
    public void DeleteAllClearsEntriesAndRecordingsAndSaves()
    {
        var store = Open();
        var path = $"2026-09-15/{Guid.NewGuid():D}.wav";
        MakeRecording(path);
        MakeRecording("2026-09-01/orphan.wav");
        store.Add(Entry("a", 0, path));
        store.Add(Entry("b", 1));

        store.DeleteAll();
        Assert.Empty(store.Entries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Paths.RecordingsFolder));
        Assert.Empty(Open().Entries);
        Assert.True(File.Exists(folder.Paths.HistoryFile), "deleteAll saves at once");
    }

    [Fact]
    public void SearchIgnoresCaseAndYo()
    {
        var store = Open();
        var tree = Entry("Купили ЁЛКУ на рынке", 0);
        var hedgehog = Entry("ежик в тумане", 1);
        var english = Entry("Deploy the Kubernetes cluster", 2);
        foreach (var entry in new[] { tree, hedgehog, english })
        {
            store.Add(entry);
        }
        Assert.Equal([tree.Id], store.Search("елку").Select(e => e.Id));
        Assert.Equal([hedgehog.Id], store.Search("Ёжик").Select(e => e.Id));
        Assert.Equal([english.Id], store.Search("kubernetes").Select(e => e.Id));
        Assert.Equal(3, store.Search("  ").Count);
        Assert.Equal(3, store.Search("").Count);
        Assert.Empty(store.Search("postgres"));
    }

    [Fact]
    public void GroupsByDayNewestFirst()
    {
        var calendar = new DayCalendar(TimeZoneInfo.Utc, DayOfWeek.Monday);
        var dayStart = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000 - 1_789_000_000 % 86_400);
        var morning = Entry("morning", 0) with { Date = dayStart.AddHours(8) };
        var evening = Entry("evening", 0) with { Date = dayStart.AddHours(20) };
        var yesterday = Entry("yesterday", 0) with { Date = dayStart.AddHours(-1) };

        var groups = HistoryStore.GroupedByDay([morning, yesterday, evening], calendar);
        Assert.Equal(2, groups.Count);
        Assert.Equal(dayStart, groups[0].Day);
        Assert.Equal([evening.Id, morning.Id], groups[0].Entries.Select(e => e.Id));
        Assert.Equal(dayStart.AddDays(-1), groups[1].Day);
        Assert.Equal([yesterday.Id], groups[1].Entries.Select(e => e.Id));
        Assert.Empty(HistoryStore.GroupedByDay([], calendar));
    }

    [Fact]
    public void GroupsByTheLocalDayOfTheCalendar()
    {
        var moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        var calendar = new DayCalendar(moscow, DayOfWeek.Monday);
        // 22:30 UTC is already the next day in Moscow (UTC+3).
        var late = Entry("late", 0) with { Date = new DateTimeOffset(2026, 9, 15, 22, 30, 0, TimeSpan.Zero) };
        var group = Assert.Single(HistoryStore.GroupedByDay([late], calendar));
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.FromHours(3)), group.Day);
    }

    [Fact]
    public void SurvivesACorruptFile()
    {
        File.WriteAllText(folder.Paths.HistoryFile, "[{\"id\": 42}]");
        var store = Open();
        Assert.Empty(store.Entries);
        Assert.Contains("history.json", store.LoadWarning);
        Assert.Single(folder.Files("history.corrupt-"));

        store.Add(Entry("fresh start", 0));
        store.Flush();
        Assert.Single(Open().Entries);
    }

    [Fact]
    public void KeepsReadableEntriesAndTheOriginalFile()
    {
        var good = Entry("хорошая запись", 0);
        var newer = Entry("запись из новой версии", 0);
        var array = System.Text.Json.Nodes.JsonNode.Parse(Json.SlovoJson.Serialize(new List<HistoryEntry> { good, newer }))!.AsArray();
        // An engine a newer build knows: this one entry cannot be decoded.
        array[1]!["engineID"] = "whisper-v9";
        File.WriteAllText(folder.Paths.HistoryFile, array.ToJsonString());
        var original = File.ReadAllBytes(folder.Paths.HistoryFile);

        var store = Open();
        Assert.Equal([good], store.Entries);
        var warning = Assert.IsType<string>(store.LoadWarning);
        Assert.Contains("history.json", warning);
        var backup = Assert.Single(folder.Files("history.corrupt-"));
        Assert.Contains(backup, warning);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(folder.Root, backup)));
        Assert.Equal(original, File.ReadAllBytes(folder.Paths.HistoryFile));

        store.Add(Entry("новая", 0));
        store.Flush();
        var reopened = Open();
        Assert.Equal(2, reopened.Entries.Count);
        Assert.Null(reopened.LoadWarning);
    }

    [Fact]
    public void RecordingsLiveUnderTheirLocalDay()
    {
        var id = Guid.NewGuid();
        var relative = WavFile.RelativePath(id, clock.GetUtcNow());
        var store = Open();
        var path = MakeRecording(relative);
        var entry = Entry("a", 0, relative);
        store.Add(entry);
        Assert.Equal(Path.GetFullPath(path), store.AudioPath(entry));
        Assert.EndsWith($"{id.ToString("D").ToUpperInvariant()}.wav", relative);
    }
}
