using Slovo.Core.Model;
using Slovo.Core.Settings;

namespace Slovo.Core.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void IntegersSetThisSessionReadBackAsIntsAndDoubles()
    {
        var settings = new AppSettings(SettingsStore.InMemory());
        settings.IdleUnload = EngineIdleTimeout.Minutes60;
        settings.ExpectedSpeakers = 4;
        settings.McpMaxAgeDays = 30;
        Assert.Equal(EngineIdleTimeout.Minutes60, settings.IdleUnload);
        Assert.Equal(4, settings.ExpectedSpeakers);
        Assert.Equal(30, settings.McpMaxAgeDays);
        settings.Store.Set("x", 7);
        Assert.Equal(7.0, settings.Store.GetDouble("x"));
    }

    [Fact]
    public void ValuesSurviveTheFileAndAreReadByTheSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "slovo-settings-" + Guid.NewGuid() + ".json");
        try
        {
            var settings = new AppSettings(SettingsStore.Open(path));
            settings.McpEnabled = true;
            settings.McpMaxAgeDays = 90;
            settings.MaxHandsFreeSeconds = 600;
            settings.DictationKey = DictationKey.RightCtrl;
            var snapshot = new AppSettings(SettingsStore.ReadOnlySnapshot(path));
            Assert.True(snapshot.McpEnabled);
            Assert.Equal(90, snapshot.McpMaxAgeDays);
            Assert.Equal(600, snapshot.MaxHandsFreeSeconds);
            Assert.Equal(DictationKey.RightCtrl, snapshot.DictationKey);
            Assert.Throws<InvalidOperationException>(() => snapshot.McpEnabled = false);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADamagedFileIsMovedAsideAndDefaultsApply()
    {
        var folder = Path.Combine(Path.GetTempPath(), "slovo-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, "{ not json");
            var settings = new AppSettings(SettingsStore.Open(path));
            Assert.Equal(DictationKey.CtrlWin, settings.DictationKey);
            Assert.Single(Directory.GetFiles(folder, "settings.corrupt-*.json"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AnEngineWindowsCannotRunFallsBack()
    {
        var store = SettingsStore.InMemory();
        store.Set(SettingsKey.RussianEngine, "apple-dictation");
        Assert.Equal(EngineId.GigaamRnnt, new AppSettings(store).RussianEngine);
    }
}
