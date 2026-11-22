using System.Globalization;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.Core.Mcp;

/// <summary>
/// Slovo's settings as a second process sees them (port of the macOS <c>SlovoPreferences</c>).
///
/// The MCP process reads the app's <c>settings.json</c> again before every tool call, resource
/// read and prompt, so a switch changed in the running app applies to the next request. The
/// app replaces the file atomically, so a read sees the old or the new settings. Missing keys
/// use the <see cref="AppSettings"/> defaults. Nothing is ever written.
/// </summary>
public sealed class SlovoPreferences(string settingsFile)
{
    /// <summary>The settings file of this data folder.</summary>
    public string SettingsFile { get; } = settingsFile;

    public SlovoPreferences(AppPaths paths) : this(paths.SettingsFile)
    {
    }

    /// <summary>
    /// <c>integrations.mcp.enabled</c>, <c>integrations.mcp.writeEnabled</c> and
    /// <c>integrations.mcp.maxAgeDays</c>, re-read from disk. An age limit that is not one of
    /// the offered choices means all meetings, as it does in the app: a bad value never hides
    /// meetings the user expects Claude to see, and the access switch is what decides whether
    /// Claude sees any.
    /// </summary>
    public McpAccess Access()
    {
        var store = SettingsStore.ReadOnlySnapshot(SettingsFile);
        var enabled = Bool(store, SettingsKey.McpEnabled) ?? false;
        var canWrite = Bool(store, SettingsKey.McpWriteEnabled) ?? false;
        var days = Int(store, SettingsKey.McpMaxAgeDays) ?? 0;
        return new McpAccess(enabled, canWrite, AppSettings.NormalizedMcpMaxAgeDays(days));
    }

    /// <summary><c>app.language</c>, re-read from disk; unknown or missing values mean System.</summary>
    public AppLanguage Language()
    {
        var store = SettingsStore.ReadOnlySnapshot(SettingsFile);
        return EnumText.ParseOr(store.GetString(SettingsKey.AppLanguage), AppLanguage.System);
    }

    /// <summary>Accepts what <c>UserDefaults.bool(forKey:)</c> accepts: booleans, numbers and "YES"/"true"/"1".</summary>
    static bool? Bool(SettingsStore store, string key)
    {
        if (store.GetBool(key) is { } flag)
        {
            return flag;
        }
        if (store.GetDouble(key) is { } number)
        {
            return number != 0;
        }
        if (store.GetString(key) is { } text)
        {
            return McpUnicode.TrimHorizontalWhitespace(text).ToLowerInvariant() is "yes" or "true" or "1";
        }
        return null;
    }

    /// <summary>Accepts what <c>UserDefaults.integer(forKey:)</c> accepts: numbers, booleans and numeric strings.</summary>
    static int? Int(SettingsStore store, string key)
    {
        if (store.GetDouble(key) is { } number)
        {
            return double.IsFinite(number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
        }
        if (store.GetBool(key) is { } flag)
        {
            return flag ? 1 : 0;
        }
        if (store.GetString(key) is { } text
            && int.TryParse(McpUnicode.TrimHorizontalWhitespace(text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
