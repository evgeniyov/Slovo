using System.Globalization;
using System.Reflection;
using Slovo.Core.Model;

namespace Slovo.Core.Localization;

/// <summary>
/// String tables for one interface language, built from embedded resources: the tables
/// shared with the macOS app first, then the Windows tables that replace lines naming
/// macOS and add Windows-only keys.
/// </summary>
public sealed class StringTable
{
    readonly Dictionary<string, string> strings;
    readonly Dictionary<string, PluralEntry> plurals;

    public StringTable(string languageCode, Dictionary<string, string> strings, Dictionary<string, PluralEntry> plurals)
    {
        LanguageCode = languageCode;
        this.strings = strings;
        this.plurals = plurals;
        Culture = CultureInfo.GetCultureInfo(languageCode);
    }

    public string LanguageCode { get; }
    public CultureInfo Culture { get; }
    public IReadOnlyCollection<string> Keys => strings.Keys;
    public IReadOnlyCollection<string> PluralKeys => plurals.Keys;

    public bool Contains(string key) => strings.ContainsKey(key) || plurals.ContainsKey(key);

    /// <summary>The string for a key, or the key itself when it is missing.</summary>
    public string Get(string key) => strings.TryGetValue(key, out var value) ? value : key;

    public string Format(string key, params object?[] arguments) =>
        AppleFormat.Format(Get(key), Culture, arguments);

    /// <summary>
    /// A plural entry filled like the macOS <c>Localizer.plural</c>: argument 1 is the
    /// count (it picks the form), argument 2 the count with digit grouping.
    /// </summary>
    public string Plural(string key, long count)
    {
        var grouped = count.ToString("N0", Culture);
        if (!plurals.TryGetValue(key, out var entry))
        {
            return strings.TryGetValue(key, out var plain) ? AppleFormat.Format(plain, Culture, count, grouped) : key;
        }
        var category = PluralRules.Category(LanguageCode, count);
        if (!entry.Forms.TryGetValue(category, out var form) && !entry.Forms.TryGetValue("other", out form))
        {
            return key;
        }
        return AppleFormat.Format(form, Culture, count, grouped);
    }

    /// <summary>
    /// Loads every embedded table whose logical name starts with one of the prefixes
    /// (for example <c>Slovo.Strings.Core/</c>), later prefixes overriding earlier ones.
    /// Resource names look like <c>&lt;prefix&gt;en.lproj/Meetings.strings</c> or
    /// <c>&lt;prefix&gt;en/Meetings.strings</c>.
    /// </summary>
    public static StringTable Load(string languageCode, IEnumerable<(Assembly Assembly, string Prefix)> sources)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var plurals = new Dictionary<string, PluralEntry>(StringComparer.Ordinal);
        foreach (var (assembly, prefix) in sources)
        {
            foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var rest = name[prefix.Length..].Replace('\\', '/');
                var folder = rest.Split('/')[0];
                var code = folder.EndsWith(".lproj", StringComparison.Ordinal) ? folder[..^6] : folder;
                if (code != languageCode)
                {
                    continue;
                }
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    continue;
                }
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                if (name.EndsWith(".stringsdict", StringComparison.Ordinal))
                {
                    foreach (var pair in StringsFile.ParseStringsDict(content))
                    {
                        plurals[pair.Key] = pair.Value;
                    }
                }
                else
                {
                    foreach (var pair in StringsFile.Parse(content))
                    {
                        strings[pair.Key] = pair.Value;
                    }
                }
            }
        }
        return new StringTable(languageCode, strings, plurals);
    }
}

/// <summary>
/// The language Slovo shows, readable from any thread (engines report errors from
/// background threads, as SlovoCore's <c>CoreStrings</c> does on macOS).
/// </summary>
public static class CoreStrings
{
    static readonly Lock Gate = new();
    static readonly Dictionary<string, StringTable> Tables = [];
    static volatile string currentCode = "en";

    public static readonly string[] SupportedCodes = ["en", "ru"];

    /// <summary>Raised after the language changes, on the thread that changed it.</summary>
    public static event Action? LanguageChanged;

    public static string CurrentCode => currentCode;

    /// <summary>The Core tables for the current language.</summary>
    public static StringTable Table => TableFor(currentCode);

    public static void SetLanguage(AppLanguage language)
    {
        var code = ResolveCode(language);
        if (code == currentCode)
        {
            return;
        }
        currentCode = code;
        LanguageChanged?.Invoke();
    }

    /// <summary>The table code for a choice: the explicit one, or the system's when Slovo has it.</summary>
    public static string ResolveCode(AppLanguage language, CultureInfo? system = null) => language switch
    {
        AppLanguage.En => "en",
        AppLanguage.Ru => "ru",
        _ => SupportedCodes.Contains((system ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName)
            ? (system ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName
            : "en",
    };

    public static StringTable TableFor(string code)
    {
        lock (Gate)
        {
            if (!Tables.TryGetValue(code, out var table))
            {
                var assembly = typeof(CoreStrings).Assembly;
                table = StringTable.Load(code, [(assembly, "Slovo.Strings.Core/"), (assembly, "Slovo.Strings.CoreWindows/")]);
                Tables[code] = table;
            }
            return table;
        }
    }

    public static string Get(string key) => Table.Get(key);

    public static string Format(string key, params object?[] arguments) => Table.Format(key, arguments);
}
