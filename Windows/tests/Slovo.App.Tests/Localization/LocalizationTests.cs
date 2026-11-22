using System.Text.RegularExpressions;
using Slovo.App.Localization;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Tests.Localization;

public partial class LocalizationTests
{
    [Fact]
    public void EnglishAndRussianHaveTheSameKeys()
    {
        var english = Loc.Instance.KeysFor("en").ToHashSet();
        var russian = Loc.Instance.KeysFor("ru").ToHashSet();
        Assert.True(english.Count > 500, "the shared macOS table should be embedded");
        Assert.Empty(english.Except(russian));
        Assert.Empty(russian.Except(english));
        Assert.Equal(Loc.Instance.PluralKeysFor("en").Order(), Loc.Instance.PluralKeysFor("ru").Order());
    }

    [Fact]
    public void WindowsTablesOverrideMacWording()
    {
        var table = StringTable.Load("en", [(typeof(Loc).Assembly, "Slovo.Strings.App/"), (typeof(Loc).Assembly, "Slovo.Strings.AppWindows/")]);
        Assert.Equal("Copied — press Ctrl+V", table.Get("flowbar.copied"));
        Assert.Equal("Show in Explorer", table.Get("meetings.action.finder"));
        var russian = StringTable.Load("ru", [(typeof(Loc).Assembly, "Slovo.Strings.App/"), (typeof(Loc).Assembly, "Slovo.Strings.AppWindows/")]);
        Assert.Equal("Скопировано — нажмите Ctrl+V", russian.Get("flowbar.copied"));
    }

    [Fact]
    public void WindowsOverridesKeepFormatSpecifiers()
    {
        foreach (var code in new[] { "en", "ru" })
        {
            var mac = StringTable.Load(code, [(typeof(Loc).Assembly, "Slovo.Strings.App/")]);
            var merged = StringTable.Load(code, [(typeof(Loc).Assembly, "Slovo.Strings.App/"), (typeof(Loc).Assembly, "Slovo.Strings.AppWindows/")]);
            foreach (var key in mac.Keys)
            {
                Assert.True(Specifiers(mac.Get(key)).SequenceEqual(Specifiers(merged.Get(key))), $"{code}: {key} changed its format specifiers");
            }
        }
    }

    /// <summary>The same text as the macOS app's abbreviated DateComponentsFormatter (probed on macOS 27).</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void DurationsMatchTheMacApp()
    {
        double[] seconds = [0, 59, 720, 10800, 11520, 97500];
        Assert.Equal(["0m", "0m", "12m", "3h 0m", "3h 12m", "27h 5m"], seconds.Select(ViewModels.Formatting.Duration));
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            Assert.Equal(["0 мин", "0 мин", "12 мин", "3 ч 0 мин", "3 ч 12 мин", "27 ч 5 мин"], seconds.Select(ViewModels.Formatting.Duration));
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void RussianPluralsFollowCldr()
    {
        CoreStrings.SetLanguage(AppLanguage.Ru);
        try
        {
            Assert.Equal("1 слово", Loc.Instance.Plural("plural.words", 1));
            Assert.Equal("22 слова", Loc.Instance.Plural("plural.words", 22));
            Assert.Equal("11 слов", Loc.Instance.Plural("plural.words", 11));
            Assert.EndsWith("слов", Loc.Instance.Plural("plural.words", 12480));
        }
        finally
        {
            CoreStrings.SetLanguage(AppLanguage.En);
        }
    }

    [Fact]
    public void MissingKeysFallBackToTheKey() => Assert.Equal("no.such.key", Loc.Instance.Get("no.such.key"));

    static IEnumerable<string> Specifiers(string text) =>
        SpecifierPattern().Matches(text).Select(m => m.Value.Replace("l", "", StringComparison.Ordinal));

    [GeneratedRegex(@"%(\d+\$)?[-+ #0]*\d*(\.\d+)?(l|ll|h)?[@dfisu%]")]
    private static partial Regex SpecifierPattern();
}
