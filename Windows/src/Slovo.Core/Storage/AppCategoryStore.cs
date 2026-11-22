using System.Globalization;
using Slovo.Core.Model;
using Slovo.Core.Platform;
using Slovo.Core.Text;

namespace Slovo.Core.Storage;

/// <summary>
/// <c>app-categories.json</c>: the user's category choices over the built-in defaults (port of
/// the macOS <c>AppCategoryStore</c>). On Windows an app is identified by its lower-case
/// executable name (<c>telegram.exe</c>); the macOS bundle ids stay in the defaults so a
/// file copied from a Mac still means the same. Use it from the UI thread.
/// </summary>
public sealed class AppCategoryStore : Observable, IAppCategoryStore
{
    readonly StoreFile<List<AppCategoryAssignment>> storage;
    readonly Func<string, string?> installedNameLookup;
    readonly Dictionary<string, string?> installedNames = new(StringComparer.Ordinal);
    IReadOnlyList<AppCategoryAssignment> assignments;

    /// <param name="installedNameLookup">
    /// The display name of an installed app by its identifier, when the platform can tell
    /// (the file description of an executable, say); null by default. Looked up once per app.
    /// </param>
    public AppCategoryStore(AppPaths paths, IMainThread mainThread, TimeProvider? time = null, Func<string, string?>? installedNameLookup = null)
    {
        this.installedNameLookup = installedNameLookup ?? (_ => null);
        storage = new StoreFile<List<AppCategoryAssignment>>(paths.AppCategoriesFile, mainThread, time);
        LoadWarning = storage.LoadWarning;
        assignments = storage.InitialValue ?? [];
    }

    /// <summary>
    /// Built-in categories by app identifier. Windows executables are lower-case file names;
    /// the macOS bundle ids (Telegram, Slack, Mail and the rest) are kept for files copied
    /// from a Mac. Other builds of these apps are caught by <see cref="DefaultCategoriesByName"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AppCategory> DefaultCategories = new Dictionary<string, AppCategory>(StringComparer.Ordinal)
    {
        // Windows executables.
        ["telegram.exe"] = AppCategory.Personal,
        ["whatsapp.exe"] = AppCategory.Personal,
        ["whatsapp.root.exe"] = AppCategory.Personal,
        ["discord.exe"] = AppCategory.Personal,
        // Phone Link, where Windows reads and sends text messages (the Messages of macOS).
        ["phoneexperiencehost.exe"] = AppCategory.Personal,
        ["slack.exe"] = AppCategory.Work,
        ["ms-teams.exe"] = AppCategory.Work,
        ["teams.exe"] = AppCategory.Work,
        ["zoom.exe"] = AppCategory.Work,
        ["bitrix24.exe"] = AppCategory.Work,
        // Яндекс Телемост.
        ["telemost.exe"] = AppCategory.Work,
        ["outlook.exe"] = AppCategory.Email,
        // The new Outlook for Windows.
        ["olk.exe"] = AppCategory.Email,
        // Mail and Calendar, the mail app of Windows 10 and early Windows 11.
        ["hxoutlook.exe"] = AppCategory.Email,
        ["thunderbird.exe"] = AppCategory.Email,
        ["mailspring.exe"] = AppCategory.Email,
        // macOS bundle ids.
        ["ru.keepcoder.Telegram"] = AppCategory.Personal,
        ["org.telegram.desktop"] = AppCategory.Personal,
        ["com.tdesktop.Telegram"] = AppCategory.Personal,
        ["com.apple.MobileSMS"] = AppCategory.Personal,
        ["net.whatsapp.WhatsApp"] = AppCategory.Personal,
        ["com.hnc.Discord"] = AppCategory.Personal,
        ["com.tinyspeck.slackmacgap"] = AppCategory.Work,
        ["com.microsoft.teams2"] = AppCategory.Work,
        ["us.zoom.xos"] = AppCategory.Work,
        ["ru.bitrix.bitrix24desktop"] = AppCategory.Work,
        ["ru.yandex.desktop.telemost"] = AppCategory.Work,
        ["com.apple.mail"] = AppCategory.Email,
        ["com.microsoft.Outlook"] = AppCategory.Email,
    };

    /// <summary>
    /// Apps matched by normalized name (lowercased, ".app" or ".exe" dropped, dots, dashes and
    /// underscores read as spaces) when the identifier is not in <see cref="DefaultCategories"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AppCategory> DefaultCategoriesByName = new Dictionary<string, AppCategory>(StringComparer.Ordinal)
    {
        ["bitrix24"] = AppCategory.Work,
        ["битрикс24"] = AppCategory.Work,
        ["yandex telemost"] = AppCategory.Work,
        ["яндекс телемост"] = AppCategory.Work,
        ["telemost"] = AppCategory.Work,
        ["телемост"] = AppCategory.Work,
        ["spark"] = AppCategory.Email,
        ["spark desktop"] = AppCategory.Email,
        ["spark mail"] = AppCategory.Email,
    };

    /// <summary>Set when <c>app-categories.json</c> was damaged and moved aside at launch; localized.</summary>
    public string? LoadWarning { get; }

    public IReadOnlyList<AppCategoryAssignment> Assignments => assignments;

