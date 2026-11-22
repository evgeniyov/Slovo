using System.Text;

namespace Slovo.Core.Text;

/// <summary>
/// Cyrillic to Latin transliteration used to compare Russian spellings of English terms
/// with the terms themselves (кубернетес → kubernetes).
/// </summary>
public static class Transliterator
{
    /// <summary>
    /// Lowercased Latin transliteration; Latin letters, digits, spaces and punctuation pass
    /// through: «юзер дефолтс» → "yuzer defolts".
    /// </summary>
    public static string Latin(string text)
    {
        var lowered = SwiftUnicode.Lowercased(SwiftString.Canonical(text));
        var output = new StringBuilder(lowered.Length + 8);
        foreach (var character in TextElements.Characters(lowered))
        {
            output.Append(Table.TryGetValue(character, out var mapped) ? mapped : character);
        }
        return output.ToString();
    }

    /// <summary>
    /// The transliteration with everything except Latin letters and digits removed, the form
    /// term matching compares: «гитхаб экшенс» → "githabekshens".
    /// </summary>
    public static string CompactLatin(string text)
    {
        // Swift folds diacritics (é → e) and then keeps a–z and 0–9; folding one scalar at a
        // time leaves the same letters, since it only strips marks from their base.
        var latin = Latin(text);
        var output = new StringBuilder(latin.Length);
        foreach (var rune in latin.EnumerateRunes())
        {
            if (IsCompactScalar(rune.Value))
            {
                output.Append((char)rune.Value);
            }
            else if (SwiftUnicode.AsciiFolding(rune.Value) is > 0 and var folded)
            {
                output.Append((char)folded);
            }
        }
        return output.ToString();
    }

    internal static bool IsCompactScalar(int scalar) => scalar is (>= 0x61 and <= 0x7A) or (>= 0x30 and <= 0x39);

    // Close to everyday Russian spelling of English words rather than a formal standard:
    // х is "h" (гитхаб → githab), й and ы are "y", soft and hard signs vanish.
    static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["а"] = "a", ["б"] = "b", ["в"] = "v", ["г"] = "g", ["д"] = "d", ["е"] = "e", ["ё"] = "yo", ["ж"] = "zh",
        ["з"] = "z", ["и"] = "i", ["й"] = "y", ["к"] = "k", ["л"] = "l", ["м"] = "m", ["н"] = "n", ["о"] = "o",
        ["п"] = "p", ["р"] = "r", ["с"] = "s", ["т"] = "t", ["у"] = "u", ["ф"] = "f", ["х"] = "h", ["ц"] = "ts",
        ["ч"] = "ch", ["ш"] = "sh", ["щ"] = "shch", ["ъ"] = "", ["ы"] = "y", ["ь"] = "", ["э"] = "e", ["ю"] = "yu",
        ["я"] = "ya", ["і"] = "i", ["ї"] = "yi", ["є"] = "ye", ["ґ"] = "g",
    };
}
