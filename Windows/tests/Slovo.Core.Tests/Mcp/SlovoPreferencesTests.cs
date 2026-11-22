using Slovo.Core.Mcp;
using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.Core.Tests.Mcp;

/// <summary>SlovoPreferences against a throwaway settings.json; nothing is ever written by the reader.</summary>
public sealed class SlovoPreferencesTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "SlovoPreferences-" + Guid.NewGuid().ToString("N"));

    public SlovoPreferencesTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    string SettingsFile => Path.Combine(folder, "settings.json");

    void WriteSettings(string json) => File.WriteAllText(SettingsFile, json);

    [Fact]
    public void MissingKeysMeanAccessOffAndTheSystemLanguage()
    {
        var preferences = new SlovoPreferences(SettingsFile);
        Assert.Equal(new McpAccess(false, false, 0), preferences.Access());
        Assert.Equal(AppLanguage.System, preferences.Language());
        Assert.False(File.Exists(SettingsFile), "reading never creates the file");
        WriteSettings("{ not json");
        Assert.Equal(new McpAccess(false, false, 0), preferences.Access());
        Assert.True(File.Exists(SettingsFile), "a damaged file is left where it is");
    }

    [Fact]
    public void ReadsWhatTheAppWritesOnEveryCall()
    {
        var preferences = new SlovoPreferences(SettingsFile);
        var settings = new AppSettings(SettingsStore.Open(SettingsFile));
        settings.McpEnabled = true;
        settings.McpMaxAgeDays = 30;
        settings.AppLanguage = AppLanguage.Ru;
        Assert.Equal(new McpAccess(true, false, 30), preferences.Access());
        Assert.Equal(AppLanguage.Ru, preferences.Language());
        settings.McpEnabled = false;
        settings.AppLanguage = AppLanguage.En;
        Assert.Equal(new McpAccess(false, false, 30), preferences.Access());
        Assert.Equal(AppLanguage.En, preferences.Language());
    }

    /// <summary>Writing is its own switch, and reading it does not turn reading on.</summary>
    [Fact]
    public void WritingIsASwitchOfItsOwn()
    {
        var preferences = new SlovoPreferences(SettingsFile);
        var settings = new AppSettings(SettingsStore.Open(SettingsFile));
        Assert.False(preferences.Access().CanWrite);
        settings.McpWriteEnabled = true;
        Assert.Equal(new McpAccess(false, true, 0), preferences.Access());
        settings.McpEnabled = true;
        Assert.Equal(new McpAccess(true, true, 0), preferences.Access());
        settings.McpWriteEnabled = false;
        Assert.Equal(new McpAccess(true, false, 0), preferences.Access());
    }

    [Fact]
    public void ToleratesOddStoredValues()
    {
        var preferences = new SlovoPreferences(SettingsFile);
        WriteSettings("""{"integrations.mcp.enabled": "YES", "integrations.mcp.maxAgeDays": "7", "integrations.mcp.writeEnabled": " true "}""");
        Assert.Equal(new McpAccess(true, true, 7), preferences.Access());
        WriteSettings("""{"integrations.mcp.enabled": 1, "integrations.mcp.maxAgeDays": 45}""");
        Assert.Equal(new McpAccess(true, false, 0), preferences.Access());
        WriteSettings("""{"integrations.mcp.enabled": ["yes"], "integrations.mcp.maxAgeDays": 90.0, "app.language": "klingon"}""");
        Assert.Equal(new McpAccess(false, false, 90), preferences.Access());
        Assert.Equal(AppLanguage.System, preferences.Language());
    }
}