    /// <summary>The user's assignment, else the built-in default, else Other.</summary>
    public AppCategory CategoryFor(string? bundleId)
    {
        if (string.IsNullOrEmpty(bundleId))
        {
            return AppCategory.Other;
        }
        if (AssignedCategory(bundleId) is { } assigned)
        {
            return assigned;
        }
        return DefaultCategory(bundleId, InstalledName(bundleId));
    }

    /// <summary>Like <see cref="CategoryFor(string?)"/>, also using the name the app reported, which matches apps no longer installed.</summary>
    public AppCategory CategoryFor(AppIdentity app)
    {
        if (AssignedCategory(app.BundleId) is { } assigned)
        {
            return assigned;
        }
        var reported = DefaultCategory(app.BundleId, app.Name);
        if (reported != AppCategory.Other)
        {
            return reported;
        }
        return DefaultCategory(app.BundleId, InstalledName(app.BundleId));
    }

    public void SetCategory(AppCategory category, AppIdentity app)
    {
        var assignment = new AppCategoryAssignment(app.BundleId, app.Name, category);
        var list = assignments.ToList();
        var index = list.FindIndex(existing => existing.BundleId == app.BundleId);
        if (index >= 0)
        {
            if (list[index] == assignment)
            {
                return;
            }
            list[index] = assignment;
        }
        else
        {
            list.Add(assignment);
        }
        assignments = list;
        OnPropertyChanged(nameof(Assignments));
        storage.ScheduleSave(list);
    }

    /// <summary>Writes pending changes now. Throws SlovoException when the file cannot be written.</summary>
    public void Flush() => storage.Flush();

    /// <summary>
    /// Apps in a category among those seen in history, sorted by name. Apps the user assigned
    /// a category stay listed even after their history is gone.
    /// </summary>
    public IReadOnlyList<AppIdentity> Apps(AppCategory category, IEnumerable<HistoryEntry> history)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in history)
        {
            if (string.IsNullOrEmpty(entry.AppBundleId) || names.ContainsKey(entry.AppBundleId))
            {
                continue;
            }
            var name = entry.AppName?.Trim() ?? "";
            names[entry.AppBundleId] = name.Length == 0 ? entry.AppBundleId : name;
        }
        foreach (var assignment in assignments)
        {
            if (!names.ContainsKey(assignment.BundleId))
            {
                names[assignment.BundleId] = assignment.AppName.Length == 0 ? assignment.BundleId : assignment.AppName;
            }
        }
        return names
            .Select(pair => new AppIdentity(pair.Key, pair.Value))
            .Where(app => CategoryFor(app) == category)
            .Order(Comparer<AppIdentity>.Create((lhs, rhs) =>
            {
                var order = LocalizedStandardCompare(lhs.Name, rhs.Name);
                return order != 0 ? order : string.CompareOrdinal(lhs.BundleId, rhs.BundleId);
            }))
            .ToList();
    }

    /// <summary>The built-in category for an app: by identifier, else by name, else Other.</summary>
    public static AppCategory DefaultCategory(string? bundleId, string? name)
    {
        if (bundleId is not null)
        {
            if (DefaultCategories.TryGetValue(bundleId, out var category))
            {
                return category;
            }
            // An executable name reported with its original casing: Telegram.exe.
            if (bundleId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && DefaultCategories.TryGetValue(bundleId.ToLowerInvariant(), out category))
            {
                return category;
            }
        }
        if (name is not null && DefaultCategoriesByName.TryGetValue(NormalizedName(name), out var byName))
        {
            return byName;
        }
        return AppCategory.Other;
    }

    internal static string NormalizedName(string name)
    {
        var text = SwiftUnicode.Lowercased(SwiftString.Canonical(name));
        if (text.EndsWith(".app", StringComparison.Ordinal) || text.EndsWith(".exe", StringComparison.Ordinal))
        {
            text = text[..^4];
        }
        var parts = text.Split(c => c is '.' or '-' or '_' || char.IsWhiteSpace(c));
        return string.Join(' ', parts.Where(part => part.Length > 0));
    }

    /// <summary>Finder's order (<c>localizedStandardCompare</c>): case-insensitive, numbers by value, in the user's language.</summary>
    internal static int LocalizedStandardCompare(string lhs, string rhs)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            return culture.CompareInfo.Compare(lhs, rhs, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.NumericOrdering);
        }
        catch (Exception error) when (error is ArgumentException or PlatformNotSupportedException)
        {
            return culture.CompareInfo.Compare(lhs, rhs, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth);
        }
    }

    AppCategory? AssignedCategory(string bundleId) =>
        assignments.FirstOrDefault(assignment => assignment.BundleId == bundleId)?.Category;

    string? InstalledName(string bundleId)
    {
        if (!installedNames.TryGetValue(bundleId, out var name))
        {
            name = installedNameLookup(bundleId);
            installedNames[bundleId] = name;
        }
        return name;
    }
}

file static class SplitExtensions
{
    /// <summary>Splits on every character that satisfies the predicate.</summary>
    public static string[] Split(this string text, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (isSeparator(text[index]))
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }
        parts.Add(text[start..]);
        return [.. parts];
    }
}
