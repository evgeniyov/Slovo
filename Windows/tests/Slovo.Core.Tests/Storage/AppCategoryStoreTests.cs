using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Storage;

namespace Slovo.Core.Tests.Storage;

/// <summary>AppCategoryStore of InputStoreTests, with the Windows executables.</summary>
public sealed class AppCategoryStoreTests : IDisposable
{
    readonly TemporaryFolder folder = new();
    readonly ManualTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_789_000_000));

    public void Dispose() => folder.Dispose();

    AppCategoryStore Open(Func<string, string?>? installedNames = null) =>
        new(folder.Paths, ImmediateMainThread.Shared, clock, installedNames ?? (_ => null));

    [Fact]
    public void DefaultCategoriesCoverTheSpecOnBothSystems()
    {
        var expected = new Dictionary<string, AppCategory>
        {
            // Windows.
            ["telegram.exe"] = AppCategory.Personal, ["whatsapp.exe"] = AppCategory.Personal, ["whatsapp.root.exe"] = AppCategory.Personal,
            ["discord.exe"] = AppCategory.Personal, ["slack.exe"] = AppCategory.Work, ["ms-teams.exe"] = AppCategory.Work,
            ["teams.exe"] = AppCategory.Work, ["zoom.exe"] = AppCategory.Work, ["bitrix24.exe"] = AppCategory.Work,
            ["telemost.exe"] = AppCategory.Work, ["outlook.exe"] = AppCategory.Email, ["olk.exe"] = AppCategory.Email,
            ["thunderbird.exe"] = AppCategory.Email, ["mailspring.exe"] = AppCategory.Email,
            // macOS, for a file copied from a Mac.
            ["ru.keepcoder.Telegram"] = AppCategory.Personal, ["org.telegram.desktop"] = AppCategory.Personal,
            ["com.apple.MobileSMS"] = AppCategory.Personal, ["net.whatsapp.WhatsApp"] = AppCategory.Personal,
            ["com.hnc.Discord"] = AppCategory.Personal, ["com.tinyspeck.slackmacgap"] = AppCategory.Work,
            ["com.microsoft.teams2"] = AppCategory.Work, ["us.zoom.xos"] = AppCategory.Work,
            ["com.apple.mail"] = AppCategory.Email, ["com.microsoft.Outlook"] = AppCategory.Email,
        };
        foreach (var (bundleId, category) in expected)
        {
            Assert.Equal(category, AppCategoryStore.DefaultCategories[bundleId]);
        }
        Assert.All(AppCategoryStore.DefaultCategories.Keys.Where(key => key.EndsWith(".exe", StringComparison.Ordinal)),
                   key => Assert.Equal(key.ToLowerInvariant(), key));
    }

    [Theory]
    [InlineData("com.example.Bitrix", "Bitrix24", AppCategory.Work)]
    [InlineData(null, "Yandex.Telemost", AppCategory.Work)]
    [InlineData(null, "Яндекс Телемост", AppCategory.Work)]
    [InlineData("com.readdle.SparkDesktop", "Spark Desktop.app", AppCategory.Email)]
    [InlineData("spark.exe", "Spark Desktop.exe", AppCategory.Email)]
    [InlineData("com.example.sparkle", "Sparkle", AppCategory.Other)]
    [InlineData("Telegram.exe", "Telegram", AppCategory.Personal)]
    [InlineData("TELEMOST.EXE", null, AppCategory.Work)]
    [InlineData("notepad.exe", "Notepad", AppCategory.Other)]
    public void DefaultCategoryByIdentifierThenName(string? bundleId, string? name, AppCategory expected) =>
        Assert.Equal(expected, AppCategoryStore.DefaultCategory(bundleId, name));

    [Fact]
    public void CategoryLookupOrder()
    {
        var store = Open(bundleId => bundleId is "com.readdle.SparkDesktop" or "sparkdesktop.exe" ? "Spark Desktop" : null);
        Assert.Equal(AppCategory.Other, store.CategoryFor((string?)null));
        Assert.Equal(AppCategory.Other, store.CategoryFor(""));
        Assert.Equal(AppCategory.Personal, store.CategoryFor("ru.keepcoder.Telegram"));
        Assert.Equal(AppCategory.Personal, store.CategoryFor("telegram.exe"));
        Assert.Equal(AppCategory.Work, store.CategoryFor("com.tinyspeck.slackmacgap"));
        Assert.Equal(AppCategory.Work, store.CategoryFor("ms-teams.exe"));
        Assert.Equal(AppCategory.Email, store.CategoryFor("com.apple.mail"));
        Assert.Equal(AppCategory.Email, store.CategoryFor("olk.exe"));
        Assert.Equal(AppCategory.Email, store.CategoryFor("com.readdle.SparkDesktop"));
        Assert.Equal(AppCategory.Email, store.CategoryFor("sparkdesktop.exe"));
        Assert.Equal(AppCategory.Other, store.CategoryFor("devenv.exe"));
        Assert.Equal(AppCategory.Work, store.CategoryFor(new AppIdentity("com.bitrix.other", "Битрикс24")));
        Assert.Equal(AppCategory.Work, store.CategoryFor(new AppIdentity("b24.exe", "Bitrix24")));
    }

    [Fact]
    public void InstalledNamesAreLookedUpOncePerApp()
    {
        var lookups = 0;
        var store = Open(_ => { lookups++; return null; });
        store.CategoryFor("devenv.exe");
        store.CategoryFor("devenv.exe");
        store.CategoryFor(new AppIdentity("devenv.exe", "Visual Studio"));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void UserAssignmentsOverrideDefaultsAndPersist()
    {
        var store = Open();
        var telegram = new AppIdentity("telegram.exe", "Telegram");
        var editor = new AppIdentity("code.exe", "Visual Studio Code");
        var changes = 0;
        store.PropertyChanged += (_, _) => changes++;
        store.SetCategory(AppCategory.Work, telegram);
        store.SetCategory(AppCategory.Work, editor);
        store.SetCategory(AppCategory.Email, editor);
        store.SetCategory(AppCategory.Email, editor);
        Assert.Equal(3, changes);
        Assert.Equal(2, store.Assignments.Count);
        Assert.Equal(AppCategory.Work, store.CategoryFor(telegram.BundleId));
        Assert.Equal(AppCategory.Email, store.CategoryFor(editor));
        store.Flush();

        var reopened = Open();
        Assert.Equal(store.Assignments, reopened.Assignments);
        Assert.Equal(AppCategory.Email, reopened.CategoryFor("code.exe"));
    }

    [Fact]
    public void AFileCopiedFromAMacStillWorks()
    {
        File.WriteAllText(folder.Paths.AppCategoriesFile, """
            [
              {
                "appName" : "Slack",
                "bundleID" : "com.tinyspeck.slackmacgap",
                "category" : "personal"
              }
            ]
            """);
        var store = Open();
        Assert.Null(store.LoadWarning);
        Assert.Equal(AppCategory.Personal, store.CategoryFor("com.tinyspeck.slackmacgap"));
        Assert.Equal(AppCategory.Work, store.CategoryFor("slack.exe"));
    }

    [Fact]
    public void AppsInCategoryComeFromHistoryAndAssignments()
    {
        var store = Open();
        HistoryEntry Entry(string text, double daysAgo, (string Id, string Name)? app) => new()
        {
            Date = clock.GetUtcNow().AddDays(-daysAgo), Text = text, RawText = text, AppBundleId = app?.Id, AppName = app?.Name,
        };
        HistoryEntry[] history =
        [
            Entry("a", 0, ("slack.exe", "Slack")),
            Entry("b", 1, ("slack.exe", "Slack (old name)")),
            Entry("c", 0, ("zoom.exe", "Zoom")),
            Entry("d", 0, ("devenv.exe", "Visual Studio")),
            Entry("e", 0, ("bitrix24.exe", "Bitrix24")),
            Entry("f", 0, null),
        ];
        store.SetCategory(AppCategory.Work, new AppIdentity("linear.exe", "Linear"));
        store.SetCategory(AppCategory.Personal, new AppIdentity("zoom.exe", "Zoom"));

        Assert.Equal(["Bitrix24", "Linear", "Slack"], store.Apps(AppCategory.Work, history).Select(app => app.Name));
        Assert.Equal(["zoom.exe"], store.Apps(AppCategory.Personal, history).Select(app => app.BundleId));
        Assert.Equal(["Visual Studio"], store.Apps(AppCategory.Other, history).Select(app => app.Name));
        Assert.Empty(store.Apps(AppCategory.Email, history));
    }

    [Fact]
    public void NamesSortLikeFinder()
    {
        Assert.True(AppCategoryStore.LocalizedStandardCompare("app 2", "App 10") < 0);
        Assert.True(AppCategoryStore.LocalizedStandardCompare("bitrix24", "Linear") < 0);
    }
}
