using System.ComponentModel;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Settings;
using Slovo.Core.Storage;

namespace Slovo.Core.Tests.Storage;

/// <summary>DictionaryStore, SnippetStore and NoteStore of InputStoreTests.</summary>
public sealed class UserContentStoreTests : IDisposable
{
    readonly TemporaryFolder folder = new();
    readonly ManualTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_789_000_000));

    public void Dispose() => folder.Dispose();

    // MARK: - DictionaryStore

    DictionaryStore Dictionary() => new(folder.Paths, ImmediateMainThread.Shared, clock);

    [Fact]
    public void DictionaryRoundTripAndKinds()
    {
        var store = Dictionary();
        var term = DictionaryItem.Term("Kubernetes", clock.GetUtcNow());
        var replacement = DictionaryItem.ReplacementRule("гитхаб", "GitHub", clock.GetUtcNow());
        store.Add(term);
        store.Add(replacement);
        Assert.Equal([term.Id], store.Terms.Select(item => item.Id));
        Assert.Equal([replacement.Id], store.Replacements.Select(item => item.Id));

        term = term with { Text = "Kubernetes (k8s)" };
        store.Update(term);
        store.Add(term);
        Assert.Equal(2, store.Items.Count);
        store.Flush();

        var reopened = Dictionary();
        Assert.Equal([term, replacement], reopened.Items);
        reopened.Delete(replacement.Id);
        reopened.Flush();
        Assert.Equal([term], Dictionary().Items);
    }

    [Fact]
    public void DictionarySeedsOnceAndDeletedTermsStayDeleted()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var store = Dictionary();
        store.Add(DictionaryItem.Term("docker"));
        store.SeedIfNeeded(settings, ["Kubernetes", "Docker", " Postgres ", "", "kubernetes"]);
        Assert.True(settings.DictionarySeeded);
        Assert.Equal(["docker", "Kubernetes", "Postgres"], store.Terms.Select(item => item.Text));
        Assert.True(File.Exists(folder.Paths.DictionaryFile), "the seed is written at once");

        var kubernetes = store.Terms.Single(item => item.Text == "Kubernetes");
        store.Delete(kubernetes.Id);
        store.SeedIfNeeded(settings, ["Kubernetes"]);
        Assert.DoesNotContain(store.Terms, item => item.Text == "Kubernetes");
        store.Flush();
        Assert.Equal(["docker", "Postgres"], Dictionary().Terms.Select(item => item.Text));
    }

    [Fact]
    public void AnOlderSeededDictionaryGetsOnlyTheNewTermsOnce()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var store = Dictionary();
        // Seeded by version 1: only later additions are eligible.
        store.Add(DictionaryItem.Term("Kubernetes"));
        settings.DictionarySeeded = true;
        settings.DictionarySeedVersion = 1;
        store.SeedIfNeeded(settings, ["Kubernetes", "Docker"]);
        string[] newerTerms = ["iOS", "AppKit", "ChatGPT", "OpenAI", "DeepSeek"];
        Assert.Equal(["Kubernetes", "GitHub", ..newerTerms], store.Terms.Select(item => item.Text));
        Assert.Equal(DictionarySeed.LatestVersion, settings.DictionarySeedVersion);

        // Deleting it afterwards is final.
        store.Delete(store.Terms.Single(item => item.Text == "GitHub").Id);
        store.SeedIfNeeded(settings, ["Kubernetes", "Docker"]);
        Assert.Equal(["Kubernetes", ..newerTerms], store.Terms.Select(item => item.Text));
    }

    [Fact]
    public void AFirstSeedIsAlreadyTheLatestVersion()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var store = Dictionary();
        store.SeedIfNeeded(settings, ["Kubernetes"]);
        Assert.Equal(DictionarySeed.LatestVersion, settings.DictionarySeedVersion);
        Assert.Equal(["Kubernetes"], store.Terms.Select(item => item.Text));
    }

    [Fact]
    public void EveryAddedTermIsInTheBundledVocabulary()
    {
        var bundled = DictionarySeed.BundledTerms().ToHashSet();
        foreach (var (version, terms) in DictionarySeed.Additions)
        {
            Assert.True(version > 1);
            Assert.All(terms, term => Assert.Contains(term, bundled));
        }
    }

    [Fact]
    public void DictionaryDoesNotMarkSeededWithoutTerms()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var store = Dictionary();
        store.SeedIfNeeded(settings, []);
        Assert.False(settings.DictionarySeeded);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void AFailedSeedIsTriedAgainNextLaunch()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        var blocked = new AppPaths(Path.Combine(folder.Root, "blocker"));
        File.WriteAllText(blocked.Base, "x");
        var store = new DictionaryStore(blocked, ImmediateMainThread.Shared, clock);
        store.SeedIfNeeded(settings, ["Kubernetes"]);
        Assert.False(settings.DictionarySeeded);
        Assert.Single(store.Items);
    }

    [Fact]
    public void VocabularyParsingSkipsCommentsBlanksAndDuplicates()
    {
        const string text = "# Комментарий\n\nKubernetes\n  GitHub Actions  \n#API\nkubernetes\r\nuser id\n";
        Assert.Equal(["Kubernetes", "GitHub Actions", "user id"], DictionarySeed.TermsFromVocabulary(text));
    }

    [Fact]
    public void BundledVocabularyIsTheMacOSOne()
    {
        var terms = DictionarySeed.BundledTerms();
        Assert.Contains("Kubernetes", terms);
        Assert.Contains("GitHub Actions", terms);
        Assert.Contains("UserDefaults", terms);
        Assert.DoesNotContain(terms, term => term.StartsWith('#') || term.Length == 0);
        // The file the Swift package bundles, when the repository is at hand.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "Sources", "SlovoCore", "Resources", "vocabulary.txt");
            if (File.Exists(source))
            {
                Assert.Equal(DictionarySeed.TermsFromVocabulary(File.ReadAllText(source)), terms);
                return;
            }
        }
    }

    // MARK: - SnippetStore

    SnippetStore Snippets() => new(folder.Paths, ImmediateMainThread.Shared, clock);

    [Fact]
    public void SnippetsRoundTripAndUpdateStamp()
    {
        var store = Snippets();
        var created = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var snippet = new Snippet { Trigger = "my email", Expansion = "me@example.com", CreatedAt = created, UpdatedAt = created };
        store.Add(snippet);
        store.Add(new Snippet { Trigger = "адрес", Expansion = "Москва", CreatedAt = created, UpdatedAt = created });
        Assert.Equal(2, store.Snippets.Count);

        clock.Advance(TimeSpan.FromSeconds(5));
        snippet = snippet with { Expansion = "me@example.org" };
        store.Update(snippet);
        var updated = store.Snippets.Single(s => s.Id == snippet.Id);
        Assert.Equal("me@example.org", updated.Expansion);
        Assert.Equal(clock.GetUtcNow(), updated.UpdatedAt);
        Assert.True(updated.UpdatedAt > created);
        store.Flush();

        var reopened = Snippets();
        Assert.Equal(store.Snippets, reopened.Snippets);
        reopened.Delete(snippet.Id);
        Assert.Equal(["адрес"], reopened.Snippets.Select(s => s.Trigger));
    }

    [Fact]
    public void AnUpdateThatSetsItsOwnStampKeepsIt()
    {
        var store = Snippets();
        var snippet = new Snippet { Trigger = "a", Expansion = "b", CreatedAt = AppleDateZero, UpdatedAt = AppleDateZero };
        store.Add(snippet);
        var stamped = snippet with { Expansion = "c", UpdatedAt = AppleDateZero.AddDays(1) };
        store.Update(stamped);
        Assert.Equal(stamped, store.Snippets[0]);
    }

    static readonly DateTimeOffset AppleDateZero = Json.AppleDate.ReferenceDate;

    [Fact]
    public void SnippetsSurviveACorruptFile()
    {
        File.WriteAllBytes(folder.Paths.SnippetsFile, [0xFF, 0xFE, 0x00]);
        var store = Snippets();
        Assert.Empty(store.Snippets);
        Assert.NotNull(store.LoadWarning);
    }

    // MARK: - NoteStore

    NoteStore Notes() => new(folder.Paths, ImmediateMainThread.Shared, clock);

    [Fact]
    public void NotesCreateUpdateOrderAndRoundTrip()
    {
        var store = Notes();
        var first = store.Create("Shopping list");
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = store.Create("Ideas");
        Assert.Equal([second.Id, first.Id], store.Notes.Select(n => n.Id));

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Update(first with { Text = "Shopping list\nmilk" });
        Assert.Equal([first.Id, second.Id], store.Notes.Select(n => n.Id));
        Assert.Equal("Shopping list", store.Notes[0].Title);
        store.Flush();

        var reopened = Notes();
        Assert.Equal(store.Notes, reopened.Notes);
        reopened.Delete(second.Id);
        Assert.Equal([first.Id], reopened.Notes.Select(n => n.Id));
    }

    [Fact]
    public void AnUnchangedNoteIsNotSaved()
    {
        var store = Notes();
        var note = store.Create("text");
        store.Flush();
        var changes = 0;
        ((INotifyPropertyChanged)store).PropertyChanged += (_, _) => changes++;
        store.Update(note);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void NoteAppendSeparatesDictations()
    {
        var store = Notes();
        var empty = store.Create("");
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Append("  первая фраза ", empty.Id);
        Assert.Equal("первая фраза", store.Notes.Single(n => n.Id == empty.Id).Text);
        store.Append("вторая", empty.Id);
        Assert.Equal("первая фраза вторая", store.Notes.Single(n => n.Id == empty.Id).Text);

        clock.Advance(TimeSpan.FromSeconds(1));
        var lines = store.Create("Line one\n");
        store.Append("line two", lines.Id);
        Assert.Equal("Line one\nline two", store.Notes.Single(n => n.Id == lines.Id).Text);

        var older = store.Notes[^1];
        store.Append("   ", older.Id);
        Assert.Equal(older, store.Notes[^1]);
        store.Append("text", Guid.NewGuid());

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Append("bumped", older.Id);
        Assert.Equal(older.Id, store.Notes[0].Id);
        Assert.Equal("a b", NoteStore.Joined("a", "b"));
        Assert.Equal("a b", NoteStore.Joined("a ", "b"));
        Assert.Equal("b", NoteStore.Joined("", "b"));
    }

    // MARK: - Recovery (InputStoreRecoveryTests)

    [Fact]
    public void EveryStoreDropsOnlyItsBadEntries()
    {
        var term = DictionaryItem.Term("Docker", AppleDateZero);
        Write(folder.Paths.DictionaryFile, $$"""
            [{"createdAt": 0, "id": "{{term.Id.ToString().ToUpperInvariant()}}", "kind": "term", "replacement": "", "text": "Docker"},
             {"createdAt": 0, "id": "{{Guid.NewGuid()}}", "kind": "synonym", "replacement": "", "text": "broken"}]
            """);
        Assert.Equal([term], Dictionary().Items);

        var snippet = new Snippet { Trigger = "адрес", Expansion = "Москва", CreatedAt = AppleDateZero, UpdatedAt = AppleDateZero };
        Write(folder.Paths.SnippetsFile, $$"""
            [{"createdAt": 0, "expansion": "Москва", "id": "{{snippet.Id}}", "trigger": "адрес", "updatedAt": 0},
             {"createdAt": 0, "id": "{{Guid.NewGuid()}}", "trigger": "x", "updatedAt": 0}]
            """);
        Assert.Equal([snippet], Snippets().Snippets);

        var note = new Note { Text = "список", CreatedAt = AppleDateZero, UpdatedAt = AppleDateZero };
        Write(folder.Paths.NotesFile, $$"""
            [{"createdAt": 0, "id": "{{Guid.NewGuid()}}", "text": 5, "updatedAt": 0},
             {"createdAt": 0, "id": "{{note.Id}}", "text": "список", "updatedAt": 0}]
            """);
        Assert.Equal([note], Notes().Notes);

        Write(folder.Paths.AppCategoriesFile, """
            [{"appName": "Slack", "bundleID": "com.tinyspeck.slackmacgap", "category": "work"},
             {"appName": "B", "bundleID": "a.b", "category": "gaming"}]
            """);
        var categories = new AppCategoryStore(folder.Paths, ImmediateMainThread.Shared, clock);
        Assert.Equal([new AppCategoryAssignment("com.tinyspeck.slackmacgap", "Slack", AppCategory.Work)], categories.Assignments);
        Assert.NotNull(categories.LoadWarning);
    }

    [Fact]
    public void ALockedFileIsNeverOverwritten()
    {
        const string original = "[]";
        File.WriteAllText(folder.Paths.NotesFile, original);
        NoteStore store;
        using (new FileStream(folder.Paths.NotesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = Notes();
        }
        Assert.Empty(store.Notes);
        Assert.Contains("notes.json", store.LoadWarning);
        store.Create("новая");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(original, File.ReadAllText(folder.Paths.NotesFile));
        var error = Assert.Throws<SlovoException>(store.Flush);
        Assert.Contains("notes.json", error.Message);
        Assert.Equal(original, File.ReadAllText(folder.Paths.NotesFile));
    }

    [Fact]
    public void AnUnreadableFileThatCannotBeMovedIsNeverOverwritten()
    {
        var damaged = "{ not json";
        File.WriteAllText(folder.Paths.SnippetsFile, damaged);
        SnippetStore store;
        if (OperatingSystem.IsWindows())
        {
            // An open handle without delete sharing keeps the file from being renamed.
            using (new FileStream(folder.Paths.SnippetsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                store = Snippets();
            }
        }
        else
        {
            // A read-only folder: the damaged file cannot be renamed out of the way.
            File.SetUnixFileMode(folder.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                store = Snippets();
            }
            finally
            {
                File.SetUnixFileMode(folder.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.Empty(store.Snippets);
        Assert.Contains("snippets.json", store.LoadWarning);
        Assert.Empty(folder.Files("snippets.corrupt-"));

        // The file can be moved again, but the store must still not replace the original.
        store.Add(new Snippet { Trigger = "адрес", Expansion = "Москва" });
        clock.Advance(TimeSpan.FromMilliseconds(800));
        Assert.Equal(damaged, File.ReadAllText(folder.Paths.SnippetsFile));
        var error = Assert.Throws<SlovoException>(store.Flush);
        Assert.Contains("snippets.json", error.Message);
        Assert.Equal(damaged, File.ReadAllText(folder.Paths.SnippetsFile));
    }

    static void Write(string path, string json) => File.WriteAllText(path, json);
}
