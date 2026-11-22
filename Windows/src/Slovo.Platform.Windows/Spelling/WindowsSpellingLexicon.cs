using System.Runtime.Versioning;
using Slovo.Core.Logging;
using Slovo.Core.Text;
using Slovo.Platform.Windows.Interop;
using Slovo.Platform.Windows.Threading;

namespace Slovo.Platform.Windows.Spelling;

/// <summary>The word lexicon the text pipeline asks on this system.</summary>
public static class SpellingLexicon
{
    /// <summary>
    /// The Windows spell checker; elsewhere (developing on a Mac) a lexicon with no
    /// dictionaries, which term correction handles the way the macOS app does without one.
    /// </summary>
    public static IWordLexicon CreateSystem() => OperatingSystem.IsWindows() ? new WindowsSpellingLexicon() : WordLexicon.Missing;
}

/// <summary>
/// Tells real words from misspellings through the Windows Spell Checking API (the counterpart
/// of <c>SystemSpelling</c>, which asks NSSpellChecker): ISpellCheckerFactory →
/// CreateSpellChecker("ru-RU" / "en-US") → Check.
/// </summary>
/// <remarks>
/// <para>
/// Raw COM through vtables, like <c>UiaFocusedInput</c>; the slots come from spellcheck.h, and
/// each interface starts with IUnknown's QueryInterface (0), AddRef (1) and Release (2).
/// </para>
/// <para>
/// A language is available when the factory supports one of its tags (a language pack or
/// "Basic typing" feature installs the dictionary). Without one, <see cref="HasLanguage"/> is
/// false and no word is correct — what the macOS lexicon answers for a missing dictionary.
/// </para>
/// <para>
/// Threading: every COM call runs on one MTA worker thread, so the lexicon can be used from
/// any thread at once. Answers are cached (20,000 words, then the cache starts over). A check
/// that does not finish within <see cref="Timeout"/>, or fails, counts as a misspelling and is
/// not cached.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsSpellingLexicon : IWordLexicon, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    static readonly Guid ClsidSpellCheckerFactory = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    static readonly Guid IidSpellCheckerFactory = new("8E018A9D-2415-4677-BF08-794EA61F94BB");

    // IUnknown.
    const int ReleaseSlot = 2;
    // ISpellCheckerFactory: get_SupportedLanguages 3, IsSupported 4, CreateSpellChecker 5.
    const int IsSupportedSlot = 4;
    const int CreateSpellCheckerSlot = 5;
    // ISpellChecker: get_LanguageTag 3, Check 4.
    const int CheckSlot = 4;
    // IEnumSpellingError: Next 3.
    const int NextSlot = 3;

    const int SOk = 0;
    const int SFalse = 1;

    /// <summary>Language tags to try for each language code, most common first.</summary>
    static readonly IReadOnlyDictionary<string, string[]> Tags = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["ru"] = ["ru-RU", "ru"],
        ["en"] = ["en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en"],
    };

    readonly WorkerQueue queue = new("Slovo spelling");
    readonly Logger log = Log.For("Spelling");
    readonly IWordLexicon lexicon;
    /// <summary>ISpellChecker per language code, 0 when the system has none. Touched on the queue only.</summary>
    readonly Dictionary<string, nint> checkers = new(StringComparer.Ordinal);
    nint factory;
    bool comInitialized;
    bool factoryFailed;
    bool disposed;

    public WindowsSpellingLexicon()
    {
        lexicon = WordLexicon.Cached(
            language => queue.Invoke(() => (bool?)(Checker(language) != 0), Timeout, null),
            (word, language) => queue.Invoke(() => Check(word, language), Timeout, null));
    }

    /// <summary>Whether the system has a spelling dictionary for the language code ("ru", "en").</summary>
    public bool HasLanguage(string language) => !disposed && lexicon.HasLanguage(language);

    /// <summary>False when the word is misspelled or the system has no dictionary for the language.</summary>
    public bool IsCorrect(string word, string language) => !disposed && lexicon.IsCorrect(word, language);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        queue.Invoke(() =>
        {
            foreach (var checker in checkers.Values)
            {
                if (checker != 0)
                {
                    Release(checker);
                }
            }
            checkers.Clear();
            if (factory != 0)
            {
                Release(factory);
                factory = 0;
            }
            if (comInitialized)
            {
                Ole32.CoUninitialize();
                comInitialized = false;
            }
        }, TimeSpan.FromSeconds(1));
        queue.Dispose();
    }

    /// <summary>On the queue. True when the checker finds no error in the word, null when it could not check.</summary>
    bool? Check(string word, string language)
    {
        var checker = Checker(language);
        if (checker == 0)
        {
            return false;
        }
        nint errors = 0;
        int result;
        fixed (char* text = word)
        {
            var check = (delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)(*(nint**)checker)[CheckSlot];
            result = check(checker, text, &errors);
        }
        if (result < 0 || errors == 0)
        {
            log.Error($"ISpellChecker.Check failed: 0x{result:X8}");
            return null;
        }
        try
        {
            nint error = 0;
            var next = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)errors)[NextSlot];
            result = next(errors, &error);
            if (error != 0)
            {
                Release(error);
            }
            return result switch
            {
                SOk => error == 0,
                SFalse => true,
                _ => null,
            };
        }
        finally
        {
            Release(errors);
        }
    }

    /// <summary>On the queue. The spell checker for a language code, created on first use; 0 when there is none.</summary>
    nint Checker(string language)
    {
        if (checkers.TryGetValue(language, out var existing))
        {
            return existing;
        }
        if (!EnsureFactory())
        {
            return 0;
        }
        nint checker = 0;
        var tags = Tags.TryGetValue(language, out var known) ? known : [language];
        foreach (var tag in tags)
        {
            fixed (char* languageTag = tag)
            {
                var supported = 0;
                var isSupported = (delegate* unmanaged[Stdcall]<nint, char*, int*, int>)(*(nint**)factory)[IsSupportedSlot];
                if (isSupported(factory, languageTag, &supported) < 0 || supported == 0)
                {
                    continue;
                }
                var create = (delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)(*(nint**)factory)[CreateSpellCheckerSlot];
                var result = create(factory, languageTag, &checker);
                if (result >= 0 && checker != 0)
                {
                    log.Info($"Spell checking {language} with the {tag} dictionary");
                    break;
                }
                log.Error($"CreateSpellChecker({tag}) failed: 0x{result:X8}");
                checker = 0;
            }
        }
        if (checker == 0)
        {
            log.Info($"No spelling dictionary for {language}");
        }
        checkers[language] = checker;
        return checker;
    }

    bool EnsureFactory()
    {
        if (factory != 0)
        {
            return true;
        }
        if (factoryFailed)
        {
            return false;
        }
        if (!comInitialized)
        {
            // S_OK or S_FALSE (already initialized): either way it needs a matching CoUninitialize.
            comInitialized = Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED) >= 0;
        }
        var result = Ole32.CoCreateInstance(in ClsidSpellCheckerFactory, 0, Ole32.CLSCTX_INPROC_SERVER, in IidSpellCheckerFactory, out var instance);
        if (result < 0 || instance == 0)
        {
            // Windows 7 and older have no spell checking API: no language is available.
            log.Error($"CoCreateInstance(SpellCheckerFactory) failed: 0x{result:X8}");
            factoryFailed = true;
            return false;
        }
        factory = instance;
        return true;
    }

    static void Release(nint unknown)
    {
        var vtable = *(nint**)unknown;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[ReleaseSlot])(unknown);
    }
}
