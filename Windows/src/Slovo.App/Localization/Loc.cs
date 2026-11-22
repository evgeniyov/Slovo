using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Slovo.Core.Localization;
using Slovo.Core.Model;

namespace Slovo.App.Localization;

/// <summary>
/// The interface's strings in the chosen language, switchable at runtime (the Windows
/// counterpart of the macOS <c>Localizer</c>). The tables are the macOS app's
/// <c>Localizable.strings</c>/<c>.stringsdict</c>, then the Windows tables in
/// <c>Strings/&lt;lang&gt;/</c> that replace lines naming macOS and add Windows-only keys.
/// Views bind with <c>{loc:T key}</c>; code calls <see cref="Get"/>, <see cref="Format"/>
/// and <see cref="Plural"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    readonly Dictionary<string, StringTable> tables = [];
    StringTable table;

    Loc()
    {
        table = TableFor(CoreStrings.CurrentCode);
        CoreStrings.LanguageChanged += () =>
        {
            table = TableFor(CoreStrings.CurrentCode);
            // Bindings live on the UI thread; a change made elsewhere is announced there.
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Announce();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Announce);
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Announce()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        LanguageChanged?.Invoke();
    }

    /// <summary>Raised after the interface language changed.</summary>
    public event Action? LanguageChanged;

    public string LanguageCode => table.LanguageCode;

    /// <summary>
    /// The culture for dates and numbers: the interface language with the user's own
    /// region when it matches (so an English interface in Russia keeps 24-hour time).
    /// </summary>
    public CultureInfo Culture
    {
        get
        {
            var system = CultureInfo.CurrentCulture;
            return system.TwoLetterISOLanguageName == table.LanguageCode ? system : table.Culture;
        }
    }

    public string this[string key] => Get(key);

    public string Get(string key) => table.Contains(key) ? table.Get(key) : CoreStrings.Table.Get(key);

    public string Format(string key, params object?[] arguments) =>
        AppleFormat.Format(Get(key), Culture, arguments);

    /// <summary>A plural entry: "1 слово", "22 слова", "12 480 слов".</summary>
    public string Plural(string key, long count) => table.Plural(key, count);

    public bool Contains(string key) => table.Contains(key) || CoreStrings.Table.Contains(key);

    public static void SetLanguage(AppLanguage language) => CoreStrings.SetLanguage(language);

    StringTable TableFor(string code)
    {
        if (!tables.TryGetValue(code, out var loaded))
        {
            var assembly = typeof(Loc).Assembly;
            loaded = StringTable.Load(code, [(assembly, "Slovo.Strings.App/"), (assembly, "Slovo.Strings.AppWindows/")]);
            tables[code] = loaded;
        }
        return loaded;
    }

    /// <summary>All keys of a language's app tables, for the parity tests.</summary>
    internal IReadOnlyCollection<string> KeysFor(string code) => TableFor(code).Keys;

    internal IReadOnlyCollection<string> PluralKeysFor(string code) => TableFor(code).PluralKeys;
}

/// <summary><c>{loc:T sidebar.home}</c>: a string that follows the interface language.</summary>
public sealed class T : MarkupExtension
{
    public T()
    {
    }

    public T(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
